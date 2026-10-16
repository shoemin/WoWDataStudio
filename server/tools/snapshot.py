# SPDX-License-Identifier: MIT
"""Read-only snapshot of a folder tree: file count, total size, SHA-1 of every file (or a sample).

    python snapshot.py <folder> <out.json> [--sample N]
    python snapshot.py --compare <a.json> <b.json>

Used to prove that the live Server\\data and the client Data folder were not modified.
"""
import hashlib
import json
import os
import sys


def sha1_file(path):
    h = hashlib.sha1()
    with open(path, 'rb') as f:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def snapshot(root, sample=0):
    files = []
    for dp, dn, fn in os.walk(root):
        dn.sort()
        for n in sorted(fn):
            p = os.path.join(dp, n)
            st = os.stat(p)
            files.append((os.path.relpath(p, root).replace('\\', '/'), st.st_size, st.st_mtime_ns))
    step = max(1, len(files) // sample) if sample else 1
    out = {'root': root, 'count': len(files), 'bytes': sum(f[1] for f in files), 'files': {}}
    for i, (rel, size, mtime) in enumerate(files):
        e = {'size': size, 'mtime_ns': mtime}
        if i % step == 0:
            e['sha1'] = sha1_file(os.path.join(root, rel))
        out['files'][rel] = e
    return out


def compare(a, b):
    fa, fb = a['files'], b['files']
    diffs = []
    for k in sorted(set(fa) | set(fb)):
        if k not in fa:
            diffs.append('added   ' + k)
        elif k not in fb:
            diffs.append('removed ' + k)
        else:
            x, y = fa[k], fb[k]
            if x['size'] != y['size'] or x['mtime_ns'] != y['mtime_ns'] or \
                    ('sha1' in x and 'sha1' in y and x['sha1'] != y['sha1']):
                diffs.append('changed ' + k)
    print('A: %d files, %d bytes' % (a['count'], a['bytes']))
    print('B: %d files, %d bytes' % (b['count'], b['bytes']))
    print('hashed in both: %d' % sum(1 for k in fa if 'sha1' in fa[k] and 'sha1' in fb.get(k, {})))
    print('differences: %d' % len(diffs))
    for d in diffs[:50]:
        print('  ' + d)
    return 1 if diffs else 0


def main():
    if sys.argv[1] == '--compare':
        with open(sys.argv[2]) as f:
            a = json.load(f)
        with open(sys.argv[3]) as f:
            b = json.load(f)
        sys.exit(compare(a, b))
    sample = 0
    if '--sample' in sys.argv:
        sample = int(sys.argv[sys.argv.index('--sample') + 1])
    s = snapshot(sys.argv[1], sample)
    with open(sys.argv[2], 'w') as f:
        json.dump(s, f, indent=0)
    print('%s: %d files, %d bytes, %d hashed' % (sys.argv[1], s['count'], s['bytes'],
                                                 sum(1 for e in s['files'].values() if 'sha1' in e)))


if __name__ == '__main__':
    main()
