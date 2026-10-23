#!/usr/bin/env python3
"""Analyze real host evidence. Never infers human/AT success from harness timing."""
import argparse
import collections
import json
import math
import pathlib
import statistics


def summary(values):
    values = sorted(values)
    return {'n': len(values), 'median': statistics.median(values),
            'p95': values[math.ceil(.95 * len(values)) - 1],
            'min': values[0], 'max': values[-1],
            'mad': statistics.median(abs(x - statistics.median(values)) for x in values)}


def analyze(root):
    rows = json.loads((root / 'samples.json').read_text())
    checks = json.loads((root / 'checks.json').read_text())
    metadata = json.loads((root / 'metadata.json').read_text())
    checks_only = metadata['options']['ChecksOnly']
    empty = [r for r in rows if r['kind'] == 'empty-host']
    errors = list(checks['failures'])
    groups = collections.defaultdict(list)
    for row in rows:
        if row['kind'] == 'trial':
            groups[(row['pass'], row['fixture'], row['variant'])].append(row)
    results = []
    for pass_name, fixture, variant in sorted(groups):
        group = groups[(pass_name, fixture, variant)]
        measured = [r for r in group if not r['warmup']]
        if len(measured) != (1 if checks_only else 20) or sum(r['warmup'] for r in group) != (0 if checks_only else 5):
            errors.append(f'{pass_name}/{fixture}/{variant}: expected exactly 5 warmups + 20 measurements')
        if not measured:
            continue
        metrics = {name: summary([r[name] for r in measured]) for name in
                   ('firstImageMs', 'renderFenceMs', 'decodeMs', 'cpuMs', 'peakWorkingBytes', 'peakPrivateBytes')}
        interaction = collections.defaultdict(list)
        for row in measured:
            for item in row['interactions']:
                interaction[item['name']].append(item['fenceMs'])
        results.append({'pass': pass_name, 'fixture': fixture, 'variant': variant,
                        'metrics': metrics, 'interactions': {k: summary(v) for k, v in interaction.items()}})
    # Pair identity checks use the shared allocation token as well as hash,
    # dimensions and decode bytes. No different thumbnail can pass this check.
    for (pass_name, fixture, variant), group in groups.items():
        if variant != 0:
            continue
        other = {r['cycle']: r for r in groups[(pass_name, fixture, 1)]}
        for row in group:
            b = other.get(row['cycle'])
            fields = ['sourceSha256', 'encodedBytes', 'decodedBytes', 'pixelWidth', 'pixelHeight']
            if pass_name == 'shared-bitmap':
                fields.append('bitmapToken')
            if b is None or any(row[f] != b[f] for f in fields):
                errors.append(f'{pass_name}/{fixture}/{row["cycle"]}: unequal decode inputs/allocation')
    if len(groups) != 20:  # 5 valid fixtures × 2 passes × 2 variants
        errors.append(f'Expected 20 trial groups, found {len(groups)}')
    expected_errors = [r for r in rows if r['kind'] == 'expected-error']
    if len(expected_errors) != 8 or any(not r['message'] for r in expected_errors):
        errors.append('All 4 rejection fixtures must fail visibly for both variants')
    close_cycles = {}
    for variant in (0, 1):
        cycles = [r for r in rows if r['kind'] == 'close-cycle' and r['variant'] == variant]
        if len(cycles) != (0 if checks_only else 50) or any(r['heldBitmaps'] or r['heldBytes'] or not r['sourcesClear'] for r in cycles):
            errors.append(f'{variant}: invalid 50-cycle ownership check')
        if cycles:
            # Report actual trend rather than equating ownership counters with
            # absence of native renderer/cache memory. Three cycles per fixture
            # allow high-peak and low-peak images to affect both windows equally.
            first = statistics.median(r['workingBytes'] for r in cycles[10:20])
            last = statistics.median(r['workingBytes'] for r in cycles[-10:])
            close_cycles[variant] = {'workingBytes': summary([r['workingBytes'] for r in cycles]),
                                     'medianCycles10to19': first, 'medianLast10': last,
                                     'residualGrowthBytes': last - first}
    regressions = []
    indexed = {(r['pass'], r['fixture'], r['variant']): r for r in results}
    for (pass_name, fixture, variant), result in indexed.items():
        if variant != 0 or (pass_name, fixture, 1) not in indexed:
            continue
        b = indexed[(pass_name, fixture, 1)]
        for metric, stat in [('firstImageMs', 'median'), ('firstImageMs', 'p95'),
                             ('cpuMs', 'median'), ('peakWorkingBytes', 'p95')]:
            a_value = result['metrics'][metric][stat]
            b_value = b['metrics'][metric][stat]
            # A max-min is deliberately shown as a conservative noise estimate;
            # a single noisy run never establishes a reproducible regression.
            spread = result['metrics'][metric]['max'] - result['metrics'][metric]['min']
            if b_value > a_value * 1.10 and b_value - a_value > spread:
                regressions.append({'pass': pass_name, 'fixture': fixture, 'metric': metric, 'stat': stat,
                                    'a': a_value, 'b': b_value, 'ratio': b_value / max(a_value, 1e-9),
                                    'aSpread': spread, 'status': 'REPEAT on same frozen source to establish reproducibility'})
    return {'metadata': metadata, 'errors': errors, 'groups': results, 'closeCycles': close_cycles,
            'emptyHost': {name: summary([r[name] for r in empty]) for name in
                          ('elapsedMs', 'cpuMs', 'peakWorkingBytes', 'peakPrivateBytes')} if empty else {},
            'regressionCandidates': regressions,
            'decision': 'KEEP DEFAULT; measured suitability requires native input/AT review, visual evidence, repeat any candidate regression and concrete benefit assessment.',
            'limitations': ['Two-frame submission fence is not physical display latency.',
                            'Sampled RAM may miss short-lived codec peaks; supplement with /usr/bin/time -v.',
                            'Bitmap counters cover host-owned decoded bitmaps; native renderer/cache residuals are reported separately.',
                            'EN/DE × scale × theme × mode matrix requires distinct process runs.']}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('evidence', type=pathlib.Path)
    args = parser.parse_args()
    result = analyze(args.evidence)
    target = args.evidence / 'analysis.json'
    target.write_text(json.dumps(result, indent=2))
    print(json.dumps({'analysis': str(target), 'errors': result['errors'],
                      'regressionCandidates': len(result['regressionCandidates'])}, indent=2))
    raise SystemExit(1 if result['errors'] else 0)


if __name__ == '__main__':
    main()
