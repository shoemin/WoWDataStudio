"""MPQ archive writer for patch archives (`Data\\patch-3.MPQ`).

Why our own writer instead of StormLib: the archives we need are small and simple, and the format
is the same one our reader already handles. Writing it in ~150 lines of Python keeps the tool free of
native builds (StormLib would need a DLL built with Visual Studio and a ctypes binding), and the output
mirrors Blizzard's own 1.12 patch archive `patch-2.MPQ` field by field:

  * format version 1 header (44 bytes; the extended fields zero), sector size 4096 (shift 3);
  * every file sector-compressed with zlib (mask 0x02), a sector stored raw when zlib does not help;
    block flags EXISTS | COMPRESS (patch-2 also sets SECTOR_CRC; the base archives do not, and the
    client does not need it);
  * hash and block tables encrypted with the standard keys; locale 0 (neutral), platform 0;
  * `(listfile)` (names, CRLF separated) and `(attributes)` (version 100, flags 7 = CRC32 + FILETIME + MD5,
    FILETIME zero as in patch-2.MPQ).
No file encryption, no PKWARE implode. The result is verified by reading it back (see `verify`).

This module only ever creates NEW files. It refuses to write over the client's stock archives.
"""
import hashlib
import os
import struct
import zlib

from . import crypto
from .mpq import FLAG_COMPRESS, FLAG_EXISTS, MPQArchive

SECTOR_SHIFT = 3
SECTOR_SIZE = 512 << SECTOR_SHIFT
HEADER_SIZE_V1 = 44


class MPQWriteError(Exception):
    pass


def _compress_file(data, level=9):
    """Sector-compressed body (offset table + sectors). Returns (body, flags)."""
    if not data:
        return b'', FLAG_EXISTS
    nsect = (len(data) + SECTOR_SIZE - 1) // SECTOR_SIZE
    sectors = []
    for i in range(nsect):
        raw = data[i * SECTOR_SIZE:(i + 1) * SECTOR_SIZE]
        z = zlib.compress(raw, level)
        sectors.append(b'\x02' + z if len(z) + 1 < len(raw) else raw)
    table_size = (nsect + 1) * 4
    offsets = [table_size]
    for s in sectors:
        offsets.append(offsets[-1] + len(s))
    body = struct.pack('<%dI' % (nsect + 1), *offsets) + b''.join(sectors)
    if len(body) >= len(data):
        return data, FLAG_EXISTS             # incompressible: store the file plain
    return body, FLAG_EXISTS | FLAG_COMPRESS


def _hash_table_size(n):
    size = 16
    while size < n * 2:
        size *= 2
    return size


def build(files, out_path, attributes=True, overwrite=False):
    """Write a new MPQ with `files` ({archive path: bytes}). Returns a summary dict.

    Archive paths use backslashes (forward slashes are converted). `out_path` must not exist unless
    overwrite=True; it may never be one of the client's stock archives.
    """
    from .vfs import STOCK_ARCHIVES
    base = os.path.basename(out_path).lower()
    if base in [n.lower() for n in STOCK_ARCHIVES]:
        raise MPQWriteError('refusing to write %s: that is a stock client archive name' % base)
    if os.path.exists(out_path) and not overwrite:
        raise MPQWriteError('%s exists (pass overwrite=True to replace a file we built)' % out_path)
    names = []
    seen = set()
    for n in files:
        nn = crypto.normalize(n).lstrip('\\')
        if nn.upper() in seen:
            raise MPQWriteError('duplicate path (case-insensitive): %s' % nn)
        if nn.startswith('('):
            raise MPQWriteError('reserved name: %s' % nn)
        seen.add(nn.upper())
        names.append((nn, files[n]))
    names.sort(key=lambda t: t[0].upper())

    listfile = ''.join(n + '\r\n' for n, _ in names).encode('utf-8')
    entries = [(n, d) for n, d in names] + [('(listfile)', listfile)]
    if attributes:
        entries.append(('(attributes)', None))   # filled below
    nblocks = len(entries)

    blocks = []   # (pos, csize, fsize, flags)
    tmp = out_path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(b'\0' * HEADER_SIZE_V1)
        pos = HEADER_SIZE_V1
        crcs, md5s = [], []
        for name, data in entries:
            if name == '(attributes)':
                crcs.append(0)
                md5s.append(b'\0' * 16)
                data = (struct.pack('<II', 100, 7) + struct.pack('<%dI' % nblocks, *crcs) +
                        b'\0' * (8 * nblocks) + b''.join(md5s))
            else:
                crcs.append(zlib.crc32(data) & 0xFFFFFFFF)
                md5s.append(hashlib.md5(data).digest())
            body, flags = _compress_file(data)
            f.write(body)
            blocks.append((pos, len(body), len(data), flags))
            pos += len(body)
        hsize = _hash_table_size(nblocks)
        table = [(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFF, 0xFFFF, 0xFFFFFFFF)] * hsize
        for bi, (name, _) in enumerate(entries):
            i = crypto.hash_string(name, crypto.HASH_TABLE_OFFSET) & (hsize - 1)
            while table[i][4] != 0xFFFFFFFF:
                i = (i + 1) & (hsize - 1)
            table[i] = (crypto.hash_string(name, crypto.HASH_NAME_A), crypto.hash_string(name, crypto.HASH_NAME_B), 0, 0, bi)
        hraw = b''.join(struct.pack('<IIHHI', *e) for e in table)
        braw = b''.join(struct.pack('<IIII', *b) for b in blocks)
        hash_pos = pos
        f.write(crypto.encrypt(hraw, crypto.HASH_TABLE_KEY))
        pos += len(hraw)
        block_pos = pos
        f.write(crypto.encrypt(braw, crypto.BLOCK_TABLE_KEY))
        pos += len(braw)
        header = struct.pack('<4sIIHHIIII', b'MPQ\x1a', HEADER_SIZE_V1, pos, 1, SECTOR_SHIFT,
                             hash_pos, block_pos, hsize, nblocks) + struct.pack('<QHH', 0, 0, 0)
        f.seek(0)
        f.write(header)
    os.replace(tmp, out_path)
    return {'path': out_path, 'files': len(names), 'blocks': nblocks, 'hash_table': hsize, 'size': pos}


def verify(path, files):
    """Read the archive back with our reader and compare every file. Returns a list of problems."""
    problems = []
    with MPQArchive(path) as a:
        if a.format_version != 1 or a.header_size != HEADER_SIZE_V1:
            problems.append('header: version %d size %d' % (a.format_version, a.header_size))
        listed = {n.upper() for n in a.listfile()}
        for n, data in files.items():
            nn = crypto.normalize(n).lstrip('\\')
            if a.storm_lookup(nn) is None:
                problems.append('%s: not found by the client-style hash probe' % nn)
            got = a.read(nn)
            if got != data:
                problems.append('%s: content differs after read-back' % nn)
            if nn.upper() not in listed:
                problems.append('%s: missing from (listfile)' % nn)
        attr = a.read('(attributes)')
        if attr is not None:
            ver, flags = struct.unpack_from('<II', attr)
            if (ver, flags) != (100, 7):
                problems.append('(attributes) header %r' % ((ver, flags),))
    return problems
