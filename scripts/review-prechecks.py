#!/usr/bin/env python3
"""Deterministic stage-0 observations, not model verdicts. No user-profile input."""
from __future__ import annotations
import collections
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
A = '{https://github.com/avaloniaui}'

def ratio(a, b):
    def lum(value):
        value = value.lstrip('#')
        if len(value) != 6:
            raise ValueError('opaque RGB required')
        channels = [int(value[n:n+2], 16) / 255 for n in (0, 2, 4)]
        linear = [c / 12.92 if c <= .04045 else ((c + .055) / 1.055) ** 2.4 for c in channels]
        return sum(c * w for c, w in zip(linear, (.2126, .7152, .0722)))
    lo, hi = sorted((lum(a), lum(b)))
    return (hi + .05) / (lo + .05)

def run(root: Path, evidence: Path):
    findings, measurements, incomplete = [], [], []
    def add(check, **data):
        findings.append({'check': check, **data})
    catalogs_file = evidence / 'source-catalogs.json'
    if catalogs_file.exists():
        catalogs = json.loads(catalogs_file.read_text())
        en = catalogs['en']
        for p in sorted((root / 'Desktop/Localization/Packs').glob('*.json')):
            doc = json.loads(p.read_text())
            catalogs[doc['code']] = doc['strings']
        plural_bases = {k[:-4] for k in en if k.endswith('.one')}
        for lang, strings in sorted(catalogs.items()):
            expected = set(en)
            if lang in ('ja', 'ko', 'zh-Hans'):
                expected = {k for k in expected if not re.search(r'\.(one|few|many|two|zero)$', k)}
            for key in sorted(expected - strings.keys()):
                add('missing-language-key', language=lang, key=key)
            for key in sorted(strings.keys() - en.keys()):
                if not re.search(r'\.(few|many|two|zero)$', key):
                    add('extra-language-key', language=lang, key=key)
            if lang != 'en':
                for key in sorted(en.keys() & strings.keys()):
                    if strings[key].encode() == en[key].encode():
                        add('byte-identical-english', language=lang, key=key, text=en[key], classification='observation; names/technical terms may be intentional')
            suffixes = [] if lang in ('ja', 'ko', 'zh-Hans') else ['one']
            if lang in ('ru', 'pl', 'cs'): suffixes += ['few']
            for base in sorted(plural_bases):
                for suffix in suffixes:
                    if base + '.' + suffix not in strings:
                        add('missing-plural-form', language=lang, key=base, form=suffix)
    else:
        incomplete.append('language catalog export unavailable')
    shortcuts = evidence / 'shortcuts.json'
    if shortcuts.exists():
        for platform in json.loads(shortcuts.read_text()):
            for issue in platform['issues']:
                add('shortcut-catalogue-issue', platform=platform['platform'], issue=issue)
            measurements.append({'check': 'shortcuts', 'platform': platform['platform'], 'commands': platform['commands'], 'issues': len(platform['issues'])})
    else:
        incomplete.append('runtime command catalogue export unavailable')
    style_path = root / 'Desktop/WorkspaceStyles.axaml'
    style = ET.parse(style_path).getroot()
    tokens = {}
    for node in style.iter():
        key = node.get(X + 'Key', '')
        if key.startswith('Workspace.Space.') and node.text:
            try: tokens[key] = float(node.text.strip())
            except ValueError: pass
    spacing_values = set(tokens.values()) | {0}
    for path in sorted((root / 'Desktop').rglob('*.axaml')):
        for number, line in enumerate(path.read_text().splitlines(), 1):
            matches = re.findall(r'\b(Margin|Padding|Spacing|RowSpacing|ColumnSpacing)="([0-9., -]+)"', line)
            matches += re.findall(r'Property="(Margin|Padding|Spacing|RowSpacing|ColumnSpacing)"\s+Value="([0-9., -]+)"', line)
            for attr, literal in matches:
                values = [float(v) for v in re.split(r'[, ]+', literal) if v]
                off = [v for v in values if abs(v) not in spacing_values]
                if off:
                    add('spacing-off-token-scale', file=str(path.relative_to(root)), line=number, property=attr, literal=literal, off_scale=off)
                elif any(values):
                    add('spacing-literal-not-token-reference', file=str(path.relative_to(root)), line=number, property=attr, literal=literal)
    for path in sorted((root / 'Desktop').rglob('*.cs')):
        for number, line in enumerate(path.read_text().splitlines(), 1):
            # Only syntactically literal assignments, never treat arbitrary geometry as spacing.
            matches = re.findall(r'\b(Margin|Padding)\s*=\s*new(?:\s+Thickness)?\(\s*([0-9., -]+)\)', line)
            matches += re.findall(r'\b(Spacing|RowSpacing|ColumnSpacing)\s*=\s*([0-9.]+)\s*[,;}]', line)
            for attr, literal in matches:
                values = [float(v) for v in re.split(r'[, ]+', literal) if v]
                off = [v for v in values if abs(v) not in spacing_values]
                if off:
                    add('spacing-off-token-scale', file=str(path.relative_to(root)), line=number, property=attr, literal=literal, off_scale=off)
                elif any(values):
                    add('spacing-literal-not-token-reference', file=str(path.relative_to(root)), line=number, property=attr, literal=literal)
    measurements.append({'check': 'spacing', 'tokens': tokens, 'coverage': 'XAML literal spacing properties/setters and C# literal spacing assignments; computed geometry is not inferred'})
    for dictionary in style.iter(A + 'ResourceDictionary'):
        theme = dictionary.get(X + 'Key')
        if theme not in ('Light', 'Dark'): continue
        resources = {n.get(X + 'Key'): (n.text or '').strip() for n in dictionary if n.get(X + 'Key')}
        def colour(key, seen=()):
            if key in seen: raise ValueError('resource cycle')
            value = resources[key]
            match = re.fullmatch(r'\{(?:Static|Dynamic)Resource ([^}]+)\}', value)
            return colour(match[1], (*seen, key)) if match else value
        pairs = []
        surfaces = ['Background', 'Panel', 'Raised', 'Card', 'Hover', 'Overlay', 'AccentSurface', 'ChipSurface', 'ReaderCodeSurface']
        for fg in ['Text', 'Muted', 'Accent']:
            pairs += [(f'Workspace.{fg}', f'Workspace.{bg}', 4.5) for bg in surfaces]
        for fg in ['ControlBorder', 'ChipBorder']:
            pairs += [(f'Workspace.{fg}', f'Workspace.{bg}', 3) for bg in surfaces[:6]]
        pairs += [('Workspace.OnAccent', 'Workspace.Accent', 4.5), ('Workspace.Progress', 'Workspace.ProgressTrack', 3)]
        for fg, bg, minimum in pairs:
            try:
                value = ratio(colour(fg), colour(bg))
                item = {'check': 'palette-contrast', 'theme': theme, 'foreground': fg, 'background': bg, 'ratio': value, 'minimum': minimum}
                measurements.append(item)
                if value < minimum: findings.append(item)
            except (KeyError, ValueError) as exc:
                incomplete.append(f'contrast {theme} {fg}/{bg}: {exc}')
    counts = dict(sorted(collections.Counter(f['check'] for f in findings).items()))
    return {'schema_version': 1, 'scope': 'source catalogue, bundled packs, XAML spacing and opaque theme token pairs; no model judgement', 'counts': counts,
            'incomplete': incomplete, 'findings': findings, 'measurements': measurements}
