# SPDX-License-Identifier: MIT
"""Adds "patch-3.MPQ" to mapextractor's CONF_mpq_list (contrib/extractor/System.cpp), binary-safe (keeps CRLF).

    python apply_patch3.py <server_src> [<backup_dir>]

Idempotent: does nothing if the entry is already there. Saves the untouched file as <backup_dir>/System.cpp.orig.
"""
import os
import shutil
import sys

src = sys.argv[1]
backup_dir = sys.argv[2] if len(sys.argv) > 2 else None
path = os.path.join(src, 'contrib', 'extractor', 'System.cpp')
data = open(path, 'rb').read()
if b'"patch-3.MPQ"' in data:
    print('already patched: ' + path)
    sys.exit(0)
old = b'    "patch-2.MPQ",\r\n};'
if data.count(old) != 1:
    sys.exit('anchor not found exactly once in ' + path)
if backup_dir:
    os.makedirs(backup_dir, exist_ok=True)
    dst = os.path.join(backup_dir, 'System.cpp.orig')
    if not os.path.exists(dst):
        shutil.copyfile(path, dst)
new = b'    "patch-2.MPQ",\r\n    "patch-3.MPQ",\r\n};'
data = data.replace(old, new)
with open(path, 'wb') as f:
    f.write(data)
print('patched: ' + path)
