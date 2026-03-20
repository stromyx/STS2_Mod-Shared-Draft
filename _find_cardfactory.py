import os

home = os.path.expanduser('~')
dll = os.path.join(home, 'Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64/sts2.dll')

with open(dll, 'rb') as f:
    data = f.read()

# Search the entire metadata area for "CardFactory" with broader context
b = b'CardFactory'
s = 8600000  # Look in the type metadata area
while True:
    p = data.find(b, s)
    if p == -1 or p > 9500000:
        break
    cs = max(0, p - 150)
    ce = min(len(data), p + len(b) + 50)
    r = ''.join(chr(x) if 32 <= x < 127 else '.' for x in data[cs:ce])
    print(f'@{p}: {r}')
    s = p + 1

print("\n--- Searching for CreateForReward ---")
b2 = b'CreateForReward'
s = 0
while True:
    p = data.find(b2, s)
    if p == -1:
        break
    cs = max(0, p - 100)
    ce = min(len(data), p + len(b2) + 100)
    r = ''.join(chr(x) if 32 <= x < 127 else '.' for x in data[cs:ce])
    print(f'@{p}: {r}')
    s = p + 1

print("\n--- Searching for CardCreationResult ---")
b3 = b'CardCreationResult'
s = 8500000
while True:
    p = data.find(b3, s)
    if p == -1 or p > 9500000:
        break
    cs = max(0, p - 100)
    ce = min(len(data), p + len(b3) + 100)
    r = ''.join(chr(x) if 32 <= x < 127 else '.' for x in data[cs:ce])
    print(f'@{p}: {r}')
    s = p + 1
