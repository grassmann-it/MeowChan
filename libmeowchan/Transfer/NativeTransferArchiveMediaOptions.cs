namespace MeowChan.Transfer;

// Explicit opt-in to the complete passive inventory. The legacy bool overload retains
// its metadata-only/original-only contract; inspecting an inventory never grants writes.
internal sealed record NativeTransferArchiveMediaOptions(bool ReadInventory, bool IncludeOriginals, bool IncludeDerived);
