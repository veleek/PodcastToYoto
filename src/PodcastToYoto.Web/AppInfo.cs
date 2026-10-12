using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PodcastToYoto.Web;

/// <summary>
/// Build and runtime facts for the Settings page's About section. The commit comes from the
/// <c>+&lt;sha&gt;</c> suffix the SDK appends to InformationalVersion: SourceLink fills it from .git on local
/// builds, and the Dockerfile passes it in as <c>SourceRevisionId</c> (build arg <c>GIT_SHA</c>) since .git isn't
/// in the build context.
/// </summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/veleek/PodcastToYoto";

    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    public static BuildVersion Version { get; } = BuildVersion.Parse(
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>When the app assembly was compiled (its file time; in the container image, the image build).</summary>
    public static DateTimeOffset? BuiltAt { get; } = GetBuiltAt();

    public static DateTimeOffset StartedAt { get; } = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static string Runtime => $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})";

    public static string OperatingSystem => RuntimeInformation.OSDescription;

    public static bool InContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? GetBuiltAt()
    {
        var path = Assembly.Location;
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(path));
    }
}

/// <param name="Version">The version without the commit, e.g. "0.8.1".</param>
/// <param name="Commit">Full commit sha, or null when the build didn't know it.</param>
/// <param name="Dirty">The build had uncommitted changes on top of <paramref name="Commit"/>.</param>
public sealed record BuildVersion(string Version, string? Commit, bool Dirty)
{
    public string? ShortCommit => Commit is null ? null : Commit[..Math.Min(7, Commit.Length)];

    public string? CommitUrl => Commit is null ? null : $"{AppInfo.RepositoryUrl}/commit/{Commit}";

    /// <summary>"0.8.1+89bae451e8f9…-dirty" → ("0.8.1", "89bae451e8f9…", true).</summary>
    public static BuildVersion Parse(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0) return new(informationalVersion, null, false);

        var version = informationalVersion[..plus];
        var commit = informationalVersion[(plus + 1)..];
        var dirty = commit.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase);
        if (dirty) commit = commit[..^"-dirty".Length];
        return new(version, commit.Length == 0 ? null : commit, dirty);
    }
}
