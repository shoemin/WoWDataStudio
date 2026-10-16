"""Readers for the 1.12.1 (build 5875) map files: ADT tiles, WDT and WDL.

Everything here reads the files the way the 1.12 client does (see validate.py for the evidence from our
decompile). Chunk magics are stored reversed on disk ('REVM' for MVER); they are returned forward here.

The readers never raise on malformed input: they record what they could not read in `.problems`, so the
validator can report it. Written for this project from our decompile and the real client files (MIT).
"""
import struct

MCNK_HEADER_SIZE = 0x80
MCVT_SIZE = 145 * 4           # 9x9 + 8x8 float heights
MCNR_SIZE = 145 * 3           # signed-byte normals (Blizzard pads 13 bytes after the chunk)
MCNR_PAD = 13
MCSH_SIZE = 64 * 64 // 8      # 1-bit shadow map
ALPHA_LAYER_SIZE = 64 * 64 // 2   # 4-bit alpha, 2048 bytes
BIG_ALPHA_LAYER_SIZE = 64 * 64    # 8-bit alpha (WotLK), 4096 bytes
MCLY_SIZE = 16
MCSE_SIZE = 0x34              # 52-byte 1.12 sound emitter
MCLQ_LAYER_SIZE = 0xC9 * 4    # 804 bytes: min, max, 9x9 x 8 bytes, 8x8 tile bytes, n_flowvs, 2 x 40 bytes
MCLQ_TILES_OFS = 8 + 81 * 8   # tile flag bytes inside one layer
MDDF_SIZE = 0x24
MODF_SIZE = 0x40
MCIN_ENTRY = 16
WDL_MARE_DATA = 0x442         # 17*17 + 16*16 int16
WDT_MPHD_SIZE = 0x20
WDT_MAIN_SIZE = 64 * 64 * 8
WDL_MAOF_SIZE = 64 * 64 * 4

# MCNK header field offsets (inside the 0x80-byte header that follows the 8-byte MCNK chunk header)
MCNK_FIELDS = [
    ('flags', 0x00), ('indexX', 0x04), ('indexY', 0x08), ('nLayers', 0x0C), ('nDoodadRefs', 0x10),
    ('ofsHeight', 0x14), ('ofsNormal', 0x18), ('ofsLayer', 0x1C), ('ofsRefs', 0x20), ('ofsAlpha', 0x24),
    ('sizeAlpha', 0x28), ('ofsShadow', 0x2C), ('sizeShadow', 0x30), ('areaId', 0x34), ('nMapObjRefs', 0x38),
    ('holes', 0x3C), ('ofsSndEmitters', 0x58), ('nSndEmitters', 0x5C), ('ofsLiquid', 0x60),
    ('sizeLiquid', 0x64), ('posX', 0x68), ('posY', 0x6C), ('posZ', 0x70), ('ofsMCCV', 0x74),
    ('unused78', 0x78), ('unused7C', 0x7C),
]
FLOAT_FIELDS = {'posX', 'posY', 'posZ'}
MCNK_LIQUID_FLAGS = (0x04, 0x08, 0x10, 0x20)   # one MCLQ layer per bit, in this order

# MHDR data fields (offsets relative to the MHDR data start = file offset 0x14)
MHDR_FIELDS = ['flags', 'ofsMCIN', 'ofsMTEX', 'ofsMMDX', 'ofsMMID', 'ofsMWMO', 'ofsMWID', 'ofsMDDF',
               'ofsMODF', 'ofsMFBO', 'ofsMH2O', 'ofsMTXF', 'unused30', 'unused34', 'unused38', 'unused3C']
MHDR_CHUNKS = [('ofsMCIN', 'MCIN'), ('ofsMTEX', 'MTEX'), ('ofsMMDX', 'MMDX'), ('ofsMMID', 'MMID'),
               ('ofsMWMO', 'MWMO'), ('ofsMWID', 'MWID'), ('ofsMDDF', 'MDDF'), ('ofsMODF', 'MODF')]

# chunks that exist only in later expansions (never in a 1.12 file)
WOTLK_TOP_CHUNKS = {'MH2O', 'MFBO', 'MTXF', 'MTXP', 'MAMP', 'MBMH', 'MBBB', 'MBNV', 'MBMI', 'MLHD', 'MLVH',
                    'MLVI', 'MLLL', 'MLND', 'MLSI', 'MLLD', 'MLLN', 'MLLV', 'MLLI', 'MLMD', 'MLMX', 'MLDD',
                    'MLDX', 'MLDL', 'MLFD', 'MLMB', 'MTCG', 'MAHO', 'MLDB', 'MWDR', 'MWDS'}
