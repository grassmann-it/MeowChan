# Linux text-library binding

The Linux bootstrap loads only the delivered `libHarfBuzzSharp.so`, using
`RTLD_NOW | RTLD_DEEPBIND` and local visibility. It keeps the handle for the
process lifetime and installs a resolver on the HarfBuzzSharp managed assembly
which returns that exact handle. A single empty managed font creation/disposal
verifies the resolver before application XAML can shape text. Initialization is
idempotent and failures stop startup with a diagnostic; there is no unprotected
fallback or system-HarfBuzz search. Other operating systems do not activate it.

Published/single-file packages resolve the native library beside the apphost.
Framework-dependent development builds may instead use the exact process-RID
asset below `runtimes/linux-*/native/`. No environment-controlled library search
path is introduced. The targeted Linux release RIDs use glibc; this is not a
musl compatibility claim.

## Reproduced failure and diagnosis (2026-10-23)

Unmodified Linux single-file package source:
`c9efee478a52d3fd1945352568a709a27f7bc9f0`.
Apphost SHA256:
`f1d8d25615b0a87a978fc662dfe62c54c55c127d3496da5e8a21f0c26279aa17`.
Delivered HarfBuzz library SHA256:
`1d5c3afef13545bf34bf8f068b14e25ee619c3b6dee235c44e260fe61cb24018`.
Host: system HarfBuzz 14.4.0, glibc 2.44; delivered HarfBuzzSharp 8.3.1.3.

Both ordinary startup and the plugin workflow aborted with `free(): invalid
pointer`. The native stack showed system `libharfbuzz.so.0` called from delivered
`libHarfBuzzSharp.so:hb_font_create+0xc7`. The loader trace confirmed that the
latter's internal `hb_ot_font_set_funcs` relocation resolved to the incompatible
system library, despite its `libHarfBuzzSharp` symbol version. The crashing
frames did not involve SQLite or GLX; the loaded GLX vendor was Mesa/llvmpipe.

A controlled early `LD_PRELOAD` of the unmodified delivered library let both
workflow phases pass. This is retained as a diagnostic counterexample, not the
product correction or final acceptance. More directly, loading system HarfBuzz
globally first, then calling the delivered native `hb_font_create`, reproduced
SIGABRT with ordinary lazy/local loading. The same delivered bytes completed
font creation/disposal with NOW/DEEPBIND, even in that difficult order.

Evidence is retained under
`artifacts/continuation/2026-10-23/integration/`:

- `plugin-native-diag-01`: gdb stack and loaded libraries.
- `plugin-native-loader-01`: selected loader bindings and native stack.
- `plugin-normal-native-01`: same failure during ordinary startup.
- `plugin-hb-preload-01`: diagnostic-only workflow success with early preload.
- `plugin-hb-abi-01`: independent native load-order comparison.
- `plugin-hb-regression-01`: committed regression script, baseline -6/protected 0.

All probes used the build lock, relocated/read-only package closure, private
HOME/profile/display/bus and network namespace. The native regression requires
no SDK, .NET runtime, display, font files or network:

```sh
python scripts/test-linux-harfbuzz-binding.py /absolute/delivered/libHarfBuzzSharp.so --require-baseline-abort
```

Omit `--require-baseline-abort` on unaffected hosts: successful protected native
font creation is still required. For release evidence, invoke it inside the
existing SDK-free private namespace. The outer plugin runner reports failure
when used merely as the diagnostic wrapper, because this native-only script
correctly emits no product lifecycle marker; the nested regression result is
the native evidence.

## Corrected original-package acceptance (2026-10-23)

**PASS:** `artifacts/continuation/2026-10-23/integration/package-02/plugin-workflow-02/`
contains the full SDK-free workflow against the newly published original
Linux-x64 single-file package, without `LD_PRELOAD`. Both download and paused
restore exited 0 and emitted the product lifecycle PASS marker. The configured
phase limit was the existing 120 seconds. The earlier `--timeout 180` invocation
was rejected by argument parsing (exit 2), remains recorded separately, and is
not counted as a product run or timeout failure.

This package is bound to **snapshot**
`8b706e3f1b99e97027809ed2e9f6327183673476` plus retained `source.diff` and
`source-file-hashes.json`, including unrelated working-copy changes. The
informational assembly version reports that HEAD, but HEAD alone does not
identify this dirty snapshot. `source-changes-during-build.json` is empty.
An independent read-only audit reconstructed the tracked sources from HEAD plus
the diff and matched 1,116 file hashes. Five untracked file hashes matched their
current copies at audit time; the diff itself does not retain those contents.
This native acceptance is not a claim about subsequent settings/UI changes or a
latest release candidate.

- Archive SHA256:
  `6916155e4f4f7109055dd2ae8c582c8f6285b700b67077f2bd26ccaa7ffb43bd`.
- Apphost SHA256:
  `e9044b4ea0dcdbcd3a02fb55f96b45422cbcbaa2cb90ea813855a5399713532d`.
- Complete archive closure fingerprint, identical before/after execution:
  `209c82281502eb689eb4c7dc424624fc4298cdefdd75d8f781eddafa9cd75831`
  (637 entries, 612 files, 304,707,022 file bytes).
- Actual .NET bundle v6, ID `H3grEZHXM_NJ`, 228 embedded entries. Both empty
  managed assembly locations were bound by loaded MVID to the inspected bundle
  entries and authenticated running apphost bytes. MVID is module identity,
  not an in-memory cryptographic byte hash.

The independent audit rehashed the original archive and extracted payload,
reparsed every bundle entry and revalidated the recorded loaded assembly
bindings against those bytes. See `independent-audit.json` and its retained
`independent-audit.py` in the canonical workflow evidence directory.

Download used real `PluginSession` activation of all five delivered plugins,
an uncached delivered-compiler build, two source workers and the dimensions
filter worker. Exactly six permitted fixture URLs were requested once each.
The source-bound harness checked archived posts, saved media bytes/inventory and
durable filter Match/NoMatch decisions. Paused restore ran in a different process
with the same five execution revisions, no HTTP requests and no production
workers. Both phases checked normal disposal, zero remaining worker snapshots,
dead owned worker PIDs, empty staging directories and a reacquired profile lease.
Those cleanup assertions are recorded product evidence, not a later external
survey of the already-removed temporary profile.

The audited commands and source-bound environment whitelist retain the private
Xvfb/Openbox/bus, mode-0700 HOME, fresh profiles, relocated read-only package,
network namespace, absent personal home and hidden SDK/NuGet access. No preload
was supplied or inherited. The native library's bytes are the same as in the
failing package; the corrected product startup performs the binding isolation.

Coverage is Linux-x64 on this host at 1× with terminal synthetic fixture HTTP.
There is no 1.5×/2×, live-source, physical desktop, audio, musl,
Windows/macOS/ARM runtime or general release-approval claim in this evidence.
