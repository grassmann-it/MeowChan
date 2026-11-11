# Source-stability run harness

Runs the live protocol of [source-stability.md](source-stability.md) (section 3) and writes
the evidence file of section 4. It never sends anything unless `--live` is given.

## What a run does

For each chan named in the manifest, per board, in this order and nothing else:

1. GET the catalog (the URL is computed by the plugin's own code with no network).
2. Pick the thread in the run: the user's thread URL, or the first catalog entry that has
   at least one attachment (a catalog without attachment counts, such as Kohlchan, uses its
   first entry; this is recorded in `details.selection`).
3. GET the thread (through the production watch, so the archive is written by production code).
4. GET the first image attachment of at most 2 MiB (the original).

A chan that lists boards by `boardsFromList` costs one more GET, the board list at `boardListUrl`.
Caps: 8 MiB for metadata, 2 MiB for the original. Retries, HEAD, Range, thumbnails, a second
media file and every redirect that is not in `plannedRedirects` are refused before the socket and
recorded. The first unexpected response (challenge page, login, 4xx/5xx, wrong content type, empty
body, size cap) stops that chan and is recorded in `failuresObserved`.

Integrity is strict and no exception is ever granted: expected vs received bytes, every provider
hash and the SHA-256 of the received bytes are recorded; a mismatch is an integrity failure and the
outcome is `fail`. A source that declares neither a size nor a hash cannot pass.

Then the archive entry, the verified media file and the reader/gallery projection are checked in
the same host. The driver then starts a second OS process (network namespace unshared with
`bwrap`) that opens the same throwaway profile with a transport that rejects every request and
checks thread and media again. The profile and all downloaded bytes are deleted at the end.

The plugin bundle is the delivered one from the test output (`plugins/*.plugin.json`, `assemblies/`);
the package file name and SHA-256, the assembly SHA-256, the execution revision and the description
fingerprint (`SourceWording.DescriptionFingerprint`) are in the evidence. `expectedAssemblySha256`
in the manifest ends the chan before any request if the delivered assembly differs.

## Manifest

```json
{
  "schemaVersion": 1,
  "sources": [
    {
      "pluginId": "meowchan.source.lainchan",
      "sourceId": "lainchan",
      "approval": "2026-11-11, chat",
      "boards": [{"board": "sec"}, {"board": "lit"}, {"board": "music", "thread": "https://lainchan.org/music/res/1.html"}]
    },
    {
      "pluginId": "meowchan.source.capybarachan",
      "sourceId": "capybarachan",
      "approval": "2026-11-11, chat",
      "boardsFromList": 3,
      "boardListUrl": "https://capybarachan.org/boards.js?json=1"
    }
  ]
}
```

Optional per chan: `plannedRedirects` (`[{"from": "2ch.hk", "to": "2ch.su"}]`, exact hosts, at most
four) and `expectedAssemblySha256`. Unknown fields, duplicates and out-of-range values fail closed.
A chan that is not in the manifest gets no host and no request; `--only a,b` can only narrow it.
The `boardListUrl` of CapybaraChan above is a guess: the plugin has no board-list endpoint, and the
URL must be checked before approval.

## Commands

Dry run (verifies the payload, prints the exact request plan, sends nothing):

    scripts/run-stability-live.py --manifest artifacts/stability-run.json

Live run (after the user has approved each chan and date in chat):

    scripts/run-stability-live.py --manifest artifacts/stability-run.json --live

Build first with `nice -n 10 dotnet build tests/MeowChan.DesktopTests -c Debug -v q`. The driver
refuses an uncommitted contract file. Exit codes: 0 all chans pass, 1 a chan failed (its evidence
file is written), 2 usage, 3 infrastructure. `--selftest` runs both phases against an in-memory stub
site in two real OS processes and needs no network and no manifest.

Evidence files go to `plugins/Examples/<Plugin>/Evidence/stability-<YYYYMMDD>.json` (an existing
file is never overwritten). The raw HTTP audits and the phase logs stay in
`artifacts/stability/<run>/evidence/` outside Git; the evidence file names each with its SHA-256.

## Test entry

`StabilityRunLiveTests.RunTheRequestedPhase` is skipped unless `MEOWCHAN_STABILITY_RUN_MANIFEST`
is set. The driver sets `..._MODE` (plan, online, offline), `..._EVIDENCE`, `..._WORK`, `..._ONLY`
and, for the online phase of `--live` only, `..._NETWORK=live` plus `..._LIVE=1`. The older
`LiveSourceAcceptanceTests` are unchanged.
