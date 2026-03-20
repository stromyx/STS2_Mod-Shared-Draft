#!/usr/bin/env python3
"""Parse a Godot .pck file to understand its structure."""
import struct, sys, os

def parse_pck(path):
    with open(path, "rb") as f:
        magic = f.read(4)
        print(f"Magic: {magic}")
        
        pack_ver = struct.unpack("<I", f.read(4))[0]
        g_major = struct.unpack("<I", f.read(4))[0]
        g_minor = struct.unpack("<I", f.read(4))[0]
        g_patch = struct.unpack("<I", f.read(4))[0]
        print(f"Pack version: {pack_ver}, Godot: {g_major}.{g_minor}.{g_patch}")
        
        flags = struct.unpack("<I", f.read(4))[0]
        file_base = struct.unpack("<q", f.read(8))[0]
        print(f"Flags: {flags}, File base: {file_base}")
        
        f.read(64)  # reserved
        
        count = struct.unpack("<I", f.read(4))[0]
        print(f"File count: {count}\n")
        
        files = []
        for i in range(count):
            plen = struct.unpack("<I", f.read(4))[0]
            path_raw = f.read(plen)
            fpath = path_raw.decode("utf-8", errors="replace").rstrip('\x00')
            offset = struct.unpack("<q", f.read(8))[0]
            size = struct.unpack("<q", f.read(8))[0]
            md5 = f.read(16).hex()
            fflags = struct.unpack("<I", f.read(4))[0]
            files.append((fpath, offset, size, md5, fflags))
            print(f"  [{i}] {fpath}")
            print(f"       offset={offset} size={size} flags={fflags}")
        
        # Read first few files content
        print("\n=== File Contents (first 500 bytes) ===")
        for fpath, offset, size, md5, fflags in files:
            if any(x in fpath.lower() for x in ['json', 'manifest', 'mainfile']):
                f.seek(file_base + offset)
                data = f.read(min(size, 500))
                print(f"\n--- {fpath} ({size} bytes) ---")
                try:
                    print(data.decode("utf-8", errors="replace"))
                except:
                    print(data[:100])

if __name__ == "__main__":
    pck = sys.argv[1] if len(sys.argv) > 1 else ""
    if not pck or not os.path.exists(pck):
        print("Usage: python3 parse_pck.py <file.pck>")
        sys.exit(1)
    parse_pck(pck)
