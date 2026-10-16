"""Read-only MPQ archive reader (pure Python, no native dependency).

Supports what the 1.12 client archives use: format version 0 (32-byte header) and version 1
(44-byte header, patch.MPQ / patch-2.MPQ), zlib and stored sectors, single-unit files,
encrypted files (with or without FIX_KEY), sector CRC tables, delete markers. bzip2 sectors are
also decoded (Python's bz2). PKWARE implode, Huffman and ADPCM are not used by any 1.12 archive
(checked by tests/test_mpq.py) and raise UnsupportedCompression.

Archives are always opened read-only ('rb'). Nothing in this module can write to an archive.
"""
import bz2
import hashlib
import os
import pickle
import struct
import threading
import zlib

from . import crypto

# block flags
FLAG_IMPLODE = 0x00000100
FLAG_COMPRESS = 0x00000200
FLAG_ENCRYPTED = 0x00010000
FLAG_FIX_KEY = 0x00020000
FLAG_SINGLE_UNIT = 0x01000000
FLAG_DELETE_MARKER = 0x02000000
FLAG_SECTOR_CRC = 0x04000000
FLAG_EXISTS = 0x80000000

# per-sector compression mask bits
COMP_HUFFMAN = 0x01
COMP_ZLIB = 0x02
COMP_PKWARE = 0x08
COMP_BZIP2 = 0x10
COMP_ADPCM_MONO = 0x40
COMP_ADPCM_STEREO = 0x80

HASH_EMPTY = 0xFFFFFFFF
HASH_DELETED = 0xFFFFFFFE


class MPQError(Exception):
    pass


class UnsupportedCompression(MPQError):
    pass


