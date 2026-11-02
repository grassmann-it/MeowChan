# GP12/13: B-eigener inaktiver Medienkandidat v1

Enger technischer Producer unter Profil A/G4/G5, kein vollständiger Import. Root
implementiert `MediaStore.PassiveImport.cs`; disjunkte Testautoren und ein
unabhängiger Astrareview. Die bestehende passive Repräsentation bleibt gültig.

## API und Datenhoheit

`MediaStore.MaterializePassiveImportCandidateAsync(stage, resource, revisionKeys,
candidateRoot, operationId, importedAtUtc, cancellationToken, checkpoint)` arbeitet
nur mit tatsächlich im validierten, eigenen Stage enthaltenen Originalanhängen.
Die Auswahl ist eine nichtleere Menge exakter Attachmentkeys. Legacy-nullkeys
werden aus Resource/Post/Position/Provider abgeleitet, nicht als aktive Beobachtung
umklassifiziert. Retained Bindings erfinden keine Posts. Previewrollen bleiben
abgewiesen; unbekannte/mehrdeutige/inkonsistente Auswahl wird vor Dateierzeugung
abgewiesen. Keine konstruierten Bindings vom Caller annehmen.

`candidateRoot` muss bereits existieren, kanonisch absolut/linkfrei und vom künftigen
Profilowner gewählt sein. Es ist **kein Paketpfad**. Darin entsteht ein exklusiv
neu angelegtes, zufälliges `.meowchan-passive-…`-Verzeichnis. Keine Mutation alter
Medienwurzeln, kein Register/Capture/SQL/Favoriten/Grant. Alle resultierenden
Bindings sind passiv. Diese Wurzel bleibt inaktiv; ein Ready-Receipt ist keine
Katalog-/Publikationsautorität. Insbesondere darf ein kompletter Konfliktkandidat
nicht ungeprüft als zusätzliche aktive Resolverwurzel registriert werden.

## Schreiben und Wiederanlauf

Die neue Candidate-Directory-Lease schützt den einzelnen Producer, nicht sämtliche
Aufrufe unter dem Parent. Rootweite Idempotenz/Discovery übernimmt erst der spätere
Koordinator; ein unbekannter Ausgang ist vor Wiederholung zu inspizieren. Journal1 enthält lokale Operation,
Paketfingerprint, Resource, Importzeit, sortierte Keys, Phase und bei Ready den
SHA256 der exakten Manifestbytes. Keine fremden Pfade/Erwerbsrechte.

1. Journal `copying` create-only schreiben und Datei flush-to-disk.
2. Je ausgewählter Payload eigene `.part` create-only schreiben; Stage misst am
   eigenen Handle Länge/SHA256/MD5/SHA1/CRC erneut. Datei flush-to-disk, anschließend
   no-overwrite Rename. Endnamen nur lokale Hashes + `original.bin`, nie Paketnamen.
3. Manifest5 aus diesen gemessenen Originalen und passiven exakten Bindings
   schreiben. Zielbytes danach eigenständig erneut messen (je physischem Payload
   genau einmal pro Prüfung, nicht einmal pro Binding). Vorhandene aktive Prüfregeln bleiben unverändert.
4. Ready-Journal in temporärer eigener Datei schreiben/flushen, atomar ersetzen.
   Danach Erfolg; eine spätere Cancellation darf diesen Erfolg nicht umdeuten.

Dateien bleiben bei Fehler/Cancel inaktiv erhalten; kein rekursives Cleanup und
kein Delete eigener/alter Originale. Caller kann unbekannten Ausgang anhand des
neu angelegten Kandidaten inspizieren. Die Testcheckpoint-Action beobachtet nur
reale Phasengrenzen und kann Fehler injizieren, nicht Messungen ersetzen.

