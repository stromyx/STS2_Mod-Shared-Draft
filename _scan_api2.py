#!/usr/bin/env python3
"""Deeper scan for Character.AllCards and ToCardModel context."""

import os
home = os.path.expanduser('~')
dll_path = os.path.join(home, 'Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64/sts2.dll')

with open(dll_path, 'rb') as f:
    data = f.read()

# Extended search with more context
for kw in ['GenerateAllCards', 'get_AllCards', '_allCards', 'ToCardModel', 'ToCardModelOrNull',
           'CardFactory', 'CreateCard', 'CardModel.', 'ICardModel',
           'CardIdExtensions', 'ModelIdExtensions', 'CardModelExtensions']:
    utf8 = kw.encode('utf-8')
    pos = data.find(utf8)
    if pos != -1:
        s = max(0, pos - 150)
        e = min(len(data), pos + len(utf8) + 150)
        raw = data[s:e]
        readable = ''.join(chr(b) if 32 <= b < 127 else '.' for b in raw)
        print(f'"{kw}" @{pos}:')
        print(f'  {readable}')
        print()
