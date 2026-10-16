"""The client's view of its data: archives in the client's priority order, plus optional loose overlays.

Mount order (from our decompile of PatchFiles_MountArchives @00403740):
  1. Data\\patch-?.MPQ (exactly one character after the dash), sorted descending: patch-9 .. patch-3, patch-2
  2. Data\\patch.MPQ
  3. (speech2.MPQ / the `speech2` locale sub-archive, absent in this client)
  4. the base archives in this order: model, texture, terrain, wmo, sound, misc, interface, fonts, speech, dbc
A file is taken from the first archive in that list that has it. `base.MPQ` and `backup.MPQ` (installer
and launcher files) are NOT mounted by the game; they are read only with `include_unmounted=True`.

The client asks for models as `.mdx` / `.mdl` (DBC, ADT and WMO references), but the files are stored
as `.m2`; M2Cache_GetShared @00706a50 does the rename, and so does `resolve_name` here.
"""
import fnmatch
import os
import re

from . import crypto
from .mpq import MPQArchive

BASE_ARCHIVES = ['model.MPQ', 'texture.MPQ', 'terrain.MPQ', 'wmo.MPQ', 'sound.MPQ', 'misc.MPQ',
                 'interface.MPQ', 'fonts.MPQ', 'speech.MPQ', 'dbc.MPQ']
UNMOUNTED_ARCHIVES = ['base.MPQ', 'backup.MPQ']
STOCK_ARCHIVES = BASE_ARCHIVES + UNMOUNTED_ARCHIVES + ['patch.MPQ', 'patch-2.MPQ']
_PATCH_RE = re.compile(r'^patch-[^.]\.mpq$', re.I)


def resolve_name(path):
    """Normalized archive path: backslashes, no leading separator, .mdx/.mdl -> .m2."""
    p = crypto.normalize(path).lstrip('\\')
    low = p.lower()
    if low.endswith('.mdx') or low.endswith('.mdl'):
        p = p[:-4] + '.m2'
    return p


def client_archive_order(data_dir):
    """Archive file names in the client's lookup order (highest priority first)."""
    names = [n for n in os.listdir(data_dir) if _PATCH_RE.match(n)]
    names.sort(key=str.lower, reverse=True)
    order = names + ['patch.MPQ']
    if os.path.exists(os.path.join(data_dir, 'speech2.MPQ')):
        order.append('speech2.MPQ')
    order += BASE_ARCHIVES
    out = []
    for n in order:
        real = _find_case(data_dir, n)
        if real:
            out.append(real)
    return out


def _find_case(d, name):
    p = os.path.join(d, name)
    if os.path.exists(p):
        # report the name as it is on disk
        low = name.lower()
        for n in os.listdir(d):
            if n.lower() == low:
                return n
    return None


class Source:
    """Where a file was found."""

    def __init__(self, kind, name, archive=None, path=None):
        self.kind = kind          # 'archive' or 'loose'
        self.name = name          # archive file name or overlay folder
        self.archive = archive
        self.path = path          # loose file path on disk

    def __repr__(self):
        return '%s:%s' % (self.kind, self.name)