class MPQArchive:
    """One MPQ archive. `cache_dir` (optional) stores the decrypted tables for a faster next open."""

    def __init__(self, path, cache_dir=None):
        self.path = os.path.abspath(path)
        self.name = os.path.basename(path)
        self._lock = threading.Lock()
        self._f = open(self.path, 'rb')
        self._read_header()
        self._load_tables(cache_dir)
        self._listfile = None

    # ------------------------------------------------------------------ header / tables
    def _read_header(self):
        f = self._f
        offset = 0
        size = os.path.getsize(self.path)
        while offset < size:
            f.seek(offset)
            magic = f.read(4)
            if magic == b'MPQ\x1a':
                break
            offset += 0x200
        else:
            raise MPQError('%s: no MPQ header' % self.path)
        f.seek(offset)
        hd = f.read(44)
        (_, self.header_size, self.archive_size, self.format_version, sector_shift,
         self.hash_table_pos, self.block_table_pos, self.hash_table_size, self.block_table_size) = struct.unpack_from('<4sIIHHIIII', hd, 0)
        self.header_offset = offset
        self.sector_size = 512 << sector_shift
        self.sector_shift = sector_shift
        self.hi_block_table_pos = 0
        if self.format_version >= 1 and self.header_size >= 44:
            self.hi_block_table_pos, hash_hi, block_hi = struct.unpack_from('<QHH', hd, 32)
            self.hash_table_pos |= hash_hi << 32
            self.block_table_pos |= block_hi << 32

    def _load_tables(self, cache_dir):
        st = os.stat(self.path)
        cache_file = None
        if cache_dir:
            tag = hashlib.sha1(('%s|%d|%d' % (self.path.lower(), st.st_size, int(st.st_mtime))).encode()).hexdigest()[:16]
            cache_file = os.path.join(cache_dir, 'mpqtables_%s_%s.pickle' % (self.name, tag))
            if os.path.exists(cache_file):
                try:
                    with open(cache_file, 'rb') as cf:
                        self.hash_table, self.block_table = pickle.load(cf)
                    self._index_hash()
                    return
                except Exception:
                    pass
        f = self._f
        f.seek(self.header_offset + self.hash_table_pos)
        raw = f.read(self.hash_table_size * 16)
        ht = crypto.decrypt(raw, crypto.HASH_TABLE_KEY)
        self.hash_table = [struct.unpack_from('<IIHHI', ht, i * 16) for i in range(self.hash_table_size)]
        f.seek(self.header_offset + self.block_table_pos)
        raw = f.read(self.block_table_size * 16)
        bt = crypto.decrypt(raw, crypto.BLOCK_TABLE_KEY)
        hi = None
        if self.hi_block_table_pos:
            f.seek(self.header_offset + self.hi_block_table_pos)
            hi = struct.unpack('<%dH' % self.block_table_size, f.read(self.block_table_size * 2))
        self.block_table = []
        for i in range(self.block_table_size):
            pos, csize, fsize, flags = struct.unpack_from('<IIII', bt, i * 16)
            if hi:
                pos |= hi[i] << 32
            self.block_table.append((pos, csize, fsize, flags))
        self._index_hash()
        if cache_file:
            try:
                os.makedirs(cache_dir, exist_ok=True)
                with open(cache_file + '.tmp', 'wb') as cf:
                    pickle.dump((self.hash_table, self.block_table), cf, protocol=pickle.HIGHEST_PROTOCOL)
                os.replace(cache_file + '.tmp', cache_file)
            except OSError:
                pass

    def _index_hash(self):
        # (nameA, nameB) -> [(locale, platform, blockIndex)]; built once, makes lookups O(1)
        idx = {}
        for a, b, loc, plat, blk in self.hash_table:
            if blk == HASH_EMPTY or blk == HASH_DELETED:
                continue
            idx.setdefault((a, b), []).append((loc, plat, blk))
        self._hidx = idx

    def close(self):
        if self._f:
            self._f.close()
            self._f = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def __repr__(self):
        return '<MPQArchive %s v%d %d files>' % (self.name, self.format_version, self.block_table_size)

    # ------------------------------------------------------------------ lookup
    def block_index(self, name, locale=0):
        """Block-table index of `name` (preferring `locale`, then the neutral locale 0), or None."""
        name = crypto.normalize(name)
        key = (crypto.hash_string(name, crypto.HASH_NAME_A), crypto.hash_string(name, crypto.HASH_NAME_B))
        cands = self._hidx.get(key)
        if not cands:
            return None
        best = None
        for loc, plat, blk in cands:
            if loc == locale:
                best = blk
                break
            if loc == 0 and best is None:
                best = blk
        if best is None:
            best = cands[0][2]
        if best >= len(self.block_table):
            return None
        return best

    def storm_lookup(self, name):
        """Block index found the way the client's Storm does it (SFile_LocateFile @006549a0): start at
        bucket hash(name, 0) & (size-1) and probe linearly until the name or an empty entry. Used to
        verify archives we write (the dict lookup above would also find a misplaced entry)."""
        name = crypto.normalize(name)
        size = len(self.hash_table)
        i = crypto.hash_string(name, crypto.HASH_TABLE_OFFSET) & (size - 1)
        a = crypto.hash_string(name, crypto.HASH_NAME_A)
        b = crypto.hash_string(name, crypto.HASH_NAME_B)
        for _ in range(size):
            ea, eb, loc, plat, blk = self.hash_table[i]
            if blk == HASH_EMPTY:
                return None
            if ea == a and eb == b and blk != HASH_DELETED:
                return blk
            i = (i + 1) & (size - 1)
        return None

    def entry_state(self, name):
        """'file', 'deleted' (a delete marker: hides the file in every lower-priority archive, as the
        client's SFile_LocateFile does), 'invalid' (hash entry without an existing block) or None."""
        bi = self.block_index(name)
        if bi is None:
            return None
        flags = self.block_table[bi][3]
        if flags & FLAG_DELETE_MARKER:
            return 'deleted'
        if flags & FLAG_EXISTS:
            return 'file'
        return 'invalid'

    def file_entry(self, name):
        """(position, compressed size, file size, flags) or None. Delete markers count as missing."""
        bi = self.block_index(name)
        if bi is None:
            return None
        ent = self.block_table[bi]
        if not ent[3] & FLAG_EXISTS or ent[3] & FLAG_DELETE_MARKER:
            return None
        return ent

    def has_file(self, name):
        return self.file_entry(name) is not None

    def file_size(self, name):
        ent = self.file_entry(name)
        return None if ent is None else ent[2]

    # ------------------------------------------------------------------ reading
    def read(self, name):
        """Whole file as bytes, or None when the archive does not contain it."""
        ent = self.file_entry(name)
        if ent is None:
            return None
        return self._read_entry(name, ent)

    def _read_raw(self, pos, size):
        with self._lock:
            self._f.seek(self.header_offset + pos)
            return self._f.read(size)

    def _key(self, name, ent):
        pos, csize, fsize, flags = ent
        return crypto.file_key(name, pos, fsize, bool(flags & FLAG_FIX_KEY))

    def _read_entry(self, name, ent):
        pos, csize, fsize, flags = ent
        if fsize == 0:
            return b''
        raw = self._read_raw(pos, csize)
        key = self._key(name, ent) if flags & FLAG_ENCRYPTED else None
        if flags & FLAG_SINGLE_UNIT:
            data = crypto.decrypt(raw, key) if key is not None else raw
            if flags & (FLAG_COMPRESS | FLAG_IMPLODE) and len(data) < fsize:
                return self._decompress(data, fsize, flags)
            return data[:fsize]
        if not flags & (FLAG_COMPRESS | FLAG_IMPLODE):
            # stored, possibly encrypted per sector
            if key is None:
                return raw[:fsize]
            out = bytearray()
            for i in range(0, fsize, self.sector_size):
                out += crypto.decrypt(raw[i:i + self.sector_size], (key + i // self.sector_size) & 0xFFFFFFFF)
            return bytes(out[:fsize])
        nsect = (fsize + self.sector_size - 1) // self.sector_size
        ntab = nsect + 1 + (1 if flags & FLAG_SECTOR_CRC else 0)
        table = raw[:ntab * 4]
        if key is not None:
            table = crypto.decrypt(table, (key - 1) & 0xFFFFFFFF)
        offs = struct.unpack('<%dI' % ntab, table)
        out = bytearray()
        for i in range(nsect):
            d = raw[offs[i]:offs[i + 1]]
            if key is not None:
                d = crypto.decrypt(d, (key + i) & 0xFFFFFFFF)
            expect = min(self.sector_size, fsize - i * self.sector_size)
            out += self._decompress(d, expect, flags)
        return bytes(out)

    @staticmethod
    def _decompress(d, expect, flags):
        if len(d) >= expect:
            return d[:expect]
        if flags & FLAG_IMPLODE:
            raise UnsupportedCompression('PKWARE implode (block flag) is not supported')
        mask = d[0]
        body = d[1:]
        if mask == COMP_ZLIB:
            return zlib.decompress(body)
        if mask == COMP_BZIP2:
            return bz2.decompress(body)
        raise UnsupportedCompression('sector compression mask 0x%02x is not supported' % mask)

    def sector_masks(self, name):
        """Compression mask byte of every compressed sector (diagnostics: proves which codecs an archive uses)."""
        ent = self.file_entry(name)
        if ent is None:
            return []
        pos, csize, fsize, flags = ent
        if fsize == 0 or not flags & FLAG_COMPRESS:
            return []
        if flags & FLAG_SINGLE_UNIT:
            raw = self._read_raw(pos, min(csize, 1))
            return [raw[0]] if csize < fsize else []
        nsect = (fsize + self.sector_size - 1) // self.sector_size
        ntab = nsect + 1 + (1 if flags & FLAG_SECTOR_CRC else 0)
        table = self._read_raw(pos, ntab * 4)
        key = self._key(name, ent) if flags & FLAG_ENCRYPTED else None
        if key is not None:
            table = crypto.decrypt(table, (key - 1) & 0xFFFFFFFF)
        offs = struct.unpack('<%dI' % ntab, table)
        masks = []
        for i in range(nsect):
            expect = min(self.sector_size, fsize - i * self.sector_size)
            if offs[i + 1] - offs[i] < expect:
                b = self._read_raw(pos + offs[i], 4)
                if key is not None:
                    b = crypto.decrypt(b, (key + i) & 0xFFFFFFFF)
                masks.append(b[0])
        return masks

    # ------------------------------------------------------------------ listfile
    def listfile(self):
        """Names from the archive's (listfile), in archive order (case as stored)."""
        if self._listfile is None:
            data = self.read('(listfile)') or b''
            text = data.decode('utf-8', 'replace')
            names = []
            for part in text.replace(';', '\n').replace('\r', '\n').split('\n'):
                part = part.strip().strip('\0')
                if part:
                    names.append(part)
            self._listfile = names
        return self._listfile
