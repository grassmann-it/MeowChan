# Desktop formatting utilities

`MeowChan.Desktop.SizeText` belongs to the desktop reference client, in [Desktop/SizeText.cs](../Desktop/SizeText.cs). It is internal and not part of the `libmeowchan` package. Library consumers receive numeric values and format them for their own interface.

`SizeText.Format(long bytes)` is the one file-size text of the desktop. It uses binary units (B, KiB, MiB, GiB, TiB; 1 KiB = 1024 B) and writes the number in the selected language's format. The unit names are not translated:

```csharp
long fileSize = 12345678;
string readableSize = SizeText.Format(fileSize);
Console.WriteLine(readableSize); // "11.8 MiB" (German: "11,8 MiB")
```

Whole bytes below 1 KiB, KiB with one decimal only below 10 KiB, one decimal from MiB. A small file is never shown as "0 MiB". The former `Utils.ToHumanReadableSize` and the separate binary formatters were removed in its favour.
