using Xunit;

namespace BitKraken.Tests;

public class AppInfoTests
{
    [Theory]
    [InlineData("1.0.3", "1.0.3")]
    [InlineData("1.0.3+abc1234", "1.0.3")]                       // the SDK's commit metadata is UI noise
    [InlineData("1.0.3-preview", "1.0.3-preview")]               // the pre-release suffix is part of the version
    [InlineData("1.0.3-preview+abc1234", "1.0.3-preview")]
    [InlineData("1.0.3+abc+def", "1.0.3")]
    [InlineData("", "")]
    public void TrimBuildMetadata_keeps_the_pre_release_suffix_and_drops_the_build_metadata(string informational, string expected) =>
        Assert.Equal(expected, AppInfo.TrimBuildMetadata(informational));

    [Fact]
    public void Version_is_never_empty_and_display_version_is_prefixed()
    {
        // An unstamped local build still has to render something in the title bar.
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.DoesNotContain("+", AppInfo.Version);
        Assert.Equal("v" + AppInfo.Version, AppInfo.DisplayVersion);
    }
}
