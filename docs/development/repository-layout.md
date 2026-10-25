# Repository layout

The repository root contains the solution, desktop project, application bootstrap,
build configuration and introductory documentation. Desktop views live beside
the existing desktop services, grouped by the part of the application they serve.

| Location | Contents |
| --- | --- |
| `App.axaml`, `App.axaml.cs`, `Program.cs` | Application startup and shared theme registration. |
| `Desktop/Shell/` | `MainWindow` and its partial files: navigation, window behavior and connections to desktop services. |
| `Desktop/Views/About/` | About window. |
| `Desktop/Views/Admission/` | Board admission and experimental-source activation dialogs. |
| `Desktop/Views/Archive/` | Archive page and its search, media, collection and cleanup interactions. |
| `Desktop/Views/Collections/` | Bookmarks, reader groups and saved-search dialogs. |
| `Desktop/Views/Diagnostics/` | Diagnostics page. |
| `Desktop/Views/Plugins/` | Plugin management page. |
| `Desktop/Views/Rules/` | Rules page and draft/template interactions. |
| `Desktop/Views/Settings/` | Settings editor and its containing window. |
| `Desktop/Views/Sources/` | Source management page. |
| `Desktop/` | Existing desktop services, controls, session integration and supporting models. |
| `Assets/` | Application images and icons, retaining their existing resource paths. |
| `libmeowchan/` | UI-independent library. |
| `MeowChan.Plugin*/`, `plugins/` | Plugin infrastructure and supplied plugin implementations. |
| `tests/`, `samples/` | Tests, graphical harnesses and developer examples. |
| `scripts/`, `packaging/` | Validation, packaging and release tooling. |
| `docs/` | User, developer and release documentation. |

Keep each view's XAML, code-behind and other partial files together. Folder names
describe ownership; existing namespaces and type names remain unchanged. This
organization does not create new projects or assemblies. `MeowChan.csproj` and
`MeowChan.sln` stay at the root, so the documented build commands continue to use
the same project and solution paths.

XAML resources are compiled from their new locations. Application assets and
`Desktop/WorkspaceStyles.axaml` keep their absolute `avares://MeowChan/…` paths.
When adding a file-based source check, use the view's actual path in this layout
instead of assuming that every desktop view is at the repository root.
