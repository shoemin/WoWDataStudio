"""Map validator and diff: every stock 1.12 map file must pass; known-bad mutations must be caught."""
import os
import struct
import unittest

from common import CFG, needs_client, client, data_dir_snapshot, tempdir

from wowdata.maps import formats as F
from wowdata.maps import validate as V
from wowdata.maps import diff as D

TILE = 'World\\Maps\\Azeroth\\Azeroth_30_50.adt'   # has sound emitters, liquids, 3-4 layers, doodads + WMOs
WDT = 'World\\Maps\\Azeroth\\Azeroth.wdt'
WDL = 'World\\Maps\\Azeroth\\Azeroth.wdl'


def codes(report, severity=None):
    return {f.code for f in report.findings if severity is None or f.severity == severity}


def put32(b, pos, v):
    struct.pack_into('<I', b, pos, v)


@needs_client
class TestStockFiles(unittest.TestCase):
    def test_all_stock_map_files_pass(self):
        """All 2,429 ADTs, 43 WDTs and 43 WDLs of the stock client: no error, no warning (calibration)."""
        before = data_dir_snapshot()
        cd = client()
        r = V.Report()
        for pattern in ('World\\Maps\\*.wdt', 'World\\Maps\\*.wdl', 'World\\Maps\\*.adt'):
            for n in cd.glob(pattern):
                V.validate_bytes(cd.read(n), n, r)
        self.assertGreaterEqual(r.files, 2515)
        bad = [str(f) for f in r.findings if f.severity in (V.ERROR, V.WARNING)]
        self.assertEqual(bad, [], '\n'.join(bad[:20]))
        self.assertEqual(before, data_dir_snapshot(), 'the client Data folder changed')

    def test_map_set_cross_checks(self):
        cd = client()
        items = [(n, (lambda n=n: cd.read(n))) for n in (WDT, WDL, TILE)]
        r = V.validate_map_set(items)
        self.assertTrue(r.ok)
        self.assertFalse(codes(r, V.WARNING))


