namespace MeowChan.Desktop.Localization;

// P4B conversation-view status text. The command label and depth label belong to the P0 reader-view catalog;
// keeping this failure sentence separate lets the conversation package register its own language slice.
internal static class ReaderConversationCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.conversation.failed", "The conversation view could not be built: {0}", "Die Gesprächsansicht konnte nicht aufgebaut werden: {0}"),
    };
}
