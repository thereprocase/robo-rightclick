using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

public class SecurityLimitsTests
{
    [Fact]
    public void A_summary_list_shows_at_most_a_thousand_rows_and_counts_the_rest()
    {
        Assert.Equal(1_000, DisplayText.MaxListedRows);
        Assert.Equal("and 249,000 more are not listed here.", DisplayText.UnlistedRowsText(250_000 - DisplayText.MaxListedRows));
    }
}
