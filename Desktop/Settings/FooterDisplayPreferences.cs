namespace MeowChan.Desktop;

internal readonly record struct FooterDisplayPreferences(bool Network, bool Disk, bool Status)
{
    internal static FooterDisplayPreferences FromConfig(Config config) =>
        new(config.ShowFooterNetwork, config.ShowFooterDisk, config.ShowFooterStatus);

    // Settings drafts and the quick menu edit the same preferences. Only changed
    // draft fields should replace a newer choice from the menu.
    internal void ApplyChangesTo(Config config, FooterDisplayPreferences? baseline)
    {
        if (Network != baseline?.Network) config.ShowFooterNetwork = Network;
        if (Disk != baseline?.Disk) config.ShowFooterDisk = Disk;
        if (Status != baseline?.Status) config.ShowFooterStatus = Status;
    }
}
