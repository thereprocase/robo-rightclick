using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class WinPathTests
{
    [Theory]
    [InlineData(@"C:\a\b", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"c:", "c:")]
    [InlineData(@"\\srv\share\dir\f.txt", @"\\srv\share\")]
    [InlineData(@"\\srv\share", @"\\srv\share\")]
    [InlineData(@"relative\x", "")]
    public void GetRoot(string path, string expected) => Assert.Equal(expected, WinPath.GetRoot(path));

    [Theory]
    [InlineData(@"C:\a\b\", @"C:\a\b")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:/a/b/", @"C:\a\b")]
    [InlineData(@"\\srv\share\", @"\\srv\share\")]
    public void TrimTrailingSeparators(string path, string expected) =>
        Assert.Equal(expected, WinPath.TrimTrailingSeparators(path));

    [Theory]
    [InlineData(@"C:\a\b.txt", @"C:\a", "b.txt")]
    [InlineData(@"C:\a", @"C:\", "a")]
    [InlineData(@"\\srv\share\a", @"\\srv\share\", "a")]
    [InlineData(@"C:\", "", "")]
    public void ParentAndName(string path, string parent, string name)
    {
        Assert.Equal(parent, WinPath.GetParent(path));
        Assert.Equal(name, WinPath.GetFileName(path));
    }

    [Theory]
    [InlineData(@"C:\", "x", @"C:\x")]
    [InlineData(@"C:\a\", "x", @"C:\a\x")]
    [InlineData(@"C:\a", "x", @"C:\a\x")]
    public void Combine(string dir, string name, string expected) => Assert.Equal(expected, WinPath.Combine(dir, name));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"c:\A\B", @"C:\a\", true)]
    [InlineData(@"C:\a", @"C:\a", false)]
    [InlineData(@"C:\ab", @"C:\a", false)] // sibling with a shared prefix is not inside
    [InlineData(@"C:\a\b", @"C:\", true)]
    public void IsStrictlyUnder(string candidate, string ancestor, bool expected) =>
        Assert.Equal(expected, WinPath.IsStrictlyUnder(candidate, ancestor));

    [Theory]
    [InlineData("report.txt", "report", ".txt")]
    [InlineData("archive.tar.gz", "archive.tar", ".gz")]
    [InlineData(".gitignore", ".gitignore", "")]
    [InlineData("README", "README", "")]
    public void SplitExtension(string name, string stem, string ext) =>
        Assert.Equal((stem, ext), WinPath.SplitExtension(name));
}
