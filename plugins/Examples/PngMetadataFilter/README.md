# PNG metadata presence / PNG-Metadatenfelder

Editable SDK-only filter: `meowchan.filter.png-metadata`, filter key
`png-metadata-presence`. It uses the existing staged original, not a second
download. No HTTP, helper process, native decoder or extra package is required.

Choose `parameters`, `workflow`, `prompt` or **Custom PNG keyword**, then whether
that field must be **Present** or **Absent**. Keys are exact and case-sensitive:
`prompt` and `Prompt` are different. A key with an empty value counts as present.
Custom keys use the PNG keyword rules: 1–79 Latin-1 bytes, characters 32–126 or
161–255, no leading/trailing/consecutive spaces.

The filter scans PNG `tEXt`, `zTXt` and `iTXt` chunks through the complete `IEND`,
including text fields after image data. It validates signature, chunk bounds,
critical structure, checksums and text-field headers. Reads never exceed 64 KiB;
the existing 32 MiB original / 16 megapixel limits remain in force. It does not
decode image pixels or decompress metadata values. Presence therefore says
nothing about the validity or meaning of a compressed text value or image pixels.
Text values are never extracted, logged, persisted or interpreted as JSON.

Non-PNG originals are **Unavailable**, corrupt/incomplete PNG structure is
**Error**, cancellation remains cancellation. A technical failure is never a
claim that a field is absent. Match/NoMatch describe only this rule; the host
still owns grants, revision checks and durable byte acceptance. A match does not
prove AI generation, and absence does not prove a manually created image.

Deutsch: Der Filter prüft ausschließlich, ob ein exakt benanntes PNG-Textfeld
vorhanden ist. Groß-/Kleinschreibung zählt; auch ein leerer Wert ist vorhanden.
Komprimierte Werte werden nicht entpackt. Kein KI-Detektor, keine Inhaltsuche,
kein Tagging und keine Änderung vorhandener Archivdateien. Wie bei den anderen
Filtern fällt der Originaltransfer vor der Prüfung an. Pro effektiver Regel ist
zurzeit ein Filter wählbar; dieser Filter bildet keine automatische Filterkette.

Stable setting keys: `field` (`parameters`/`workflow`/`prompt`/`custom`),
`customKeyword`, `presence` (`present`/`absent`); settings schema `1`.

Dedicated tests: `tests/MeowChan.ExamplePlugins.Tests/PngMetadataFilterTests.cs`.
Delivery integration and packaged-worker/GUI checks belong to FE-05; a source
file or unit test alone is not a delivered-package acceptance result.