`InspectPassiveImportCandidateAsync(directory)` öffnet ausschließlich bestehende
Lease/Journal/Manifest und schreibt nichts. Es prüft Journal strikt/begrenzt,
exakte Bindingauswahl/Provenienz, Manifestgeneration und alle realen Bytes. Nur
Ready+vollständig verifiziert ergibt Ready. Eine gehaltene Kooperations-Lease
liefert nach begrenzter Wartezeit Busy statt Invalid, Cancellation wird propagiert.
Copying liefert nur bei konsistentem begrenztem Zwischenstand Incomplete: alle
Verzeichniseinträge müssen reguläre Dateien sein, temporäre Dateien tragen nur die
eigenen GUID-Namensformen für Payload/Journal/Manifest. Deren unvollständige Bytes
sind ausdrücklich keine fertigen Daten und werden nicht als solche interpretiert.
Finale Hash-Payloaddateien werden auch ohne Manifest gegen ihren Namen gemessen;
ein vorhandenes vollständiges Manifest wird gegen Operation/Keyset/Provenienz und
alle Zielbytes geprüft. Vor-/Nachstamps begleiten jeden realen Hashlauf. Copying
wird auch dann niemals Ready. Beschädigte, fremde, unerwartete Dateien/Reparse-Points
oder unpassende Manifeste ergeben Invalid; keine automatische Reparatur,
Resume-Publication oder Löschung.

Journal1 ist unknown-field-abweisend. Manifest5 behält den bestehenden gemeinsamen
vorwärtskompatiblen Leser (unbekannte Felder können ignoriert werden), lehnt aber
doppelte JSONfelder, falsche Generation/Bindungen und Bytes ab; Ready bindet die
exakten Manifestbytes per SHA256. Kein neuer unabhängiger Manifestparser. Die lokale
Importzeit ist ein DateTimeOffset ohne zusätzliche Uhrzeit-Plausibilitätsbehauptung;
sie ist keine Quellenfreigabe.

Das sind Prozessabbruch-/Offline-Wiederanlaufgrenzen mit echten Flush/Rename, kein
plattformübergreifendes Hardware-Powerloss-/Directory-fsync-Versprechen und keine
Sandbox gegen fremde Prozesse, die Betriebssystem-Dateileases ignorieren.

## Unveränderte Restgates

Noch nötig: gemeinsames FS/Manifest/SQL-Journal mit Receipts/Fences gegen
Capture/Delete-Recreate/Umzug/Cleanup/Zweitimport/Shutdown, aktive konfliktfreie
Teilmenge, additive Attachmentmutation, echte Konfliktauflösung/Verwerfen sowie
A1–20+B15–30/404=>B1–30 mit Favoriten/Neustart/Desktop. Dieser Producer ersetzt
keines dieser Gates und erklärt GP12/13 nicht für fertig.


### Dateitypen vor dem Öffnen

Die Candidateinspektion prüft Lease, Journal, Manifest und Payloads vor dem Öffnen
positiv auf einen regulären Dateityp. FileAttributes allein unterscheidet unter
Unix keine FIFOs/Sockets/Devices. Dafür nutzt der interne net10-Producer die
normalisierte `System.Native`-PAL-`LStat`-ABI statt einer architekturspezifischen
libc-stat-Struktur oder eines Shellprogramms. Fehlende Runtimefunktion wird
fail-closed abgewiesen. Windows behält seine normale Attribute-/Reparseprüfung.
Die ABIdeklaration folgt dem MIT-lizenzierten .NET-Runtimequellcode:
[Interop.Stat.cs](https://github.com/dotnet/runtime/blob/v10.0.1/src/libraries/Common/src/Interop/Unix/System.Native/Interop.Stat.cs)
und [pal_io.h](https://github.com/dotnet/runtime/blob/v10.0.1/src/native/libs/System.Native/pal_io.h).
Native macOS-/Windowspaketabnahme bleibt separat nötig. Diese lokale Typprüfung
ist keine Sandbox gegen einen fremden Prozess, der zwischen Prüfung und Öffnen
die Datei austauscht; nur kooperierende Writer sind durch die Lease serialisiert.
