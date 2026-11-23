# MeowChan 1.1.1 · fix-plugin-settings-lock

Basis ist `1c0aa076a`. Anlass ist Befund 1 aus Astras Review vom 23. November 2026.

## Befund

Bestätigt. In der Basis wählt der Desktop für das Einstellungsformular den beobachteten neuen Build. Nach der Validierung im Testworker schreibt der Store dessen Einstellungen in `Pending`. Die Startprüfung wertet einen passenden `Pending`-Eintrag als Build-Auswahl. Der Produktionsfilter ermittelt seine gespeicherte Freigabe anschließend über Plugin-ID, Filterschlüssel und Geltungsbereich. Die Freigabe konnte so trotz neuem ausführbarem Build weiterwirken.

Fundstellen in der Basis sind `Desktop/Plugins/PluginManagementServices.cs:27`, `Desktop/Views/Plugins/PluginsPage.Testing.cs:75`, `Desktop/Views/Plugins/PluginsPage.axaml.cs:526`, `MeowChan.PluginHost/Configuration/PluginProfileStore.cs:336`, `:510`, `:583`, `:907` und `Desktop/PluginSession.Filters.cs:41`.

## Korrektur

- Der Profilstore hält den validierten Einstellungsentwurf dauerhaft beim exakten Kandidaten. Die Startprüfung fordert für einen geänderten lokalen Build die ausdrücklich gespeicherte Auswahl dieses ausführbaren Builds und seiner Deklaration. Ein früherer, mehrdeutiger `Pending`-Eintrag für den gesperrten Build wird als Entwurf behalten. [PluginProfileStore.cs](../MeowChan.PluginHost/Configuration/PluginProfileStore.cs#L343)
- Die Plugins-Seite lädt die gespeicherten Werte wieder, meldet die weiter bestehende Sperre und führt nach dem Speichern zur Build-Auswahl. Der Text ist auf Deutsch und Englisch sowie in den zehn mitgelieferten Sprachpaketen verfügbar. [PluginsPage.Testing.cs](../Desktop/Views/Plugins/PluginsPage.Testing.cs#L75) · [PluginCatalog.cs](../Desktop/Localization/PluginCatalog.cs#L293)
- Beim erfolgreichen Wechsel auf den geänderten lokalen Build widerruft der Store die bisherigen Filterfreigaben atomar. Für den neuen Build können sie in den Medienregeln erneut erteilt werden. [PluginProfileStore.cs](../MeowChan.PluginHost/Configuration/PluginProfileStore.cs#L405)
- Der Bundled-Pfad verwendet weiterhin den vorhandenen Auslieferungsnachweis und seine Integritäts- und Berechtigungsprüfungen. [PluginProfileStore.cs](../MeowChan.PluginHost/Configuration/PluginProfileStore.cs#L228)

## Prüfungen

| Release-Testlauf | Bestanden |
| --- | ---: |
| `MeowChan.PluginHost.Configuration.Tests` | 425 |
| Plugins-Seite, Einstellungen und Reparaturrouten | 162 |
| Lokalisierung, Sprachpakete und IdenticalLocalization | 176 |
| DesignLint | 37 |
| Danbooru-Paketwechsel und Worker-Komposition, offline | 2 |

- Die Store-Regressionen decken Quell- und Manifest-Kandidaten, Neustarts nach reinem Einstellungsspeichern, die anschließende exakte Auswahl, die alten Filterfreigaben, ältere Profile mit mehrdeutigem `Pending` und einen Neubau vor der ersten gültigen Konfiguration ab. [PluginProfileStoreTests.BundledUpgrade.cs](../tests/MeowChan.PluginHost.Configuration.Tests/PluginProfileStoreTests.BundledUpgrade.cs#L49)
- Die Plugins-Seite wurde über die Präsentation und die Verdrahtung von Speicherstatus und Fokus geprüft. [PluginEntryPresentationTests.cs](../tests/MeowChan.DesktopTests/PluginEntryPresentationTests.cs#L61) · [SourcesPluginsDesktopContractTests.cs](../tests/MeowChan.DesktopTests/SourcesPluginsDesktopContractTests.cs#L149)
- Der Danbooru-Test verwendet eine Kopie des Pluginzustands, den das veröffentlichte Linux-x64-Paket v1.1.0 angelegt hat. Beide Paketmanifeste haben verschiedene Assembly-Hashes. Die Deklaration und Konfiguration blieben bytegleich. Der neue Build wurde in beiden Starts nach dem Verzeichniswechsel automatisch aktiv und stellte die Booru-Dienste bereit. Nach der ausdrücklichen Rollback-Auswahl startete die alte Revision. [BooruPluginRuntimeCompositionTests.cs](../tests/MeowChan.DesktopTests/BooruPluginRuntimeCompositionTests.cs#L96) · [MeowChan v1.1.0](https://github.com/grassmann-it/MeowChan/releases/tag/v1.1.0)

Das Archiv `07-MeowChan-1.1.0-linux-x64-system-vlc.tar.gz` wurde gegen die veröffentlichte Prüfsumme `f7e3aeeb83b8ab10b4d756ce49afd81e8b0a1df80493bb96e548f5fb022684b1` verifiziert. Der Release-Apphost hat den v2-Pluginzustand und die Einstellungen im frischen Profil unter Xvfb mit eigener Netzwerk-Namespace geschrieben. Der Starthelfer beendete den Release-Apphost nach seinem Zeitlimit. Für den Replay wurde der vollständig geschriebene, integritätsgeprüfte Pluginzustand kopiert. Der Test lässt sich mit `MEOWCHAN_RELEASED110_DANBOORU_PROFILE` und dem Testfilter `ReleasedDanbooruMovesToTheCurrentDeliveredBuild` ausführen.

Der gebaute Desktop-Katalog enthält 5410 englische Schlüssel. Alle zehn eingebetteten Packs enthalten jeweils einen zusätzlichen Eintrag. Anzahl und Prüfsumme der mit Englisch identischen Zeichenketten wurden aus der gebauten Assembly neu berechnet und entsprechen den Audit-Erwartungen. [IdenticalLocalizationStringsAuditTests.cs](../tests/MeowChan.DesktopTests/IdenticalLocalizationStringsAuditTests.cs#L11)

PluginHost, Desktop und Testassemblies wurden in Release mit aktiven Analyzern erstellt. Der Build nutzte bereits kompilierte Abhängigkeiten, deren Quellen zwischen ihren Build-Revisionen und der Paketbasis identisch sind. Die Analyzer-Baseline umfasst 1019 Zeilen. Eine vorhandene Diagnose wurde an den neuen LF-normalisierten SHA-256 gebunden. `PublicAPI.Shipped.txt` entspricht der Basis. `global.json` hat die bereits vorhandene lokale SDK-Auswahl 10.0.112. Der Branch enthält dafür keinen Commit.
