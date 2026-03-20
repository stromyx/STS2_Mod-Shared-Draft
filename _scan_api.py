#!/usr/bin/env python3
"""Scan sts2.dll for CardModel creation/lookup API names and context."""

import os
home = os.path.expanduser('~')
dll_path = os.path.join(home, 'Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64/sts2.dll')

with open(dll_path, 'rb') as f:
    data = f.read()

keywords = [
    'CreateCard', 'ToCardModel', 'ToCardModelOrNull',
    'TryGetCard', 'TryGetCardId', 'AllCards', 'CardFactory',
    'Instantiate', 'get_AllCards', 'GenerateAllCards',
    'CardDatabase', 'CardRegistry', 'CardCache',
    'Character', 'CardDefinition', 'CardCatalog',
]

for kw in keywords:
    utf8 = kw.encode('utf-8')
    positions = []
    start = 0
    while True:
        pos = data.find(utf8, start)
        if pos == -1:
            break
        positions.append(pos)
        start = pos + 1
    if positions:
        print(f'"{kw}" found {len(positions)} time(s)')
        for p in positions[:3]:
            s = max(0, p - 80)
            e = min(len(data), p + len(utf8) + 80)
            raw = data[s:e]
            readable = ''.join(chr(b) if 32 <= b < 127 else '.' for b in raw)
            print(f'  @{p}: {readable}')
        print()
