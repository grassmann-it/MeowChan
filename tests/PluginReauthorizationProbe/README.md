# Synthetic reauthorization process probe

Invoked by `MeowChan.PluginRuntime.Tests/PluginReauthorizationProcessTests.cs`.
Each command is a separate OS host process. Commands share only a synthetic
fixture input, a frozen review row, and the production profile store on disk.
They use the existing public inventory, consent, startup and worker APIs; no
private binding injection, new persistence semantics or product hooks.

Two paths are covered: unchanged declarations with inherited permissions, and
changed declarations requiring both review and permission acknowledgement.
The probe delivers a plugin from the separate SDK-only
`PluginReauthorizationFixture` project. Stamping its embedded release marker
yields distinct synthetic PE images. The worker reads the marker from the
verified image it loads into memory. C is normally delivered from a third
folder: B's exact consent cannot authorize a further relocation. Separate cases
exercise decision #130's established same-path, non-widening update policy and
keep same-path replacements blocked before B's first activation or when W adds a
new HTTP host declaration. Replaying B's frozen consent row with the latest profile
revision must fail in every case, without changing persisted bytes.
This is **not** compiler rebuild,
DesktopSessionHost, genuine-profile migration, packaging or release acceptance.
The real worker must describe the exact active release, return its marker and
persisted setting, and round-trip a scoped synthetic image grant. HTTP fails
unconditionally; there are no provider requests.

The parent runs commands sequentially, bounds each lifetime, and kills only its
own child tree on timeout. Workers have bounded handshake/operation lifetimes,
are disposed before the helper exits and must leave no workspace. Disabled and
blocked releases never enter the worker operation path. An unrelated disabled,
pinned plugin and all retained configuration, ownership, grants and immutable
backup files are checked across the transitions.

`OCHAN_REAUTH_EVIDENCE` selects an external evidence directory (otherwise the
test deletes its temporary directory). Every invocation saves
PID, JSON and stderr; JSON includes host/worker PID, source SHA from
`OCHAN_TEST_SOURCE_SHA`, probe, fixture, worker and delivered target hashes,
and profile and backup hashes. The test independently hashes the executable and
the unmodified fixture output and compares each with the probe's reported hash.
The runner must set that SHA to the checked-out commit and rebuild the test
project with dependencies before using `--no-build`. The SHA is provenance
supplied by the runner, not an independent freshness assertion by the helper.
Use a private HOME/TMPDIR/DOTNET_CLI_HOME/XDG tree and the shared heavy lock for
all restore/build/test commands; restore only from cache with `--locked-mode`.
No solution-file registration or shared test resolver modification is needed.
