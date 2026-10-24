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
| shortcuts | Settings shortcut search/list; Record/cancel; text/normal command contexts in EN/DE | Native Enter/Space/Escape, exact selected-action return, native Tab/Shift+Tab, one suppressed/active real application shortcut; source-built Host state and draft assertions |

The C# fixtures only expose real existing controls and wait at the established
test-only `NativeFeatureCheckpoint` protocol. They do not change automation
properties, relax assertions, invoke IME or fabricate speech. `inspect-features.py`
reads the live AT-SPI tree of the exact launched process; full snapshots, events,
focus intervals and X11 recipient evidence are retained. A focused control is
first left by a real Tab before refocusing, so an actual new Orca utterance can be
observed without relying on a duplicate focus request. For the plugin/source/search suites, no text is entered and
no action button is invoked by this observer. The shortcut slice below performs
its explicitly described native key actions.

Each future focus interval ends only after a new exact target native focus event,
a following Orca locus transition to that name/role, and its exact name utterance
from newly appended debug records. A subsequent focus loss/other target fails the
probe. This live completion barrier uses the existing 55-second C# checkpoint
deadline (45 seconds from stage publication for observation, then final evidence
and acknowledgement); it adds no retry or global timeout. Old failed intervals
remain unchanged and must be rerun against a separately bound source freeze.
The private Orca customization makes its existing debug file line buffered and
records `MEOWCHAN NATIVE DEBUG FILE: line buffering enabled` during Orca initialization.
This exposes actual complete log records to the live observer immediately;
Python's default block buffering can otherwise retain a valid focus/utterance
until unrelated later activity. Speech generation and completion gates stay the
same. The Orca bundle itself is unchanged.

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


## Native shortcut capture and context slice

`--suite shortcuts` reuses the existing real Settings ShortcutEditor checkpoint
and runs **both EN and DE** per invocation, like the sources slice. `--language`
does not restrict these two suites. Start at actual1× with fresh output; the same
runner accepts1.5×/2× for separately bound later observations.

```sh
flock --close /tmp/meowchan-ui-1.0-build.lock python3 "$MC_SOURCE_ROOT/scripts/native-input/run-native.py" --source "$MC_SOURCE_ROOT" --source-manifest "$MC_MANIFEST" --assembly "$MC_SMOKE_DLL" --bundle "$MC_TOOLS" --suite shortcuts --scale 1 --output "$MC_NATIVE_EVIDENCE/shortcuts-en-de-1x"
```

The six required stages are `shortcuts`, `shortcut-text` and `shortcut-normal`
for each locale. The existing source-manifest/build/tool binding, private0700
HOME/state/runtime, random root ownership marker, private DBus/AT-SPI sockets,
exact process/frame X11 recipient, Orca flush and event-bound speech gates remain
in force. This is a source-built isolated Linux X11 fixture, separate from the
current delivered package, physical desktop/tray shell and human acceptance.

The observer now starts **Record additional shortcut with real XTest Enter and
Space**, each followed by real Escape. It requires actual capture focus and the
same unique selected native action path/name/role/id with selected/focused/showing
state after Escape. It makes no focus request after Escape. The existing C#
postcheck independently requires the same real ListBoxItem object, FocusManager
target, scroll viewport, unchanged shortcut draft and released capture lease.
AT-SPI `grab_focus` is explicit test setup; Record is not an accessibility-action
substitute or a routed in-process key event.

The text stage sends actual fresh-profile Linux `Ctrl+Shift+P` while shortcut
search has focus, then Tab and Shift+Tab. The native observer requires unchanged
raw text and exact reverse focus return. C# independently requires unchanged
Host/durable pause state, page and text, as well as visible focus. The normal
stage sends the same chord on the real Pause button; native Pause/Resume label
change plus actual Host/durable pause transition must occur. C# restores the
isolated pause state through the Host API separately, without claiming that
cleanup as native input. An unexpected remapped binding fails instead of testing
another shortcut. No shortcut is saved and no watch or media is created.

`native-actions.jsonl` records each actor key/chord with its exact PID/frame
recipient proof; per-activation selected-action-return JSON records the native
before/after identity. Normal focus/Orca intervals and source-built screenshots
remain separate evidence. The observer shares the unchanged checkpoint deadline;
there is no retry or extra timeout. Failed attempts remain immutable.

This slice establishes representative native Enter/Space/Escape, one forward/
reverse Tab pair, capture cancellation and one text/normal command boundary.
It does **not** establish complete N07 traversal/actions for Plugins/Sources/
search dialogs, every shortcut/tray context, OS-global shortcuts, IME composition,
physical key hardware or preference acceptance. Orca still gates exact spoken
names for its recorded focus intervals; it does not prove spoken role/value/error/
state or the entire capture interaction. Package-bound N06/N07 and these N08
remainders stay open.
