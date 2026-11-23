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

The native regression requires
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