WOTLK_SUB_CHUNKS = {'MCCV', 'MCLV', 'MCBB', 'MCMT', 'MCDD', 'MCRD', 'MCRW', 'MCLW'}


def magic_at(b, pos):
    if pos < 0 or pos + 4 > len(b):
        return None
    return b[pos:pos + 4][::-1].decode('latin-1')


def u32(b, pos):
    return struct.unpack_from('<I', b, pos)[0]


def chunks(b, pos=0, end=None):
    """Sequential top-level chunks: (offset of the 8-byte header, magic, size). Stops at a truncated header
    or a chunk that runs past `end` (that last chunk is still yielded, with its declared size)."""
    end = len(b) if end is None else end
    while pos + 8 <= end:
        m = magic_at(b, pos)
        s = u32(b, pos + 4)
        yield pos, m, s
        if pos + 8 + s > end:
            return
        pos += 8 + s


def zstrings(data):
    """{offset: string} of the zero-terminated strings in a string block."""
    out = {}
    pos = 0
    while pos < len(data):
        e = data.find(b'\0', pos)
        if e < 0:
            out[pos] = data[pos:].decode('latin-1')
            break
        out[pos] = data[pos:e].decode('latin-1')
        pos = e + 1
    return out


class McnkRecord:
    """One MCNK as the client sees it (header fields + sub-chunk data located through the header offsets)."""

    def __init__(self, b, index, offset, mcin_size):
        self.index = index
        self.offset = offset            # file offset of the 8-byte 'MCNK' header
        self.mcin_size = mcin_size
        self.magic = magic_at(b, offset)
        self.size = u32(b, offset + 4) if offset + 8 <= len(b) else None
        self.h = {}
        if offset + 8 + MCNK_HEADER_SIZE <= len(b):
            for name, o in MCNK_FIELDS:
                fmt = '<f' if name in FLOAT_FIELDS else '<I'
                self.h[name] = struct.unpack_from(fmt, b, offset + 8 + o)[0]
            self.low_quality_map = b[offset + 8 + 0x40: offset + 8 + 0x50]
            self.no_effect_doodad = u32(b, offset + 8 + 0x50)
            self.header_bytes = b[offset + 8: offset + 8 + MCNK_HEADER_SIZE]
        self.end = offset + 8 + (self.size or 0)

    def sub(self, b, field, length):
        """(data offset, bytes) of the sub-chunk addressed by header field `field`: the client takes
        MCNK start + offset + 8 (it never checks the sub-chunk magic or size)."""
        ofs = self.h.get(field, 0)
        start = self.offset + ofs + 8
        return start, b[start:start + length]


