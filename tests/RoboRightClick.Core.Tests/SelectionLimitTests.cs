using System.Globalization;
using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>An oversized selection is refused whole, with a toast that states the limit and names no path.</summary>
public class SelectionLimitTests
{
    [Fact]
    public void Limits_match_the_clipboard_so_an_accepted_selection_fits_on_it()
    {
        Assert.Equal(ClipboardPayload.MaxDropFilesPaths, SelectionLimits.MaxItems);
        Assert.Equal(250_000, SelectionLimits.MaxItems);
        Assert.Equal(ClipboardPayload.MaxDropFilesBytes, SelectionLimits.MaxBlockBytes);
        Assert.Equal(ClipboardPayload.MaxDropFilesBytes / 2, SelectionLimits.MaxTotalChars);
        Assert.Equal(32_767, SelectionLimits.MaxPathChars);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(250_000L, false)]
    [InlineData(250_001L, true)]
    [InlineData(uint.MaxValue, true)]
    public void Item_count_limit_is_inclusive(long count, bool tooMany)
    {
        Assert.Equal(tooMany, SelectionLimits.TooManyItems(count));
    }

    [Theory]
    [InlineData(0UL, false)]
    [InlineData(64UL * 1024 * 1024, false)]
    [InlineData(64UL * 1024 * 1024 + 1, true)]
    [InlineData(ulong.MaxValue, true)]
    public void Block_size_limit_is_inclusive(ulong bytes, bool tooLarge)
    {
        Assert.Equal(tooLarge, SelectionLimits.BlockTooLarge(bytes));
    }

    [Theory]
    [InlineData(32_767, 32_767L, false)]
    [InlineData(32_768, 32_768L, true)]
    [InlineData(10, 32L * 1024 * 1024, false)]
    [InlineData(10, 32L * 1024 * 1024 + 1, true)]
    public void A_path_is_too_long_alone_or_by_the_running_total(int pathChars, long total, bool tooLong)
    {
        Assert.Equal(tooLong, SelectionLimits.PathTooLong(pathChars, total));
    }

    [Fact]
    public void The_toast_states_the_limit_and_what_to_do_instead()
    {
        var toast = ToastText.ForRefusal(VerbRefusal.SelectionTooLarge);
        Assert.Contains(SelectionLimits.MaxItems.ToString("N0", CultureInfo.CurrentCulture), toast.Body);
        Assert.Contains("folder", toast.Body);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.NotEqual(ToastText.ForRefusal(VerbRefusal.ClipboardTooLarge).Title, toast.Title);
    }

    [Fact]
    public void The_toast_names_no_path()
    {
        var toast = ToastText.ForRefusal(VerbRefusal.SelectionTooLarge);
        Assert.DoesNotContain(@":\", toast.Title + toast.Body);
        Assert.DoesNotContain(@"\\", toast.Title + toast.Body);
    }
}
