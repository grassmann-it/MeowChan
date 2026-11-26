using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>How a quip is meant: a joke or a plain statement. Kept as data so a provider (or a later setting) can filter by it.</summary>
internal enum TaglineTone { Playful, Plain }

internal enum TaglineCategory { General, Release, Seasonal }

/// <summary>An inclusive, annually recurring date window. The end may fall in the next calendar year.</summary>
internal readonly record struct TaglineSeason((int Month, int Day) Start, (int Month, int Day) End)
{
    internal bool Includes(DateOnly date)
    {
        var day = (date.Month, date.Day);
        bool onOrAfterStart = day.CompareTo(Start) >= 0;
        bool onOrBeforeEnd = day.CompareTo(End) <= 0;
        return Start.CompareTo(End) <= 0 ? onOrAfterStart && onOrBeforeEnd : onOrAfterStart || onOrBeforeEnd;
    }
}

/// <summary>
/// One quip, ready to show: its stable <paramref name="Id"/> (what the "Fixed" setting stores), the version of MeowChan that introduced it
/// (<paramref name="Since"/>, what the release highlight compares with) and the <paramref name="Text"/> in the language it was asked for.
/// </summary>
internal sealed record TaglineEntry(string Id, Version Since, TaglineTone Tone, string Text,
    TaglineCategory Category = TaglineCategory.General, TaglineSeason? Season = null);

/// <summary>
/// A source of quips. The built-in one reads the language packs; a theme (or any later feature) can add its own through
/// <see cref="TaglineCatalog.Register"/> without touching the picker, the setting or the header. A provider returns only the entries it can
/// show in the active UI language (<see cref="Texts.Language"/>), including any shared English text chosen for that language.
/// </summary>
internal interface ITaglineProvider
{
    /// <summary>Names the source in diagnostics; entry ids must be unique across providers (the first provider registered wins a clash).</summary>
    string ProviderId { get; }

    /// <summary>The entries available in the active UI language. Called on the UI thread, at most when the pool is rebuilt.</summary>
    IReadOnlyList<TaglineEntry> Entries();
}

/// <summary>The quips that ship with MeowChan: ids, introducing version and tone here; the text of <c>x</c> is <c>shell.tagline.quip.x</c> in every pack.</summary>
internal sealed class BuiltInTaglineProvider : ITaglineProvider
{
    private sealed record Definition(string Id, Version Since, TaglineTone Tone,
        TaglineCategory Category = TaglineCategory.General, TaglineSeason? Season = null);

    private static readonly Version TaglinePackVersion = new(1, 1, 2);
    private static readonly Version TaglinePackVersion113 = new(1, 1, 3);

