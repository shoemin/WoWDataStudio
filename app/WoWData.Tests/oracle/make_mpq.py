"""Test oracle for the C# MPQ reader / writer and the index: the Python reference results, written into <outdir>.

Usage:
    python make_mpq.py <outdir>                         generate everything (done.txt is written last)
    python make_mpq.py --verify <archive> <inputdir>    read an archive written by the C# writer with the Python
                                                        reader + mpqwrite.verify(); prints JSON

<outdir> holds Blizzard game data: keep it out of source control (the C# tests use app/.testcache/oracle/mpq/).
The client is only read.
Files written:
    order.json            vfs.client_archive_order
    names.json            ClientData.names(): [[path, winning archive], ...] sorted by upper-case path
    deleted.json          ClientData.deleted_names(), same shape
    hashes.json           {path: [size, sha256]} for every DBC, the boar files and 600 random files (seed 1234)
    where.json            ClientData.where() for a few paths: [[kind, archive], ...]
    writer_input/...      the files given to the Python writer (archive paths mirrored)
    writer_input.json     their archive paths, in the order given
    py_patch-3.MPQ        mpqwrite.build() output for those files
    index_counts.json     index.build() counts without the server
"""
import hashlib
import json
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))   # oracle -> WoWData.Tests -> app -> WoWDataTool
for p in (os.getcwd(), TOOL):
    if p not in sys.path:
        sys.path.insert(0, p)

from wowdata import config, mpqwrite, vfs   # noqa: E402
from wowdata.mpq import MPQArchive          # noqa: E402

BS = chr(92)


def writer_files(client):
    """A deterministic input set: real game files plus synthetic edge cases."""
    rnd = random.Random(99)
    files = {}
    for p in ('Creature' + BS + 'Boar' + BS + 'BoarSkinIvory.blp', 'Interface' + BS + 'Icons' + BS + 'INV_Axe_09.blp',
              'DBFilesClient' + BS + 'CreatureDisplayInfo.dbc'):
        files[p] = client.read(p)
    files['empty.txt'] = b''
    files['Interface' + BS + 'tiny.txt'] = b'abc'
    files['random.bin'] = bytes(rnd.getrandbits(8) for _ in range(3 * 4096 + 17))
    files['zeros.bin'] = bytes(50000)
    for i in range(300):
        files['Dir%d%sFile%04d.dat' % (i % 7, BS, i)] = (b'%d' % i) * (i % 50)
    return files


def main(out):
    os.makedirs(out, exist_ok=True)
    cfg = config.load()
    c = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)

    def dump(name, obj):
        with open(os.path.join(out, name), 'w', encoding='utf-8') as f:
            json.dump(obj, f, ensure_ascii=False)

    dump('order.json', vfs.client_archive_order(cfg.data_dir))
    names = c.names()
    dump('names.json', [list(names[k]) for k in sorted(names)])
    deleted = c.deleted_names()
    dump('deleted.json', [list(deleted[k]) for k in sorted(deleted)])

    rnd = random.Random(1234)
    keys = sorted(names)
    pick = set(rnd.sample(keys, 600))
    pick |= {k for k in keys if k.endswith('.DBC')}
    pick |= {k for k in keys if k.startswith('CREATURE' + BS + 'BOAR' + BS)}
    hashes = {}
    for k in sorted(pick):
        path = names[k][0]
        data = c.read(path)
        hashes[path] = [len(data), hashlib.sha256(data).hexdigest()]
    dump('hashes.json', hashes)

    where = {}
    for p in ('Creature' + BS + 'OgreMage' + BS + 'OgreMage.m2', 'DBFilesClient' + BS + 'CreatureDisplayInfo.dbc',
              'Creature' + BS + 'Boar' + BS + 'Boar.mdx', 'Creature' + BS + 'Boar' + BS + 'BoarSkinIvory.blp'):
        where[p] = [[s.kind, s.name] for s in c.where(p)]
    dump('where.json', where)

    files = writer_files(c)
    inp = os.path.join(out, 'writer_input')
    for p, data in files.items():
        dest = os.path.join(inp, *p.split(BS))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, 'wb') as f:
            f.write(data)
    dump('writer_input.json', list(files))
    target = os.path.join(out, 'py_patch-3.MPQ')
    if os.path.exists(target):
        os.remove(target)
    mpqwrite.build(files, target)

    from wowdata import index
    counts = index.build(c, os.path.join(out, 'py_index.sqlite'), cfg, use_server=False, log=lambda *a: None)
    dump('index_counts.json', counts)
    c.close()
    with open(os.path.join(out, 'done.txt'), 'w') as f:
        f.write('ok\n')


def verify(archive, inputdir):
    with open(os.path.join(os.path.dirname(inputdir.rstrip('/' + BS)), 'writer_input.json'), encoding='utf-8') as f:
        paths = json.load(f)
    files = {}
    for p in paths:
        with open(os.path.join(inputdir, *p.split(BS)), 'rb') as f:
            files[p] = f.read()
    problems = mpqwrite.verify(archive, files)
    with MPQArchive(archive) as a:
        info = {'format_version': a.format_version, 'header_size': a.header_size, 'sector_size': a.sector_size,
                'listfile': a.listfile(), 'blocks': len(a.block_table)}
    print(json.dumps({'problems': problems, 'info': info}))


if __name__ == '__main__':
    if len(sys.argv) >= 2 and sys.argv[1] == '--verify':
        verify(sys.argv[2], sys.argv[3])
    else:
        main(sys.argv[1])
