using System.ComponentModel.DataAnnotations;

namespace PodcastToYoto.Web.Options;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>External URL users reach the app on (behind Traefik). Used to build the OAuth redirect URI.</summary>
    [Required, Url]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Holds the SQLite DB, Data Protection keys and temp downloads. Relative paths resolve against the content root.</summary>
    [Required]
    public string DataDirectory { get; set; } = "/data";

    public string YotoRedirectUri => $"{PublicBaseUrl.TrimEnd('/')}/auth/callback";
}

public sealed class YotoOptions
{
    public const string Section = "Yoto";

    [Required]
    public string ClientId { get; set; } = "";

    public string Scopes { get; set; } = PodcastToYoto.Yoto.YotoOAuthOptions.DefaultScopes;
}

public sealed class SyncOptions
{
    public const string Section = "Sync";

    public TimeSpan DefaultPollInterval { get; set; } = TimeSpan.FromHours(1);
    public bool RunOnStartup { get; set; } = true;

    /// <summary>Global kill-switch: plan and log, but never write to Yoto.</summary>
    public bool DryRun { get; set; }
}
