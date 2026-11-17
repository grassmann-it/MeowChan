# Paket 6A – Localization-Befund

## Ergebnis

Die Prüfung von `Desktop/Localization/PluralRules.cs` bestätigt, dass der Resolver
für ganze Zahlen folgende Kategorien anfordert:

- Russisch (`ru`): `one`, `few`, `many`
- Polnisch (`pl`): `one`, `few`, `many`
- Tschechisch (`cs`): `one`, `few`, `other`

Der Befund ist daher kein Resolver-Falsch-Positiv. Fehlende Varianten würden auf
die Basissprache zurückfallen, wenn sie nicht im jeweiligen Pack vorhanden sind.

## Reproduzierbare Prüfung

Ausgeführt im Repository-Root:

```sh
python3 - <<'PY'
import json
from pathlib import Path

for language in ["ru", "cs", "pl"]:
    strings = json.loads(
        Path(f"Desktop/Localization/Packs/{language}.json").read_text()
    )["strings"]
    categories = {
        "ru": ("few", "many"),
        "pl": ("few", "many"),
        "cs": ("few",),
    }[language]
    bases = {
        key[:-4]
        for key in strings
        if key.endswith(".one") and key[:-4] in strings
    }
    missing = [
        f"{base}.{category}"
        for base in sorted(bases)
        for category in categories
        if f"{base}.{category}" not in strings
    ]
    print(language, len(missing))
PY
```

Ergebnis auf diesem Stand:

```text
ru 195
cs 66
pl 172
```

Die `cs`-Zahl entspricht dem Auftrag. Die ru/pl-Zahlen enthalten alle fehlenden
Varianten aus den vorhandenen pluralen Pack-Familien; der Review-Fixture-Befund
muss für die endgültige Schlüsselmenge weiterhin gegen den autoritativen
`prechecks.json`-Datensatz abgeglichen werden.

## Blocker

Es wurden keine Übersetzungen ergänzt. Die Sprachpakete sind als `Untested`
markiert, und der Auftrag verbietet maschinelle Übersetzungen ausdrücklich. Für
einen belastbaren Änderungs-PR fehlen daher autorisierte Übersetzungen für die
betroffenen `few`/`many`-Formen. Ein pauschaler Resolver-Fallback auf `other`
wäre nicht korrekt, weil `ru` und `pl` für ganze Zahlen weiterhin echte `few`-
und `many`-Kategorien anfordern.