    // Ids are persisted in Config.HeaderTaglineFixedId. Release eligibility is explicit and independent of Since and Tone.
    private static readonly Definition[] Definitions =
    [
        new("cats", new Version(1, 0, 0), TaglineTone.Playful),
        new("plain", new Version(1, 0, 0), TaglineTone.Plain),
        new("offline", new Version(1, 0, 0), TaglineTone.Plain),
        new("notfound", new Version(1, 0, 0), TaglineTone.Playful),
        new("hoard", new Version(1, 0, 0), TaglineTone.Playful),
        new("reader", new Version(1, 0, 0), TaglineTone.Plain),
        new("units", new Version(1, 0, 0), TaglineTone.Playful),
        new("booru", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("flashbang", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("workviews", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("tags", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("vibetested", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("pink", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        new("workflow", new Version(1, 1, 0), TaglineTone.Playful, TaglineCategory.Release),
        // Legacy self-deprecating quips retain their original Since values.
        new("jdownloader", new Version(1, 0, 0), TaglineTone.Playful),
        new("worksonmine", new Version(1, 0, 0), TaglineTone.Playful),
        new("noai", new Version(1, 0, 0), TaglineTone.Playful),
        new("whatbooru", new Version(1, 0, 0), TaglineTone.Playful),
        new("features", new Version(1, 0, 0), TaglineTone.Playful),
        // Legacy 1.1.1 additions: self-deprecating and UX lines, goals, and release quips.
        new("settingsforsettings", new Version(1, 0, 0), TaglineTone.Playful),
        new("tooltipflicker", new Version(1, 0, 0), TaglineTone.Playful),
        new("bugfeature", new Version(1, 0, 0), TaglineTone.Playful),
        new("greenhere", new Version(1, 0, 0), TaglineTone.Playful),
        new("pixelsbyhand", new Version(1, 0, 0), TaglineTone.Playful),
        new("scopecreep", new Version(1, 0, 0), TaglineTone.Playful),
        new("bugshide", new Version(1, 0, 0), TaglineTone.Playful),
        new("tidytoolbars", new Version(1, 0, 0), TaglineTone.Plain),
        new("keyboardfirst", new Version(1, 0, 0), TaglineTone.Plain),
        new("galleryreader", new Version(1, 0, 0), TaglineTone.Plain),
        new("evenspacing", new Version(1, 0, 0), TaglineTone.Playful),
        new("lightdark", new Version(1, 0, 0), TaglineTone.Plain),
        new("fewerclicks", new Version(1, 0, 0), TaglineTone.Plain),
        new("boorutabs", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("downloadall", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("threadswitch", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("taglinesfeature", new Version(1, 1, 1), TaglineTone.Playful, TaglineCategory.Release),
        new("yourarchive", new Version(1, 0, 0), TaglineTone.Plain),
        new("sortnothoard", new Version(1, 0, 0), TaglineTone.Plain),
        new("tabsbreed", new Version(1, 1, 2), TaglineTone.Playful),
        new("pawprint", new Version(1, 1, 2), TaglineTone.Playful),
        new("snowed", new Version(1, 1, 2), TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 1), (1, 6))),
        new("trickorthread", new Version(1, 1, 2), TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 24), (11, 1))),
        new("traypurr", TaglinePackVersion, TaglineTone.Playful),
        new("hiddenqueue", TaglinePackVersion, TaglineTone.Playful),
        new("traycurl", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarperfect", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarrewrite", TaglinePackVersion, TaglineTone.Playful),
        new("toolbarversion", TaglinePackVersion, TaglineTone.Playful),
        new("archiveemall", TaglinePackVersion, TaglineTone.Playful),
        new("zerolimit", TaglinePackVersion, TaglineTone.Playful),
        new("threadcowboy", TaglinePackVersion, TaglineTone.Playful),
        new("sitboy", TaglinePackVersion, TaglineTone.Playful),
        new("chevronseven", TaglinePackVersion, TaglineTone.Playful),
        new("over9000", TaglinePackVersion, TaglineTone.Playful),
        new("ninjaway", TaglinePackVersion, TaglineTone.Playful),
        new("nameofthemoon", TaglinePackVersion, TaglineTone.Playful),
        new("getinshinji", TaglinePackVersion, TaglineTone.Playful),
        new("thethreads", TaglinePackVersion, TaglineTone.Playful),
        new("threadripper", TaglinePackVersion, TaglineTone.Playful),
        new("riptag", TaglinePackVersion, TaglineTone.Playful),
        new("ripthread", TaglinePackVersion, TaglineTone.Playful),
        new("hangingthread", TaglinePackVersion, TaglineTone.Playful),
        new("lostthread", TaglinePackVersion, TaglineTone.Playful),
        new("threadbare", TaglinePackVersion, TaglineTone.Playful),
        new("curiosity", TaglinePackVersion, TaglineTone.Playful),
        new("ninelives", TaglinePackVersion, TaglineTone.Playful),
        new("knocksoff", TaglinePackVersion, TaglineTone.Playful),
        new("ifitfits", TaglinePackVersion, TaglineTone.Playful),
        new("threadevolved", TaglinePackVersion, TaglineTone.Playful),
        new("lasttab", TaglinePackVersion, TaglineTone.Playful),
        new("truthinhere", TaglinePackVersion, TaglineTone.Playful),
        new("jacktheripper", TaglinePackVersion, TaglineTone.Playful),
        new("pullthread", TaglinePackVersion, TaglineTone.Playful),
        new("threadlightly", TaglinePackVersion, TaglineTone.Playful),
        new("boardlostthread", TaglinePackVersion, TaglineTone.Playful),
        new("treatbag", TaglinePackVersion, TaglineTone.Playful),
        new("closeddoors", TaglinePackVersion, TaglineTone.Playful),
        new("heartsandstars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("heartedstarred", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("roughhearts", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("fivestars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("favefinal", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("firstclick", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("polishrelease", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("macugly", TaglinePackVersion, TaglineTone.Playful),
        new("gatekeeper", TaglinePackVersion, TaglineTone.Playful),
        new("cmdorctrl", TaglinePackVersion, TaglineTone.Playful),
        new("macthinking", TaglinePackVersion, TaglineTone.Playful),
        new("notnative", TaglinePackVersion, TaglineTone.Playful),
        new("macmenu", TaglinePackVersion, TaglineTone.Playful),
        new("blackcat", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 24), (10, 31))),
        new("mirrorghost", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((10, 29), (10, 31))),
        new("giftbox", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 18), (12, 26))),
        new("treefell", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 18), (12, 26))),
        new("ribbon", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 24), (12, 26))),
        new("newyearbacklog", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 30), (1, 2))),
        new("underbed", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 31), (1, 1))),
        new("mediaforall", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("eighthours", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("unpaidmods", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("adalovelace", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 8), (3, 8))),
        new("breadroses", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 8), (3, 8))),
        new("bikeride", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("breathingthumbs", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("baselwheels", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 19), (4, 19))),
        new("blazingfast", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("catsnacks", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("catnipresearch", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((4, 20), (4, 20))),
        new("cloudindependence", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("fireworksgif", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("vacuumindependence", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((7, 4), (7, 4))),
        new("unodostres", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 5), (5, 5))),
        new("pueblacover", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 5), (5, 5))),
        new("redheart", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 14), (2, 14))),
        new("inmyfavorites", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 10), (2, 14))),
        new("roseswilt", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((2, 14), (2, 14))),
        new("catday", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("catdaysearch", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("goodfolder", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 8), (8, 8))),
        new("archivebewithyou", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("notthethreads", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("smallmoon", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 4), (5, 4))),
        new("pithreads", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 14), (3, 14))),
        new("pistars", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 14), (3, 14))),
        new("dontpanic", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 25), (5, 25))),
        new("forgottowel", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 25), (5, 25))),
        new("plunderboards", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("pirateparrot", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("treasuremisc", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 19), (9, 19))),
        new("nocloud", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((9, 15), (9, 21))),
        new("linuxbirthday", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((8, 25), (8, 25))),
        new("worldbackup", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((3, 31), (3, 31))),
        new("lastyeardownload", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((12, 31), (1, 1))),
        new("queueovertime", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("catsvote", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Seasonal, new TaglineSeason((5, 1), (5, 1))),
        new("archivetreasure", TaglinePackVersion, TaglineTone.Playful),
        new("onepiece", TaglinePackVersion, TaglineTone.Playful),
        new("kingarchivists", TaglinePackVersion, TaglineTone.Playful),
        new("milaspike", TaglinePackVersion, TaglineTone.Playful),
        new("moonprism", TaglinePackVersion, TaglineTone.Playful),
        new("threeepisodes", TaglinePackVersion, TaglineTone.Playful),
        new("almwifi", TaglinePackVersion, TaglineTone.Playful),
        new("seventhreads", TaglinePackVersion, TaglineTone.Playful),
        new("linaqueue", TaglinePackVersion, TaglineTone.Playful),
        new("ranmacat", TaglinePackVersion, TaglineTone.Playful),
        new("pegasusmeteor", TaglinePackVersion, TaglineTone.Playful),
        new("burncosmo", TaglinePackVersion, TaglineTone.Playful),
        new("cannotclosetab", TaglinePackVersion, TaglineTone.Playful),
        new("runawaybacklog", TaglinePackVersion, TaglineTone.Playful),
        new("patternblue", TaglinePackVersion, TaglineTone.Playful),
        new("archivecomplete", TaglinePackVersion, TaglineTone.Playful),
        new("atfield", TaglinePackVersion, TaglineTone.Playful),
        new("lagoondelivers", TaglinePackVersion, TaglineTone.Playful),
        new("roanapurrules", TaglinePackVersion, TaglineTone.Playful),
        new("revyslots", TaglinePackVersion, TaglineTone.Playful),
        new("brisbanearchive", TaglinePackVersion, TaglineTone.Playful),
        new("policegirl", TaglinePackVersion, TaglineTone.Playful),
        new("inspaaace", TaglinePackVersion, TaglineTone.Playful),
        new("archivesit", TaglinePackVersion, TaglineTone.Playful),
        new("threadsoutthere", TaglinePackVersion, TaglineTone.Playful),
        new("trustnoone", TaglinePackVersion, TaglineTone.Playful),
        new("believebackup", TaglinePackVersion, TaglineTone.Playful),
        new("familybusiness", TaglinePackVersion, TaglineTone.Playful),
        new("waywardthread", TaglinePackVersion, TaglineTone.Playful),
        new("threadslayer", TaglinePackVersion, TaglineTone.Playful),
        new("hackthethreads", TaglinePackVersion, TaglineTone.Playful),
        new("vastqueue", TaglinePackVersion, TaglineTone.Playful),
        new("ghostarchive", TaglinePackVersion, TaglineTone.Playful),
        new("sixtyseconds", TaglinePackVersion, TaglineTone.Playful),
        new("catkeptcopy", TaglinePackVersion, TaglineTone.Playful),
        new("allfourpaws", TaglinePackVersion, TaglineTone.Playful),
        new("finaldownload", TaglinePackVersion, TaglineTone.Playful),
        new("kakarot", TaglinePackVersion, TaglineTone.Playful),
        new("shadowclone", TaglinePackVersion, TaglineTone.Playful),
        new("narutoqueue", TaglinePackVersion, TaglineTone.Playful),
        new("treasuretab", TaglinePackVersion, TaglineTone.Playful),
        new("matchpoint", TaglinePackVersion, TaglineTone.Playful),
        new("lunawand", TaglinePackVersion, TaglineTone.Playful),
        new("transformribbons", TaglinePackVersion, TaglineTone.Playful),
        new("crescenttag", TaglinePackVersion, TaglineTone.Playful),
        new("tsubasabar", TaglinePackVersion, TaglineTone.Playful),
        new("driveshot", TaglinePackVersion, TaglineTone.Playful),
        new("bulmaradar", TaglinePackVersion, TaglineTone.Playful),
        new("fourstarball", TaglinePackVersion, TaglineTone.Playful),
        new("dragonballstorage", TaglinePackVersion, TaglineTone.Playful),
        new("radarfilter", TaglinePackVersion, TaglineTone.Playful),
        new("coldprofile", TaglinePackVersion, TaglineTone.Playful),
        new("ryogafolder", TaglinePackVersion, TaglineTone.Playful),
        new("seiyatabs", TaglinePackVersion, TaglineTone.Playful),
        new("magivote", TaglinePackVersion, TaglineTone.Playful),
        new("unplugged", TaglinePackVersion, TaglineTone.Playful),
        new("elevatordownload", TaglinePackVersion, TaglineTone.Playful),
        new("penpenfolder", TaglinePackVersion, TaglineTone.Playful),
        new("rocktie", TaglinePackVersion, TaglineTone.Playful),
        new("revyminimum", TaglinePackVersion, TaglineTone.Playful),
        new("dutchengine", TaglinePackVersion, TaglineTone.Playful),
        new("alucardtrash", TaglinePackVersion, TaglineTone.Playful),
        new("alucardpreview", TaglinePackVersion, TaglineTone.Playful),
        new("diskspaaace", TaglinePackVersion, TaglineTone.Playful),
        new("jeicefolder", TaglinePackVersion, TaglineTone.Playful),
        new("muldermetadata", TaglinePackVersion, TaglineTone.Playful),
        new("mulderposter", TaglinePackVersion, TaglineTone.Playful),
        new("saltcircle", TaglinePackVersion, TaglineTone.Playful),
        new("impalatrunk", TaglinePackVersion, TaglineTone.Playful),
        new("sunnydalefines", TaglinePackVersion, TaglineTone.Playful),
        new("hellmouth", TaglinePackVersion, TaglineTone.Playful),
        new("gibsonfile", TaglinePackVersion, TaglineTone.Playful),
        new("puppetprofile", TaglinePackVersion, TaglineTone.Playful),
        new("sixmonitors", TaglinePackVersion, TaglineTone.Playful),
        new("stanleyzip", TaglinePackVersion, TaglineTone.Playful),
        new("sofathread", TaglinePackVersion, TaglineTone.Playful),
        new("keyboardcat", TaglinePackVersion, TaglineTone.Playful),
        new("polishontop", TaglinePackVersion, TaglineTone.Playful, TaglineCategory.Release),
        new("justsaiyan", TaglinePackVersion, TaglineTone.Playful),
        new("cloudpain", TaglinePackVersion, TaglineTone.Playful),
        new("meddlingkids", TaglinePackVersion, TaglineTone.Playful),
        new("unmaskerror", TaglinePackVersion, TaglineTone.Playful),
        new("scoobysnacks", TaglinePackVersion, TaglineTone.Playful),
        new("eternitydownload", TaglinePackVersion, TaglineTone.Playful),
        new("mayoformat", TaglinePackVersion, TaglineTone.Playful),
        new("thisispatrick", TaglinePackVersion, TaglineTone.Playful),
        new("hoardingmock", TaglinePackVersion, TaglineTone.Playful),
        new("imaginationarchive", TaglinePackVersion, TaglineTone.Playful),
        new("mayoinstrument", TaglinePackVersion, TaglineTone.Playful),
        new("bookmarkbackup", TaglinePackVersion, TaglineTone.Playful),
        new("fastershadow", TaglinePackVersion, TaglineTone.Playful),
        new("daltonsize", TaglinePackVersion, TaglineTone.Playful),
        new("sunsetqueue", TaglinePackVersion, TaglineTone.Playful),
        new("joegallery", TaglinePackVersion, TaglineTone.Playful),
        new("everynight", TaglinePackVersion, TaglineTone.Playful),
        new("pondertags", TaglinePackVersion, TaglineTone.Playful),
        new("pinkyfinal", TaglinePackVersion, TaglineTone.Playful),
        new("queueempty", TaglinePackVersion, TaglineTone.Playful),
        new("whatsupthread", TaglinePackVersion, TaglineTone.Playful),
        new("acmeanvils", TaglinePackVersion, TaglineTone.Playful),
        new("wildthread", TaglinePackVersion, TaglineTone.Playful),
        new("supereffective", TaglinePackVersion, TaglineTone.Playful),
        new("flapsatnight", TaglinePackVersion, TaglineTone.Playful),
        new("dangerousdownload", TaglinePackVersion, TaglineTone.Playful),
        new("accidentalclosetab", TaglinePackVersion, TaglineTone.Playful),
        new("digivolves", TaglinePackVersion, TaglineTone.Playful),
        new("trapcard", TaglinePackVersion, TaglineTone.Playful),
        new("grandpadeck", TaglinePackVersion, TaglineTone.Playful),
        new("tamagotchi", TaglinePackVersion, TaglineTone.Playful),
        new("snakegrowth", TaglinePackVersion, TaglineTone.Playful),
        new("queuetail", TaglinePackVersion, TaglineTone.Playful),
        new("scroogemedia", TaglinePackVersion, TaglineTone.Playful),
        new("moneybinjpeg", TaglinePackVersion, TaglineTone.Playful),
        new("catcaughtthread", TaglinePackVersion, TaglineTone.Playful),
        new("onetruth", TaglinePackVersion, TaglineTone.Playful),
        new("kogorotyping", TaglinePackVersion, TaglineTone.Playful),
        new("smurfarchive", TaglinePackVersion, TaglineTone.Playful),
        new("powerscombined", TaglinePackVersion, TaglineTone.Playful),
        new("looksarchiving", TaglinePackVersion, TaglineTone.Playful),
        new("quietmodem", TaglinePackVersion, TaglineTone.Playful),
        new("greenhill", TaglinePackVersion, TaglineTone.Playful),
        new("jpegeyebrows", TaglinePackVersion, TaglineTone.Playful),
        new("duckseason", TaglinePackVersion, TaglineTone.Playful),
        new("acmeerror", TaglinePackVersion, TaglineTone.Playful),
        new("luckydime", TaglinePackVersion, TaglineTone.Playful),
        new("scouterqueue", TaglinePackVersion113, TaglineTone.Playful),
        new("nappaboard", TaglinePackVersion113, TaglineTone.Playful),
        new("popofaster", TaglinePackVersion113, TaglineTone.Playful),
        new("popowatches", TaglinePackVersion113, TaglineTone.Playful),
        new("somethingthumbnail", TaglinePackVersion113, TaglineTone.Playful),
        new("raditzarchived", TaglinePackVersion113, TaglineTone.Playful),
        new("celltags", TaglinePackVersion113, TaglineTone.Playful),
        new("ginyupose", TaglinePackVersion113, TaglineTone.Playful),
        new("zarbonfetch", TaglinePackVersion113, TaglineTone.Playful),
        new("queueninethousand", TaglinePackVersion113, TaglineTone.Playful),
        new("brokenscouter", TaglinePackVersion113, TaglineTone.Playful),
        new("lendbandwidth", TaglinePackVersion113, TaglineTone.Playful),
        new("spiritbyte", TaglinePackVersion113, TaglineTone.Playful),
        new("kamehamesave", TaglinePackVersion113, TaglineTone.Playful),
        new("chargingdownload", TaglinePackVersion113, TaglineTone.Playful),
        new("namekzip", TaglinePackVersion113, TaglineTone.Playful),
        new("namekepisodes", TaglinePackVersion113, TaglineTone.Playful),
        new("senzubean", TaglinePackVersion113, TaglineTone.Playful),
        new("nextdownload", TaglinePackVersion113, TaglineTone.Playful),
        new("teafirst", TaglinePackVersion113, TaglineTone.Playful),
        new("searchdestroyarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("waltertea", TaglinePackVersion113, TaglineTone.Playful),
        new("amenthread", TaglinePackVersion113, TaglineTone.Playful),
        new("boneofmyarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("policegirltea", TaglinePackVersion113, TaglineTone.Playful),
        new("lightningspike", TaglinePackVersion113, TaglineTone.Playful),
        new("spikespeed", TaglinePackVersion113, TaglineTone.Playful),
        new("endlesspitch", TaglinePackVersion113, TaglineTone.Playful),
        new("georgiethread", TaglinePackVersion113, TaglineTone.Playful),
        new("talkingcat", TaglinePackVersion113, TaglineTone.Playful),
        new("lunaartemis", TaglinePackVersion113, TaglineTone.Playful),
        new("crescentarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("moontiara", TaglinePackVersion113, TaglineTone.Playful),
        new("nothreadleft", TaglinePackVersion113, TaglineTone.Playful),
        new("lunanotes", TaglinePackVersion113, TaglineTone.Playful),
        new("venusgoddess", TaglinePackVersion113, TaglineTone.Playful),
        new("croppingluna", TaglinePackVersion113, TaglineTone.Playful),
        new("nekoken", TaglinePackVersion113, TaglineTone.Playful),
        new("genmasign", TaglinePackVersion113, TaglineTone.Playful),
        new("hotwaterarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("akanecooks", TaglinePackVersion113, TaglineTone.Playful),
        new("happosaicopy", TaglinePackVersion113, TaglineTone.Playful),
        new("grayskulldownloads", TaglinePackVersion113, TaglineTone.Playful),
        new("meetinarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("skeletorbackup", TaglinePackVersion113, TaglineTone.Playful),
        new("sightbeyondsight", TaglinePackVersion113, TaglineTone.Playful),
        new("cowabungasaved", TaglinePackVersion113, TaglineTone.Playful),
        new("downloadersrollout", TaglinePackVersion113, TaglineTone.Playful),
        new("meetstheeye", TaglinePackVersion113, TaglineTone.Playful),
        new("rocknegotiates", TaglinePackVersion113, TaglineTone.Playful),
        new("kokodeal", TaglinePackVersion113, TaglineTone.Playful),
        new("lovepeacebackups", TaglinePackVersion113, TaglineTone.Playful),
        new("doubledollars", TaglinePackVersion113, TaglineTone.Playful),
        new("bangarchived", TaglinePackVersion113, TaglineTone.Playful),
        new("einfolder", TaglinePackVersion113, TaglineTone.Playful),
        new("endlessarchive", TaglinePackVersion113, TaglineTone.Playful),
        new("justdownloads", TaglinePackVersion113, TaglineTone.Playful),
        new("shotguntags", TaglinePackVersion113, TaglineTone.Playful),
        new("idjitbackup", TaglinePackVersion113, TaglineTone.Playful),
        new("crowleydeal", TaglinePackVersion113, TaglineTone.Playful),
        new("saltnburn", TaglinePackVersion113, TaglineTone.Playful),
        new("babyparking", TaglinePackVersion113, TaglineTone.Playful),
        new("babycassettes", TaglinePackVersion113, TaglineTone.Playful),
        new("impalacrop", TaglinePackVersion113, TaglineTone.Playful),
        new("babyvehicles", TaglinePackVersion113, TaglineTone.Playful),
        new("passthesalt", TaglinePackVersion113, TaglineTone.Playful),
        new("gasandlinks", TaglinePackVersion113, TaglineTone.Playful),
        new("dadsinitials", TaglinePackVersion113, TaglineTone.Playful),
        new("highressoul", TaglinePackVersion113, TaglineTone.Playful),
        new("crowleyterms", TaglinePackVersion113, TaglineTone.Playful),
        new("idjitsave", TaglinePackVersion113, TaglineTone.Playful),
        new("bobbyfolder", TaglinePackVersion113, TaglineTone.Playful),
        new("bobbycopies", TaglinePackVersion113, TaglineTone.Playful),
        new("polishzrozumiano", TaglinePackVersion113, TaglineTone.Playful, TaglineCategory.Release),
        new("lookatthispolish", TaglinePackVersion113, TaglineTone.Playful, TaglineCategory.Release),
        new("bobrapproves", TaglinePackVersion113, TaglineTone.Playful, TaglineCategory.Release),
        new("formerskyline", TaglinePackVersion113, TaglineTone.Playful),
        new("scouterloadmore", TaglinePackVersion113, TaglineTone.Playful),
        new("scouterreposts", TaglinePackVersion113, TaglineTone.Playful),
        new("nappabumps", TaglinePackVersion113, TaglineTone.Playful),
        new("krillingallery", TaglinePackVersion113, TaglineTone.Playful),
        new("peckingorder", TaglinePackVersion113, TaglineTone.Playful),
        new("poposcat", TaglinePackVersion113, TaglineTone.Playful),
        new("poporoot", TaglinePackVersion113, TaglineTone.Playful),
        new("technicallythumbnail", TaglinePackVersion113, TaglineTone.Playful),
        new("wholethreadjpeg", TaglinePackVersion113, TaglineTone.Playful),
        new("sixstars", TaglinePackVersion113, TaglineTone.Playful),
        new("tworaditz", TaglinePackVersion113, TaglineTone.Playful),
        new("gokubrother", TaglinePackVersion113, TaglineTone.Playful),
        new("losslesscell", TaglinePackVersion113, TaglineTone.Playful),
        new("uncompressedform", TaglinePackVersion113, TaglineTone.Playful),
        new("ginyuformation", TaglinePackVersion113, TaglineTone.Playful),
        new("zarbonbefore", TaglinePackVersion113, TaglineTone.Playful),
        new("scouterbeauty", TaglinePackVersion113, TaglineTone.Playful),
    ];

    private readonly Func<string, string?> _lookup;

    /// <param name="lookup">Text of a key in the active language only, null when that language lacks it. Default: <see cref="Texts.Own"/>.</param>
    internal BuiltInTaglineProvider(Func<string, string?>? lookup = null) => _lookup = lookup ?? Texts.Own;

    public string ProviderId => "built-in";

    internal static IReadOnlyList<string> Ids => Definitions.Select(definition => definition.Id).ToArray();

    internal static string KeyFor(string id) => "shell.tagline.quip." + id;

    public IReadOnlyList<TaglineEntry> Entries()
    {
        List<TaglineEntry> result = new(Definitions.Length);
        foreach (Definition definition in Definitions)
        {
            string? text = TaglineCatalog.CleanText(_lookup(KeyFor(definition.Id)));
            if (text != null) result.Add(new TaglineEntry(definition.Id, definition.Since, definition.Tone, text, definition.Category, definition.Season));
        }
        return result;
    }
}

/// <summary>The pool of quips (built-in plus registered providers) and the small normalizers the settings use.</summary>
internal static class TaglineCatalog
{
    /// <summary>The classic quip: the default of the "Fixed" mode and the fallback when a stored id no longer exists.</summary>
    internal const string DefaultId = "cats";

    /// <summary>Fixed ceilings, documented and not settings: they only keep a hostile provider or settings file from costing memory. Longer ids read as unknown; longer texts are dropped.</summary>
    internal const int MaximumIdLength = 64, MaximumTextLength = 120;

    private static readonly object Gate = new();
    private static readonly List<ITaglineProvider> Extra = [];

    /// <summary>Adds a source of quips (a theme's fortunes). Returns what removes it again; registering the same instance twice is a no-op.</summary>
    internal static IDisposable Register(ITaglineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
            if (!Extra.Contains(provider)) Extra.Add(provider);
        return new Registration(provider);
    }

    private sealed class Registration(ITaglineProvider provider) : IDisposable
    {
        public void Dispose() { lock (Gate) Extra.Remove(provider); }
    }

    /// <summary>
    /// Every quip available in the active language, sorted by id (a stable order, which the daily rule depends on). The built-in provider comes
    /// first and wins an id clash; a provider that throws or returns garbage is skipped, never fatal for the header.
    /// </summary>
    internal static IReadOnlyList<TaglineEntry> Available(IEnumerable<ITaglineProvider>? providers = null)
    {
        List<ITaglineProvider> sources;
        if (providers != null) sources = providers.ToList();
        else
        {
            sources = [new BuiltInTaglineProvider()];
            lock (Gate) sources.AddRange(Extra);
        }
        Dictionary<string, TaglineEntry> byId = new(StringComparer.Ordinal);
        foreach (ITaglineProvider source in sources)
        {
            IReadOnlyList<TaglineEntry> entries;
            try { entries = source.Entries(); }
            catch (Exception) { continue; }
            foreach (TaglineEntry entry in entries ?? [])
            {
                if (entry == null || !IsValidId(entry.Id) || entry.Since == null) continue;
                string? text = CleanText(entry.Text);
                if (text != null) byId.TryAdd(entry.Id, entry with { Text = text });
            }
        }
        return byId.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Trimmed text, or null when empty, too long or containing a control character (a quip is one plain line).</summary>
    internal static string? CleanText(string? text)
    {
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaximumTextLength || trimmed.Any(char.IsControl)) return null;
        return trimmed;
    }

    internal static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaximumIdLength && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or '/');

    /// <summary>A stored id as the setting keeps it: unchanged when well formed, else the classic one.</summary>
    internal static string NormalizeId(string? value)
    {
        string? trimmed = value?.Trim();
        return IsValidId(trimmed) ? trimmed! : DefaultId;
    }

    /// <summary>"1.1.0", "1.1.0+abc", "1.1.0-beta.2" and "1.1" read as 1.1.0; anything else is no version.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string core = text.Trim().TrimStart('v', 'V');
        int cut = core.IndexOfAny(['+', '-']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out Version? parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
    }

    /// <summary>The stored form of a version: major.minor.build, or empty.</summary>
    internal static string NormalizeVersion(string? value) => ParseVersion(value)?.ToString(3) ?? string.Empty;
}
