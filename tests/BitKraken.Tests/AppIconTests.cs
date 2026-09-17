using BitKraken.Services;
using Xunit;

namespace BitKraken.Tests;

public class AppIconTests
{
    /// <summary>The logo has to reach disk: a path is the only thing notify-send or a toast can show.</summary>
    [Fact]
    public void ExtractWritesThePngWhereItSaysItDid()
    {
        var path = AppIcon.Extract(TestEnvironment.NewDirectory());

        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        // The first eight bytes of any PNG, so this asserts the image came out whole, not just a file.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, File.ReadAllBytes(path)[..8]);
    }

    [Fact]
    public void ExtractLeavesNoStagingFileBehind()
    {
        var directory = TestEnvironment.NewDirectory();

        AppIcon.Extract(directory);
        AppIcon.Extract(directory);

        Assert.Equal(["bitkraken.png"], Directory.GetFiles(directory).Select(Path.GetFileName).Order());
    }

    /// <summary>A directory that cannot be written is a missing logo, never a failed notification.</summary>
    [Fact]
    public void ExtractReturnsNullWhenItCannotWrite()
    {
        var file = Path.Combine(TestEnvironment.NewDirectory(), "not-a-directory");
        File.WriteAllText(file, "");

        Assert.Null(AppIcon.Extract(file));
    }
}
