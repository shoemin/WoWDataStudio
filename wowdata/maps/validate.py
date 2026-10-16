"""Validator for 1.12.1 (build 5875) map files: ADT tiles, WDT and WDL.

Every rule below is derived from what the 1.12 client's loaders actually read, taken from our decompile of
WoW.exe build 5875 (WoWDecomp/work/out61/src, names/analysis.csv, include/wow_types.h), and calibrated on
the real client data: all 2,429 ADTs, 43 WDTs and 43 WDLs of the stock client must pass with no error and
no warning (tests/test_maps.py). Written for this project (MIT); no code from Noggit or other GPL tools.

Severities
  error    the 1.12 client (or the server's extractor) would read wrong data, garble, overrun or crash.
  warning  the client copes, but the file differs from every stock 1.12 file in a way that indicates a
           wrong writer (e.g. WotLK leftovers), or the server would see something the client does not.
  info     harmless differences from the stock files.

Evidence (function names/addresses from our decompile):

WDL  CMap_LoadWdl @006944a0 reads POSITIONALLY: 8+4 bytes (MVER header + version), ONE 8-byte chunk header
     that is not checked, then 0x4000 bytes as the 64x64 offset table (index = y*64 + x). For every non-zero
     offset it seeks there, skips an 8-byte header and reads 0x442 bytes (17*17 + 16*16 int16 heights).
     => WDL-POS: MAOF must start at file offset 12 with size 0x4000; WDL-MARE: each offset -> 'MARE' with
     0x442 bytes inside the file. Chunks the client never reads (MAHO, MWMO/MWID/MODF) are reported.
WDT  CMap_LoadWdt @00694760 reads POSITIONALLY: MVER (8+4), MPHD header + 0x20 bytes, MAIN header + 0x8000
     bytes (64x64 x 8 bytes, index = y*64 + x); if MPHD flags & 1 (global-WMO map) one more header + the
     MWMO name (size taken from that header) and a header + one 0x40-byte MODF record.
     The MAIN flags become g_TileStateTable; CMap_PumpTileLoads (@006985d0) loads a tile when flags & 1 and
     only if bit 2 ("load requested", set by CMap_BeginLoadTile @006c2720) is clear => a MAIN entry with bit 2
     set in the file is never loaded.
ADT  CMapArea_BeginAsyncRead @006c1e70 reads the whole file; CMapArea_ParseAdtHeader @006c2010 takes the MHDR
     at file + 8 + MVER.size, and MCIN/MTEX/MMDX/MMID/MWMO/MWID/MDDF/MODF at MHDR data (file 0x14) + offset
     + 8 (the chunk magic is not checked). MDDF count = size / 0x24, MODF count = size >> 6.
     CMapChunk_CreateFromTile @006c2880 takes MCNK i (i = row*16 + col) at file + MCIN[i].offset.
     CMapChunk_SetupMcnkPointers @006af970: every sub-chunk is at MCNK start + header offset + 8 (MCVT 0x14,
     MCNR 0x18, MCLY 0x1C, MCRF 0x20, MCAL 0x24, MCSH 0x2C, MCSE 0x58, MCLQ 0x60); magics/sizes unchecked.
     CMapChunk_FinishLoad @006af5f0: nLayers (0x0C) records of MCLY go to CMapChunk_AddLayer @006aff80, which
     stores them in a 4-entry array (CMapChunk +0x128..+0x134, count at +0x138) => more than 4 layers
     overwrite the chunk. textureId indexes the tile's MTEX texture table. Layers with flag 0x100 own a 4-bit
     64x64 alpha map (2048 bytes) at MCAL data + ofsAlpha (TerrainAlpha_PackSingle64_* @006b0ac0/@006b0bf0,
     TerrainAlpha_Pack64_* @006b03d0/@006b0770). MCSH (512 bytes) is used when MCNK flag 1 is set.
     CMapChunk_CreatePlacedObjects @006b00f0: MCRF is nDoodadRefs (0x10) uint32 MDDF indices followed by
     nMapObjRefs (0x38) uint32 MODF indices; MDDF.nameId -> MMID[nameId] -> MMDX string, MODF.nameId ->
     MWID[nameId] -> MWMO string; nothing is range-checked.
     CMapChunk_CreateLiquids @006af760: for each of the MCNK flag bits 0x4, 0x8, 0x10, 0x20 (in this order)
     one 0xC9-dword (804-byte) liquid layer at MCLQ data; the client renders on (tile & 0xF) != 0xF.
     The server's mapextractor treats any tile byte != 0x0F as liquid, so tile bits 0x10/0x20 matter there.
     CMapChunk_CreateSoundEmitters @006af810: nSndEmitters (0x5C) records of 0x34 (52) bytes at MCSE data.
Stock files (2,429 ADTs) additionally always have: chunk order MVER MHDR MCIN MTEX MMDX MMID MWMO MWID MDDF MODF
     MCNK*256; MHDR flags and MFBO/MH2O/MTXF offsets 0; MCIN flags/asyncId 0; MCNK index = MCIN position;
     MCVT 580, MCNR 435 (+13 pad), MCLQ chunk size 0 with sizeLiquid = 8 + 804 * layers; MCNK 0x74..0x7F 0.
"""
import os
import struct

