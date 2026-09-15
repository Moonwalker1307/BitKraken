using Xunit;

namespace BitKraken.Tests;

/// <summary>
/// <see cref="App.NormalizeSource"/> is what a magnet link clicked in a browser lands on: the shell
/// hands us a command-line argument or a URL, and anything that isn't a torrent has to be dropped.
/// </summary>
public class SourceNormalizationTests
{
    [Theory]
    [InlineData("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")]
    [InlineData("MAGNET:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a")]   // schemes are case-insensitive
    public void A_magnet_link_passes_through_untouched(string magnet) =>
        Assert.Equal(magnet, App.NormalizeSource(magnet));

    [Fact]
    public void A_magnet_link_is_kept_without_the_file_having_to_exist()
    {
        // The magnet branch returns before any disk check; nothing on disk corresponds to it.
        Assert.NotNull(App.NormalizeSource("magnet:?xt=urn:btih:deadbeef&dn=nothing+on+disk"));
    }

    [Fact]
    public void An_existing_torrent_path_is_kept()
    {
        var path = CreateTorrentFile("sample.torrent");

        Assert.Equal(path, App.NormalizeSource(path));
    }

    [Fact]
    public void A_file_url_becomes_a_local_path()
    {
        // Browsers and file managers hand over file:// URLs rather than plain paths.
        var path = CreateTorrentFile("from-url.torrent");

        Assert.Equal(path, App.NormalizeSource(new Uri(path).AbsoluteUri));
    }

    [Fact]
    public void A_file_url_with_escaped_characters_becomes_a_local_path()
    {
        var path = CreateTorrentFile("a name with spaces.torrent");
        var url = new Uri(path).AbsoluteUri;

        Assert.Contains("%20", url);                      // guards the test itself
        Assert.Equal(path, App.NormalizeSource(url));
    }

    [Theory]
    [InlineData("https://example.com/linux.torrent")]     // a URL we would have to download first
    [InlineData("not-a-torrent")]
    [InlineData("")]
    public void Anything_that_is_not_a_magnet_or_an_existing_file_is_dropped(string source) =>
        Assert.Null(App.NormalizeSource(source));

    [Fact]
    public void A_path_that_does_not_exist_is_dropped()
    {
        var missing = Path.Combine(TestEnvironment.NewDirectory(), "gone.torrent");

        Assert.Null(App.NormalizeSource(missing));
    }

    [Fact]
    public void A_file_url_pointing_at_nothing_is_dropped()
    {
        var missing = new Uri(Path.Combine(TestEnvironment.NewDirectory(), "gone.torrent")).AbsoluteUri;

        Assert.Null(App.NormalizeSource(missing));
    }

    private static string CreateTorrentFile(string name)
    {
        // Contents are irrelevant here - normalization only asks whether the file is there.
        var path = Path.Combine(TestEnvironment.NewDirectory(), name);
        File.WriteAllText(path, "d4:infod4:name6:sampleee");
        return path;
    }
}
