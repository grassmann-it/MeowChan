# MeowChan 1.2.0 veröffentlichen

Der Release-Kandidat und die Veröffentlichung verwenden dieselben neun
Anwendungspakete und `MeowChan-1.2.0-current-video-sources.7z`. Die drei
Entwicklerpakete im Kandidaten gehören zum separaten NuGet-Veröffentlichungsweg.
Der Desktop-Draft enthält zehn Assets und `SHA256SUMS`.

Auf Linux in derselben Bash arbeiten. Benötigt werden `gh`, Git, Python ≥ 3.11,
`jq`, `sha256sum`, `7z` und freier Speicher für den Kandidaten, das Staging
und das entpackte Quellarchiv.

## 1. Freigegebenen SHA und Kandidatenlauf binden

Im Checkout des freigegebenen Release-Commits ausführen. Der Commit muss über
den angegebenen Ref auf GitHub erreichbar sein.

```bash
set -euo pipefail
export GH_REPO=grassmann-it/MeowChan
export SHA='<vollständiger freigegebener SHA>' BR=1.2.0 V=1.2.0 TAG=v1.2.0
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]]
test "$(git rev-parse HEAD)" = "$SHA"
test "$(git ls-remote --exit-code origin "refs/heads/$BR" | cut -f1)" = "$SHA"
git show "$SHA:Directory.Build.props" | grep -F '<Version>1.2.0</Version>'
export W
W=$(mktemp -d "${TMPDIR:-/tmp}/meowchan-120.XXXXXXXX")
gh workflow run dotnet-desktop.yml --ref "$BR" -f mode=stable-candidate \
  -f platform=all -f scenario=all | tee "$W/dispatch.txt"
```

Den Run anhand der Dispatch-Ausgabe oder mit
`gh run list -w dotnet-desktop.yml --branch "$BR" --commit "$SHA" --event workflow_dispatch`
zuordnen. Bei Wiederaufnahme das gespeicherte Arbeitsverzeichnis und dieselbe
Run-ID verwenden.

```bash
export RUN='<ID des stable-candidate-Laufs>'
[[ "$RUN" =~ ^[1-9][0-9]*$ ]]
printf '%s\n' "$SHA" "$RUN" > "$W/run-ids.txt"
gh run watch "$RUN" --interval 30 --compact --exit-status
gh run view "$RUN" --json headSha,status,conclusion,jobs > "$W/run.json"
python3 scripts/aggregate-release-gates.py run --json-file "$W/run.json" \
  --expected-sha "$SHA" --required-job 'Release candidate gate' --report "$W/gate.json"
gh run download "$RUN" --name "stable-candidate-$SHA" --dir "$W/candidate"
```

## 2. Lokal und offline prüfen

```bash
python3 scripts/assemble-stable-release.py --candidate-dir "$W/candidate" \
  --candidate-run-id "$RUN" --source-commit "$SHA" --version "$V" --output "$W/staged"
(cd "$W/staged" && sha256sum --check --strict SHA256SUMS)
export SRC="MeowChan-$V-current-video-sources.7z"
mkdir "$W/extracted"
(cd "$W/extracted" && 7z x -y "$W/staged/$SRC")
jq -e --arg sha "$SHA" --arg version "$V" \
  '.source_commit == $sha and .version == $version' "$W/extracted/manifest.json"
jq -r '.runtimes | to_entries[] | .key as $rid | .value.packages[] |
  [$rid, .file, .bytes, .sha256] | @tsv' "$W/extracted/manifest.json"
```

Der Assembler gleicht die Binärpakete, das Archiv und die Prüfsummen mit dem
Run-Inventar ab. Er entpackt die Quellen in ein frisches Verzeichnis und prüft
jede Quellreferenz gegen die Paket- und RID-Inventare. Fehlende Patches,
abweichende Architekturen, falsche Paket-Hashes und zusätzliche Quellassets
stoppen das Staging. Der Auszug aus `manifest.json` listet die Bindung der neun
Pakete. Die Quellen und Referenzen lassen sich aus dem einen Archiv öffnen.
[Archivvertrag und Größenmessung](../native-video-archive.md)

