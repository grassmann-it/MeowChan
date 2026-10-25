# File rules

SDK-only, metadata-only filter `meowchan.filter.file-rules` / `file-rules`.
The editable `FileRulesFilter.cs` is self-contained and references only the SDK
and framework. It does not open an image, make network requests or scan the archive.

Each effective filter rule stores its own optional inclusive byte minimum and
maximum, literal filename comparison and case choice. The size is the host's
measured staged `OriginalSize`; the name is only the source's `OriginalFileName`.
The filter never guesses a name from a URL or local path. Case-insensitive matching
uses ordinal comparison, independent of the UI language. Filename text is literal,
not a wildcard or regular expression, and is limited to 256 characters.

All enabled conditions must match. A missing name when needed is `Unavailable`
(`original_name_unavailable`), not a rejected file. Invalid settings and invalid
staged sizes produce `Error`. With no conditions the result is explicitly
`Match` / `no_file_conditions`.

The transfer has already happened before this filter runs. Existing host media
rules, byte limits, grant/revision checks and commit rules remain in force; the
filter changes no archived data. Only one plugin filter can currently be selected
per effective rule.
