#!/usr/bin/env python3
"""Regenerate advisory class weights from saved TRX and optional partial console logs.

Inputs are LABEL=PATH; labels should identify run/artifact/platform. No network,
execution, or coverage inference: even failed test durations are useful scheduling
observations, never acceptance evidence. Partial console sums are lower bounds,
not extrapolated completion times. Review/commit the output with its provenance.
"""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import xml.etree.ElementTree as ET


def trx_observations(path):
    root = ET.parse(path).getroot()
    definitions = {}
    for test in root.findall('./{*}TestDefinitions/{*}UnitTest'):
        method = test.find('./{*}TestMethod')
        definitions[test.get('id')] = method.get('className')
    elapsed, identities = Counter(), {}
    for result in root.findall('./{*}Results/{*}UnitTestResult'):
        identity = result.get('testId')
        name = definitions[identity]
        hours, minutes, seconds = result.get('duration', '0:0:0').split(':')
        ms = math.ceil((int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000)
        if ms < 0:
            raise ValueError('Negative duration')
        elapsed[name] += ms
        identities.setdefault(name, set()).add(identity)
    if not elapsed:
        raise ValueError('No TRX results')
    return elapsed, {name: len(ids) for name, ids in identities.items()}


def console_observations(path):
    # Display arguments are deliberately opaque: only the method prefix and final
    # elapsed suffix matter. These observations NEVER establish test identities.
    elapsed = Counter()
    pattern = r'^  (?:Passed|Failed) ([\w.+`]+)(?:\(.*\))? \[<?([\d.]+) (ms|s)\]$'
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        match = re.match(pattern, line)
        if match:
            elapsed[match[1].rsplit('.', 1)[0]] += math.ceil(float(match[2]) * (1000 if match[3] == 's' else 1))
    if not elapsed:
        raise ValueError('No console durations')
    return elapsed


def generate(trx, consoles):
    classes, sources = {}, []
    for kind, inputs in (('trx', trx), ('partial-console', consoles)):
        for label, path in sorted(inputs):
            elapsed, counts = trx_observations(path) if kind == 'trx' else (console_observations(path), {})
            sources.append(dict(label=label, kind=kind, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                                classes=len(elapsed), observed_ms=sum(elapsed.values())))
            for name, ms in elapsed.items():
                entry = classes.setdefault(name, dict(milliseconds=1, cases=0))
                entry['milliseconds'] = max(entry['milliseconds'], ms)
                entry['cases'] = max(entry['cases'], counts.get(name, 0))
    # A partial-only class cannot supply a baseline case count; fail rather than
    # pretending the partial log is a complete inventory.
    if not classes or any(not c['cases'] for c in classes.values()):
        raise ValueError('Every class needs TRX identity counts')
    fallback = max(1, math.ceil(statistics.median(c['milliseconds'] / c['cases'] for c in classes.values())))
    return dict(schema=1, algorithm='class-lpt-ms-v1', fallback_case_ms=fallback,
                sources=sources, classes=classes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trx', action='append', default=[], metavar='LABEL=PATH')
    parser.add_argument('--partial-console', action='append', default=[], metavar='LABEL=PATH')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    def inputs(values):
        return [(label, Path(path)) for label, path in (v.split('=', 1) for v in values)]
    result = generate(inputs(args.trx), inputs(args.partial_console))
    args.output.write_text(json.dumps(result, indent=2, sort_keys=True) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
