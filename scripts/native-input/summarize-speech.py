"""Require real Orca utterances in independently recorded native focus intervals."""
import datetime
import json
from pathlib import Path
import re
import sys

output = Path(sys.argv[1])
lines = (output / 'orca-debug.log').read_text(errors='replace').splitlines()
speech = []
for line in lines:
    match = re.match(r'(\d\d):(\d\d):(\d\d\.\d+) - SPEECH OUTPUT:', line)
    if match:
        speech.append((int(match[1]) * 3600 + int(match[2]) * 60 + float(match[3]), line))


def seconds(stamp):
    date = datetime.datetime.fromtimestamp(stamp)
    return date.hour * 3600 + date.minute * 60 + date.second + date.microsecond / 1e6


results = []
for path in sorted(output.glob('*-focus.json')):
    probe = json.loads(path.read_text())
    name = probe['target']['name'] if probe['target'] else ''
    start, end = seconds(probe['start']), seconds(probe['end'])
    utterances = [line for clock, line in speech
                 if (start <= clock <= end if end >= start else clock >= start or clock <= end)]
    results.append({
        'probe': probe['stage'], 'focus': bool(probe['target'] and 'focused' in probe['target']['states']),
        'name': name, 'direct_exact_name_spoken': bool(name) and any(
            'SPEECH OUTPUT: ' + repr(name) in line for line in utterances),
        'speech': utterances,
    })
report = {
    'scope': 'Actual Orca SPEECH OUTPUT logs, correlated by focus time and exact accessible name. Audio disabled; no human hearing or IME acceptance.',
    'focus_intervals': len(results), 'actual_focus': sum(row['focus'] for row in results),
    'direct_exact_name_spoken': sum(row['direct_exact_name_spoken'] for row in results),
    'passed': bool(results) and all(row['focus'] and row['direct_exact_name_spoken'] for row in results),
    'probes': results,
}
(output / 'speech-summary.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n')
print(json.dumps({key: value for key, value in report.items() if key != 'probes'}))
raise SystemExit(0 if report['passed'] else 1)