Die öffentlichen Namen entsprechen dem Staging:

| Plattform | Assets |
| --- | --- |
| Windows | `MeowChan-1.2.0-win-x64.zip` |
| macOS | `MeowChan-1.2.0-osx-arm64.dmg`, `MeowChan-1.2.0-osx-x64.dmg` |
| Linux x64 | `MeowChan-1.2.0-linux-x64.AppImage`, `MeowChan-1.2.0-linux-x64-bundled-vlc.tar.gz`, `MeowChan-1.2.0-linux-x64-system-vlc.tar.gz` |
| Linux ARM64 | `MeowChan-1.2.0-linux-arm64.AppImage`, `MeowChan-1.2.0-linux-arm64-bundled-vlc.tar.gz`, `MeowChan-1.2.0-linux-arm64-system-vlc.tar.gz` |
| Quellen | `MeowChan-1.2.0-current-video-sources.7z` |
| Prüfsummen | `SHA256SUMS` |

## 3. Draft erstellen und geprüfte Bytes laden

Den freigegebenen bilingualen Release-Body unter `$W/body.md` ablegen. Die
Downloadlinks müssen auf die Namen aus der Tabelle zeigen. Validierungsstatus
und manuelle Plattformnachweise am konkreten Run festmachen.

```bash
test "$(git ls-remote --exit-code origin "refs/heads/$BR" | cut -f1)" = "$SHA"
gh release create "$TAG" --target "$SHA" --draft \
  --title 'MeowChan 1.2.0 🐾' --notes-file "$W/body.md"
export DRAFT_ID
DRAFT_ID=$(gh release view "$TAG" --json databaseId,tagName,targetCommitish,isDraft,isPrerelease |
  jq -er --arg sha "$SHA" --arg tag "$TAG" \
    'select(.isDraft and (.isPrerelease | not) and .tagName == $tag and .targetCommitish == $sha) | .databaseId')
printf '%s\n' "$DRAFT_ID" > "$W/draft-id"
gh workflow run upload-verified-release.yml --ref "$BR" \
  -f version="$V" -f source_run="$RUN" -f source_commit="$SHA" -f release_id="$DRAFT_ID"
```

Bei einem vorhandenen Draft Zeilen ab `DRAFT_ID=$(…)` ausführen und die ID mit
`$W/draft-id` vergleichen. Der Uploadworkflow verifiziert dieselbe
Kandidaten-Run-ID und übernimmt die Archivbytes aus dem Kandidaten. Nach dem
Upload den erfolgreichen Uploadlauf und dessen Prüfsummenbeleg festhalten.

## 4. Remote-Inventar vergleichen und Draft freigeben

```bash
gh api "repos/$GH_REPO/releases/$DRAFT_ID" > "$W/remote.json"
jq -e --arg sha "$SHA" --arg tag "$TAG" \
  '.draft and (.prerelease | not) and .tag_name == $tag and .target_commitish == $sha' "$W/remote.json"
(
  cd "$W/staged"
  sha256sum --check --strict SHA256SUMS
  for asset in *; do
    printf '%s\t%s\tsha256:%s\n' "$asset" "$(stat -c %s "$asset")" "$(sha256sum "$asset" | cut -d' ' -f1)"
  done | LC_ALL=C sort
) > "$W/local.tsv"
jq -r '.assets[] | select(.state == "uploaded") | [.name, .size, .digest] | @tsv' "$W/remote.json" |
  LC_ALL=C sort > "$W/remote.tsv"
diff -u "$W/local.tsv" "$W/remote.tsv"
```

Tom entscheidet über die Veröffentlichung nach Kontrolle des Drafts.
Anschließend öffentliche Dateinamen und Digests erneut abgleichen.
