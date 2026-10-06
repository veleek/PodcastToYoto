using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PodcastToYoto.Web.Options;

namespace PodcastToYoto.Web.Sync;

/// <summary>
/// Sends notifications to Home Assistant, either or both of:
/// <list type="bullet">
/// <item><b>Webhook</b> (<c>WebhookId</c>): POST JSON to <c>/api/webhook/{id}</c>; build whatever automation you like.
/// No token needed.</item>
/// <item><b>Service</b> (<c>Service</c> + <c>Token</c>): call e.g. <c>notify.mobile_app_phone</c> or
/// <c>persistent_notification.create</c> via the REST API with a long-lived access token.</item>
/// </list>
/// </summary>
public sealed class HomeAssistantChannel(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<HomeAssistantOptions> options,
    ILogger<HomeAssistantChannel> logger) : INotificationChannel
{
    public const string HttpClientName = "homeassistant";

    public string Name => "Home Assistant";

    public bool IsEnabled => options.CurrentValue.IsConfigured;

    public async Task SendAsync(NotificationContext c, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var n = c.Notification;
        if (!o.ShouldSend(n.Kind)) return;

        var http = httpFactory.CreateClient(HttpClientName);
        var baseUrl = o.BaseUrl!.TrimEnd('/');
        var title = c.SubscriptionName is null || n.Kind == NotificationKind.PlaylistUpdated ? n.Title : $"{n.Title}: {c.SubscriptionName}";

        if (!string.IsNullOrWhiteSpace(o.WebhookId))
        {
            var payload = new
            {
                kind = n.Kind.ToString(),
                title,
                message = n.Message,
                subscription_id = n.SubscriptionId,
                subscription = c.SubscriptionName,
                subscription_url = c.SubscriptionUrl,
                playlist_url = c.PlaylistUrl,
            };
            using var resp = await http.PostAsJsonAsync($"{baseUrl}/api/webhook/{Uri.EscapeDataString(o.WebhookId)}", payload, ct);
            await EnsureOkAsync(resp, "webhook", ct);
        }

        if (!string.IsNullOrWhiteSpace(o.Service) && !string.IsNullOrWhiteSpace(o.Token))
        {
            var (domain, service) = SplitService(o.Service);
            object body = domain == "notify"
                ? new { title, message = n.Message, data = new { url = c.SubscriptionUrl, clickAction = c.SubscriptionUrl } }
                : new { title, message = n.Message };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/services/{domain}/{service}")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
            using var resp = await http.SendAsync(req, ct);
            await EnsureOkAsync(resp, o.Service, ct);
        }

        logger.LogDebug("Home Assistant notified: {Kind}", n.Kind);
    }

    private static (string Domain, string Service) SplitService(string s)
    {
        var dot = s.IndexOf('.');
        if (dot <= 0 || dot == s.Length - 1) throw new InvalidOperationException($"Service must look like 'domain.service', got '{s}'.");
        return (s[..dot], s[(dot + 1)..]);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Home Assistant {what} returned {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
    }
}