class ClientData:
    """Virtual file system over the client's archives.

    overlays: folders whose files take precedence over every archive (first one wins), e.g. the
    staging folder or the client's Data folder in loose-file mode. Paths inside an overlay mirror
    archive paths (`<overlay>\\Creature\\Boar\\BoarSkinIvory.blp`).
    """

    def __init__(self, data_dir, include_unmounted=False, overlays=(), cache_dir=None):
        self.data_dir = data_dir
        self.overlays = [o for o in overlays if o and os.path.isdir(o)]
        self.archives = []
        for n in client_archive_order(data_dir):
            self.archives.append((n, MPQArchive(os.path.join(data_dir, n), cache_dir=cache_dir), True))
        if include_unmounted:
            for n in UNMOUNTED_ARCHIVES:
                real = _find_case(data_dir, n)
                if real:
                    self.archives.append((real, MPQArchive(os.path.join(data_dir, real), cache_dir=cache_dir), False))
        self._names = None

    def close(self):
        for _, a, _ in self.archives:
            a.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def archive(self, name):
        for n, a, _ in self.archives:
            if n.lower() == name.lower():
                return a
        return None

    # ------------------------------------------------------------------ lookup
    def _loose(self, overlay, name):
        p = os.path.join(overlay, *name.split('\\'))
        if os.path.isfile(p):
            return p
        # case-insensitive match (the client's loose-file map is case-insensitive)
        cur = overlay
        for part in name.split('\\'):
            try:
                hit = [e for e in os.listdir(cur) if e.lower() == part.lower()]
            except OSError:
                return None
            if not hit:
                return None
            cur = os.path.join(cur, hit[0])
        return cur if os.path.isfile(cur) else None

    def find(self, path):
        """First Source that has `path` (overlays first, then archives in priority order), or None.

        A delete marker in an archive hides the file in all lower-priority archives (patch.MPQ has 5,708
        of them, e.g. Creature\\OgreMage\\OgreMage.m2, moved to Creature\\Ogre\\): the client's
        SFile_LocateFile @006549a0 stops at the marker and reports the file as missing."""
        name = resolve_name(path)
        for o in self.overlays:
            p = self._loose(o, name)
            if p:
                return Source('loose', o, path=p)
        for n, a, _ in self.archives:
            st = a.entry_state(name)
            if st == 'file':
                return Source('archive', n, archive=a)
            if st is not None:
                return None
        return None

    def where(self, path):
        """Every Source that has `path`, highest priority first (the first one wins). A delete marker is
        listed as a Source of kind 'deleted'; copies below it are listed with kind 'hidden'."""
        name = resolve_name(path)
        out = []
        for o in self.overlays:
            p = self._loose(o, name)
            if p:
                out.append(Source('loose', o, path=p))
        hidden = False
        for n, a, _ in self.archives:
            st = a.entry_state(name)
            if st == 'file':
                out.append(Source('hidden' if hidden else 'archive', n, archive=a))
            elif st is not None:
                out.append(Source('deleted', n, archive=a))
                hidden = True
        return out

    def exists(self, path):
        return self.find(path) is not None

    def read(self, path):
        """Bytes of the winning copy, or None."""
        src = self.find(path)
        if src is None:
            return None
        if src.kind == 'loose':
            with open(src.path, 'rb') as f:
                return f.read()
        return src.archive.read(resolve_name(path))

    def size(self, path):
        src = self.find(path)
        if src is None:
            return None
        if src.kind == 'loose':
            return os.path.getsize(src.path)
        return src.archive.file_size(resolve_name(path))

    # ------------------------------------------------------------------ listing
    def names(self):
        """{UPPER path: (path as listed, winning archive)} from every archive's (listfile) (archives only).

        Files hidden by a delete marker are left out (see deleted_names); names a listfile mentions but its
        archive does not contain are looked up in the next archives."""
        if self._names is None:
            names = {}
            deleted = {}
            for n, a, _ in self.archives:
                for nm in a.listfile():
                    if nm.startswith('('):
                        continue
                    u = nm.upper()
                    if u in names or u in deleted:
                        continue
                    st = a.entry_state(nm)
                    if st == 'file':
                        names[u] = (nm, n)
                    elif st is not None:
                        deleted[u] = (nm, n)
            self._names = names
            self._deleted = deleted
        return self._names

    def deleted_names(self):
        """{UPPER path: (path, archive with the delete marker)}: files the client cannot load."""
        self.names()
        return self._deleted

    def glob(self, pattern):
        """Archive paths matching a wildcard pattern (case-insensitive, '\\' or '/')."""
        pat = resolve_name(pattern).upper() if not pattern.endswith('*') else crypto.normalize(pattern).upper()
        return sorted(v[0] for k, v in self.names().items() if fnmatch.fnmatchcase(k, pat))
