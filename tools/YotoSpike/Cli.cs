namespace YotoSpike;

/// <summary>Tiny "command [positional...] [--flag [value]]" parser; avoids a package dependency for a throwaway tool.</summary>
internal sealed class Cli
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var name = a[2..];
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) value = args[++i];
                cli._options[name] = value;
            }
            else if (cli.Command is null) cli.Command = a.ToLowerInvariant();
            else cli.Positional.Add(a);
        }
        return cli;
    }

    public bool Has(string name) => _options.ContainsKey(name);
    public string? Get(string name) => _options.GetValueOrDefault(name);
    public int? GetInt(string name) => int.TryParse(Get(name), out var v) ? v : null;
}
