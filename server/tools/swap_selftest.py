# SPDX-License-Identifier: MIT
"""Exercises swap_in.ps1's REAL install + restore path against a FAKE server data folder (never Server\\data).

    python swap_selftest.py <server_tool_dir> <staging> <live_server_data> <work_dir> <map_id>

Copies the map's live maps/mmaps files into <work_dir>/fake_serverdata, adds one extra .map (must be removed)
and deletes one .mmtile (must be added), then runs a copy of swap_in.ps1 whose mangosd check is stubbed out
(the copy lives in <work_dir>; the real script is unchanged), checks the result, restores, and checks again.
"""
import hashlib
import os
import shutil
import subprocess
import sys

tool, staging, live, work, mid = sys.argv[1:6]
mid3 = '%03d' % int(mid)
fake = os.path.join(work, 'fake_serverdata')
backups = os.path.join(work, 'fake_backups')
for d in (fake, backups):
    if os.path.isdir(d):
        shutil.rmtree(d)


def h(p):
    return hashlib.sha1(open(p, 'rb').read()).hexdigest()


def snap(root):
    out = {}
    for dp, dn, fn in os.walk(root):
        for n in fn:
            p = os.path.join(dp, n)
            out[os.path.relpath(p, root)] = h(p)
    return out


for kind in ('maps', 'mmaps'):
    os.makedirs(os.path.join(fake, kind))
    for n in os.listdir(os.path.join(live, kind)):
        if n.startswith(mid3):
            shutil.copyfile(os.path.join(live, kind, n), os.path.join(fake, kind, n))
extra = os.path.join(fake, 'maps', mid3 + '0000.map')            # not produced by the extractor -> remove
shutil.copyfile(os.path.join(fake, 'maps', sorted(os.listdir(os.path.join(fake, 'maps')))[0]), extra)
gone = sorted(n for n in os.listdir(os.path.join(staging, 'mmaps')) if n.endswith('.mmtile'))[0]
os.remove(os.path.join(fake, 'mmaps', gone))                     # missing in "live" -> add
before = snap(fake)

src = open(os.path.join(tool, 'swap_in.ps1'), encoding='utf-8').read()
stub = 'function Test-MangosRunning {\n    return \'\'   # STUB for the self-test copy only\n}\nfunction Test-MangosRunning-Real {'
assert src.count('function Test-MangosRunning {') == 1
test_script = os.path.join(work, 'swap_in_TESTCOPY.ps1')
with open(test_script, 'w', encoding='utf-8') as f:
    f.write(src.replace('function Test-MangosRunning {', stub).replace("$here = $PSScriptRoot", "$here = '%s'" % tool))


def ps(*args):
    r = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', test_script] + list(args),
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    print(r.stdout)
    return r.returncode


rc = ps('-Staging', staging, '-ServerData', fake, '-BackupRoot', backups, '-Maps', mid)
after = snap(fake)
ok = rc == 0
for kind in ('maps', 'mmaps'):
    for n in os.listdir(os.path.join(staging, kind)):
        if n.startswith(mid3):
            ok &= after.get(os.path.join(kind, n)) == h(os.path.join(staging, kind, n))
ok &= os.path.join('maps', mid3 + '0000.map') not in after
ok &= os.path.join('mmaps', gone) in after
print('INSTALL CHECK', 'PASS' if ok else 'FAIL')

bdir = os.path.join(backups, os.listdir(backups)[0])
rc2 = ps('-ServerData', fake, '-Restore', bdir)
restored = snap(fake)
ok2 = rc2 == 0 and restored == before
print('RESTORE CHECK', 'PASS' if ok2 else 'FAIL (%d files differ)' % len(set(restored.items()) ^ set(before.items())))
sys.exit(0 if ok and ok2 else 1)
