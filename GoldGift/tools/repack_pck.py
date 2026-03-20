#!/usr/bin/env python3
"""
Repack a Godot .pck file from flags=2 to flags=0 format.
flags=2 means the file index is stored differently (MegaDot custom).
We parse the raw data and rebuild with standard format.
"""
import struct, hashlib, os, sys

def pad4(data):
    rem = len(data) % 4
    if rem:
        data += b'\x00' * (4 - rem)
    return data

def read_pck_entries_flags2(path):
    """Read entries from a flags=2 pck by scanning for res:// paths in the file index section."""
    fsize = os.path.getsize(path)
    with open(path, 'rb') as f:
        raw = f.read()
    
    # Find the file index section at the end of the file
    # In flags=2 format, the index seems to be after the data section
    # We need to find patterns of: path_len + "res://..." + offset + size + md5 + flags
    
    entries = []
    # The file index in BaseLib.pck starts around 0x8c40 based on our hex analysis
    # Let's scan for the pattern
    
    idx = 0
    while idx < fsize:
        pos = raw.find(b'res://', idx)
        if pos < 0:
            break
        
        # Check if this looks like a file index entry:
        # 4 bytes before should be the path length
        if pos >= 4:
            path_len_raw = raw[pos-4:pos]
            path_len = struct.unpack('<I', path_len_raw)[0]
            
            # Path length should be reasonable and include "res://"
            if 8 < path_len < 256:
                path_data = raw[pos:pos + path_len]
                # Strip nulls
                path_str = path_data.split(b'\x00')[0].decode('utf-8', errors='replace')
                
                # After path (padded), should be offset(8) + size(8) + md5(16) + flags(4) = 36
                after_path = pos + path_len
                if after_path + 36 <= fsize:
                    offset = struct.unpack('<q', raw[after_path:after_path+8])[0]
                    size = struct.unpack('<q', raw[after_path+8:after_path+16])[0]
                    md5 = raw[after_path+16:after_path+32]
                    fflags = struct.unpack('<I', raw[after_path+32:after_path+36])[0]
                    
                    if 0 <= size < fsize and 0 <= offset < fsize:
                        entries.append({
                            'path': path_str,
                            'offset': offset,
                            'size': size,
                            'md5': md5,
                            'flags': fflags,
                        })
        
        idx = pos + 1
    
    return entries, raw

def repack_pck(input_path, output_path):
    """Repack pck with flags=0 standard format."""
    entries, raw = read_pck_entries_flags2(input_path)
    
    print(f"Found {len(entries)} entries")
    
    # Read the original header to get file_base
    with open(input_path, 'rb') as f:
        f.read(4)  # magic
        f.read(4)  # pack_ver
        f.read(4)  # major
        f.read(4)  # minor
        f.read(4)  # patch
        orig_flags = struct.unpack('<I', f.read(4))[0]
        orig_file_base = struct.unpack('<q', f.read(8))[0]
    
    print(f"Original flags: {orig_flags}, file_base: {orig_file_base}")
    
    # Extract file contents
    files = []
    for e in entries:
        data_start = orig_file_base + e['offset'] if orig_file_base else e['offset']
        if data_start + e['size'] <= len(raw):
            content = raw[data_start:data_start + e['size']]
            # Verify MD5
            actual_md5 = hashlib.md5(content).digest()
            match = "OK" if actual_md5 == e['md5'] else "MISMATCH"
            print(f"  {e['path']} ({e['size']}b) md5={match}")
            files.append((e['path'], content))
        else:
            print(f"  {e['path']} ({e['size']}b) - DATA OUT OF RANGE at {data_start}")
    
    if not files:
        print("ERROR: No files extracted!")
        return
    
    # Rebuild with flags=0
    HEADER_SIZE = 96
    
    index_size = 4  # file count
    for res_path, content in files:
        path_bytes = pad4(res_path.encode('utf-8') + b'\x00')
        index_size += 4 + len(path_bytes) + 8 + 8 + 16 + 4
    
    file_base = HEADER_SIZE + index_size
    
    with open(output_path, 'wb') as f:
        # Header
        f.write(b'GDPC')
        f.write(struct.pack('<I', 3))    # pack version
        f.write(struct.pack('<I', 4))    # major
        f.write(struct.pack('<I', 5))    # minor
        f.write(struct.pack('<I', 1))    # patch
        f.write(struct.pack('<I', 0))    # flags = 0 (standard)
        f.write(struct.pack('<q', file_base))
        f.write(b'\x00' * 64)
        
        # File index
        f.write(struct.pack('<I', len(files)))
        
        data_offset = 0
        offsets = []
        for res_path, content in files:
            path_bytes = pad4(res_path.encode('utf-8') + b'\x00')
            f.write(struct.pack('<I', len(path_bytes)))
            f.write(path_bytes)
            f.write(struct.pack('<q', data_offset))
            f.write(struct.pack('<q', len(content)))
            f.write(hashlib.md5(content).digest())
            f.write(struct.pack('<I', 0))
            
            padded_size = len(pad4(content))
            offsets.append((data_offset, content))
            data_offset += padded_size
        
        # File data
        for off, content in offsets:
            f.write(pad4(content))
    
    print(f"\nCreated {output_path} ({os.path.getsize(output_path)} bytes)")

if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python3 repack_pck.py <input.pck> <output.pck>")
        sys.exit(1)
    repack_pck(sys.argv[1], sys.argv[2])
