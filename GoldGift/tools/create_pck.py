#!/usr/bin/env python3
"""
Create a minimal Godot 4.5.1 .pck file containing only the mod manifest JSON.
This is a workaround for not having MegaDot to export .pck files.

Godot PCK format (v3):
  Header:
    4 bytes: magic "GDPC"
    4 bytes: pack version (3)
    4 bytes: godot major (4)
    4 bytes: godot minor (5)
    4 bytes: godot patch (1)
    4 bytes: flags (0 = standard)
    8 bytes: file_base offset (offset where file data begins)
    64 bytes: reserved (zeros)
  File index (count + entries):
    4 bytes: file count
    For each file:
      4 bytes: path length (padded to 4-byte alignment)
      N bytes: path string (null-terminated, 4-byte aligned)
      8 bytes: file data offset (relative to file_base)
      8 bytes: file data size
      16 bytes: MD5 hash
      4 bytes: flags (0 = no compression)
  File data:
    Raw file contents (each aligned to 4 bytes)
"""
import struct, hashlib, json, sys, os

def pad4(data):
    """Pad bytes to 4-byte alignment."""
    rem = len(data) % 4
    if rem:
        data += b'\x00' * (4 - rem)
    return data

def create_pck(output_path, files):
    """
    Create a .pck file.
    files: list of (res_path, file_content_bytes)
    """
    # Build file entries
    entries = []
    for res_path, content in files:
        md5 = hashlib.md5(content).digest()
        entries.append({
            'path': res_path,
            'content': content,
            'md5': md5,
        })

    # Calculate header size
    # Header: 4 + 4 + 4 + 4 + 4 + 4 + 8 + 64 = 96 bytes
    HEADER_SIZE = 96

    # Calculate file index size
    # 4 bytes for count
    index_size = 4
    for e in entries:
        path_bytes = e['path'].encode('utf-8') + b'\x00'
        path_bytes = pad4(path_bytes)
        # 4 (path_len) + path_bytes + 8 (offset) + 8 (size) + 16 (md5) + 4 (flags) = 40 + path
        index_size += 4 + len(path_bytes) + 8 + 8 + 16 + 4

    file_base = HEADER_SIZE + index_size

    # Calculate file data offsets
    data_offset = 0
    for e in entries:
        e['offset'] = data_offset
        content_padded = pad4(e['content'])
        e['content_padded'] = content_padded
        data_offset += len(content_padded)

    # Write the PCK
    with open(output_path, 'wb') as f:
        # Header
        f.write(b'GDPC')
        f.write(struct.pack('<I', 3))       # pack version
        f.write(struct.pack('<I', 4))       # godot major
        f.write(struct.pack('<I', 5))       # godot minor
        f.write(struct.pack('<I', 1))       # godot patch
        f.write(struct.pack('<I', 0))       # flags (0 = standard, index after header)
        f.write(struct.pack('<q', file_base))  # file_base
        f.write(b'\x00' * 64)              # reserved

        # File index
        f.write(struct.pack('<I', len(entries)))
        for e in entries:
            path_bytes = e['path'].encode('utf-8') + b'\x00'
            path_bytes = pad4(path_bytes)
            f.write(struct.pack('<I', len(path_bytes)))
            f.write(path_bytes)
            f.write(struct.pack('<q', e['offset']))
            f.write(struct.pack('<q', len(e['content'])))
            f.write(e['md5'])
            f.write(struct.pack('<I', 0))  # flags

        # File data
        for e in entries:
            f.write(e['content_padded'])

    print(f"Created {output_path} ({os.path.getsize(output_path)} bytes)")
    print(f"  {len(entries)} files, file_base={file_base}")
    for e in entries:
        print(f"    {e['path']} ({len(e['content'])} bytes)")


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print("Usage: python3 create_pck.py <output.pck> <manifest.json>")
        sys.exit(1)

    output = sys.argv[1]
    manifest_path = sys.argv[2]

    with open(manifest_path, 'rb') as f:
        manifest_content = f.read()

    # The mod manifest must be at the root of res:// 
    # with the same name as the mod id + .json
    manifest_data = json.loads(manifest_content)
    mod_id = manifest_data.get('id', 'GoldGift')
    res_path = f"res://{mod_id}.json"

    files = [
        (res_path, manifest_content),
    ]

    create_pck(output, files)
