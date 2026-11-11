# Source stability — when a bundled chan leaves "experimental"

Status 11.11.2026: **criteria contract for 1.1.0 (QUELLEN110 · Q1).** The user approved
writing the criteria down. Reclassifying any concrete chan still needs its own live
acceptance (Q2, per-chan approval) and the old-profile decision (Q3). Plan and per-chan
evidence status: [QUELLEN110](../architecture/gesamtprogramm-2026-10-31/pakete/QUELLEN110-stabile-quellen.md).

Scope: sources that ship as bundled MeowChan plugins. "Stable" says MeowChan's adapter
has been checked against the live site as described here. It is no promise about the
provider's availability, content or completeness, and it grants no execution authority.
Every source keeps running in the plugin worker; nothing moves into the app process.

## 1. What "experimental" is in the code today

Three independent switches. A reclassification must change all of them on purpose and
must not loosen anything else by accident.

| Switch | Where | Effect today |
| --- | --- | --- |
| Declared maturity | `Maturity = SourceMaturity.Experimental` in the plugin's `SourceDescription` (13 plugins in `plugins/Examples/`) | UI label "experimentell"; changing it changes the assembly and therefore the execution revision |
| Host opt-in list | `Desktop/ExperimentalSourceActivationPolicy.cs` (15 IDs) | Off on a profile's first sighting of the plugin, explicit activation flow, integrity policy below |
| Integrity policy | same file: `IsStrictExperimentalSource` (2ch, Uboachan, ZZZchan, SushiChan) and `RequestsMismatchPermission` (the other 11) | strict: no size/MD5 exception can be granted, a saved grant is ignored; others: activation asks for the exception |

Two consequences for Q3:

- **Leaving the opt-in list also drops the strict lock.** `IsStrictExperimentalSource` is
  only true for listed IDs; `DesktopSessionHost.SourcePolicy.cs` then accepts a saved or
  new size/MD5 grant like for any unlisted source. A stable source must keep size and
  hash checks at least as strict as before. Q3 has to move the strict lock to a separate
  host list (or make strict the default for unlisted bundled sources) with a test.
- **"Stabil" is a label nobody sets yet.** `SourceWordingMaturity.Stable` exists, but only
  host evidence bound to the exact plugin ID, source ID, execution revision and
  description hash can produce it (`SourceWording.Classify`, "Stable is never inferred").
  No production code supplies that evidence. 4chan, 8kun and Lainchan are default-on
  but are shown as "Nicht eingestuft". Q3 decides whether a promoted chan (and these
  three) get the label; if so, through a host evidence table like
  `Desktop/Plugins/SourceWordingEvidence.cs`, never from the plugin's own description.

## 2. Criteria

All must hold for the exact build that is to ship. One failure keeps or puts the source
back in "experimental".

**S1 · Live path.** Catalog → thread → original → archive → reader/gallery → offline,
on **three different boards**, in **two runs at least seven days apart**. Both runs use
the same parser code; the second run uses the exact assembly SHA-256 that ships.

**S2 · Integrity without exception.** Every downloaded original matches the declared
size byte for byte and every hash the provider supplies. No integrity exception
granted or needed during the run. A source that needs one (today Kohlchan, Wizchan,
Endchan, Leftypol) cannot become stable.

A source that provides **neither a hash nor an exact size** for a file (7chan shows only a
rounded size) can still count; the evidence then keeps `integrityBasis: "none"` and a note
(`original.integrityNote`). This applies only when the source provides nothing for that
file: a provided size or hash that mismatches stays a failure. In that case the run must
show: the HTTP `Content-Length` of the original, if present, equals the received bytes
(`contentLength`, `contentLengthMatched`; a response without Content-Length is recorded
as such, a differing one fails); the saved file decodes through the gallery's image codec
(`decoded`); and the thread and file reopen offline after the process restart.

**S3 · Real delivery.** The run uses the bundle from the built package
(`scripts/package-plugins.py`), not a source build. Offline is checked after a **real
OS process restart** with network blocked, not with a fresh host in the same process
(the 24.10. runs only did the latter).

