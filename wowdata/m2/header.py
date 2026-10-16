"""Minimal M2 (version 256, 1.12) header reader: fast, enough for the search index and `extract`.

The full reader/writer is model.py. Offsets come from M2Shared_FixupHeader @0071cdf0 in our decompile.
"""
import struct

from .model import M2Error  # noqa: F401  (one exception type for the whole package)

ARRAYS = {'global_sequences': 0x14, 'sequences': 0x1c, 'bones': 0x34, 'vertices': 0x44, 'views': 0x4c,
          'textures': 0x5c, 'attachments': 0x104, 'events': 0x114, 'particles': 0x13c}


class M2Header:
    def __init__(self, data):
        if len(data) < 0x150 or data[:4] != b'MD20':
            raise M2Error('not an M2 (MD20) file')
        self.data = data
        self.version = struct.unpack_from('<I', data, 4)[0]
        if self.version not in (0x100, 0x101):
            raise M2Error('M2 version %#x (the 1.12 client accepts 0x100/0x101)' % self.version)
        nlen, nofs = struct.unpack_from('<II', data, 8)
        self.name = data[nofs:nofs + nlen].rstrip(b'\0').decode('latin-1') if nlen else ''

    def array(self, field):
        return struct.unpack_from('<II', self.data, ARRAYS[field])

    def counts(self):
        return {k: self.array(k)[0] for k in ARRAYS}

    def textures(self):
        """[(type, flags, file name or '')]."""
        n, ofs = self.array('textures')
        out = []
        for i in range(n):
            typ, flags, nlen, nofs = struct.unpack_from('<IIII', self.data, ofs + i * 16)
            name = self.data[nofs:nofs + nlen].rstrip(b'\0').decode('latin-1') if nlen else ''
            out.append((typ, flags, name))
        return out
