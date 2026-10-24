# Install, edit and activate plugins

These instructions describe the 1.0 source-tree interface. Release packaging and
native acceptance are tracked separately in [validation](validation.md). Extract
a complete candidate application: its worker, compiler and reference files are
required for the corresponding plugin operations.

## First session

1. Open **Plugins** from the app menu or footer (under **Tools** in compact views). The supplied plugins include
   **Lainchan**, **Kohlchan**, **Wizchan**, **Endchan** and
   **Image dimensions (example)**. Selecting an entry shows
   its purpose, current status and available actions. Use the category selector
   for sources, image filters or all plugins. Sources → source details →
   **Plugin settings and tests** is another route to that source's entry.
2. Choose **Enable plugin** or **Disable plugin** to change availability now.
   These actions use the already selected valid build. **Source and watches**
   opens the related source and its watched entries when available.
3. Use **Actions** → **Open settings** to reach available plugin/source options.
   Review your changes and choose **Save for next start**, then restart MeowChan
   to use them. An **Unsaved draft** has not been saved; **Saved for next start**
   is separate from the build and settings currently in use.
4. To try the supplied dimensions filter, open Media rules for one intended
   source/thread, choose it under **Image filter**, set its width/height or
   orientation criteria, and **Grant required access for this scope**. Save the
   rule. It examines supported JPEG/PNG originals for new decisions; previously
   saved originals remain available. See [filter settings and grants](filters-and-settings.md).

Use an isolated preview profile and media folder for experiments. Only run plugin
code you trust: a separate process still has your user permissions. A successful
test leaves the active production build unchanged; saved plugin/source settings
and candidate selections take effect at the next app start.

## Purpose, status and actions

The overview puts the plugin's purpose and status first, with its type and a
compact version. Read any unavailable or attention message before acting.
Descriptions labelled **Last known description** can come from an earlier saved
build; they do not establish a successful current start. Plugin status describes
local availability. It does not establish website reachability or successful
downloads. The [dated live-source results](live-sources-2026-10-24.md) retain
unresolved metadata conflicts for Kohlchan, Wizchan and Endchan.

Right-click a plugin entry, or focus it and use the context-menu key (normally
Shift+F10), for its actions. The **Actions** buttons offer the same choices.
**Open details**, **Open settings**, **View permissions** and **Show developer
details** take you to the relevant part of that plugin. Unavailable actions show
their reason. Enable/disable does not save a settings draft; candidate selection
and rollback remain choices for the next start.

Expand **Developer details** for technical information, **Copy technical details**
and source editing/testing tools. Ordinary plugin use does not require opening
this section. To add or edit a trusted `.cs` source, follow the next sections.

## Experimental sources and media checks

**Kohlchan**, **Wizchan** and **Endchan** are labelled **Experimental**. Their
provider size or MD5 metadata can differ from the delivered file. The default
is strict checking; the label alone grants no exception.

Open **Sources**, select the source and choose **Allow provider size and MD5
mismatches** only if you want to accept those differences. Sources is the only
place to grant or revoke this permission. It is off by default and applies to
that profile, plugin owner and source. Updates from the same plugin owner retain
it; another owner or profile does not inherit it. A successful save takes effect
immediately for new media acceptance by the active source. This is separate from
plugin/source settings saved for the next start. An inactive source's saved
permission does not allow it to save new media.

Enabling this permission does not automatically retry earlier failed downloads.
After a successful save, open **Downloads**, select the affected thread and choose
**Retry missing media** (**Fehlende Medien erneut versuchen**) in its details.
This action is also available for eligible **Completed** (**Abgeschlossen**) work;
queue and source limits still apply.

The exception permits only provider size and MD5 differences. Any supplied
provider SHA256, local integrity verification, complete HTTP transfer, byte/pixel
and temporary-storage limits, cancellation and filter decisions remain strict.
For each accepted attachment revision, the original provider metadata, measured
local length/hashes and acceptance record remain distinct and refer to that
concrete medium.

Clear the checkbox to revoke permission. Once the change succeeds, new
acceptance requiring the exception is blocked. Already accepted archived media
remains usable when its local length and SHA256 verify. An earlier acceptance
record does not authorize a new attachment revision or reference.

