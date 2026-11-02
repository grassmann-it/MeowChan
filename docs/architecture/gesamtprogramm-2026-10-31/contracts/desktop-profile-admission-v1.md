# P08c — Desktop-Owner-/Command-Lifecycle

Technischer Integrationsschnitt auf dem [P08b-Kern](profile-admission-kernel-v1.md),
kein vollständiges Import-/Applygate. Umsetzung und Abnahme werden separat in
RUNSTATE und der Root-Reconciliation belegt; dieser Vertrag ist kein Testbeleg.

- Ein Startup-Token gehört zum Host ab Leaseakquisition, vor Profilowner-Konstruktion.
  Erst nach Abschluss der Startupwriter wird Ready veröffentlicht. Restorefehler führen
  zu RecoveryFailed; Shutdown kann Ready unter derselben Lifecycleordnung verhindern.
- Akzeptierte Interactive-/Control-Kommandos halten Ordinary vor Workerdispatch bis
  Ende von Mutation, Projektion und Cleanup. Abweisung/Control-Deduplizierung erzeugen
  keine zusätzliche Tokenownership und leaken keine Host-Commandbuchhaltung.
- Eine Favoriten-Schreibsequenz besitzt genau ein Token über Zwischenräume hinweg,
  bis sie geschlossen ist und ihr letzter tatsächlich akzeptierter Write endet.
- BeginShutdown schließt synchron Hostcommand-Admission und versiegelt den Profilkern.
  Stop wartet auf Startup, Engine-/Command- und bisherige Finalwrite-Drains, danach auf
  Profiladmission. Erst anschließend werden Profilkern abgeschlossen, Owner geschlossen
  und zuletzt die OS-Profillease freigegeben. Token werden nicht unter `_admission`
  disposed; kein synchrones Drain unter Monitoren.

## Bewusste Grenze

View-/Reading-/StartupPreview-Finalwrites behalten das bestehende getrennte,
nach BeginShutdown bis StopAsync akzeptierte und explizit drainierte Fenster.
Nicht neue Ordinary-Tokens nach Seal akquirieren. Queue-, Catalog-, Media-, Plugin-,
Cleanup- und direkte Storewriter sind damit **nicht vollständig integriert**.
P08c stellt keine produktive Exclusive-Anforderung, keine öffentliche API und
keine neue Consent-/Applyautorität bereit. GP12/13 bleiben `in_progress`.

## Enger Nachweis

Bounded synthetische Hosttests: Starting/Ready/RecoveryFailed, Stop vor Start und
während Restore ohne spätes Ready, Command hinter Semaphore und tatsächliches Drain,
FavoriteSequence-Ende, Leasefreigabe/Reacquire. Dazu vorhandene Admission-/Finalwrite-/
GalleryFavorite-/StartupPreview-Regressionsfälle und unabhängiger enger Review.
Keine persönlichen Profile/Medienfixtures oder neue native Windows/macOS-Abnahme.
