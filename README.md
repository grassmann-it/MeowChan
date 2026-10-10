# oChan

oChan watches threads and boards on 4chan and 8kun and downloads their media. The Avalonia desktop app uses the independently consumable `libochan` library. Other applications can supply providers through the library contracts.

Version **0.9.0** is in preparation with a new desktop interface on top of the download and session foundation released in 0.8.0. The [0.9.0 release-notes draft](docs/releases/v0.9.0.md) and [changelog](CHANGELOG.md) describe the changes and upgrade considerations. The [platform validation record](docs/platform-support.md) separates historical checks from the remaining validation of the new UI and final packages.

## What's new in 0.9.0

- A redesigned Avalonia interface with Simple, Advanced and Barebones views of the same running session.
- Dark and Light themes, normal or compact density, English and German, and optional JSON language packs.
- Comma- and newline-separated URL lists, with an individual result for every entry and a summary of the latest import.
- Thread and board details with their assigned folders, source state, current errors and media progress.
- Single-URL and whole-session copying in a format that can be imported again.

The 4chan/8kun providers, shared download queue, verified storage and durable sessions remain the foundation. `libochan` continues to work independently of the UI. Final native package acceptance for 0.9.0 is still open.

## Desktop views

The first start uses **Simple**, **Dark**, **English** and normal density. Switch views without restarting downloads or losing the current selection and URL input:

| View | Presentation |
| --- | --- |
| **Simple** | The default: source, board, title, status and media progress. Thread IDs appear beneath titles. |
| **Advanced** | Adds separate sortable columns for thread ID, queued media and active media downloads. |
| **Barebones** | A smaller window with dense rows, a separate thread-ID column and fewer decorative elements. |

Every view keeps thread/board tabs, URL admission, sorting, adjustable columns, details, context actions and settings. Advanced changes the information shown; it uses the same downloader. The view, theme, language and density are saved preferences. Window size, column layout and details-panel width are not saved across restarts.

![Simple view with a running download and thread details](docs/screenshots/v0.9.0-simple.png)

![Compact Barebones view of the same running download](docs/screenshots/v0.9.0-barebones.png)

These screenshots show the productive UI in the native Linux test harness at 100% scaling. The download uses controlled offline HTTP responses; the thread title, URL and media are test fixtures.

## Build and run

