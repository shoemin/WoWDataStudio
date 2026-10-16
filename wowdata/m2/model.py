"""Full M2 (MD20 v256/v257) reader and writer.

`M2.parse(data)` reads EVERYTHING the file contains into numpy record arrays (see schema.py), keeping the
original bytes (`m.raw`) and the offset and size of every data block (`m.blocks`).
`m.serialize()` writes the structure back. It lays the blocks out the way Blizzard's exporter did, which
we derived from the files themselves (all 9,691 client models follow it):
  * the header first (0x144 bytes for v256, 0x154 for v257);
  * then every non-empty array in header-field order, depth first: an array of records is followed by the
    arrays of its first element (in field order: e.g. a bone's translation ranges, timestamps, values,
    then rotation, then scale), then those of the second element, and so on;
  * every block starts on a 16-byte boundary; gaps and the end of the file are zero-filled to 16 bytes;
  * an empty array is written as {count 0, offset 0}.
An unchanged model therefore re-serialises byte-identically, which the tests check for every model.
Edits (Phase 3) change the arrays and serialize() recomputes every count and offset.
"""
import numpy as np

from .schema import CHAR, HEADER_256, HEADER_257, Rec

MAGIC = b'MD20'


class M2Error(Exception):
    pass


def _elem_dtype(elem):
    return None if elem == CHAR or isinstance(elem, Rec) else np.dtype(elem)


def _elem_size(elem):
    if elem == CHAR:
        return 1
    if isinstance(elem, Rec):
        return elem.size
    return np.dtype(elem).itemsize


def _empty(elem):
    if elem == CHAR:
        return b''
    if isinstance(elem, Rec):
        return RecArray(elem, np.zeros(0, elem.dtype), {a: [] for a, _ in elem.arrays}, None)
    dt = np.dtype(elem)
    if dt.subdtype:
        return np.zeros((0,) + dt.subdtype[1], dt.subdtype[0])
    return np.zeros(0, dt)


class Track:
    """One animation track of one record (a view into its RecArray)."""

    def __init__(self, owner, index, name):
        self._o, self._i, self.name = owner, index, name

    @property
    def interpolation(self):
        return int(self._o.flat[self.name + '.interpolation'][self._i])

    @property
    def global_sequence(self):
        return int(self._o.flat[self.name + '.global_sequence'][self._i])

    def _sub(self, s):
        key = '%s.%s' % (self.name, s)
        return self._o.sub[key][self._i] if key in self._o.sub else None

    @property
    def ranges(self):
        return self._sub('ranges')

    @property
    def timestamps(self):
        return self._sub('timestamps')

    @property
    def values(self):
        return self._sub('values')

    def set(self, ranges=None, timestamps=None, values=None, interpolation=None, global_sequence=None):
        for s, v in (('ranges', ranges), ('timestamps', timestamps), ('values', values)):
            if v is not None:
                self._o.sub['%s.%s' % (self.name, s)][self._i] = v
        if interpolation is not None:
            self._o.flat[self.name + '.interpolation'][self._i] = interpolation
        if global_sequence is not None:
            self._o.flat[self.name + '.global_sequence'][self._i] = global_sequence

    def __len__(self):
        t = self.timestamps
        return 0 if t is None else len(t)

    def __repr__(self):
        return '<Track %s interp=%d gseq=%d keys=%d ranges=%d>' % (self.name, self.interpolation, self.global_sequence,
                                                                 len(self), len(self.ranges))


class Item:
    """Attribute view of one record: item.parent, item.pivot, item.rotation (a Track), item.filename (bytes)..."""

    def __init__(self, owner, index):
        object.__setattr__(self, '_o', owner)
        object.__setattr__(self, '_i', index)

    def __getattr__(self, key):
        o, i = self._o, self._i
        if key in o.rec.plain:
            return o.flat[key][i]
        if key in o.sub:
            return o.sub[key][i]
        if any(t == key for t, _ in o.rec.tracks):
            return Track(o, i, key)
        raise AttributeError(key)

    def __setattr__(self, key, value):
        if key in self._o.rec.plain:
            self._o.flat[key][self._i] = value
        elif key in self._o.sub:
            self._o.sub[key][self._i] = value
        else:
            raise AttributeError(key)

    def tracks(self):
        return [Track(self._o, self._i, t) for t, _ in self._o.rec.tracks]


