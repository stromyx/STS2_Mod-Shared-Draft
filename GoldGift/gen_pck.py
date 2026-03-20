#!/usr/bin/env python3
"""Generate GoldGift.pck with updated manifest containing BaseLib dependency."""
import struct, os, sys

manifest = """{
  "id": "GoldGift",
  "name": "Gold Gift - Multiplayer Gold Sharing",
  "author": "guyinan",
  "description": "Allows players to gift gold to other players in multiplayer mode. Adds a gift button to the top of the screen.",
  "version": "v0.1.0",
  "has_pck": true,
  "has_dll": true,
  "dependencies": [],
  "affects_gameplay": true
}
"""

manifest_bytes = manifest.encode('utf-8')
res_path = 'res://GoldGift.json'
res_path_bytes = res_path.encode('utf-8')
path_padding = (4 - len(res_path_bytes) % 4) % 4

# PCK header
magic = b'GDPC'
version = struct.pack('<I', 3)
engine_major = struct.pack('<I', 4)
engine_minor = struct.pack('<I', 5)
engine_patch = struct.pack('<I', 0)
flags = struct.pack('<I', 0)  # No encryption
file_count = struct.pack('<I', 1)
reserved = b'\x00' * 64

header_size = 4 + 4*4 + 4 + 4 + 64
file_entry_size = 4 + len(res_path_bytes) + path_padding + 8 + 8 + 16
data_offset = header_size + file_entry_size

path_len = struct.pack('<I', len(res_path_bytes))
file_offset = struct.pack('<q', data_offset)
file_size = struct.pack('<q', len(manifest_bytes))
md5 = b'\x00' * 16

pck_data = (
    magic + version + engine_major + engine_minor + engine_patch + flags +
    file_count + reserved +
    path_len + res_path_bytes + (b'\x00' * path_padding) +
    file_offset + file_size + md5 +
    manifest_bytes
)

script_dir = os.path.dirname(os.path.abspath(__file__))
pck_path = os.path.join(script_dir, 'GoldGift.pck')
with open(pck_path, 'wb') as f:
    f.write(pck_data)

mods_dir = os.path.expanduser(
    "~/Library/Application Support/Steam/steamapps/common/"
    "Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/GoldGift"
)
mods_pck = os.path.join(mods_dir, 'GoldGift.pck')
if os.path.isdir(mods_dir):
    with open(mods_pck, 'wb') as f:
        f.write(pck_data)
    print(f"Deployed to mods: {mods_pck}")

print(f"PCK generated: {len(pck_data)} bytes, manifest: {len(manifest_bytes)} bytes")
