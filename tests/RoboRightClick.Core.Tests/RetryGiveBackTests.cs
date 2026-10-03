using RoboRightClick.Core;

namespace RoboRightClick.Core.Tests;

/// <summary>A "Try again" child that ended having done nothing gives its parent's "Try again" back.</summary>
public class RetryGiveBackTests
{
    private static JobSnapshot Child(JobState state) =>
        DisplayAndTrayTests.Job(state) with { ParentId = Guid.NewGuid(), DoneFiles = 0, ErrorCount = 0 };

    [Fact]
    public void A_child_canceled_before_doing_anything_gives_the_retry_back()
    {
        // The user closed the child's conflict question to look at the folder first.
        Assert.True(RetryRules.GivesBackParentRetry(Child(JobState.Canceled)));
    }

    [Fact]
    public void A_child_that_did_anything_keeps_the_retry_used()
    {
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Canceled) with { DoneFiles = 1 }));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Canceled) with { DamagedOnCancel = 1 }));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Canceled) with { MayBeIncomplete = 1 }));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Canceled) with { ErrorCount = 1 }));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Done)));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.DoneWithErrors)));
        Assert.False(RetryRules.GivesBackParentRetry(Child(JobState.Canceled) with { ParentId = null }));
    }
}