class RecArray:
    """An array of records: `flat` is the numpy structured array of the fixed-size part, `sub[name][i]` holds the
    arrays referenced by element i (numpy arrays, bytes for CHAR, nested RecArrays). `offset` is where the block
    was in the original file (None for new data)."""

    def __init__(self, rec, flat, sub, offset):
        self.rec, self.flat, self.sub, self.offset = rec, flat, sub, offset

    def __len__(self):
        return len(self.flat)

    def __getitem__(self, key):
        if isinstance(key, str):
            return self.flat[key]
        if key < 0:
            key += len(self.flat)
        if not 0 <= key < len(self.flat):
            raise IndexError(key)
        return Item(self, key)

    def __iter__(self):
        return (Item(self, i) for i in range(len(self.flat)))

    def __repr__(self):
        return '<%s x%d>' % (self.rec.name, len(self))


# ---------------------------------------------------------------------- reading

class _Reader:
    def __init__(self, data):
        self.data = data
        self.size = len(data)
        self.blocks = []

    def array(self, count, offset, elem, path):
        count, offset = int(count), int(offset)
        if count == 0:
            return _empty(elem)
        nbytes = count * _elem_size(elem)
        # the client's check (M2Fixup_*): offset <= size and offset + count * elemsize <= size
        if offset > self.size or offset + nbytes > self.size:
            raise M2Error('%s: %d x %d bytes at %#x run past the end of the file (%#x)' % (path, count, _elem_size(elem), offset, self.size))
        self.blocks.append((path, offset, nbytes))
        if elem == CHAR:
            return bytes(self.data[offset:offset + count])
        if isinstance(elem, Rec):
            flat = np.frombuffer(self.data, elem.dtype, count, offset).copy()
            return RecArray(elem, flat, self.children(elem, flat, path), offset)
        return np.frombuffer(self.data, np.dtype(elem), count, offset).copy()

    def children(self, rec, flat, path):
        sub = {}
        if not rec.arrays:
            return sub
        ns = {a: flat[a + '#n'] for a, _ in rec.arrays}
        os_ = {a: flat[a + '#o'] for a, _ in rec.arrays}
        for a, _ in rec.arrays:
            sub[a] = [None] * len(flat)
        for i in range(len(flat)):   # element by element, in file order (mirrors the writer)
            for a, elem in rec.arrays:
                where = a if path is None else '%s[%d].%s' % (path, i, a)
                sub[a][i] = self.array(ns[a][i], os_[a][i], elem, where)
        return sub


