namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the gallery's mouse wheel setting (Config.GalleryWheelMode): the settings row and the "⋯" menu row.</summary>
internal static class GalleryWheelCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("settings.galleryWheel", "Gallery mouse wheel", "Mausrad in der Galerie"),
        ("settings.galleryWheel.hint", "What the mouse wheel does over the picture or video in the gallery. Zoom (default): the wheel zooms at the pointer and Ctrl+wheel moves to the next or previous medium. Browse media: the wheel moves to the next or previous medium and Ctrl+wheel zooms. The filmstrip, the video controls and the panels keep their own wheel.", "Was das Mausrad über dem Bild oder Video in der Galerie tut. Zoomen (Standard): das Rad zoomt an der Zeigerstelle, Strg+Rad wechselt zum nächsten oder vorigen Medium. Medien durchblättern: das Rad wechselt zum nächsten oder vorigen Medium, Strg+Rad zoomt. Der Filmstreifen, die Videosteuerung und die Seitenbereiche behalten ihr eigenes Rad."),
        ("settings.galleryWheel.zoom", "Zoom (Ctrl+wheel browses)", "Zoomen (Strg+Rad blättert)"),
        ("settings.galleryWheel.browse", "Browse media (Ctrl+wheel zooms)", "Medien durchblättern (Strg+Rad zoomt)"),
        ("gallery.wheelBrowse", "Mouse wheel browses media", "Mausrad blättert durch die Medien"),
        ("gallery.wheelBrowse.help", "On: the mouse wheel moves to the next or previous medium and Ctrl+wheel zooms. Off: the wheel zooms and Ctrl+wheel moves to the next or previous medium.", "An: das Mausrad wechselt zum nächsten oder vorigen Medium, Strg+Rad zoomt. Aus: das Rad zoomt, Strg+Rad wechselt zum nächsten oder vorigen Medium."),
    };
}
