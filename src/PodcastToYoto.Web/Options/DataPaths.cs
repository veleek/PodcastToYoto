namespace PodcastToYoto.Web.Options;

/// <summary>The resolved (absolute) data directory and well-known paths inside it. Registered once at startup.</summary>
public sealed record DataPaths(string Root)
{
    public string Keys => Path.Combine(Root, "keys");
    public string Database => Path.Combine(Root, "app.db");
    public string Temp => Path.Combine(Root, "tmp");
    public string IconCache => Path.Combine(Root, "icons.json");
}
