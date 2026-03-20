#!/usr/bin/env python3
import struct, sys
with open(sys.argv[1], 'rb') as f:
    raw = f.read()

print("=== HEADER (0-31) ===")
for off in range(0, 32, 4):
    v = struct.unpack('<I', raw[off:off+4])[0]
    print(f"  @{off:3d}: {v:12d}  (0x{v:08x})")

v8 = struct.unpack('<q', raw[24:32])[0]
print(f"  @24 (q): file_base = {v8}")

print("\n=== RESERVED (32-95) ===")
for i in range(16):
    off = 32 + i*4
    v = struct.unpack('<I', raw[off:off+4])[0]
    if v != 0:
        print(f"  @{off:3d}: {v:12d}  (0x{v:08x})")
    else:
        print(f"  @{off:3d}: 0")

# Read reserved as int64 too
reserved8 = struct.unpack('<q', raw[32:40])[0]
print(f"  @32 (q): {reserved8}")

print(f"\n=== After header (96-128) ===")
for off in range(96, 160, 4):
    if off+4 <= len(raw):
        v = struct.unpack('<I', raw[off:off+4])[0]
        print(f"  @{off:3d} (0x{off:02x}): {v:12d}  (0x{v:08x})")

# Godot PCK v2 format had: header(8*4=32), then reserved(16*4=64), total=96
# Then file_count(4), entries
# In v3 (Godot 4.5), they added flags(4) and file_base(8) between version and reserved
# Total header = 4+4+4+4+4 + 4+8 + 64 = 96

# BUT WAIT - Godot 4.5 might have increased reserved to hold more data
# Let me check: the reserved[0] has value 0x9a00 = 39424
print(f"\n=== Reserved field 0 as int64 ===")
v = struct.unpack('<q', raw[32:40])[0]
print(f"  {v} (0x{v:016x})")
