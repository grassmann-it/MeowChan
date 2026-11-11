# MeowChan 1.1.0 — beschlossener nächster Release, noch nicht releasefertig

## Nutzerentscheidung (11.11.2026)

**Das nächste Release wird 1.1.0.** Die frühere Wahl zwischen einer sehr
featurelastigen 1.0.3 und 1.1.0 ist damit entschieden. Es gibt bereits eine gute
Featurebasis für 1.1.0; daraus folgt ausdrücklich keine abgeschlossene Umsetzung
oder Releasefreigabe. Der Nutzer weist auf weiterhin offene Arbeit hin.

Diese Entscheidung ändert weder automatisch die Produktversion noch erzeugt sie
einen Tag oder ein Release. Der aktive Auftrag verbietet weiterhin Push, Upload,
Remote-CI, Tag und Veröffentlichung.

## Was die Basis trägt

Im Integrationsbranch liegen unter anderem die lokale Medienseite mit Filtern,
Auswahl-/Export- und Rückwegen, Archiv-/Reader-Verbesserungen, Warmgrau/Orange-Light,
Dialog-/Header-Arbeit und mehrere technische Härtungen aus der Claude-Übergabe.
Der optionale Plugin-Sammel-Submit ist als `83174f54` integriert: nur ausdrücklich
angehakte Neufreigaben, serielle Writes, Stopp beim ersten Fehler. Dazu liegen
521 gezielte Tests und ein privater Xvfb-Lauf vor. Das ist Teilbeleg, keine
Abnahme des gesamten Releases.

## Sichtbare Restarbeit und Gates

- **Lose/alte Medien tatsächlich taggbar machen:** lokale Identität, Persistenz,
  Consumer/Host, gemeinsame Rechtsklickmenüs auf allen Flächen, korrekte Ziele
  und zwei Offline-Neustarts. Alte Workerlieferungen sind noch nicht integriert;
  offene Identitätsfragen werden nicht durch einen deaktivierten Eintrag ersetzt.
- **Plugin-Lokalisierung nachziehen:** der Volltest auf `83174f54` findet fehlende
  `plugins.approval.*`-Einträge in ES/FR/RU. Die vorherigen gezielten Tests haben
  diesen Vollständigkeitsfall nicht abgedeckt.
- **Echtes Upgrade von veröffentlichtem 1.0.2:** exakter Quellstand wurde offline
  exportiert, geprüft und gebaut. Der synthetische Generator scheitert derzeit
  an der Settings-Admisson, bevor ein gültiges Ausgangsprofil vorliegt. Damit
  gibt es noch keinen Upgrade-/Zwei-Neustart-PASS. Kein echtes Nutzerprofil benutzt.
- **Privater Transfer:** Consent-/UI-/Host-/Apply-/Merge-/Recovery-Kette und die
  Grenze neuer lokaler Medienidentitäten weiter klären und end-to-end belegen.
- **Branding:** gewähltes Motiv und Prepare-only-Konverter vorhanden; finale
  Liefergrenze, Herkunft/Rechte, Produktübernahme und visuelle Kleingrößenprüfung
  bleiben getrennte offene Gates.
- **UX/Startup:** First-Frame-/Tray-Messung, passive Gates, Filterwahrheit sowie
  restliche Reader-/Medien-/Dialog-/Accessibility-Abnahme aus CX-00–10.
- **Claude-WIP und Integrationsbranches:** Filtereingabe, Statusoverlay, Journal-
  Bereinigung, Smoke-Registry, Video-Hotpath sowie Booru/Stabilität sind anhand
  ihrer jeweiligen Übergabe separat abzugleichen, zu prüfen und kontrolliert
  zu integrieren; ihre Existenz ist kein integrierter oder geprüfter Zustand.
- **API und Abschluss:** Unshipped-/Kompatibilitätsreview, gemeinsame Freeze-
  Abnahme, unabhängige Gegenreviews, Dokumentation und ehrliche Fehlermatrix.
- **Nutzer-/Plattformabnahme:** Windows, macOS, Hardware, 150 %, Screenreader und
  Distributionspaketierung werden nicht durch Linux-Unit- oder Xvfb-Tests ersetzt.

## Aktueller Abnahme-Freeze

Die ausdrücklich beauftragten Abschnitte 0 und 4 von
`.claude/coordination/HANDOFF-CODEX.md` werden im isolierten Worktree auf
`83174f54` geprüft. Der Hauptcheckout bleibt vom Testbuild getrennt; fremder WIP
bleibt erhalten. Mehrere schwere Jobs sind inzwischen ausdrücklich erlaubt;
`nice 19`, private GUI-Sitzungen, temporäres HOME und getrennte Outputs gelten weiter.

Bereits beobachtet: Debug-Solution 0 Fehler/90 Warnungen; Core-Vollsuite
9523 bestanden/0 fehlgeschlagen/11 übersprungen; Desktop-Vollsuite
3262 bestanden/12 fehlgeschlagen/12 übersprungen; drei Python-/Medienvertragssuiten
46 bestanden. Weitere Plugin-/GUI-Abnahmen laufen bzw. sind noch auszuwerten.
Die vier GUI-Driver-Versuche scheiterten zunächst vor Ausführung an der noch nicht
separat gebauten DesktopSmoke-Assembly; das ist kein Produktfehler und kein PASS.
Der echte Apphost-Start im privaten Xvfb hat separat bestanden.

Belege: `/home/tomka/mc-evidence/ux103-restpakete-20261111/acceptance-83174f54/`.
Die älteren [1.0.3-Gate-Belege](release-1.0.3-closure-gate-2026-11-11.md) bleiben
historische, commitgebundene Evidenz; ihre Aussage „nur Plattformbelege offen“
charakterisiert nicht den erweiterten aktuellen 1.1.0-Umfang.
