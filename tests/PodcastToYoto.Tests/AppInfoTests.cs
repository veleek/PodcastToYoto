using PodcastToYoto.Web;

namespace PodcastToYoto.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData("0.8.1", "0.8.1", null, false)]
    [InlineData("0.8.1+89bae451e8f93521442e336d8ad81823ad08c32b", "0.8.1", "89bae451e8f93521442e336d8ad81823ad08c32b", false)]
    [InlineData("0.8.1+89bae451e8f93521442e336d8ad81823ad08c32b-dirty", "0.8.1", "89bae451e8f93521442e336d8ad81823ad08c32b", true)]
    [InlineData("0.8.1-beta+89bae45", "0.8.1-beta", "89bae45", false)]
    [InlineData("0.8.1+", "0.8.1", null, false)]
    public void Parse_SplitsVersionCommitAndDirty(string input, string version, string? commit, bool dirty)
    {
        var v = BuildVersion.Parse(input);

        Assert.Equal(version, v.Version);
        Assert.Equal(commit, v.Commit);
        Assert.Equal(dirty, v.Dirty);
    }

    [Fact]
    public void ShortCommitAndUrl()
    {
        var v = BuildVersion.Parse("1.0.0+89bae451e8f93521442e336d8ad81823ad08c32b");

        Assert.Equal("89bae45", v.ShortCommit);
        Assert.Equal($"{AppInfo.RepositoryUrl}/commit/89bae451e8f93521442e336d8ad81823ad08c32b", v.CommitUrl);
    }

    [Fact]
    public void CurrentBuild_HasVersion() => Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version.Version));
}