from . import formats as F

ERROR, WARNING, INFO = 'error', 'warning', 'info'
SEVERITY_ORDER = {ERROR: 0, WARNING: 1, INFO: 2}


class Finding:
    __slots__ = ('severity', 'code', 'file', 'where', 'message')

    def __init__(self, severity, code, file, where, message):
        self.severity, self.code, self.file, self.where, self.message = severity, code, file, where, message

    def __str__(self):
        w = (' [%s]' % self.where) if self.where else ''
        return '%-7s %-14s %s%s: %s' % (self.severity, self.code, self.file, w, self.message)

    def as_dict(self):
        return {k: getattr(self, k) for k in self.__slots__}


class Report:
    def __init__(self):
        self.findings = []
        self.files = 0

    def add(self, *a):
        self.findings.append(Finding(*a))

    def extend(self, other):
        self.findings.extend(other.findings)
        self.files += other.files

    def count(self, severity):
        return sum(1 for f in self.findings if f.severity == severity)

    @property
    def ok(self):
        return self.count(ERROR) == 0


# --------------------------------------------------------------------------------------------- WDL
def validate_wdl(b, name='', report=None, wdt_tiles=None):
    r = report if report is not None else Report()
    r.files += 1
    w = F.Wdl(b, name)
    if len(b) < 20 + F.WDL_MAOF_SIZE:
        r.add(ERROR, 'WDL-SIZE', name, '', 'file is %d bytes; the 1.12 loader reads 0x%x bytes of header + offset table'
              % (len(b), 20 + F.WDL_MAOF_SIZE))
        return r
    if w.mver_magic != 'MVER' or w.mver_size != 4:
        r.add(ERROR, 'WDL-MVER', name, '0', 'expected MVER of size 4, found %s size %s' % (w.mver_magic, w.mver_size))
    elif w.version != 18:
        r.add(WARNING, 'WDL-VERSION', name, '8', 'MVER version %s (stock: 18)' % w.version)
    if w.table_magic != 'MAOF' or w.table_size != F.WDL_MAOF_SIZE:
        r.add(ERROR, 'WDL-POS', name, '12',
              'the 1.12 client (CMap_LoadWdl @006944a0) reads the offset table at file offset 20, right after MVER; '
              'found chunk %s (size %s) there instead of MAOF (size 0x4000)%s'
              % (w.table_magic, w.table_size,
                 ' -- WotLK low-res WMO chunks (MWMO/MWID/MODF) before MAOF shift every tile' if w.table_magic in ('MWMO', 'MWID', 'MODF') else ''))
    bad = 0
    for i, o in enumerate(w.offsets):
        if not o:
            continue
        if o + 8 + F.WDL_MARE_DATA > len(b) or F.magic_at(b, o) != 'MARE':
            bad += 1
            if bad <= 5:
                r.add(ERROR, 'WDL-MARE', name, 'tile %d,%d' % (i % 64, i // 64),
                      'offset 0x%x does not point at a MARE chunk with 0x%x bytes inside the file (%s)'
                      % (o, F.WDL_MARE_DATA, F.magic_at(b, o)))
        elif F.u32(b, o + 4) != F.WDL_MARE_DATA:
            r.add(WARNING, 'WDL-MARE-SIZE', name, 'tile %d,%d' % (i % 64, i // 64),
                  'MARE size %d (the client reads 0x442 bytes regardless)' % F.u32(b, o + 4))
    if bad > 5:
        r.add(ERROR, 'WDL-MARE', name, '', '%d more bad tile offsets' % (bad - 5))
    names = [m for _, m, _ in w.top]
    for m in ('MWMO', 'MWID', 'MODF'):
        if m in names:
            r.add(WARNING if w.table_magic == 'MAOF' else ERROR, 'WDL-WOTLK', name, '',
                  '%s chunk present (WotLK low-resolution WMO data; no stock 1.12 WDL has it)' % m)
    if 'MAHO' in names:
        r.add(INFO, 'WDL-MAHO', name, '', 'MAHO (hole masks) present: never read by 1.12, absent from stock WDLs')
    if wdt_tiles is not None:
        a, t = set(wdt_tiles), set(w.tiles())
        if t - a:
            r.add(WARNING, 'WDL-TILES', name, '', '%d WDL tiles have no ADT in the WDT MAIN table, e.g. %s'
                  % (len(t - a), sorted(t - a)[:3]))
        if a - t:
            r.add(WARNING, 'WDL-TILES', name, '', '%d WDT tiles have no WDL heights (no far terrain), e.g. %s'
                  % (len(a - t), sorted(a - t)[:3]))
    return r


# --------------------------------------------------------------------------------------------- WDT
def validate_wdt(b, name='', report=None):
    r = report if report is not None else Report()
    r.files += 1
    w = F.Wdt(b, name)
    expected_sizes = {'MVER': 4, 'MPHD': F.WDT_MPHD_SIZE, 'MAIN': F.WDT_MAIN_SIZE, 'MODF': F.MODF_SIZE}
    for expected, pos, magic, size in w.slots:
        if magic is None:
            r.add(ERROR, 'WDT-POS', name, str(pos), 'file ends before the %s chunk the 1.12 loader reads here' % expected)
            continue
        if magic != expected:
            r.add(ERROR, 'WDT-POS', name, str(pos), 'CMap_LoadWdt @00694760 reads %s here positionally; found %s'
                  % (expected, magic))
        elif expected in expected_sizes and size != expected_sizes[expected]:
            r.add(ERROR, 'WDT-SIZE', name, str(pos), '%s size %d, the 1.12 loader reads %d bytes'
                  % (expected, size, expected_sizes[expected]))
    if w.version not in (None, 18):
        r.add(WARNING, 'WDT-VERSION', name, '8', 'MVER version %s (stock: 18)' % w.version)
    if len(b) < w.end_of_read:
        r.add(ERROR, 'WDT-TRUNC', name, '', 'file is %d bytes; the 1.12 loader reads %d' % (len(b), w.end_of_read))
    fl = w.mphd_flags or 0
    if fl & 0x4:
        r.add(ERROR, 'WDT-BIGALPHA', name, 'MPHD', 'MPHD flag 0x4 (8-bit "big" alpha maps): the 1.12 client only '
              'reads 4-bit alpha, so a map saved with this flag has garbled texture blending')
    if fl & 0x2:
        r.add(WARNING, 'WDT-MCCV', name, 'MPHD', 'MPHD flag 0x2 (MCCV vertex shading, WotLK); no stock 1.12 WDT has it')
    if fl & ~0xF:
        r.add(WARNING, 'WDT-MPHD', name, 'MPHD', 'unknown MPHD flags 0x%x (stock 1.12: only 0x1)' % (fl & ~0xF))
    if fl & 0x8:
        r.add(INFO, 'WDT-MPHD', name, 'MPHD', 'MPHD flag 0x8 (doodad sort) is ignored by 1.12; no stock WDT has it')
    bad_bits = [(i % 64, i // 64, f) for i, (f, _) in enumerate(w.main) if f & ~1]
    if bad_bits:
        sev = ERROR if any(f & 2 for _, _, f in bad_bits) else WARNING
        r.add(sev, 'WDT-MAIN', name, '', '%d MAIN entries have flags other than 0x1, e.g. tile %d,%d = 0x%x '
              '(bit 0x2 makes the 1.12 client think the tile is already being loaded: it is never loaded)'
              % (len(bad_bits), bad_bits[0][0], bad_bits[0][1], bad_bits[0][2]))
    if any(a for _, a in w.main):
        r.add(WARNING, 'WDT-MAIN', name, '', 'MAIN asyncId dwords are not 0 (stock: always 0)')
    if fl & 1:
        if not w.global_wmo_name:
            r.add(ERROR, 'WDT-GLOBALWMO', name, '', 'MPHD flag 0x1 (global WMO map) but the MWMO name is empty')
    else:
        trailing = [m for p, m, _ in w.top if p >= w.after_main]
        if trailing and trailing != ['MWMO']:
            r.add(INFO, 'WDT-TAIL', name, '', 'chunks after MAIN: %s (stock: one empty MWMO; not read by 1.12)' % trailing)
        if not trailing:
            r.add(INFO, 'WDT-TAIL', name, '', 'no (empty) MWMO after MAIN (stock files have one; 1.12 does not read it)')
    return r


# --------------------------------------------------------------------------------------------- ADT
STOCK_TOP_ORDER = ['MVER', 'MHDR', 'MCIN', 'MTEX', 'MMDX', 'MMID', 'MWMO', 'MWID', 'MDDF', 'MODF']
SUB_FIELDS = [('MCVT', 'ofsHeight'), ('MCNR', 'ofsNormal'), ('MCLY', 'ofsLayer'), ('MCRF', 'ofsRefs'),
              ('MCSH', 'ofsShadow'), ('MCAL', 'ofsAlpha'), ('MCLQ', 'ofsLiquid'), ('MCSE', 'ofsSndEmitters')]
KNOWN_MCNK_FLAGS = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x8000


def _limit(r, counter, key, max_per_file=8):
    counter[key] = counter.get(key, 0) + 1
    return counter[key] <= max_per_file


def validate_adt(b, name='', report=None, max_per_code=8):
    r = report if report is not None else Report()
    r.files += 1
    seen = {}

    def add(sev, code, where, msg):
        if _limit(r, seen, code, max_per_code):
            r.add(sev, code, name, where, msg)
        elif seen[code] == max_per_code + 1:
            r.add(sev, code, name, '', '(more %s findings in this file are not listed)' % code)

    if len(b) < 0x14 + 64:
        add(ERROR, 'ADT-SIZE', '', 'file too small (%d bytes)' % len(b))
        return r
    a = F.Adt(b, name)
    # --- top level: MVER / MHDR position (CMapArea_ParseAdtHeader)
    if F.magic_at(b, 0) != 'MVER' or a.mver_size != 4:
        add(ERROR, 'ADT-MVER', '0', 'expected MVER of size 4 at offset 0 (the client finds MHDR at 8 + MVER.size); '
            'found %s size %s' % (F.magic_at(b, 0), a.mver_size))
    elif a.version != 18:
        add(WARNING, 'ADT-VERSION', '8', 'MVER version %s (stock: 18)' % a.version)
    if F.magic_at(b, a.mhdr_pos) != 'MHDR':
        add(ERROR, 'ADT-MHDR', str(a.mhdr_pos), 'no MHDR right after MVER (found %s)' % F.magic_at(b, a.mhdr_pos))
        return r
    if F.u32(b, a.mhdr_pos + 4) != 64:
        add(WARNING, 'ADT-MHDR', str(a.mhdr_pos), 'MHDR size %d (stock: 64)' % F.u32(b, a.mhdr_pos + 4))
    for field, magic in F.MHDR_CHUNKS:
        hp, size, _ = a.blocks.get(magic, (None, None, b''))
        if not a.mhdr.get(field):
            add(ERROR, 'ADT-MHDR-OFS', 'MHDR.' + field, 'offset is 0: the client would read %s from the MHDR itself' % magic)
            continue
        if size is None or hp + 8 + size > len(b):
            add(ERROR, 'ADT-MHDR-OFS', 'MHDR.' + field, '%s at 0x%x runs past the end of the file' % (magic, hp))
            continue
        if F.magic_at(b, hp) != magic:
            add(ERROR, 'ADT-MHDR-OFS', 'MHDR.' + field, 'points at %s, not %s' % (F.magic_at(b, hp), magic))
    for field in ('ofsMFBO', 'ofsMH2O', 'ofsMTXF'):
        if a.mhdr.get(field):
            add(WARNING, 'ADT-WOTLK', 'MHDR.' + field, 'non-zero (%s is a WotLK chunk; stock 1.12: 0)' % field[3:])
    if a.mhdr.get('flags'):
        add(WARNING, 'ADT-MHDR', 'MHDR.flags', '0x%x (stock 1.12: 0; flag 1 = MFBO, WotLK)' % a.mhdr['flags'])
    # --- sequential walk: order, truncation, WotLK chunks
    top = []
    end_ok = True
    for pos, m, s in a.top:
        top.append(m)
        if pos + 8 + s > len(b):
            add(ERROR, 'ADT-TRUNC', str(pos), 'chunk %s (size %d) runs past the end of the file' % (m, s))
            end_ok = False
        if m in F.WOTLK_TOP_CHUNKS:
            add(ERROR if m in ('MH2O', 'MFBO', 'MTXF') else WARNING, 'ADT-WOTLK', str(pos),
                '%s is not a 1.12 chunk (later expansions only)' % m)
    head = [m for m in top if m != 'MCNK']
    if head != STOCK_TOP_ORDER or top.count('MCNK') != 256 or (top[:10] != STOCK_TOP_ORDER):
        add(WARNING, 'ADT-ORDER', '', 'chunk order %s + %d MCNK differs from the stock order %s + 256 MCNK '
            '(the client locates chunks through MHDR/MCIN offsets, so this alone is harmless)'
            % (head, top.count('MCNK'), STOCK_TOP_ORDER))
    # --- string tables and placements
    ntex = len(a.textures)
    mmdx_starts, mwmo_starts = set(a.mmdx), set(a.mwmo)
    for i, o in enumerate(a.mmid):
        if o not in mmdx_starts:
            add(ERROR, 'ADT-MMID', 'MMID[%d]' % i, 'offset %d is not the start of an MMDX name' % o)
    for i, o in enumerate(a.mwid):
        if o not in mwmo_starts:
            add(ERROR, 'ADT-MWID', 'MWID[%d]' % i, 'offset %d is not the start of an MWMO name' % o)
    for magic, rec, lst in (('MDDF', F.MDDF_SIZE, a.mddf), ('MODF', F.MODF_SIZE, a.modf)):
        hp, size, _ = a.blocks.get(magic, (None, 0, b''))
        if size and size % rec:
            add(ERROR, 'ADT-' + magic, magic, 'size %d is not a multiple of %d bytes' % (size, rec))
    for i, d in enumerate(a.mddf):
        if d['nameId'] >= len(a.mmid):
            add(ERROR, 'ADT-MDDF', 'MDDF[%d]' % i, 'nameId %d out of range (MMID has %d entries)' % (d['nameId'], len(a.mmid)))
        if d['flags']:
            add(INFO, 'ADT-MDDF-FLAGS', 'MDDF[%d]' % i, 'flags 0x%x (stock 1.12: 0; not read by the client)' % d['flags'])
        if d['scale'] == 0:
            add(WARNING, 'ADT-MDDF', 'MDDF[%d]' % i, 'scale 0 (invisible doodad)')
    for i, d in enumerate(a.modf):
        if d['nameId'] >= len(a.mwid):
            add(ERROR, 'ADT-MODF', 'MODF[%d]' % i, 'nameId %d out of range (MWID has %d entries)' % (d['nameId'], len(a.mwid)))
    for kind, lst in (('MDDF', a.mddf), ('MODF', a.modf)):
        uids = {}
        for i, d in enumerate(lst):
            uids.setdefault(d['uid'], []).append(i)
        dup = {u: v for u, v in uids.items() if len(v) > 1}
        if dup:
            u, v = next(iter(dup.items()))
            add(WARNING, 'ADT-UID', kind, '%d unique ids used more than once in this tile, e.g. %d at %s (the client '
                'hashes placements by unique id: the duplicates are not placed)' % (len(dup), u, v))
    # --- MCIN / MCNK
    if len(a.mcin) != 256:
        add(ERROR, 'ADT-MCIN', 'MCIN', '%d entries, the client indexes 256' % len(a.mcin))
    starts = set()
    for i, (ent, c) in enumerate(zip(a.mcin, a.mcnk)):
        where = 'MCNK %d (%d,%d)' % (i, i % 16, i // 16)
        if c is None or c.magic != 'MCNK':
            add(ERROR, 'ADT-MCIN', where, 'MCIN offset 0x%x does not point at an MCNK chunk' % ent['offset'])
            continue
        if c.offset in starts:
            add(ERROR, 'ADT-MCIN', where, 'two MCIN entries point at the same MCNK')
        starts.add(c.offset)
        if c.end > len(b):
            add(ERROR, 'ADT-TRUNC', where, 'MCNK runs past the end of the file')
            continue
        if ent['size'] != c.size + 8:
            add(WARNING, 'ADT-MCIN', where, 'MCIN size %d != MCNK size + 8 (%d)' % (ent['size'], c.size + 8))
        if ent['flags'] or ent['asyncId']:
            add(WARNING, 'ADT-MCIN', where, 'MCIN flags/asyncId %d/%d (stock: 0/0; the client uses bit 0 itself)'
                % (ent['flags'], ent['asyncId']))
        if not c.h or c.size < F.MCNK_HEADER_SIZE:
            add(ERROR, 'ADT-MCNK', where, 'MCNK smaller than its 0x80-byte header')
            continue
        _validate_mcnk(b, a, c, i, ntex, add, where)
    return r


def _validate_mcnk(b, a, c, i, ntex, add, where):
    h = c.h
    fl = h['flags']
    if (h['indexX'], h['indexY']) != (i % 16, i // 16):
        add(WARNING, 'ADT-MCNK-IDX', where, 'header index %d,%d (the client places the chunk by its MCIN position)'
            % (h['indexX'], h['indexY']))
    if fl & 0x10000:
        add(ERROR, 'ADT-WOTLK', where, 'MCNK flag 0x10000 (WotLK high-resolution holes): 1.12 reads the 16-bit hole mask')
    if fl & ~(KNOWN_MCNK_FLAGS | 0x10000):
        add(WARNING, 'ADT-MCNK-FLAGS', where, 'unknown MCNK flags 0x%x' % (fl & ~(KNOWN_MCNK_FLAGS | 0x10000)))
    holes = h['holes']
    if holes > 0xFFFF and holes != 0xFFFFFFFF:
        add(WARNING, 'ADT-HOLES', where, 'hole mask 0x%x uses the high 16 bits (1.12: 16-bit mask)' % holes)
    elif holes == 0xFFFFFFFF:
        add(INFO, 'ADT-HOLES', where, 'hole mask 0xFFFFFFFF (whole-chunk hole; stock files only use the low 16 bits)')
    if h['ofsMCCV']:
        add(WARNING, 'ADT-WOTLK', where, 'ofsMCCV is set (MCCV vertex colours are WotLK)')
    if h['unused78'] or h['unused7C']:
        add(INFO, 'ADT-MCNK-TAIL', where, 'header bytes 0x78..0x7F not zero')
    end = c.end
    # sub-chunk extents: (name, data start, data length the client uses)
    nl = h['nLayers']
    nrefs = h['nDoodadRefs'] + h['nMapObjRefs']
    nliq = sum(1 for bit in F.MCNK_LIQUID_FLAGS if fl & bit)
    need = {
        'MCVT': F.MCVT_SIZE, 'MCNR': F.MCNR_SIZE, 'MCLY': F.MCLY_SIZE * nl, 'MCRF': 4 * nrefs,
        'MCSH': F.MCSH_SIZE if fl & 1 else 0, 'MCLQ': F.MCLQ_LAYER_SIZE * nliq,
        'MCSE': F.MCSE_SIZE * h['nSndEmitters'],
    }
    if nl > 4:
        add(ERROR, 'ADT-MCLY', where, '%d texture layers: the 1.12 client stores at most 4 (CMapChunk_AddLayer '
            'writes past its 4-entry layer array)' % nl)
    # MCLY records
    layers = []
    ly_ofs = c.offset + h['ofsLayer'] + 8
    for L in range(min(nl, 8)):
        p = ly_ofs + L * 16
        if p + 16 > end:
            break
        layers.append(struct.unpack_from('<4I', b, p))
    alpha_need = 0
    alpha_ofs = []
    for L, (tid, lf, oa, eff) in enumerate(layers):
        if tid >= ntex:
            add(ERROR, 'ADT-MCLY', where, 'layer %d textureId %d out of range (MTEX has %d names)' % (L, tid, ntex))
        if lf & 0x200:
            add(ERROR, 'ADT-ALPHA', where, 'layer %d has MCLY flag 0x200 (compressed alpha, WotLK): 1.12 reads raw '
                '4-bit alpha' % L)
        if lf & ~0x7FF:
            add(WARNING, 'ADT-MCLY', where, 'layer %d unknown MCLY flags 0x%x' % (L, lf))
        if L == 0 and lf & 0x100:
            add(WARNING, 'ADT-MCLY', where, 'layer 0 has an alpha map (flag 0x100); stock files never do')
        if lf & 0x100:
            alpha_ofs.append((L, oa))
            alpha_need = max(alpha_need, oa + F.ALPHA_LAYER_SIZE)
    need['MCAL'] = alpha_need
    # big-alpha detection: alpha layers spaced 4096 apart / MCAL sized 4096 per layer
    mcal_hdr = c.offset + h['ofsAlpha']
    mcal_size_field = F.u32(b, mcal_hdr + 4) if mcal_hdr + 8 <= end else None
    if alpha_ofs:
        steps = [o for _, o in alpha_ofs]
        if any(o % F.BIG_ALPHA_LAYER_SIZE == 0 and o for o in steps) and all(o % F.BIG_ALPHA_LAYER_SIZE == 0 for o in steps) \
                and h['sizeAlpha'] - 8 >= F.BIG_ALPHA_LAYER_SIZE * len(steps):
            add(ERROR, 'ADT-BIGALPHA', where, 'alpha layers are 4096 bytes apart (8-bit "big" alpha): 1.12 reads 2048-byte '
                '4-bit layers (texture blending garbled)')
        elif h['sizeAlpha'] - 8 not in (F.ALPHA_LAYER_SIZE * len(steps),) and h['sizeAlpha'] - 8 > F.ALPHA_LAYER_SIZE * len(steps):
            add(WARNING, 'ADT-ALPHA', where, 'sizeAlpha %d is larger than %d alpha layers of 2048 bytes'
                % (h['sizeAlpha'], len(steps)))
    # locate every sub-chunk the client uses and check bounds/overlap/magic
    spans = []
    for magic, field in F_SUBS:
        n = need[magic]
        ofs = h[field]
        if n == 0 and magic not in ('MCVT', 'MCNR'):
            if ofs and (c.offset + ofs + 8 > end + 0):
                add(WARNING, 'ADT-SUBOFS', where, '%s offset points outside the MCNK (unused here)' % magic)
            continue
        if not ofs:
            add(ERROR, 'ADT-SUBOFS', where, '%s needed (%d bytes) but its offset is 0' % (magic, n))
            continue
        hp = c.offset + ofs
        start, stop = hp + 8, hp + 8 + n
        if hp < c.offset + 8 + F.MCNK_HEADER_SIZE or stop > end:
            add(ERROR, 'ADT-SUBOFS', where, '%s data 0x%x..0x%x lies outside the MCNK data (0x%x..0x%x)'
                % (magic, start, stop, c.offset + 8 + F.MCNK_HEADER_SIZE, end))
            continue
        m = F.magic_at(b, hp)
        if m != magic:
            add(WARNING, 'ADT-SUBMAGIC', where, '%s offset points at %s (the client does not check, but every stock '
                'file has the magic there)' % (magic, m))
        spans.append((start, stop, magic))
    spans.sort()
    for (s1, e1, m1), (s2, e2, m2) in zip(spans, spans[1:]):
        if s2 < e1:
            add(ERROR, 'ADT-OVERLAP', where, '%s and %s overlap (0x%x < 0x%x)' % (m1, m2, s2, e1))
    # fixed sizes recorded in the sub-chunk headers (informative)
    for magic, field, expect in (('MCVT', 'ofsHeight', F.MCVT_SIZE), ('MCNR', 'ofsNormal', F.MCNR_SIZE)):
        hp = c.offset + h[field]
        if hp + 8 <= end and F.magic_at(b, hp) == magic and F.u32(b, hp + 4) != expect:
            add(WARNING, 'ADT-SUBSIZE', where, '%s size %d (stock and 1.12: %d)' % (magic, F.u32(b, hp + 4), expect))
    # MCRF references
    rf = c.offset + h['ofsRefs'] + 8
    if rf + 4 * nrefs <= end:
        refs = struct.unpack_from('<%dI' % nrefs, b, rf)
        for k, ref in enumerate(refs):
            if k < h['nDoodadRefs']:
                if ref >= len(a.mddf):
                    add(ERROR, 'ADT-MCRF', where, 'doodad ref %d -> MDDF[%d] out of range (%d entries)' % (k, ref, len(a.mddf)))
            elif ref >= len(a.modf):
                add(ERROR, 'ADT-MCRF', where, 'WMO ref %d -> MODF[%d] out of range (%d entries)' % (k, ref, len(a.modf)))
    # MCLQ
    sl = h['sizeLiquid']
    if nliq:
        if sl != 8 + F.MCLQ_LAYER_SIZE * nliq:
            add(WARNING, 'ADT-MCLQ', where, 'sizeLiquid %d, expected 8 + 804 x %d layers' % (sl, nliq))
        base = c.offset + h['ofsLiquid'] + 8
        for q in range(nliq):
            p = base + q * F.MCLQ_LAYER_SIZE + F.MCLQ_TILES_OFS
            if p + 64 > end:
                break
            tiles = b[p:p + 64]
            junk = sum(1 for t in tiles if t & 0x30)
            if junk:
                add(WARNING, 'ADT-MCLQ-BITS', where, 'liquid layer %d: %d tile bytes have bits 0x10/0x20 set (stock: '
                    'never; the server mapextractor treats tile != 0x0F as water -> phantom server water)' % (q, junk))
    elif sl not in (0, 8):
        add(WARNING, 'ADT-MCLQ', where, 'sizeLiquid %d but no liquid flag (0x4/0x8/0x10/0x20) is set' % sl)
    # MCSE: the header MCSE size, when the magic is there, must agree with 52-byte records
    n_se = h['nSndEmitters']
    hp = c.offset + h['ofsSndEmitters']
    if n_se and hp + 8 <= end and F.magic_at(b, hp) == 'MCSE':
        sz = F.u32(b, hp + 4)
        if sz != F.MCSE_SIZE * n_se:
            add(ERROR, 'ADT-MCSE', where, 'MCSE holds %d bytes for %d emitters; 1.12 records are 52 bytes (%s)'
                % (sz, n_se, '28-byte WotLK records' if sz == 28 * n_se else 'size mismatch'))
    # sub-chunks in the MCNK that the client never addresses (WotLK leftovers such as MCCV)
    # Coverage of the addressed sub-chunks (header + max(data the client uses, declared size when sane));
    # MCNR is followed by Blizzard's 13 pad bytes. Whatever is left is never read by the client.
    pos = c.offset + 8 + F.MCNK_HEADER_SIZE
    cover = []
    for magic, field in F_SUBS:
        if not h[field]:
            continue
        hp = c.offset + h[field]
        if hp < pos or hp + 8 > end:
            continue
        declared = F.u32(b, hp + 4) if F.magic_at(b, hp) == magic else 0
        if declared > end - hp - 8:
            declared = 0
        size = max(need.get(magic, 0), declared) + (F.MCNR_PAD if magic == 'MCNR' else 0)
        cover.append((hp, min(end, hp + 8 + size)))
    cover.sort()
    gaps = []
    cur = pos
    for s, e in cover:
        if s > cur:
            gaps.append((cur, s))
        cur = max(cur, e)
    if cur < end:
        gaps.append((cur, end))
    for s, e in gaps:
        m = F.magic_at(b, s) if e - s >= 8 else None
        if m in F.WOTLK_SUB_CHUNKS:
            add(WARNING, 'ADT-WOTLK', where, '%s sub-chunk (WotLK) in the MCNK at 0x%x; 1.12 never reads it' % (m, s))
        elif e - s >= 4:
            add(INFO, 'ADT-UNREAD', where, '%d bytes at 0x%x are not addressed by any MCNK offset (%s)'
                % (e - s, s, m or 'data'))


F_SUBS = [('MCVT', 'ofsHeight'), ('MCNR', 'ofsNormal'), ('MCLY', 'ofsLayer'), ('MCRF', 'ofsRefs'),
          ('MCSH', 'ofsShadow'), ('MCAL', 'ofsAlpha'), ('MCLQ', 'ofsLiquid'), ('MCSE', 'ofsSndEmitters')]


# --------------------------------------------------------------------------------------------- dispatch
def kind_of(name):
    low = name.lower()
    for ext in ('.adt', '.wdt', '.wdl'):
        if low.endswith(ext):
            return ext[1:]
    return None


def validate_bytes(b, name, report=None, wdt_tiles=None):
    k = kind_of(name)
    if k == 'adt':
        return validate_adt(b, name, report)
    if k == 'wdt':
        return validate_wdt(b, name, report)
    if k == 'wdl':
        return validate_wdl(b, name, report, wdt_tiles=wdt_tiles)
    raise ValueError('not a map file: %s' % name)


def validate_map_set(items, report=None):
    """Validate one map's files together. `items`: iterable of (name, loader) where loader() returns the bytes.
    Adds the cross-file checks: WDT MAIN tiles <-> ADT files present, WDT <-> WDL tile sets."""
    r = report if report is not None else Report()
    items = list(items)
    wdt = [(n, ld) for n, ld in items if kind_of(n) == 'wdt']
    wdt_tiles = None
    wdt_name = None
    if wdt:
        wdt_name, ld = wdt[0]
        b = ld()
        validate_wdt(b, wdt_name, r)
        w = F.Wdt(b)
        wdt_tiles = w.tiles() if not (w.mphd_flags or 0) & 1 else []
    adts = {}
    for n, ld in items:
        k = kind_of(n)
        if k == 'adt':
            validate_adt(ld(), n, r)
            xy = _tile_xy(n)
            if xy:
                adts[xy] = n
        elif k == 'wdl':
            validate_wdl(ld(), n, r, wdt_tiles=wdt_tiles)
    if wdt_tiles is not None and adts:
        missing = sorted(set(wdt_tiles) - set(adts))
        extra = sorted(set(adts) - set(wdt_tiles))
        if missing:
            r.add(INFO, 'MAP-TILES', wdt_name, '', '%d tiles flagged in MAIN have no ADT in this set, e.g. %s '
                  '(fine if they come from the client archives; the client fails on a flagged tile with no file)'
                  % (len(missing), missing[:3]))
        if extra:
            r.add(WARNING, 'MAP-TILES', wdt_name, '', '%d ADTs are not flagged in the WDT MAIN table, so the client never '
                  'loads them, e.g. %s' % (len(extra), [adts[t] for t in extra[:3]]))
    return r


def _tile_xy(name):
    base = os.path.basename(name.replace('\\', '/'))
    parts = base[:-4].split('_')
    try:
        return int(parts[-2]), int(parts[-1])
    except (ValueError, IndexError):
        return None