Use the exact .NET 10 SDK selected by `global.json` from a source checkout. Release builds use reviewed dependency lockfiles; [updating them](docs/platform-support.md#dependency-restores-and-updates) is an explicit maintainer step:

```sh
dotnet restore oChan.sln -p:Configuration=Release --locked-mode
dotnet build oChan.sln -c Release --no-restore
dotnet run --project oChan.csproj -c Release --no-build --no-restore
```

Desktop builds target Windows, macOS and Linux. Self-contained archives include the .NET runtime, so users do not need to install the SDK. Extract the complete archive: native libraries and license files beside the executable are required. macOS archives contain an unsigned `oChan.app` bundle that can be opened in Finder. Native macOS launch and Gatekeeper behavior still require release validation. See [platform requirements and package checks](docs/platform-support.md).

## Downloading

Enter one or more thread or board URLs and select **Add**, or press **Enter** in the URL field. Separate URLs with commas or newlines; **Shift+Enter** inserts a newline. A board watch discovers its threads automatically. Each entry receives a result, and invalid or failed entries do not prevent later entries from being processed. If any entry fails, the complete input list remains available for correction. Successful input is cleared only if it has not been edited during the import.

Selecting **Add** with an empty field reads the clipboard once. Explicit input uses only the entered text. **Ctrl+L** (**Cmd+L** on macOS) focuses the URL field. **Ctrl+V/Cmd+V** outside a text field in the main window imports the clipboard; inside text fields it pastes normally. The tray also offers clipboard import. These are user-triggered actions, with no automatic clipboard monitoring.

Use **Copy URL** for a single row or **Copy all URLs** for every watched thread and board, regardless of the selected tab. The copied list contains one URL per line and can be imported again.

Open **Show details**, use the row context menu, or double-click a row to see its URL, assigned destination and current errors. **Double-click now opens details instead of the folder.** Folder and browser actions remain available separately. Wide windows show details beside the table; narrow windows overlay them. **Escape** closes details and returns focus to the table.

Progress measures saved media against currently known media, not transferred bytes. The known count can grow, and reaching 100% does not stop monitoring. Queued and active counts describe media jobs, not watched threads. An archived source does not imply that a local HTML archive exists. Rate limits and temporary HTTP failures are retried within bounded limits; cancellation waits for active work to stop.

Media is stored under:

```text
<download root>/<source>/<board>/<thread>/<media ID>.<extension>
```

Completed media is verified and recorded in a manifest. Files are published atomically; unrelated files are preserved. Interrupted transfers restart from the beginning. Removing a board stops discovery while its existing threads continue. Removing a thread also suppresses its automatic rediscovery; explicitly adding its URL allows it again.

## Settings and saved sessions

Settings control the output path, polling interval, global parallel downloads and bandwidth. Existing watches retain their assigned destinations when the output root changes. Threads added directly use the current root; threads discovered from an existing board keep that board's assigned root.

Settings also control theme, language, view, compact density and minimizing to the tray. **Save & Close** validates and saves changes; **Cancel** discards pending edits. Theme and view changes made directly in the main window save immediately. Existing settings files receive defaults for the new appearance preferences while retaining their download and session settings.

When **Restore session on startup** is enabled, confirmed changes to watched URLs are saved as they happen. The application restores them at startup. Settings/session failures are shown rather than silently replacing saved state. Only one running application can own a profile.

The default profile is `%APPDATA%/.oChan` on Windows and `$HOME/.oChan` on macOS/Linux. Settings are stored in `settings.json` in that profile. Set `OCHAN_PROFILE` to an absolute directory to use an isolated profile. The default download root is `Downloads/oChan` under the user home directory.

English and German are built in. To add another language, place a plain JSON pack in `<active profile>/LanguagePacks`, restart, then select it in Settings. Missing translations fall back to English; an invalid or unavailable selected pack produces a warning and an English fallback. See the [language-pack format](Desktop/Localization/README.md). Technical provider and exception messages can remain untranslated.

## Library

The library project is `libochan/libochan.csproj`; package consumers keep the existing `oChan` namespaces. The desktop configuration, windows and Avalonia dependencies belong to the reference client.

```sh
dotnet pack libochan/libochan.csproj -c Release -o artifacts/packages
```

The [package consumer sample](samples/LibOChan.Consumer/README.md) shows how to restore the local package into a fresh cache and run a custom provider without the desktop app. The [session API](docs/Registry.md) describes lifetime and persistence ownership; the [download pipeline](docs/Downloader.md) explains completion and cancellation.

Provider registration is explicit. Dynamic plugin loading, a daemon, TUI, terminal browser and HTML archive are future work.

## Verification

```sh
dotnet test oChan.sln -c Release
dotnet build tests/oChan.DesktopSmoke/oChan.DesktopSmoke.csproj -c Release
python scripts/run-desktop-smoke.py --assembly tests/oChan.DesktopSmoke/bin/Release/net10.0/oChan.DesktopSmoke.dll --output artifacts/desktop-smoke
```

The graphical harness requires a working desktop on Windows/macOS. On Linux, its default runner uses Xvfb and a private session bus; `--native-display` uses an existing graphical session. It covers normal presentation/settings, profile conflicts, absent/failing tray behavior and a download/restore session backed by local HTTP fixtures. Offline tests and the harness make no public imageboard requests. Running the harness does not replace starting a freshly extracted release archive. See the [platform validation record](docs/platform-support.md) for evidence and remaining native checks.

## License

The project uses Artistic License 2.0; see [LICENSE](LICENSE). Desktop dependency and asset notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
