using MeowChan.Admission;

namespace MeowChan.Tests;

public sealed class TitlePatternMinerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedCounterAndPrefixRules = ["film camera club", "recent photo thread"];
    private static readonly string[] ExpectedTiedRules = ["/a/", "/z/"];

    [Fact]
    public void SlashPatternKeepsTheOriginalExampleAndUsesDistinctFirstSightings()
    {
        BoardTitleHistory[] rows = [Row(5, "/RPT/ #5", 3), Row(1, "/rpt/ #1", 11),
            Row(2, "/p/ /rpt/ #2", 9), Row(3, "/rpt/ #3", 7), Row(4, "/rpt/ #4", 5),
            Row(6, "A question about lenses", 2), Row(7, "Sony vs Fuji", 1)];

        var result = TitlePatternMiner.Mine(rows, "p", Now, 3, 12);
        var suggestion = Assert.Single(result.Items);
        Assert.Equal("/rpt/", suggestion.RuleText);
        Assert.Equal(5, suggestion.Count);
        Assert.Equal(Now.AddDays(-11), suggestion.FirstSeenAt);
        Assert.Equal(Now.AddDays(-3), suggestion.LastSeenAt);
        Assert.Equal(TimeSpan.FromDays(2), suggestion.Rhythm);
        Assert.Equal("/RPT/ #5", suggestion.ExampleTitle);
        Assert.Equal(7, result.Scanned);
    }

    [Fact]
    public void CounterAndPrefixGroupsYieldOnlyLiteralPhrases()
    {
        BoardTitleHistory[] rows = [Row(1, "Recent Photo Thread 12", 6), Row(2, "recent photo thread #13", 5),
            Row(3, "RECENT PHOTO THREAD – 14", 4), Row(4, "Film Camera Club – Autumn edition", 3),
            Row(5, "Film Camera Club Winter", 2), Row(6, "Film Camera Club Summer topics", 1)];

        Assert.Equal(ExpectedCounterAndPrefixRules,
            TitlePatternMiner.Mine(rows, "p", Now, 3, 12).Items.Select(item => item.RuleText));
    }

    [Fact]
    public void RejectsBroadAndNonliteralPatternsAndOrdersTiesDeterministically()
    {
        BoardTitleHistory[] rows = [Row(1, "Thread #1", 1), Row(2, "Thread #2", 2), Row(3, "Thread #3", 3),
            Row(4, "A different thread", 1), Row(5, "/z/ thread", 1), Row(6, "/z/ thread", 2), Row(7, "/z/ thread", 3),
            Row(8, "/a/ thread", 1), Row(9, "/a/ thread", 2), Row(10, "/a/ thread", 3),
            Row(11, "Ｆｕｌｌｗｉｄｔｈ #1", 1), Row(12, "Ｆｕｌｌｗｉｄｔｈ #2", 2), Row(13, "Ｆｕｌｌｗｉｄｔｈ #3", 3),
            Row(14, "", 1), Row(15, "１５", 1), Row(16, " a ", 1)];

        var expected = TitlePatternMiner.Mine(rows, "p", Now, 3, 12);
        Assert.Equal(ExpectedTiedRules, expected.Items.Select(item => item.RuleText));
        Assert.Equal(expected.Items.ToArray(), TitlePatternMiner.Mine(rows.Reverse().ToArray(), "p", Now, 3, 12).Items.ToArray());
        Assert.Equal("/a/", Assert.Single(TitlePatternMiner.Mine(rows, "p", Now, 3, 1).Items).RuleText);
    }

    private static BoardTitleHistory Row(int id, string title, int age) => new(id.ToString(), title, Now.AddDays(-age), Now);
}