class M2:
    """A parsed model. Header arrays are attributes: m.bones (RecArray), m.vertices (numpy), m.views,
    m.textures, m.global_sequences, ... Plain header values: m.bounds_min, m.global_flags, ..."""

    def __init__(self, header, raw=None, blocks=None):
        object.__setattr__(self, 'header', header)
        object.__setattr__(self, 'raw', raw)
        object.__setattr__(self, 'blocks', blocks or [])

    @classmethod
    def parse(cls, data):
        data = bytes(data)
        if len(data) < HEADER_256.size or data[:4] != MAGIC:
            raise M2Error('not an M2 (MD20) file')
        version = int.from_bytes(data[4:8], 'little')
        if version not in (0x100, 0x101):
            raise M2Error('M2 version %#x: the 1.12 client accepts only 0x100 and 0x101' % version)
        rec = HEADER_257 if version == 0x101 else HEADER_256
        if len(data) < rec.size:
            raise M2Error('file shorter than the %#x-byte header' % rec.size)
        r = _Reader(data)
        flat = np.frombuffer(data, rec.dtype, 1, 0).copy()
        r.blocks.append(('header', 0, rec.size))
        header = RecArray(rec, flat, r.children(rec, flat, None), 0)
        m = cls(header, data, r.blocks)
        return m

    # ------------------------------------------------------------------ access
    def __getattr__(self, key):
        h = self.header
        if key in h.sub:
            return h.sub[key][0]
        if key in h.rec.plain:
            v = h.flat[key][0]
            return v
        raise AttributeError(key)

    def __setattr__(self, key, value):
        h = self.header
        if key in h.sub:
            h.sub[key][0] = value
        elif key in h.rec.plain:
            h.flat[key][0] = value
        else:
            object.__setattr__(self, key, value)

    @property
    def version(self):
        return int(self.header.flat['version'][0])

    @property
    def model_name(self):
        return self.name.rstrip(b'\0').decode('latin-1')

    def texture_name(self, i):
        return self.textures.sub['filename'][i].rstrip(b'\0').decode('latin-1')

    def counts(self):
        return {a: len(self.header.sub[a][0]) for a, _ in self.header.rec.arrays}

    def block_bytes(self, path):
        """Original bytes of a block, by path (e.g. 'bones[3].rotation.values', 'views[0].triangles')."""
        for p, o, n in self.blocks:
            if p == path:
                return self.raw[o:o + n]
        return None

    def coverage(self):
        """(covered bytes, non-zero bytes outside every block): proves nothing in the file is left unread."""
        if self.raw is None:
            return None
        mask = np.zeros(len(self.raw), bool)
        for _, o, n in self.blocks:
            mask[o:o + n] = True
        raw = np.frombuffer(self.raw, np.uint8)
        return int(mask.sum()), int(np.count_nonzero(raw[~mask]))

    # ------------------------------------------------------------------ writing
    def serialize(self):
        w = _Writer()
        rec = self.header.rec
        w.alloc(rec.size)
        flat = self.header.flat.copy()
        for a, elem in rec.arrays:
            n, o = w.array(self.header.sub[a][0], elem)
            flat[a + '#n'][0] = n
            flat[a + '#o'][0] = o
        w.put(0, flat.tobytes())
        w.pad()
        return bytes(w.buf)


class _Writer:
    ALIGN = 16

    def __init__(self):
        self.buf = bytearray()

    def alloc(self, n):
        pos = (len(self.buf) + self.ALIGN - 1) // self.ALIGN * self.ALIGN
        self.buf.extend(b'\0' * (pos + n - len(self.buf)))
        return pos

    def put(self, pos, b):
        self.buf[pos:pos + len(b)] = b

    def pad(self):
        self.alloc(0)

    def array(self, value, elem):
        if value is None or len(value) == 0:
            return 0, 0
        if elem == CHAR:
            b = bytes(value)
            pos = self.alloc(len(b))
            self.put(pos, b)
            return len(b), pos
        if isinstance(elem, Rec):
            n = len(value)
            pos = self.alloc(n * elem.size)
            flat = np.ascontiguousarray(value.flat, elem.dtype).copy()
            for i in range(n):
                for a, sub_elem in elem.arrays:
                    cn, co = self.array(value.sub[a][i], sub_elem)
                    flat[a + '#n'][i] = cn
                    flat[a + '#o'][i] = co
            self.put(pos, flat.tobytes())
            return n, pos
        dt = np.dtype(elem)
        if dt.names:
            arr = np.ascontiguousarray(value, dt)
        elif dt.subdtype:
            base, shape = dt.subdtype
            arr = np.ascontiguousarray(value, base).reshape((-1,) + shape)
        else:
            arr = np.ascontiguousarray(value, dt).reshape(-1)
        b = arr.tobytes()
        pos = self.alloc(len(b))
        self.put(pos, b)
        return len(arr), pos


def load(client, path):
    """Parse an .m2 (or .mdx/.mdl name) from a ClientData."""
    data = client.read(path)
    if data is None:
        raise M2Error('%s not found in the client archives' % path)
    return M2.parse(data)
