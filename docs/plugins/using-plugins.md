# Install, edit and activate plugins

These instructions describe the 1.0 source-tree interface. Release packaging and
native acceptance are tracked separately in [validation](validation.md). Extract
a complete candidate application: its worker, compiler and reference files are
required for the corresponding plugin operations.

## Install a source file

1. Open **Plugins** from the application menu or Tools menu. The source detail
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

In the selected plugin, expand **Source, editor and test**. **Open in editor**
opens the saved source externally; MeowChan does not contain an embedded code
editor. Editor preferences accept an executable and one argument per line, with
`{file}`, `{line}` and `{column}` placeholders. A custom argument template must
include `{file}`. These are literal process arguments, not shell commands. Leave
the executable empty for the default application; the folder action remains a
fallback if editor handoff fails.

Save the file in the editor, then choose **Build and test**. The compiler reads
the saved bytes. The separate test worker validates the entry/description and
settings; its successful result does not replace the active production worker.
Compiler diagnostics retain the source path, line and column when available.

For a source plugin, enter a supported thread or board URL and choose **Fetch test
URL**. This is a metadata probe in a test worker: it creates no watch, archive
entry or original-media download. The normalized result distinguishes posts,
attachments, catalog entries, provider waits and failures. A preview of the first
100 entries is a UI limit; it is not permission for a parser to truncate results.
The probe uses the same host HTTP policy and may consume provider request capacity.
Use small local fixtures while developing.

**Cancel operation** cancels the test; late results are ignored. A passing probe
establishes neither normal acquisition nor persistence across restart. Those
need the [full source validation path](validation.md#source-and-filter-validation).

## Understand the selected version

| State/action | Meaning |
| --- | --- |
| Active | The build and matching configuration currently selected for production |
| Unsaved draft | Edits in the settings form; active values are unchanged |
| Save for next start | Persist validated plugin/source settings for the next app start |
| Use this candidate next start | Explicitly select a candidate for startup; does not hot-swap it now |
| Saved for next app start | A pending build/configuration selection |
| Use last good build next start | Select a retained good build with its matching configuration and pin the rollback |
| Explicit rollback pinned | Startup keeps that selected fallback until another candidate is explicitly chosen |
| Enable / Disable | Change availability now, using the already selected valid build; do not activate a pending test build or pending settings |

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

The labels below come from the implemented [localization
catalog](../../Desktop/Localization/PluginCatalog.cs).

| English | Deutsch |
| --- | --- |
| Open plugin folder | Pluginordner öffnen |
| Plugin settings and tests | Plugineinstellungen und Tests |
| Source, editor and test | Source, Editor und Test |
| Open in editor | Im Editor öffnen |
| Build and test | Bauen und testen |
| Fetch test URL | Test-URL abrufen |
| Cancel operation | Vorgang abbrechen |
| Save for next start | Für nächsten Start speichern |
| Use this candidate next start | Diesen Kandidaten beim nächsten Start verwenden |
| Use last good build next start | Letzte gute Fassung beim nächsten Start verwenden |
| Enable / Disable | Aktivieren / Deaktivieren |
| Revoke access | Zugriff widerrufen |
