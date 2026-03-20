#!/usr/bin/env python3
import struct, os, sys

pck = sys.argv[1]
with open(pck, 'rb') as f:
    raw = f.read()

fsize = len(raw)
print(f"File size: {fsize}")

# Header
file_base = struct.unpack('<q', raw[24:32])[0]
print(f"file_base = {file_base} (0x{file_base:x})")

# In standard Godot PCK v3, after the 96-byte header:
# 4 bytes: file_count, then file entries
# But with flags=2, file_base might point to data, and index is at end

# Check if there's a trailing index
# Godot 4 sometimes stores: at end of file, 4 bytes = magic "GDPC" reversed or offset
print(f"\nLast 32 bytes:")
for i in range(fsize-32, fsize, 4):
    val = struct.unpack('<I', raw[i:i+4])[0]
    print(f"  @0x{i:04x}: {val} (0x{val:08x})")

# Look at offset 0x70 (=112, file_base)
print(f"\nAt file_base (0x{file_base:x}):")
# flags=2 in Godot means "scripts encrypted" not "index at end"
# Let's try: at file_base there might be the file count
count_at_fb = struct.unpack('<I', raw[file_base:file_base+4])[0]
print(f"  uint32 = {count_at_fb}")

# Actually for Godot 4 PCK:
# After header (96 bytes), there is the file index
# file_base just tells where file DATA starts
# So index is at byte 96, data starts at file_base

print(f"\nAt offset 96 (after header):")
count = struct.unpack('<I', raw[96:100])[0]
print(f"  file count = {count}")

# Read entries
if count < 200:
    pos = 100
    for i in range(count):
        plen = struct.unpack('<I', raw[pos:pos+4])[0]
        pos += 4
        path = raw[pos:pos+plen].split(b'\x00')[0].decode('utf-8','replace')
        pos += plen
        offset = struct.unpack('<q', raw[pos:pos+8])[0]
        pos += 8
        size = struct.unpack('<q', raw[pos:pos+8])[0]
        pos += 8
        md5 = raw[pos:pos+16]
        pos += 16
        flags = struct.unpack('<I', raw[pos:pos+4])[0]
        pos += 4
        
        # Read actual content
        data_pos = file_base + offset
        print(f"\n[{i}] path='{path}' offset={offset} size={size} flags={flags}")
        print(f"     data at 0x{data_pos:x}")
        if data_pos + min(size, 100) <= fsize:
            snippet = raw[data_pos:data_pos+min(size, 200)]
            try:
                text = snippet.decode('utf-8', 'replace')
                if text.isprintable() or '\n' in text:
                    print(f"     content: {text[:200]}")
            except:
                pass
