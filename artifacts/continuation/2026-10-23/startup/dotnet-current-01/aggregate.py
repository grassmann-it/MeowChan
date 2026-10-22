#!/usr/bin/env python3
"""Aggregate completed frozen GUI outputs without executing application code."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import statistics

PHASE = re.compile(r"Plugin startup phase (\S+) candidate (.*?) elapsed ([0-9.]+) ms completed (True|False)")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--inputs", type=Path, required=True, help="Fixed group-to-frozen binding manifest")
    args = parser.parse_args()
    root = args.repo.resolve()
    integration = Path("artifacts/continuation/2026-10-23/integration")
    bindings = json.loads(args.inputs.read_text())
    references = {}

    def reference(relative):
        relative = str(relative)
        content = (root / relative).read_bytes()
        references[relative] = hashlib.sha256(content).hexdigest()
        return content

    sources = {}
    for frozen, _ in bindings.values():
        if frozen is None or frozen in sources:
            continue
        source_path = integration / frozen / "source.json"
        hashes_path = integration / frozen / "runtime-hashes.json"
        source = json.loads(reference(source_path))
        hashes = json.loads(reference(hashes_path))
        sources[frozen] = {"source": str(source_path), "sourceCommit": source["commit"],
                           "sourceStatus": source["status"], "runtimeHashes": str(hashes_path),
                           "recordedFileCount": len(hashes),
                           "recordedPrimaryHashes": {key: value for key, value in hashes.items()
                                                     if key in ("MeowChan.dll", "MeowChan.DesktopSmoke.dll", "MeowChan.PluginHost.dll",
                                                                "plugin-worker/MeowChan.PluginWorker", "plugin-compiler/toolchain.json")}}
        for relative, expected in sources[frozen]["recordedPrimaryHashes"].items():
            actual = hashlib.sha256((root / integration / frozen / "runtime" / relative).read_bytes()).hexdigest()
            assert actual == expected, str(integration / frozen / "runtime" / relative)
        sources[frozen]["primaryHashesVerifiedDuringAggregation"] = True
    for script in ("run-ui-03.py", "run-ui-03-remaining.py"):
        reference(integration / script)
    for checks in ("ui-03/checks.json", "ui-03-remaining/checks.json"):
        reference(integration / checks)
    samples = []
    for group, (frozen, binding) in bindings.items():
        result_path = integration / group / "results.json"
        result = json.loads(reference(result_path))
        for mode in result["results"]:
            log_path = integration / group / mode["mode"] / "output.log"
            log = reference(log_path).decode("utf-8", errors="replace")
            phases = []
            for number, line in enumerate(log.splitlines(), start=1):
                match = PHASE.search(line)
                if match:
                    phase, candidate, elapsed, complete = match.groups()
                    phases.append({"phase": phase, "candidate": None if candidate == "null" else candidate,
                                   "elapsedMs": float(elapsed), "completed": complete == "True", "line": number})
            totals = [phase for phase in phases if phase["phase"] == "total"]
            imports = [phase for phase in phases if phase["phase"] == "import"]
            describes = [phase for phase in phases if phase["phase"] == "describe"]
            assert len(totals) == 1 and len(imports) == 5 and len(describes) == 5, str(log_path)
            assert len({p["candidate"] for p in imports}) == 5, str(log_path)
            assert {p["candidate"] for p in imports} == {p["candidate"] for p in describes}, str(log_path)
            assert all(phase["completed"] for phase in phases), str(log_path)
            ready = "SMOKE desktop session startup completed; state=Ready." in log
            assert ready, str(log_path)
            profile = re.search(r"SMOKE isolated profile: (.+)", log)
            assert profile, str(log_path)
            total = totals[0]["elapsedMs"]
            summed = sum(phase["elapsedMs"] for phase in phases if phase["phase"] != "total")
            samples.append({"group": group, "mode": mode["mode"], "scale": result["requested_scale_factor"],
                            "frozen": frozen, "bindingEvidence": binding, "results": str(result_path), "log": str(log_path),
                            "overallGroupPassed": result["passed"], "overallPassed": mode["passed"], "overallExitCode": mode["exit_code"],
                            "startupReady": ready, "privateProfile": profile.group(1), "phases": phases,
                            "pluginStartupTotalMs": total, "importSumMs": sum(p["elapsedMs"] for p in imports),
                            "describeSumMs": sum(p["elapsedMs"] for p in describes),
                            "unattributedMs": total-summed})
    assert len({sample["privateProfile"] for sample in samples}) == len(samples)
    summaries = {}
    for label, selected in (("successfulGuiRunsWithExactRuntime", [s for s in samples if s["overallPassed"] and s["frozen"] is not None]),
                            ("failedGuiRunsWithReadyStartupAndExactRuntime", [s for s in samples if not s["overallPassed"] and s["frozen"] is not None]),
                            ("successfulGuiRunsWithoutExactRuntime", [s for s in samples if s["frozen"] is None])):
        summaries[label] = {"samples": len(selected)}
        if not selected:
            continue
        for key in ("pluginStartupTotalMs", "importSumMs", "describeSumMs", "unattributedMs"):
            values = [sample[key] for sample in selected]
            summaries[label][key] = {"min": min(values), "median": statistics.median(values), "max": max(values)}
    report = {"measurement": "Actual .NET PluginSession debug phases from frozen source-built Linux GUI harness",
              "notPublishedApphostEvidence": True, "notSpeedupEvidence": True, "fileSystemCacheControlled": False,
              "loadControlled": False, "newGuiOrBuildExecuted": False,
              "inputManifestSha256": hashlib.sha256(args.inputs.read_bytes()).hexdigest(),
              "sources": sources, "referencesSha256": references, "samples": samples, "summaries": summaries}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "RESULT.json").write_text(json.dumps(report, indent=2) + "\n")
    lines = ["# Aktuelle .NET-PluginSession: beobachtete Startphasen", "",
             "Read-only-Auswertung vorhandener gefrorener Linux-GUIläufe; kein neuer Build, Test oder GUIstart.",
             "Gemessen ist PluginSession `total`, nicht Prozessstart bis zum fertigen Desktop. Jede Zeile hat fünf Imports, fünf Describe-Phasen und danach Startup `Ready`.", "",
             "## Eindeutig gebundene, erfolgreiche Gesamtläufe", "",
             "| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |",
             "|---|---|---:|---:|---:|---:|"]

    def row(sample):
        return (f"| {sample['group']} | {sample['mode']} | {sample['scale']} | "
                f"{sample['pluginStartupTotalMs']/1000:.3f} s | {sample['importSumMs']/1000:.3f} s | "
                f"{sample['describeSumMs']/1000:.3f} s |")

    lines += [row(s) for s in samples if s["overallPassed"] and s["frozen"] is not None]
    summary = summaries["successfulGuiRunsWithExactRuntime"]
    total_stats = summary["pluginStartupTotalMs"]
    lines += ["", f"{summary['samples']} erfolgreiche, exakt gebundene Starts: Minimum {total_stats['min']/1000:.3f} s, "
              f"Median {total_stats['median']/1000:.3f} s, Maximum {total_stats['max']/1000:.3f} s.", "",
              "## Gesamtprüfung fehlgeschlagen, Startup separat Ready", "",
              "Die folgende Zeile gehört zu einem mit Exit 1 fehlgeschlagenen refinement-Gesamtlauf. Sie wird nicht in die obigen Erfolgsstatistiken gemischt.", "",
              "| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |",
              "|---|---|---:|---:|---:|---:|"]
    lines += [row(s) for s in samples if not s["overallPassed"] and s["frozen"] is not None]
    lines += ["", "## Unbekannte genaue Runtimezuordnung", "",
              "ui-reader-board-02 hat erfolgreiche Modeergebnisse, aber keine erhaltene eindeutige Invocation/Runtimebindung. Diese Werte stehen ausschließlich hier und sind aus dem Hauptvergleich ausgeschlossen. ui-02/source.json darf ihnen nicht nachträglich zugeschrieben werden.", "",
              "| Gruppe | Modus | Skalierung | PluginSession gesamt | Imports zusammen | Describe zusammen |",
              "|---|---|---:|---:|---:|---:|"]
    lines += [row(s) for s in samples if s["frozen"] is None]
    lines += ["", "## Bindung und Reproduktion", "",
              "Die exakt gebundenen Läufe verwenden `integration/ui-03/runtime`, Quellsnapshot `04e676d9c76722fd31a70d271db5d107f3212f7f`. Maßgeblich sind `artifacts/continuation/2026-10-23/integration/ui-03/source.json` einschließlich dort aufgeführtem WIP und `runtime-hashes.json` mit 1166 Einträgen. Die Hauptassemblies, Workerapphost und Toolchainmanifest wurden während der Aggregation gegen die aufgezeichneten Hashes geprüft; keine erneute Gesamtvalidierung aller 1166 Runtimeeinträge.", "",
              "`run-ui-03.py`/`ui-03/checks.json` binden refinement; `run-ui-03-remaining.py`/`ui-03-remaining/checks.json` binden die übrigen ui-03-Gruppen an denselben eingefrorenen Pfad. Modebezogene Status/Exitcodes kommen aus den jeweiligen `results.json`. RESULT.json enthält exakte Referenzpfade und SHA256 für alle ausgewerteten Logs, Ergebnisdateien, Quellen-/Hashmanifeste und Runner; jede Phase trägt ihre ursprüngliche Logzeile.", "",
              "```sh",
              "python3 artifacts/continuation/2026-10-23/startup/dotnet-current-01/aggregate.py --repo /home/tomka/Desktop/oChan --inputs artifacts/continuation/2026-10-23/startup/dotnet-current-01/inputs.json --output artifacts/continuation/2026-10-23/startup/dotnet-current-01",
              "```", "",
              "`inputs.json` fixiert die ausgewerteten, fertig gemeldeten Gruppen. Neuere oder noch laufende Gruppen werden nicht automatisch in den Nachweis aufgenommen. Die in RESULT.json erfassten Hashes der wachsenden ui-03-remaining/checks.json gelten für den Aggregationszeitpunkt; bei späterem erneuten Ausführen können dort zusätzliche Prüfungen stehen. Die unveränderten Mode-Logs/Ergebnisse und die feste Eingabeauswahl bestimmen die Zahlen.", "",
              "## Aussagegrenzen", "",
              "Die Logs zeigen je Mode einen eigenen privaten Profilpfad; alle erfassten Pfade sind verschieden. Das sind frische Profile und keine Cache-Restarts desselben Profils. Der Dateisystemcache ist durch vorherige Builds und Läufe möglicherweise warm; er wurde nicht geleert. Systemlast und CPU-/I/O-Konkurrenz sind variabel und wurden hier nicht aufgezeichnet oder kontrolliert. Skalierungen und GUIzwecke sind keine gleichartigen Benchmarkwiederholungen.", "",
              "`import` umfasst die vollständige bestehende Importarbeit einschließlich Toolchain-, Bundle- und Cachevalidierung; es ist kein isoliertes .NET-SHA256-Maß. `describe` umfasst Host-Artefaktprüfung, Ressourcen-/Startgates, Workerstart, hello/describe, SDK-Validierung und vollständiges await-Dispose. Daraus lässt sich keine einzelne Unterursache des variablen Zeitbedarfs ableiten. `PluginStartupCompleted=True` bezeichnet normalen Awaitabschluss; zusätzliches Ready und der Mode-Gesamtstatus werden separat ausgewertet.", "",
              "Die älteren 8–12 Sekunden bleiben ein eigenständiger früherer Messbefund unter anderen Bedingungen. Diese neuen Werte erklären oder widerlegen ihn nicht und belegen keinen Speedup durch die reine Instrumentierung. Sie liefern keine Plattform-, Cold-Cache-, Last- oder p95/p99-Garantie und sind kein SDKfreier publizierter Apphostnachweis.", ""]
    (args.output / "REPORT.md").write_text("\n".join(lines))
    print(json.dumps(summaries, indent=2))


if __name__ == "__main__":
    main()
