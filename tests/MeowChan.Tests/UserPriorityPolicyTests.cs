using MeowChan.Downloader;

namespace MeowChan.Tests;

public sealed class UserPriorityPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ContendedSlotsHaveBoundedNormalOpportunityAndFairHighRotation(int slots)
    {
        long[] last = [-3, -2, -1]; // two High, one Normal
        int streak = 0;
        List<int> order = [];
        for (int batch = 0; batch < 24; batch++)
        for (int slot = 0; slot < slots; slot++)
        {
            var group = UserPriorityPolicy.Preferred(true, true, streak);
            int chosen = group == UserDownloadPriority.Normal ? 2 : last[0] <= last[1] ? 0 : 1;
            order.Add(chosen);
            last[chosen] = order.Count;
            streak = UserPriorityPolicy.Advance(streak, group!.Value);
        }
        for (int i = 0; i + 4 <= order.Count; i++) Assert.Contains(2, order.Skip(i).Take(4));
        int[] high = order.Where(i => i != 2).ToArray();
        for (int i = 1; i < high.Length; i++) Assert.NotEqual(high[i - 1], high[i]);
        Assert.Equal(order.Count / 4, order.Count(i => i == 2));
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 0)]
    public void OnlyEligibleGroupsCompete(bool high, bool normal, int? expected)
        => Assert.Equal(expected, (int?)UserPriorityPolicy.Preferred(high, normal, 3));

    [Fact]
    public void CooldownTargetOrUnrunnableHighMustBeExcludedBeforeSelection()
    {
        foreach (string gate in new[] { "cooldown", "target", "filter", "cleanup", "paused-thread" })
        {
            var candidates = new[] { (High: true, Eligible: false, Gate: gate), (High: false, Eligible: true, Gate: "ready") };
            Assert.Equal(UserDownloadPriority.Normal, UserPriorityPolicy.Preferred(
                candidates.Any(c => c.High && c.Eligible), candidates.Any(c => !c.High && c.Eligible), 0));
        }
    }

    [Fact]
    public void PeekingDoesNotSpendWeightAndReturningNormalGetsImmediateOpportunity()
    {
        int streak = 0;
        for (int i = 0; i < 100; i++) Assert.Equal(UserDownloadPriority.High, UserPriorityPolicy.Preferred(true, true, streak));
        for (int i = 0; i < 100; i++) streak = UserPriorityPolicy.Advance(streak, UserDownloadPriority.High);
        Assert.Equal(3, streak);
        Assert.Equal(UserDownloadPriority.Normal, UserPriorityPolicy.Preferred(true, true, streak));
    }

    [Fact]
    public void OldestThreadWinsBeforeOriginAndOriginOnlyBreaksTies()
    {
        Assert.True(UserPriorityPolicy.Better(1, false, 2, true));
        Assert.False(UserPriorityPolicy.Better(2, true, 1, false));
        Assert.True(UserPriorityPolicy.Better(1, true, 1, false));
        Assert.False(UserPriorityPolicy.Better(1, false, 1, true));
    }
}
