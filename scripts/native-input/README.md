# Focused native Linux input/Orca checks

W20-only runtime handoff. This reuses the preserved W19 private Xvfb/DBus/AT-SPI
driver and actual Orca bundle for the **current** plugin/source/search forms.
It requires the current source-built DesktopSmoke and delivered compiler/worker
payloads beside it. It does not replace package execution, real IME composition,
audible audio, other OSes, visual/human acceptance or historical numeric tests.

No personal display, accessibility bus, audio endpoint, profile or clipboard is
used. The maintained desktop helper clears inherited session variables; the
observer validates an explicit runner-owned root and random owner marker, exact
0700 HOME/state/runtime children and real DBus/AT-SPI socket paths inside that
runtime. The temporary parent may be W20's task-private TMPDIR; no `/tmp` prefix
is assumed.
Native keys are sent only after matching the launched PID and accessible frame
title to exactly one private X11 client. Only owned processes are cleaned up.

Set these absolute paths from the actual W20 freeze. `MC_MANIFEST` is the complete
freeze file map, supporting either `files: {path: sha256}` or
`files: {path: {sha256: ...}}` and `sourceDigest`/`source_digest`. Every listed
source file is checked before launch. Assembly must be under the frozen source;
build output hashes, helper/tool hashes and source-manifest hash are recorded.

```sh
MC_TOOLS=/home/tomka/Desktop/oChan-ui-workers/boards/artifacts/ui-implementation/boards/accessibility-tools/root
# Set MC_SOURCE_ROOT, MC_MANIFEST, MC_SMOKE_DLL and fresh MC_NATIVE_EVIDENCE first.
flock /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite plugins --language en --scale 1 --output "$MC_NATIVE_EVIDENCE/plugins-en-1x"
flock /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite plugins --language de --scale 1 --output "$MC_NATIVE_EVIDENCE/plugins-de-1x"
flock /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite sources --scale 1 --output "$MC_NATIVE_EVIDENCE/sources-en-de-1x"
flock /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite saved-searches --language en --scale 1 --output "$MC_NATIVE_EVIDENCE/searches-en-1x"
flock /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite saved-searches --language de --scale 1 --output "$MC_NATIVE_EVIDENCE/searches-de-1x"
```

After initial results, repeat affected suites with fresh outputs at `--scale 1.5`
and `--scale 2` as required by the native matrix. The sources suite itself visits
EN/DE across modes/themes at actual minimum sizes. Plugin/search locale selectors
are explicit. Never reuse outputs or change assertions/timeouts for a green run.

| Suite | Required checkpoints | Actual native assertions |
| --- | --- | --- |
| plugins | Overview category/refresh; plugin-local integer error; source-worker error | Unique native exposure, exact localized name, role, error/help, preserved raw text, required initial focus, focus movement through real XTest Tab, Orca name utterance |
| sources | Strict per-source permission and Manage in Sources link in EN/DE | Initial native focus, named checkbox/button, focus movement, actual Orca output; policy state/mutation assertions remain with the existing SourceIntegrity fixture |
| saved-searches | Archive query field; Save-name dialog; manager list/close | Query draft preserved, exact dialog names/roles/initial focus, actual focus/Tab and Orca utterance; saved-search fixture assertions remain unchanged |

The C# fixtures only expose real existing controls and wait at the established
test-only `NativeFeatureCheckpoint` protocol. They do not change automation
properties, relax assertions, invoke IME or fabricate speech. `inspect-features.py`
reads the live AT-SPI tree of the exact launched process; full snapshots, events,
focus intervals and X11 recipient evidence are retained. A focused control is
first left by a real Tab before refocusing, so an actual new Orca utterance can be
observed without relying on a duplicate focus request. No text is entered and
no action button is invoked by this observer.

Native roles are recorded and required to be nonempty/known; this observer does
not assert a complete control-type-to-role mapping.

`native-findings.json` gates the native assertions and all required localized
stages. `speech-summary.json` independently requires each recorded native focus
interval to contain the exact accessible name in real `SPEECH OUTPUT` messages
from Orca. `session-results.json` combines the application, observer and speech
outcomes. A tree-only PASS with absent speech stays a failed combined run.
Orca sound/braille servers are deliberately disabled; this is real generated
screen-reader utterance evidence, with **no heard-audio claim**.

Retain failed logs and reproduce a failing field before proposing a product
change. The source-built fixture binding is separate from the actual packaged
source/filter/upgrade workflows in the [native matrix](../../docs/releases/v1.0.0-native-checks.md).
