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

The corrected managed bootstrap and a newly published original Linux package
still require Root's build, tests and full SDK-free workflow without preload.
No Windows/macOS/ARM runtime or live-source claim follows from these probes.
