"""MPQ writer: archives we build are read back by our reader and found by the client-style hash probe."""
import os
import shutil
import struct
import unittest

from common import CFG, client, needs_client, tempdir

from wowdata import crypto, mpqwrite
from wowdata.mpq import FLAG_COMPRESS, FLAG_EXISTS, MPQArchive


class WriterTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempdir()

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_edge_cases(self):
        files = {
            'empty.txt': b'',
            'Interface\\tiny.txt': b'abc',
            'random.bin': os.urandom(3 * 4096 + 17),          # incompressible, several sectors
            'zeros.bin': bytes(50000),                         # very compressible
            'forward/slash.txt': b'slashes become backslashes',
        }
        out = os.path.join(self.dir, 'patch-3.MPQ')
        s = mpqwrite.build(files, out)
        self.assertEqual(s['files'], 5)
        files_norm = {k.replace('/', '\\'): v for k, v in files.items()}
        self.assertEqual(mpqwrite.verify(out, files_norm), [])
        with MPQArchive(out) as a:
            self.assertEqual((a.format_version, a.header_size, a.sector_size), (1, 44, 4096))
            self.assertEqual(a.block_table[a.block_index('random.bin')][3], FLAG_EXISTS)                  # stored plain
            self.assertEqual(a.block_table[a.block_index('zeros.bin')][3], FLAG_EXISTS | FLAG_COMPRESS)
            self.assertEqual(sorted(a.listfile()), sorted(files_norm))
            attr = a.read('(attributes)')
            n = a.block_table_size
            self.assertEqual(len(attr), 8 + n * (4 + 8 + 16))
            self.assertEqual(struct.unpack_from('<II', attr), (100, 7))

    def test_many_files_and_collisions(self):
        files = {'Dir%d\\File%04d.dat' % (i % 7, i): (b'%d' % i) * (i % 50) for i in range(1500)}
        out = os.path.join(self.dir, 'patch-4.MPQ')
        mpqwrite.build(files, out)
        self.assertEqual(mpqwrite.verify(out, files), [])

    def test_refuses(self):
        with self.assertRaises(mpqwrite.MPQWriteError):
            mpqwrite.build({'a': b'1'}, os.path.join(self.dir, 'patch-2.MPQ'))    # stock name
        with self.assertRaises(mpqwrite.MPQWriteError):
            mpqwrite.build({'a': b'1', 'A': b'2'}, os.path.join(self.dir, 'patch-5.MPQ'))   # case duplicate
        out = os.path.join(self.dir, 'patch-6.MPQ')
        mpqwrite.build({'a': b'1'}, out)
        with self.assertRaises(mpqwrite.MPQWriteError):
            mpqwrite.build({'a': b'1'}, out)                                          # exists

    def test_tables_are_encrypted_like_blizzards(self):
        out = os.path.join(self.dir, 'patch-7.MPQ')
        mpqwrite.build({'x.txt': b'hello'}, out)
        with open(out, 'rb') as f:
            raw = f.read()
        hpos, bpos, hsize = struct.unpack_from('<III', raw, 16)
        plain = crypto.decrypt(raw[hpos:hpos + hsize * 16], crypto.HASH_TABLE_KEY)
        entries = [struct.unpack_from('<IIHHI', plain, i * 16) for i in range(hsize)]
        used = [e for e in entries if e[4] != 0xFFFFFFFF]
        self.assertEqual(len(used), 3)       # x.txt, (listfile), (attributes)
        self.assertNotEqual(raw[hpos:hpos + 16], plain[:16])


@needs_client
class RealFilesTests(unittest.TestCase):
    def test_repack_real_assets(self):
        d = tempdir()
        try:
            names = ['Creature\\Boar\\Boar.m2', 'Creature\\Boar\\BoarSkinIvory.blp', 'Character\\Orc\\Male\\OrcMale.m2',
                     'DBFilesClient\\CreatureDisplayInfo.dbc', 'World\\Maps\\Kalimdor\\Kalimdor_40_33.adt']
            files = {n: client().read(n) for n in names}
            out = os.path.join(d, 'patch-3.MPQ')
            mpqwrite.build(files, out)
            self.assertEqual(mpqwrite.verify(out, files), [])
            self.assertLess(os.path.getsize(out), sum(map(len, files.values())))     # zlib did its job
        finally:
            shutil.rmtree(d, ignore_errors=True)


class MountOrderTests(unittest.TestCase):
    def test_patch_names_like_the_client(self):
        """PatchFiles_MountArchives: Data\\patch-?.MPQ (one character), descending, then patch.MPQ, then the base list."""
        from wowdata.vfs import client_archive_order
        d = tempdir()
        try:
            for n in ('patch.MPQ', 'patch-2.MPQ', 'patch-3.MPQ', 'patch-A.MPQ', 'patch-10.MPQ', 'patch-3.MPQ.bak',
                      'model.MPQ', 'dbc.MPQ', 'base.MPQ'):
                open(os.path.join(d, n), 'wb').close()
            self.assertEqual(client_archive_order(d), ['patch-A.MPQ', 'patch-3.MPQ', 'patch-2.MPQ', 'patch.MPQ',
                                                       'model.MPQ', 'dbc.MPQ'])
        finally:
            shutil.rmtree(d, ignore_errors=True)


if __name__ == '__main__':
    unittest.main()
