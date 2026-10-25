# SHA-256 blocklist

SDK-only, metadata-only filter `meowchan.filter.hash-blocklist` /
`sha256-blocklist`. `HashBlocklistFilter.cs` is a complete editable source file.
It compares the host's actual staged `OriginalSha256`, without reading or hashing
the original a second time, and does not scan or delete archived files.

`blockedSha256` belongs to each effective filter rule. Supply 64-character
hexadecimal SHA-256 values separated by whitespace or commas. Matching ignores
case; duplicates count once. The list allows at most 256 distinct hashes and
20 KiB of UTF-8 text. Invalid entries or an exceeded limit produce a field error;
they are never silently skipped. Paths, URLs, hash prefixes and external list
subscriptions are unsupported.

A listed hash returns `NoMatch` / `sha256_blocked`; a different hash returns
`Match` / `sha256_allowed`. An empty list explicitly returns `Match` /
`empty_blocklist`. An invalid staged hash is `Error` even if the list is empty.
Result messages do not expose hashes or rejected list contents.

Initial downloads and the host's stage budgets remain necessary. Saving this
rule does not retrospectively remove existing archive entries or change other
rules. Only one plugin filter can currently be selected per effective rule.
