# Medien-Seitenpanels

Gilt ab MeowChan 1.1.2 für Galerie, Tagging-Galerie, Reader und Media-Detailpanel. Grundlage sind Toms Entscheidungen vom 23. November 2026 und der gleichentags erstellte UX-Audit.

- Der 32-DIP-Kopf hat links die Tabs, danach die gemeinsame Positions-Pill und rechts den Schließen-Knopf. Die Abstände betragen 8 DIP. Die Pill verwendet „No. n / m“, bei weiterer Auflösung mit „+“. Tagging zeigt die Position in der gesamten Queue. Tooltip und zugänglicher Name verwenden die Langform.
- Die Detailabschnitte folgen in der Reihenfolge Bewertung, Tags, Infos und Aktionen. Zwischen sichtbaren Abschnitten steht je eine Haarlinie. Aktionen sind 32 DIP hoch, links beschriftet und füllen die Inhaltsbreite.
- Das Bewertungsraster legt Entfernen rechts neben die Sterne, Favorit unter den ersten Stern und den Bewertungswert unter Entfernen. Das Popover verwendet dieselben Achsen mit größeren Sternen. Reader und Media arbeiten mit der Bewertung und den Tags des angezeigten Mediums.
- Das Faktenraster trennt Labels und Werte typografisch. Die Labelspalte bleibt bei höchstens 120 DIP und 40 Prozent der Rasterbreite. Unter 260 DIP Inhaltsbreite stehen Label und Wert untereinander. Dateinamen kürzen mittig. Tooltip sowie „Kopieren“ geben den vollständigen Namen aus.
- Der Schließen-Knopf verwendet in allen Panels dieselbe quadratische Darstellung und bleibt auch im Reader ohne Hover sichtbar. Die Tastaturbefehle richten sich nach dem jeweiligen Host.
- Booru hat ein eigenes laufendes Paket. Hashes und rohe Anbieterwerte bleiben in der Galerie in den explizit geöffneten Mediendetails.
