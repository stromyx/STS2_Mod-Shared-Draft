#!/usr/bin/env python3
"""Parse Godot 4.x .pck file - handles flags=2 (file index at end)."""
import struct, sys, os

def parse_pck(path):
    fsize = os.path.getsize(path)
    with open(path, "rb") as f:
        magic = f.read(4)
        pack_ver = struct.unpack("<I", f.read(4))[0]
        g_major = struct.unpack("<I", f.read(4))[0]
        g_minor = struct.unpack("<I", f.read(4))[0]
        g_patch = struct.unpack("<I", f.read(4))[0]
        flags = struct.unpack("<I", f.read(4))[0]
        file_base = struct.unpack("<q", f.read(8))[0]
        print(f"Magic: {magic}, Pack v{pack_ver}, Godot {g_major}.{g_minor}.{g_patch}")
        print(f"Flags: {flags}, File base: {file_base}, Total size: {fsize}")
        
        reserved = f.read(64)
        print(f"Header ends at offset: {f.tell()}")
        
        # For flags=2, file index might be at file_base offset
        # file_base=0x70=112 means index starts right after header
        f.seek(file_base)
        
        # Try reading file count
        count = struct.unpack("<I", f.read(4))[0]
        print(f"File count at offset {file_base}: {count}")
        
        if count > 1000 or count == 0:
            # Try from end of file - Godot 4 PCK format with index at end
            f.seek(fsize - 4)
            end_magic = f.read(4)
            print(f"End magic: {end_magic}")
            if end_magic == b'GDPC':
                f.seek(fsize - 8)
                idx_offset = struct.unpack("<I", f.read(4))[0]
                print(f"Index offset from end: {idx_offset}")

        # Let me try scanning for known resource paths
        f.seek(0)
        data = f.read()
        
        # Find all "res://" occurrences
        idx = 0
        res_paths = []
        while True:
            idx = data.find(b'res://', idx)
            if idx < 0:
                break
            # Read until null
            end = data.find(b'\x00', idx)
            if end > 0 and end - idx < 200:
                path_str = data[idx:end].decode('utf-8', errors='replace')
                res_paths.append((idx, path_str))
            idx += 1
        
        print(f"\nFound {len(res_paths)} resource paths:")
        for off, p in res_paths:
            print(f"  @0x{off:04x}: {p}")

        # Also find JSON content
        for keyword in [b'"id"', b'"name"', b'"has_pck"', b'"has_dll"']:
            idx = data.find(keyword)
            if idx >= 0:
                start = max(0, idx - 50)
                end = min(len(data), idx + 200)
                snippet = data[start:end].decode('utf-8', errors='replace')
                print(f"\nJSON content near '{keyword.decode()}' at 0x{idx:04x}:")
                print(snippet[:300])

if __name__ == "__main__":
    parse_pck(sys.argv[1])
