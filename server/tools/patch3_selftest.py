# SPDX-License-Identifier: MIT
"""End-to-end check that the patched mapextractor reads Data\\patch-3.MPQ and lets it win over patch.MPQ,
WITHOUT touching the real client: builds a small fake client folder.

    python patch3_selftest.py <client_dir> <work_dir> <bin_dir> <live_maps_dir>

fake <work_dir>\\fakeclient\\Data:
    patch.MPQ    Map.dbc + AreaTable.dbc + PVPZone03 (Warsong Gulch, map 489) WDT + original tile 29_29 ADT
    patch-3.MPQ  the same ADT with every MCVT height +10.0
Runs mapextractor (-e 1 -f 0) with and without patch-3 and compares 4892929.map against the live server file.
The client's archives are only read (WoWDataTool's MPQ reader); the fake archives are built with its writer.
"""
import os
import shutil
import struct
import subprocess
import sys

client, work, bindir, live = sys.argv[1:5]
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.abspath(os.path.join(here, '..', '..')))      # WoWDataTool root
from wowdata.mpq import MPQArchive        # noqa: E402
from wowdata import mpqwrite              # noqa: E402

BS = chr(92)
WDT = BS.join(['World', 'Maps', 'PVPZone03', 'PVPZone03.wdt'])
ADT = BS.join(['World', 'Maps', 'PVPZone03', 'PVPZone03_29_29.adt'])
DBCS = [BS.join(['DBFilesClient', n]) for n in ('Map.dbc', 'AreaTable.dbc')]
OUT_NAME = '4892929.map'           # map 489, ADT x=29 y=29 -> MMM YY XX

patch = MPQArchive(os.path.join(client, 'Data', 'patch.MPQ'))
files = {n: patch.read(n) for n in DBCS + [WDT, ADT]}
adt = bytearray(files[ADT])

# raise every MCVT height (145 floats per chunk; chunk ids are stored reversed: 'TVCM')
n_chunks = 0
p = adt.find(b'TVCM')
while p >= 0:
    size = struct.unpack_from('<I', adt, p + 4)[0]
    if size == 145 * 4:
        vals = struct.unpack_from('<145f', adt, p + 8)
        struct.pack_into('<145f', adt, p + 8, *[v + 10.0 for v in vals])
        n_chunks += 1
    p = adt.find(b'TVCM', p + 8)
print('modified MCVT chunks:', n_chunks)

fake = os.path.join(work, 'fakeclient')
data = os.path.join(fake, 'Data')
if os.path.isdir(fake):
    shutil.rmtree(fake)
os.makedirs(data)
# mpqwrite refuses stock archive names, so build under another name and rename (fake folder only)
mpqwrite.build(files, os.path.join(data, 'fake-base.MPQ'))
os.rename(os.path.join(data, 'fake-base.MPQ'), os.path.join(data, 'patch.MPQ'))
mpqwrite.build({ADT: bytes(adt)}, os.path.join(data, 'patch-3.MPQ'))


def extract(tag):
    out = os.path.join(work, 'out_' + tag)
    if os.path.isdir(out):
        shutil.rmtree(out)
    os.makedirs(out)
    r = subprocess.run([os.path.join(bindir, 'mapextractor.exe'), '-i', fake, '-e', '1', '-f', '0'], cwd=out,
                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    with open(os.path.join(out, 'mapextractor.log'), 'wb') as f:
        f.write(r.stdout)
    return open(os.path.join(out, 'maps', OUT_NAME), 'rb').read()


def heights(b):
    off, size = struct.unpack_from('<II', b, 16)
    sec = b[off:off + size]
    flags = struct.unpack_from('<I', sec, 4)[0]
    assert flags == 0, 'expected float heights (-f 0)'
    return struct.unpack_from('<%df' % (129 * 129 + 128 * 128), sec, 16)


ref = open(os.path.join(live, OUT_NAME), 'rb').read()
with3 = extract('with_patch3')
os.rename(os.path.join(data, 'patch-3.MPQ'), os.path.join(data, 'patch-3.MPQ.off'))
without3 = extract('without_patch3')
os.rename(os.path.join(data, 'patch-3.MPQ.off'), os.path.join(data, 'patch-3.MPQ'))

print('without patch-3: identical to live %s: %s' % (OUT_NAME, without3 == ref))
d = [a - b for a, b in zip(heights(with3), heights(ref))]
print('with patch-3:    identical to live: %s; height delta min %.4f max %.4f (expected +10)' % (
    with3 == ref, min(d), max(d)))
ok = without3 == ref and with3 != ref and abs(min(d) - 10) < 0.01 and abs(max(d) - 10) < 0.01
print('PATCH-3 SELFTEST', 'PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
