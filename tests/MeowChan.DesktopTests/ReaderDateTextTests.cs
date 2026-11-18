using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderDateTextTests : IDisposable
{
    private static readonly DateTimeOffset Opening = new(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(2));

    public ReaderDateTextTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("en", "+2 h", "3 d ago", "just now")]
    [InlineData("de", "+2 Std.", "vor 3 Tg.", "gerade eben")]
    [InlineData("es", "+2 h", "hace 3 d", "ahora mismo")]
    [InlineData("fr", "+2 h", "il y a 3 j", "à l’instant")]
    [InlineData("ru", "+2 ч", "3 дн. назад", "только что")]
    public void EveryDateStyleUsesTheChosenLanguageAndKeepsTheExactLocalDate(string language,
        string sinceOpening, string relative, string justNow)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        DateTimeOffset posted = Opening.AddHours(2);
        DateTimeOffset now = posted.AddDays(3).AddMinutes(12);
        string system = posted.ToLocalTime().ToString("g", Texts.Culture);
        string full = posted.ToLocalTime().ToString("F", Texts.Culture);

        Assert.Equal(system, ReaderDateText.Posted(posted, ReaderDateStyle.System, Opening, now));
        Assert.Equal(full, ReaderDateText.Posted(posted, ReaderDateStyle.Long, Opening, now));
        Assert.Equal(sinceOpening, ReaderDateText.Posted(posted, ReaderDateStyle.SinceOp, Opening, now));
        Assert.Equal(relative, ReaderDateText.Posted(posted, ReaderDateStyle.RelativeToNow, Opening, now));
        Assert.Equal(justNow, ReaderDateText.Posted(posted, ReaderDateStyle.RelativeToNow, Opening, posted));
        Assert.Equal(full, ReaderDateText.PostedFull(posted));
        Assert.Equal(system, ReaderDateText.Posted(posted, ReaderDateStyle.SinceOp, null, now));
        Assert.Equal(system, ReaderDateText.Posted(posted, (ReaderDateStyle)int.MaxValue, Opening, now));
        foreach (ReaderDateStyle style in Enum.GetValues<ReaderDateStyle>())
            Assert.Empty(ReaderDateText.Posted(null, style, Opening, now));
        Assert.Empty(ReaderDateText.PostedFull(null));
    }

    [Fact]
    public void DurationsUseInstantsRatherThanWallClockOffsetsAndNeverShowNegativeAges()
    {
        Texts.SetLanguage("en");
        DateTimeOffset sameInstant = Opening.ToOffset(TimeSpan.FromHours(-5));
        Assert.Equal("+0 s", ReaderDateText.Posted(sameInstant, ReaderDateStyle.SinceOp, Opening));
        Assert.Equal("+0 s", ReaderDateText.Posted(Opening.AddSeconds(-1), ReaderDateStyle.SinceOp, Opening));
        Assert.Equal("just now", ReaderDateText.Posted(Opening.AddMinutes(1), ReaderDateStyle.RelativeToNow, now: Opening));
        Assert.Equal("59 s ago", ReaderDateText.Posted(Opening, ReaderDateStyle.RelativeToNow, now: sameInstant.AddSeconds(59)));
        Assert.Equal("1 min ago", ReaderDateText.Posted(Opening, ReaderDateStyle.RelativeToNow, now: sameInstant.AddSeconds(60)));
    }

    [Fact]
    public void EveryBundledLanguageSuppliesTheExistingDateAndSettingKeys()
    {
        foreach (LanguageInfo language in Texts.Languages)
        {
            Texts.SetLanguage(language.Code);
            foreach (string key in ReaderDateCatalog.Entries.Select(entry => entry.Key).Concat(new[]
            {
                "settings.reader.thumbnailWidth", "settings.reader.thumbnailWidth.hint", "settings.reader.pageSize",
                "settings.reader.pageSize.hint", "settings.reader.dateStyle", "settings.reader.dateStyle.hint",
                "settings.reader.dateStyle.system", "settings.reader.dateStyle.long", "settings.reader.dateStyle.sinceOp",
                "settings.reader.dateStyle.relative"
            }))
            {
                string text = Texts.T(key, 2);
                Assert.NotEqual(key, text);
                Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(text), language.Code + ": " + key);
            }
        }
    }
}