@needs_client
class TestMutations(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cd = client()
        cls.adt = cd.read(TILE)
        cls.wdt = cd.read(WDT)
        cls.wdl = cd.read(WDL)
        cls.A = F.Adt(cls.adt)

    def chunk(self, pred):
        return next(c for c in self.A.mcnk if pred(c))

    def check(self, data, code, severity=V.ERROR, name=TILE):
        r = V.validate_bytes(bytes(data), name)
        self.assertIn(code, codes(r, severity), '\n'.join(map(str, r.findings)))
        return r

    # ---- WDL
    def test_wdl_wotlk_chunks_before_maof(self):
        """What Cinder wrote before fix 1: MVER, MWMO, MWID, MODF (empty), MAOF ... -> table read 6 dwords early."""
        b = self.wdl
        extra = b''.join(m[::-1].encode() + b'\0\0\0\0' for m in ('MWMO', 'MWID', 'MODF'))
        shifted = bytearray(b[:12] + extra + b[12:])
        # fix up the offsets so they are right for the shifted layout (as Cinder's writer did)
        for i in range(4096):
            p = 12 + len(extra) + 8 + i * 4
            o = struct.unpack_from('<I', shifted, p)[0]
            if o:
                put32(shifted, p, o + len(extra))
        r = self.check(shifted, 'WDL-POS', name=WDL)
        self.assertIn('WDL-WOTLK', codes(r, V.ERROR))

    def test_wdl_bad_offset(self):
        b = bytearray(self.wdl)
        i = next(i for i in range(4096) if struct.unpack_from('<I', b, 20 + i * 4)[0])
        put32(b, 20 + i * 4, len(b) - 10)
        self.check(b, 'WDL-MARE', name=WDL)

    def test_wdl_maho_is_info(self):
        b = self.wdl + b'OHAM' + struct.pack('<I', 32) + b'\0' * 32
        r = V.validate_bytes(b, WDL)
        self.assertTrue(r.ok)
        self.assertIn('WDL-MAHO', codes(r, V.INFO))

    # ---- WDT
    def test_wdt_big_alpha_flag(self):
        b = bytearray(self.wdt)
        put32(b, 20, F.u32(b, 20) | 4)
        self.check(b, 'WDT-BIGALPHA', name=WDT)

    def test_wdt_main_bit2(self):
        b = bytearray(self.wdt)
        main = 12 + 8 + 32 + 8
        i = next(i for i in range(4096) if F.u32(b, main + i * 8) & 1)
        put32(b, main + i * 8, 3)
        self.check(b, 'WDT-MAIN', name=WDT)

    def test_wdt_wrong_position(self):
        b = bytearray(self.wdt)
        b[12:16] = b'XXXX'
        self.check(b, 'WDT-POS', name=WDT)

    # ---- ADT
    def test_five_layers(self):
        c = self.chunk(lambda c: c.h['nLayers'] == 4)
        b = bytearray(self.adt)
        put32(b, c.offset + 8 + 0x0C, 5)
        self.check(b, 'ADT-MCLY')

    def test_texture_id_out_of_range(self):
        c = self.chunk(lambda c: c.h['nLayers'] >= 1)
        b = bytearray(self.adt)
        put32(b, c.offset + c.h['ofsLayer'] + 8, 999)
        self.check(b, 'ADT-MCLY')

    def test_compressed_alpha_flag(self):
        c = self.chunk(lambda c: c.h['nLayers'] >= 2)
        b = bytearray(self.adt)
        p = c.offset + c.h['ofsLayer'] + 8 + 16 + 4
        put32(b, p, F.u32(b, p) | 0x200)
        self.check(b, 'ADT-ALPHA')

    def test_big_alpha_layers(self):
        c = self.chunk(lambda c: c.h['nLayers'] == 3)
        b = bytearray(self.adt)
        for L, ofs in ((1, 0), (2, 4096)):
            put32(b, c.offset + c.h['ofsLayer'] + 8 + 16 * L + 8, ofs)
        put32(b, c.offset + 8 + 0x28, 8 + 8192)
        self.check(b, 'ADT-BIGALPHA')

    def test_mcrf_out_of_range(self):
        c = self.chunk(lambda c: c.h['nDoodadRefs'] and c.h['nMapObjRefs'])
        b = bytearray(self.adt)
        rf = c.offset + c.h['ofsRefs'] + 8
        put32(b, rf, len(self.A.mddf) + 5)
        put32(b, rf + 4 * c.h['nDoodadRefs'], len(self.A.modf))
        r = self.check(b, 'ADT-MCRF')
        self.assertGreaterEqual(sum(1 for f in r.findings if f.code == 'ADT-MCRF'), 2)

    def test_mcse_wotlk_records(self):
        """What Cinder wrote before fix 4: 28-byte records with the same count."""
        c = self.chunk(lambda c: c.h['nSndEmitters'])
        b = bytearray(self.adt)
        put32(b, c.offset + c.h['ofsSndEmitters'] + 4, 28 * c.h['nSndEmitters'])
        r = self.check(b, 'ADT-MCSE')
        self.assertIn('28-byte', ' '.join(f.message for f in r.findings))

    def test_mcse_count_overruns_chunk(self):
        c = self.chunk(lambda c: c.h['nSndEmitters'])
        b = bytearray(self.adt)
        put32(b, c.offset + 8 + 0x5C, 400)
        self.check(b, 'ADT-SUBOFS')

    def test_mclq_garbage_tile_bits(self):
        """What Cinder wrote before fix 6 (uninitialised tile bits) -> phantom server water."""
        c = self.chunk(lambda c: c.h['flags'] & 0x3C)
        b = bytearray(self.adt)
        p = c.offset + c.h['ofsLiquid'] + 8 + F.MCLQ_TILES_OFS
        b[p] |= 0x20
        self.check(b, 'ADT-MCLQ-BITS', V.WARNING)

    def test_liquid_flag_without_data(self):
        c = self.chunk(lambda c: c.h['flags'] & 0x3C)
        b = bytearray(self.adt)
        put32(b, c.offset + 8, c.h['flags'] | 0x3C)   # 4 layers claimed
        r = V.validate_bytes(bytes(b), TILE)
        self.assertTrue(codes(r) & {'ADT-MCLQ', 'ADT-SUBOFS', 'ADT-OVERLAP'}, '\n'.join(map(str, r.findings)))

    def test_high_res_holes_flag(self):
        b = bytearray(self.adt)
        c = self.A.mcnk[0]
        put32(b, c.offset + 8, c.h['flags'] | 0x10000)
        self.check(b, 'ADT-WOTLK')

    def test_mh2o_offset_and_chunk(self):
        b = bytearray(self.adt)
        put32(b, 0x14 + 0x28, len(b) - 0x14)            # MHDR.ofsMH2O -> appended chunk
        b += b'O2HM' + struct.pack('<I', 16) + b'\0' * 16
        r = self.check(b, 'ADT-WOTLK')
        self.assertIn('ADT-WOTLK', codes(r, V.WARNING))

    def test_mcin_broken(self):
        b = bytearray(self.adt)
        mcin = self.A.blocks['MCIN'][0] + 8
        put32(b, mcin + 16 * 7, 12)
        self.check(b, 'ADT-MCIN')

    def test_truncated(self):
        r = V.validate_bytes(self.adt[:len(self.adt) // 2], TILE)
        self.assertFalse(r.ok)

    def test_mddf_name_out_of_range(self):
        b = bytearray(self.adt)
        mddf = self.A.blocks['MDDF'][0] + 8
        put32(b, mddf, 100000)
        self.check(b, 'ADT-MDDF')


@needs_client
class TestDiff(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.adt = client().read(TILE)
        cls.A = F.Adt(cls.adt)

    def test_identical(self):
        d = D.diff_bytes(self.adt, self.adt, TILE)
        self.assertTrue(d.identical_bytes and d.same_content)

    def test_height_change(self):
        c = self.A.mcnk[17]
        b = bytearray(self.adt)
        p = c.offset + c.h['ofsHeight'] + 8 + 4 * 40
        struct.pack_into('<f', b, p, struct.unpack_from('<f', b, p)[0] + 2.5)
        d = D.diff_bytes(self.adt, bytes(b), TILE)
        self.assertFalse(d.same_content)
        self.assertTrue(any('MCVT: 1 of 145' in m and '2.5' in m for _, m in d.lines), d.lines)

    def test_alpha_and_liquid_and_emitter_changes(self):
        b = bytearray(self.adt)
        ca = next(c for c in self.A.mcnk if c.h['nLayers'] >= 2)
        oa = F.u32(b, ca.offset + ca.h['ofsLayer'] + 8 + 16 + 8)
        p = ca.offset + ca.h['ofsAlpha'] + 8 + oa
        b[p] ^= 0x11
        cl = next(c for c in self.A.mcnk if c.h['flags'] & 0x3C)
        b[cl.offset + cl.h['ofsLiquid'] + 8 + F.MCLQ_TILES_OFS] ^= 0x40
        cs = next(c for c in self.A.mcnk if c.h['nSndEmitters'])
        b[cs.offset + cs.h['ofsSndEmitters'] + 8 + 8] ^= 1
        d = D.diff_bytes(self.adt, bytes(b), TILE)
        text = '\n'.join(m for _, m in d.lines)
        self.assertIn('MCAL alpha layer 1: 2 of 4096 texels changed', text)
        self.assertIn('tile flag bytes', text)
        self.assertIn('MCSE', text)

    def test_moved_doodad(self):
        b = bytearray(self.adt)
        mddf = self.A.blocks['MDDF'][0] + 8
        struct.pack_into('<f', b, mddf + 8, struct.unpack_from('<f', b, mddf + 8)[0] + 1.0)
        d = D.diff_bytes(self.adt, bytes(b), TILE)
        self.assertEqual([s for s, _ in d.lines], ['MDDF'])
        self.assertIn('uid %d' % self.A.mddf[0]['uid'], d.lines[0][1])

    def test_wdl_and_wdt(self):
        wdl = client().read(WDL)
        b = bytearray(wdl)
        o = next(F.u32(b, 20 + i * 4) for i in range(4096) if F.u32(b, 20 + i * 4))
        struct.pack_into('<h', b, o + 8, struct.unpack_from('<h', b, o + 8)[0] + 7)
        d = D.diff_bytes(wdl, bytes(b), WDL)
        self.assertTrue(any('1 of 545 heights changed (max 7)' in m for _, m in d.lines), d.lines)
        wdt = client().read(WDT)
        b = bytearray(wdt)
        put32(b, 20, 4)
        d = D.diff_bytes(wdt, bytes(b), WDT)
        self.assertIn('MPHD', [s for s, _ in d.lines])


@needs_client
class TestCli(unittest.TestCase):
    def test_validate_and_diff_on_disk(self):
        import contextlib
        import io
        with contextlib.redirect_stdout(io.StringIO()):
            self._run()

    def _run(self):
        from wowdata.maps import cli as M
        import argparse
        d = tempdir()
        maps = os.path.join(d, 'World', 'Maps', 'Azeroth')
        os.makedirs(maps)
        good = client().read(TILE)
        path = os.path.join(maps, 'Azeroth_30_50.adt')
        with open(path, 'wb') as f:
            f.write(good)
        factory = lambda cfg: client()
        ns = argparse.Namespace(paths=[d], all_client=False, show_info=False, json=True, limit=50)
        self.assertEqual(M.cmd_validate(CFG, ns, factory), 0)
        ns = argparse.Namespace(a=path, b=None, layout=False, json=True, limit=10)
        self.assertEqual(M.cmd_diff(CFG, ns, factory), 0)        # same as the client copy
        bad = bytearray(good)
        c = F.Adt(good).mcnk[3]
        put32(bad, c.offset + 8 + 0x0C, 7)
        with open(path, 'wb') as f:
            f.write(bad)
        ns = argparse.Namespace(paths=[d], all_client=False, show_info=False, json=True, limit=50)
        self.assertEqual(M.cmd_validate(CFG, ns, factory), 1)
        ns = argparse.Namespace(a=d, b=None, layout=False, json=True, limit=10)
        self.assertEqual(M.cmd_diff(CFG, ns, factory), 1)


if __name__ == '__main__':
    unittest.main()