The [dated live-source report](live-sources-2026-10-24.md) retains the historical
integrity failures for these three sources. Their deployed cause, including any
EXIF processing, has not been established. The later [three-source consent
follow-up](live-sources-2026-10-24.md#experimental-source-consent-follow-up-freeze09)
passed for the selected originals after explicit permission and manual retry.
Release readiness and broader source availability remain separate assessments.

## Install a source file

1. Open **Plugins** from the application menu or footer (under **Tools** in compact views). The source detail
   action **Plugin settings and tests** opens the same catalog for that source.
2. Select **Open plugin folder**. The displayed directory is
   `<active profile>/plugins`; it is independent of the media download directory.
3. Place one complete `.cs` file directly in that directory. Include all needed
   `using` directives and exactly one public, concrete plugin entry with a public
   parameterless constructor. A project file or a directory of partial sources is
   not the drop-in format.
4. Restart MeowChan to build, describe and activate a valid new candidate. A failed
   candidate remains visible with its path and diagnostic. Refreshing the catalog
   during a session does not activate newly discovered code.

The bundled compiler targets C# 14/.NET 10. It does not run user MSBuild projects,
NuGet restores, analyzers, source generators or plugin initializers. Implicit
usings are not generated. The drop-in build has no `#load` or arbitrary dependency
resolution and does not allow `unsafe`. See the [compiler implementation
notes](../../MeowChan.PluginCompiler/README.md) for its delivered reference set.

A prebuilt plugin uses a verified `.plugin.json` bundle, not a loose DLL alone;
see [bundles and resources](hosting-and-resources.md#prebuilt-bundles).

If you copy a supplied example to edit it, its IDs still identify the same plugin
and source. The local file must not silently displace the supplied candidate.
Select the intended candidate explicitly when the catalog reports a conflict.
Changing IDs creates a different plugin/source identity; do that for a genuinely
new source, not as a way to bypass an update or recover existing watches.

## Edit and test

In the selected source-file candidate, expand **Developer details** and use
**Source, editor and test**. **Open in editor**
opens the saved source externally; MeowChan does not contain an embedded code
editor. Editor preferences accept an executable and one argument per line, with
`{file}`, `{line}` and `{column}` placeholders. A custom argument template must
include `{file}`. These are literal process arguments, not shell commands. Leave
the executable empty for the default application; the folder action remains a
fallback if editor handoff fails.

Save the file in the editor, then choose **Build and test**. The compiler reads
the saved bytes. The separate test worker validates the entry/description and
settings; its successful result does not replace the active production worker.
The delivered compiler needs no installed .NET SDK.
Compiler diagnostics retain the source path, line and column when available.

For a source plugin, enter a supported thread or board URL and choose **Fetch test
URL**. This is a metadata probe in a test worker: it creates no watch, archive
entry or original-media download. The normalized result distinguishes posts,
attachments, catalog entries, provider waits and failures. A preview of the first
100 entries is a UI limit; it is not permission for a parser to truncate results.
The probe uses the same host HTTP policy and may consume provider request capacity.
Use small local fixtures while developing.

To use the tested build for downloads, explicitly choose **Use this build next
start**, restart MeowChan, then add the supported URL in Downloads. A test or
**Refresh status** does not activate the tested build.

**Cancel operation** cancels the test; late results are ignored. A passing probe
establishes neither normal acquisition nor persistence across restart. Those
need the [full source validation path](validation.md#source-and-filter-validation).

## Understand the selected version

| State/action | Meaning |
| --- | --- |
| Active | The build and matching configuration currently selected for production |
| Unsaved draft | Edits in the settings form; active values are unchanged |
| Save for next start | Persist validated plugin/source settings for the next app start |
| Use this build next start | Explicitly select a candidate for startup; does not hot-swap it now |
| Saved for next app start | A pending build/configuration selection |
| Use last good saved build next start | Select a retained good build with its matching configuration and pin the rollback |
| Last good saved build pinned | Startup keeps that selected fallback until another candidate is explicitly chosen |
| Enable plugin / Disable plugin | Change availability now, using the already selected valid build; do not activate a pending test build or pending settings |

A selected update that fails at startup leaves the plugin unavailable with a
reason. The host retains the last good version for an explicit rollback; it does
not quietly execute old code after a broken update. A successful build is not an
activation decision. Changing source bytes, compiler/framework/SDK references or
compile options changes the compilation fingerprint; editing ordinary settings
or a file's timestamp alone does not.

Duplicate plugin/source IDs and overlapping URL ownership are conflicts, not a
filename priority rule. Missing or disabled plugins keep saved configuration,
grants and references. Their absence does not delete captured posts or saved
images. Existing offline reading and export should remain available; restoring
work does not grant a source a new watch lifetime or reset media retry deadlines.

Plugin and source settings take effect at the next app start. Image-filter
parameters belong to their media rule and affect new decisions after that rule is
saved. See [settings and grants](filters-and-settings.md) before enabling a filter.

## English and German labels

The labels below come from the implemented [plugin localization
catalog](../../Desktop/Localization/PluginCatalog.cs) and [Sources
catalog](../../Desktop/Localization/SourcesCatalog.cs).

| English | Deutsch |
| --- | --- |
| Actions | Aktionen |
| Open settings | Einstellungen öffnen |
| Source and watches | Quelle und Watches |
| Developer details | Entwicklerdetails |
| Show developer details | Entwicklerdetails anzeigen |
| Copy technical details | Technische Details kopieren |
| Open plugin folder | Pluginordner öffnen |
| Plugin settings and tests | Plugineinstellungen und Tests |
| Source, editor and test | Source, Editor und Test |
| Open in editor | Im Editor öffnen |
| Build and test | Bauen und testen |
| Fetch test URL | Test-URL abrufen |
| Cancel operation | Vorgang abbrechen |
| Save for next start | Für nächsten Start speichern |
| Use this build next start | Diese Fassung beim nächsten Start verwenden |
| Use last good saved build next start | Letzte gute gespeicherte Fassung beim nächsten Start verwenden |
| Enable plugin / Disable plugin | Plugin aktivieren / Plugin deaktivieren |
| Revoke access | Zugriff widerrufen |
| Experimental | Experimentell |
| Allow provider size and MD5 mismatches | Abweichende Anbietergröße und MD5 zulassen |
