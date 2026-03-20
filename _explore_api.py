import re

dll = '/Users/guyinan/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64/sts2.dll'
with open(dll, 'rb') as f:
    data = f.read()

for pat in [b'GameDef', b'GetAllInstances', b'ModelIdCategory', b'ModelIdSerializationCache', b'GetAllCards', b'CardFactory', b'CardDatabase']:
    idx, found = 0, []
    while True:
        idx = data.find(pat, idx)
        if idx == -1: break
        s = max(0, idx-20)
        e = min(len(data), idx+60)
        ctx = ''.join(chr(c) if 32<=c<127 else '.' for c in data[s:e])
        found.append(ctx)
        idx += len(pat)
    print(f'=== {pat.decode()} ({len(found)} hits) ===')
    for f_ in found[:3]:
        print(f'  {f_}')
    print()
