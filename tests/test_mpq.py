"""MPQ reader and client file view, on the real 1.12 archives."""
import collections
import os
import unittest

from common import CFG, client, needs_client

from wowdata import crypto, dbc, vfs
from wowdata.mpq import MPQArchive


class CryptoTests(unittest.TestCase):
    def test_known_hashes(self):
        # the well-known table keys (any MPQ documentation / StormLib)
        self.assertEqual(crypto.HASH_TABLE_KEY, 0xC3AF3770)
        self.assertEqual(crypto.BLOCK_TABLE_KEY, 0xEC83B3A3)
        self.assertEqual(crypto.hash_string('(listfile)', 0), crypto.hash_string('(LISTFILE)', 0))
        self.assertEqual(crypto.hash_string('a/b', 1), crypto.hash_string('A\\B', 1))

    def test_encrypt_roundtrip(self):
        data = bytes(range(256)) * 3 + b'xyz'
        for key in (0, 1, 0xDEADBEEF, crypto.HASH_TABLE_KEY):
            self.assertEqual(crypto.decrypt(crypto.encrypt(data, key), key), data)


@needs_client
class ArchiveTests(unittest.TestCase):
    def test_lookup_order_matches_client(self):
        order = vfs.client_archive_order(CFG.data_dir)
        # PatchFiles_MountArchives: patch-?.MPQ descending, patch.MPQ, then the base list
        self.assertEqual(order[:2], ['patch-2.MPQ', 'patch.MPQ'])
        self.assertEqual(order[2:], vfs.BASE_ARCHIVES)

    def test_all_archives_open(self):
        c = vfs.ClientData(CFG.data_dir, include_unmounted=True, cache_dir=CFG.cache_dir)
        names = [n for n, _, _ in c.archives]
        self.assertEqual(len(names), 14)   # 12 mounted by the game + base.MPQ + backup.MPQ
        self.assertEqual(sum(1 for _, _, m in c.archives if m), 12)
        for n, a, _ in c.archives:
            self.assertGreater(len(a.listfile()), 0, n)
        c.close()

    def test_boar_files(self):
        c = client()
        self.assertEqual(c.find('Creature\\Boar\\Boar.mdx').name, 'model.MPQ')   # .mdx -> .m2
        self.assertEqual(len(c.read('Creature\\Boar\\Boar.m2')), 266528)
        self.assertEqual(c.find('creature/boar/boarskinivory.blp').name, 'texture.MPQ')
        self.assertEqual(len(c.read('Creature\\Boar\\BoarSkinIvory.blp')), 44876)
        self.assertEqual(c.read('Creature\\Boar\\Boar.m2')[:4], b'MD20')

    def test_patch_priority(self):
        c = client()
        w = [s.name for s in c.where('DBFilesClient\\CreatureDisplayInfo.dbc')]
        self.assertEqual(w, ['patch-2.MPQ', 'patch.MPQ', 'dbc.MPQ'])
        self.assertEqual(c.read('DBFilesClient\\CreatureDisplayInfo.dbc'), c.archive('patch-2.MPQ').read('DBFilesClient\\CreatureDisplayInfo.dbc'))

    def test_delete_markers_hide_lower_copies(self):
        c = client()
        p = 'Creature\\OgreMage\\OgreMage.m2'
        self.assertEqual(c.archive('patch.MPQ').entry_state(p), 'deleted')
        self.assertTrue(c.archive('model.MPQ').has_file(p))
        self.assertIsNone(c.find(p))            # the client cannot load it either
        self.assertEqual([s.kind for s in c.where(p)], ['deleted', 'hidden'])
        self.assertIn(p.upper(), c.deleted_names())
        self.assertEqual(len(c.deleted_names()), 5708)

    def test_storm_probe_equals_dict_lookup(self):
        """Our client-style linear probe finds every listed file of Blizzard's archives (validates the probe
        that mpqwrite.verify relies on)."""
        c = client()
        for arc in ('patch-2.MPQ', 'dbc.MPQ', 'fonts.MPQ', 'misc.MPQ'):
            a = c.archive(arc)
            for n in a.listfile():
                self.assertEqual(a.storm_lookup(n), a.block_index(n), (arc, n))

    def test_compression_methods_used(self):
        """Every compressed sector start in every mounted archive uses zlib (0x02): no implode, bzip2, huffman, ADPCM.
        Samples the first sector of every file (the full scan of all sectors is the same code path)."""
        c = client()
        masks = collections.Counter()
        flags = collections.Counter()
        for n, a, _ in c.archives:
            for e in a.block_table:
                flags[e[3] & 0x0F0FFF00] += 1
        for n, a, _ in c.archives:
            if n not in ('dbc.MPQ', 'patch-2.MPQ', 'fonts.MPQ', 'misc.MPQ', 'interface.MPQ'):
                continue
            for name in a.listfile():
                for m in a.sector_masks(name)[:1]:
                    masks[m] += 1
        self.assertEqual(set(masks), {0x02}, masks)
        for f in flags:
            self.assertFalse(f & 0x100, 'PKWARE implode flag present')
            self.assertFalse(f & 0x10000, 'encrypted files present')

    def test_dbcs_match_server_copies(self):
        """Of the 158 server DBCs: 152 are identical to what the client loads, Spell and SkillLineAbility are the
        project's own server edits, and 4 are tables the client cannot load at all (delete markers in patch.MPQ)."""
        c = client()
        differ, deleted = [], []
        same = 0
        for fn in sorted(os.listdir(CFG.server_dbc_dir)):
            if not fn.lower().endswith('.dbc'):
                continue
            mine = c.read('DBFilesClient\\' + fn)
            with open(os.path.join(CFG.server_dbc_dir, fn), 'rb') as f:
                theirs = f.read()
            if mine is None:
                deleted.append(fn.lower())
            elif mine == theirs:
                same += 1
            else:
                differ.append(fn.lower())
        self.assertEqual(differ, ['skilllineability.dbc', 'spell.dbc'])
        self.assertEqual(deleted, ['charactercreatecameras.dbc', 'soundcharactermacrolines.dbc',
                                   'spellauranames.dbc', 'spelleffectnames.dbc'])
        self.assertEqual(same, 152)


@needs_client
class FullReadTests(unittest.TestCase):
    def test_read_every_dbc_and_some_big_files(self):
        c = client()
        for n in c.glob('DBFilesClient\\*.dbc'):
            d = c.read(n)
            self.assertEqual(d[:4], b'WDBC', n)
            dbc.DBC(d, n)
        # a multi-sector file from each big archive
        for p in ('Character\\Orc\\Male\\OrcMale.m2', 'World\\Maps\\Kalimdor\\Kalimdor_40_33.adt'):
            self.assertGreater(len(c.read(p)), 100000)


if __name__ == '__main__':
    unittest.main()
