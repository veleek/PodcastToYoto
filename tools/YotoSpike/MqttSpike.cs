using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Protocol;

namespace YotoSpike;

/// <summary>
/// Logs MQTT player events/status for every player on the account and sends commands typed on stdin
/// (docs/stream-triggers.md, "MQTT puzzle mechanics"). Needs the family:devices:view and family:devices:control scopes.
/// </summary>
internal sealed class MqttSpike(HttpClient http, Func<CancellationToken, Task<string>> accessToken, string logPath)
{
    // https://yoto.dev/players-mqtt/connecting-to-players
    private const string BrokerUrl = "wss://aqrphjqbp3u2z-ats.iot.eu-west-2.amazonaws.com/mqtt";
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(295); // Yoto drops idle connections after ~5 min

    private readonly List<(string Id, string Name, IMqttClient Client)> _players = [];
    private readonly SemaphoreSlim _logLock = new(1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last;

    public async Task<int> RunAsync(string? deviceFilter, int statusEverySeconds, CancellationToken ct)
    {
        var devices = await GetDevicesAsync(ct);
        foreach (var d in devices) Console.WriteLine($"  {d.Id}  {d.Name}  online={d.Online}");
        var selected = devices.Where(d => deviceFilter is null || d.Id == deviceFilter || string.Equals(d.Name, deviceFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) { Console.Error.WriteLine("No matching players."); return 1; }

        foreach (var d in selected)
        {
            var client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += e => OnMessageAsync(d.Name, e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString());
            client.DisconnectedAsync += async e =>
            {
                if (ct.IsCancellationRequested) return;
                Console.WriteLine($"[{d.Name}] disconnected ({e.Reason}); reconnecting with a fresh token");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                await ConnectAsync(client, d.Id, ct);
            };
            await ConnectAsync(client, d.Id, ct);
            _players.Add((d.Id, d.Name, client));
        }

        using var keepAlive = new PeriodicTimer(KeepAlive);
        _ = Task.Run(async () => { while (await keepAlive.WaitForNextTickAsync(ct)) await SendAllAsync("events/request", "{}", ct); }, ct);
        if (statusEverySeconds > 0)
        {
            var statusTimer = new PeriodicTimer(TimeSpan.FromSeconds(statusEverySeconds));
            _ = Task.Run(async () => { while (await statusTimer.WaitForNextTickAsync(ct)) await SendAllAsync("status/request", "{}", ct); }, ct);
        }

        PrintCommands();
        while (!ct.IsCancellationRequested && Console.In.ReadLine() is { } line)
        {
            try { await HandleCommandAsync(line.Trim(), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine($"  ! {ex.Message}"); }
        }
        foreach (var p in _players) await p.Client.DisconnectAsync(cancellationToken: CancellationToken.None);
        return 0;
    }

    private async Task ConnectAsync(IMqttClient client, string deviceId, CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithWebSocketServer(o => o.WithUri(BrokerUrl))
            .WithTlsOptions(o => o.UseTls())
            .WithClientId($"DASH{deviceId}")
            .WithCredentials($"{deviceId}?x-amz-customauthorizer-name=PublicJWTAuthorizer", await accessToken(ct))
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(300))
            .Build();
        await client.ConnectAsync(options, ct);
        foreach (var t in new[] { "data/events", "data/status", "response" })
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter($"device/{deviceId}/{t}").Build(), ct);
        await client.PublishStringAsync($"device/{deviceId}/command/events/request", "{}", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken: ct);
        await client.PublishStringAsync($"device/{deviceId}/command/status/request", "{}", MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken: ct);
        Console.WriteLine($"Connected to {deviceId}");
    }

    private async Task OnMessageAsync(string player, string topic, string payload)
    {
        var now = _clock.Elapsed;
        var delta = now - _last;
        _last = now;
        var kind = topic.Split('/').Last();
        JsonNode? body = null;
        try { body = JsonNode.Parse(payload); } catch (JsonException) { }

        // One readable line per message; the full payload goes to the log file.
        var summary = kind switch
        {
            "events" => Fields(body, "playbackStatus", "source", "cardId", "chapterKey", "trackKey", "position", "volume"),
            "status" => Fields(body?["status"] ?? body, "cardInserted", "activeCard", "playingStatus", "headphones", "bluetoothHp", "volume", "userVolume", "batteryLevel"),
            _ => payload,
        };
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} (+{delta.TotalMilliseconds,6:F0} ms) [{player}] {kind,-8} {summary}");

        var line = new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["player"] = player,
            ["topic"] = topic,
            ["payload"] = body ?? payload,
        };
        await _logLock.WaitAsync();
        try { await File.AppendAllTextAsync(logPath, line.ToJsonString() + "\n"); }
        finally { _logLock.Release(); }
    }

    private static string Fields(JsonNode? node, params string[] names) =>
        node is JsonObject o
            ? string.Join(' ', names.Where(o.ContainsKey).Select(n => $"{n}={o[n]?.ToJsonString().Trim('"')}"))
            : node?.ToJsonString() ?? "";

    private async Task HandleCommandAsync(string line, CancellationToken ct)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        switch (parts[0].ToLowerInvariant())
        {
            case "events": await SendAllAsync("events/request", "{}", ct); break;
            case "status": await SendAllAsync("status/request", "{}", ct); break;
            case "stop" or "pause" or "resume": await SendAllAsync($"card/{parts[0].ToLowerInvariant()}", "{}", ct); break;
            case "vol": await SendAllAsync("volume/set", new JsonObject { ["volume"] = int.Parse(parts[1]) }.ToJsonString(), ct); break;
            case "play":
                // play <cardId> [chapterKey] [trackKey] [secondsIn] [cutOff]
                var start = new JsonObject { ["uri"] = $"https://yoto.io/{parts[1]}" };
                if (parts.Length > 2) start["chapterKey"] = parts[2];
                if (parts.Length > 3) start["trackKey"] = parts[3];
                if (parts.Length > 4) start["secondsIn"] = int.Parse(parts[4]);
                if (parts.Length > 5) start["cutOff"] = int.Parse(parts[5]);
                await SendAllAsync("card/start", start.ToJsonString(), ct);
                break;
            case "preview":
                // preview <icon uri> [seconds] [animated 0|1]
                var preview = new JsonObject
                {
                    ["uri"] = parts[1],
                    ["timeout"] = parts.Length > 2 ? int.Parse(parts[2]) : 10,
                    ["animated"] = parts.Length > 3 ? int.Parse(parts[3]) : 0,
                };
                await SendAllAsync("display/preview", preview.ToJsonString(), ct);
                break;
            case "raw":
                // raw <command path under command/> <json>
                await SendAllAsync(parts[1], parts.Length > 2 ? string.Join(' ', parts[2..]) : "{}", ct);
                break;
            default: PrintCommands(); break;
        }
    }

    private async Task SendAllAsync(string command, string payload, CancellationToken ct)
    {
        foreach (var (id, _, client) in _players.Where(p => p.Client.IsConnected))
            await client.PublishStringAsync($"device/{id}/command/{command}", payload, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken: ct);
    }

    private async Task<List<(string Id, string Name, bool? Online)>> GetDevicesAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.yotoplay.com/device-v2/devices/mine");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GET devices/mine {(int)resp.StatusCode}: {body}");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(logPath)!, "devices.json"), body, ct);
        var devices = JsonNode.Parse(body)?["devices"]?.AsArray() ?? [];
        return devices.OfType<JsonObject>()
            .Select(d => ((string)d["deviceId"]!, (string?)d["name"] ?? (string)d["deviceId"]!, (bool?)d["online"]))
            .ToList();
    }

    private static void PrintCommands() => Console.WriteLine("""
        Commands (sent to every connected player):
          events | status | stop | pause | resume | vol <n>
          play <cardId> [chapterKey] [trackKey] [secondsIn] [cutOff]
          preview <icon uri> [seconds] [animated 0|1]
          raw <command path> <json>          e.g. raw sleep-timer/set {"seconds":0}
        Ctrl+C to quit.
        """);
}