class Adt:
    def __init__(self, b, name=''):
        self.name = name
        self.data = b
        self.problems = []
        self.top = list(chunks(b))
        self.mver_size = u32(b, 4) if len(b) >= 8 else None
        self.version = u32(b, 8) if len(b) >= 12 else None
        # CMapArea_ParseAdtHeader: MHDR = file + MVER.size + 8, its data 8 bytes later
        self.mhdr_pos = 8 + (self.mver_size or 0)
        self.mhdr_data = self.mhdr_pos + 8
        self.mhdr = {}
        if self.mhdr_data + 64 <= len(b):
            vals = struct.unpack_from('<16I', b, self.mhdr_data)
            self.mhdr = dict(zip(MHDR_FIELDS, vals))
        self.blocks = {}   # magic -> (header offset, size, data bytes) located through MHDR
        for field, magic in MHDR_CHUNKS:
            ofs = self.mhdr.get(field)
            if ofs is None:
                continue
            hp = self.mhdr_data + ofs
            if hp + 8 > len(b):
                self.blocks[magic] = (hp, None, b'')
                continue
            size = u32(b, hp + 4)
            self.blocks[magic] = (hp, size, b[hp + 8: hp + 8 + size])
        self.textures = list(zstrings(self.block('MTEX')).values()) if self.block('MTEX') else []
        if self.textures and self.textures[-1] == '':
            self.textures.pop()
        self.mmdx = zstrings(self.block('MMDX'))
        self.mwmo = zstrings(self.block('MWMO'))
        self.mmid = list(struct.unpack_from('<%dI' % (len(self.block('MMID')) // 4), self.block('MMID')))
        self.mwid = list(struct.unpack_from('<%dI' % (len(self.block('MWID')) // 4), self.block('MWID')))
        self.mddf = []
        d = self.block('MDDF')
        for i in range(len(d) // MDDF_SIZE):
            nameId, uid, x, y, z, rx, ry, rz, scale, flags = struct.unpack_from('<2I6f2H', d, i * MDDF_SIZE)
            self.mddf.append(dict(nameId=nameId, uid=uid, pos=(x, y, z), rot=(rx, ry, rz), scale=scale, flags=flags))
        self.modf = []
        d = self.block('MODF')
        for i in range(len(d) // MODF_SIZE):
            v = struct.unpack_from('<2I3f3f6f4H', d, i * MODF_SIZE)
            self.modf.append(dict(nameId=v[0], uid=v[1], pos=v[2:5], rot=v[5:8], extents=v[8:14],
                                  flags=v[14], doodadSet=v[15], nameSet=v[16], pad=v[17]))
        self.mcin = []
        self.mcnk = []
        d = self.block('MCIN')
        for i in range(min(256, len(d) // MCIN_ENTRY)):
            off, size, flags, async_id = struct.unpack_from('<4I', d, i * MCIN_ENTRY)
            self.mcin.append(dict(offset=off, size=size, flags=flags, asyncId=async_id))
            self.mcnk.append(McnkRecord(b, i, off, size) if off + 8 <= len(b) else None)

    def block(self, magic):
        blk = self.blocks.get(magic)
        return blk[2] if blk else b''


class Wdt:
    """Positional reader, exactly like CMap_LoadWdt @00694760."""

    def __init__(self, b, name=''):
        self.name = name
        self.data = b
        self.top = list(chunks(b))
        pos = 0
        self.slots = []   # (expected magic, header offset, actual magic, size)

        def take(expected):
            nonlocal pos
            if pos + 8 > len(b):
                self.slots.append((expected, pos, None, None))
                return None
            m, s = magic_at(b, pos), u32(b, pos + 4)
            self.slots.append((expected, pos, m, s))
            hdr = pos
            pos += 8
            return hdr, m, s

        take('MVER')
        self.version = u32(b, pos) if pos + 4 <= len(b) else None
        pos += 4
        take('MPHD')
        self.mphd_flags = u32(b, pos) if pos + 4 <= len(b) else None
        self.mphd = b[pos:pos + WDT_MPHD_SIZE]
        pos += WDT_MPHD_SIZE
        take('MAIN')
        self.main = []
        for i in range(64 * 64):
            p = pos + i * 8
            self.main.append(struct.unpack_from('<2I', b, p) if p + 8 <= len(b) else (0, 0))
        pos += WDT_MAIN_SIZE
        self.after_main = pos
        self.global_wmo = None
        if self.mphd_flags is not None and self.mphd_flags & 1:
            r = take('MWMO')
            name_size = r[2] if r and r[2] is not None else 0
            self.global_wmo_name = b[pos:pos + name_size].split(b'\0')[0].decode('latin-1')
            pos += name_size
            take('MODF')
            self.global_modf = b[pos:pos + MODF_SIZE]
            pos += MODF_SIZE
        self.end_of_read = pos

    def tiles(self):
        return [(i % 64, i // 64) for i, (f, _) in enumerate(self.main) if f & 1]


class Wdl:
    """Positional reader, exactly like CMap_LoadWdl @006944a0."""

    def __init__(self, b, name=''):
        self.name = name
        self.data = b
        self.top = list(chunks(b))
        self.mver_magic = magic_at(b, 0)
        self.mver_size = u32(b, 4) if len(b) >= 8 else None
        self.version = u32(b, 8) if len(b) >= 12 else None
        # the client reads 8 + 4 bytes, then ONE 8-byte header (unchecked), then 0x4000 bytes of offsets
        self.table_hdr = 12
        self.table_magic = magic_at(b, 12)
        self.table_size = u32(b, 16) if len(b) >= 20 else None
        self.offsets = list(struct.unpack_from('<4096I', b, 20)) if len(b) >= 20 + WDL_MAOF_SIZE else []

    def tiles(self):
        return [(i % 64, i // 64) for i, o in enumerate(self.offsets) if o]
