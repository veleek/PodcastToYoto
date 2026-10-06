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

public sealed class HomeAssistantOptions
{
    public const string Section = "Notifications:HomeAssistant";

    /// <summary>e.g. http://homeassistant:8123 or https://hass.example.com</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Webhook trigger id (POST /api/webhook/{id}); no token needed.</summary>
    public string? WebhookId { get; set; }

    /// <summary>Service to call, e.g. notify.mobile_app_phone or persistent_notification.create (needs Token).</summary>
    public string? Service { get; set; }

    /// <summary>Long-lived access token (only for Service).</summary>
    public string? Token { get; set; }

    /// <summary>Which events to send (PlaylistUpdated, SyncFailed, YotoNeedsLogin). Empty = all.</summary>
    public List<PodcastToYoto.Web.Sync.NotificationKind> Events { get; set; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && (!string.IsNullOrWhiteSpace(WebhookId) || (!string.IsNullOrWhiteSpace(Service) && !string.IsNullOrWhiteSpace(Token)));

    public bool ShouldSend(PodcastToYoto.Web.Sync.NotificationKind kind) =>
        kind == PodcastToYoto.Web.Sync.NotificationKind.Test || Events.Count == 0 || Events.Contains(kind);
}

