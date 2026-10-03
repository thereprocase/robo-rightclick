using System.Text.RegularExpressions;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>
/// scripts/publish.sh refuses to package a version its pattern does not accept. The pattern
/// must accept every release version the app orders (beta, rc, stable) and nothing
/// <see cref="AppVersion.TryParse"/> would refuse, or a release is blocked, or a package is
/// named with a version the installer cannot compare.
/// </summary>
public class PublishVersionTests
{
    private static Regex Pattern()
    {
        var script = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Scripts", "publish.sh"));
        var line = Assert.Single(script, l => l.StartsWith("version_pattern='", StringComparison.Ordinal));
        var pattern = line["version_pattern='".Length..^1];
        // Bash ERE and .NET agree on this subset: anchors, classes, groups, alternation, * + ?.
        return new Regex(pattern, RegexOptions.CultureInvariant);
    }

    [Theory]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0-beta.12")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.0.0")]
    [InlineData("2.10.3")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-0.3.7")]
    [InlineData("1.0.0-x-y.2")]
    public void Every_release_shape_the_app_orders_can_be_packaged(string version)
    {
        Assert.NotNull(AppVersion.TryParse(version));
        Assert.Matches(Pattern(), version);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+sha.abc")]
    [InlineData("1.0.0-beta_1")]
    [InlineData(" 1.0.0")]
    [InlineData("v1.0.0")]
    public void Nothing_else_is_packaged(string version) =>
        Assert.DoesNotMatch(Pattern(), version);

    [Theory]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("1.0.0-")]
    public void Whatever_the_app_refuses_the_script_refuses_too(string version)
    {
        Assert.Null(AppVersion.TryParse(version));
        Assert.DoesNotMatch(Pattern(), version);
    }
}