**S4 · No gate in the way.** Public read access without login, members-only page,
challenge or age-gate bypass. The plugin never solves challenges or takes over cookies.
8chan.moe (POWBlock) and Anon Imageboards (members-only) fail here.

**S5 · Known failures are named.** For rate limit (429/503), challenge page and domain
move/redirect: what the user sees, which state the thread keeps, and a fixture test
for each. Observed real failures go into the evidence file.

**S6 · Demotion without data loss.** A test moves the source from stable back to
experimental: archived threads, media, tags, favorites and ratings stay readable, no
saved state is deleted. What happens to the on/off switch of profiles that use the
source is Q3's decision; the test pins whichever is chosen.

## 3. Live run protocol

Each run needs the user's approval in chat for that chan and date before the first request.

- Harness as on 24.10. ([live-sources-2026-10-24](live-sources-2026-10-24.md)):
  `DesktopSessionHost` with `providers: null`, real HostHttp, exact-URL audit gate,
  isolated throwaway profile, never a personal profile.
- Budget per board: one catalog GET, one thread GET (≤ 8 MiB each), one original
  (≤ 2 MiB). No retries, no HEAD/Range probes, no unplanned redirects. Three boards ×
  two runs = 18 GETs per chan. A planned redirect (for example 2ch `.hk` → `.su`) is
  listed in the run plan before the run.
- Pick a thread with at least one image attachment; prefer the board's first catalog
  entry that has one and that the source does not mark pinned/sticky, so the choice is
  not cherry-picked. A pinned thread is skipped because it is often an endless
  megathread, not a representative or small thread (a 2ch pinned thread was 637 KB).
  The skip applies only where the catalog delivers the flag (JSON catalogs); where it is
  unknown (an HTML catalog) the first entry with an image is taken. If every candidate is
  pinned, the first one is used. The run records the skipped pinned threads
  (`details.selection`, `details.pinnedThreadsSkipped`).
- The original is the first image attachment within the 2 MiB cap. Where the source shows
  only a rounded size (7chan: "355.92KB"), an attachment whose displayed size plus 1 % for
  rounding already exceeds the cap is passed over before any request; the run records
  how many (`details.selection`, `details.originalsSkippedByDisplayedSize`). A real body
  that still exceeds the cap stops the chan as before.
- Stop at the first unexpected response (challenge, login, 4xx/5xx). Record it; do not
  retry in the same run.
- After download: archive check, reader/gallery opens the file, process exits; new
  process with rejecting transport opens thread and media offline.

## 4. Evidence format

One file per run: `plugins/Examples/<Plugin>/Evidence/stability-<YYYYMMDD>.json`, next
to the existing integrity research files. Raw receipts can stay outside Git; the file
names them with hashes.

```json
{
  "kind": "source-stability-run",
  "contract": "docs/plugins/source-stability.md@<commit>",
  "pluginId": "meowchan.source.sushichan",
  "sourceId": "sushichan",
  "executionRevision": "<from the package manifest>",
  "assemblySha256": "<bundle assembly>",
  "descriptionSha256": "<SourceWording.DescriptionFingerprint>",
  "package": "<package file name and SHA-256>",
  "approvedByUser": "2026-11-12, chat",
  "windowUtc": ["2026-11-12T10:00:00Z", "2026-11-12T10:03:00Z"],
  "boards": [
    {
      "board": "lounge",
      "thread": "12345",
      "requests": [{"url": "...", "status": 200, "bytes": 51234}],
      "original": {"expectedBytes": 98765, "receivedBytes": 98765, "providerHash": "md5:...", "hashMatched": true, "receivedMd5": "...", "sha256": "..."},
      "archive": "pass", "reader": "pass", "offlineAfterRestart": "pass"
    }
  ],
  "integrityExceptionUsed": false,
  "failuresObserved": [],
  "receipts": [{"path": "...", "sha256": "..."}],
  "outcome": "pass"
}
```

Promotion (Q3) cites the two run files. A later release check that fails S1, S2 or S4
demotes the source in the next release; the failing run file stays as evidence.
