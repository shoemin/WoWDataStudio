"""Shared fixtures: the real client archives (tests are skipped when the client is not found)."""
import os
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from wowdata import config  # noqa: E402

CFG = config.load()
HAVE_CLIENT = os.path.isdir(CFG.data_dir) and os.path.exists(os.path.join(CFG.data_dir, 'model.MPQ'))
needs_client = unittest.skipUnless(HAVE_CLIENT, 'client Data folder not found: %s' % CFG.data_dir)

_client = None


def client():
    global _client
    if _client is None:
        from wowdata.vfs import ClientData
        _client = ClientData(CFG.data_dir, cache_dir=CFG.cache_dir)
    return _client


def tempdir():
    return tempfile.mkdtemp(prefix='wowdata_test_')


def data_dir_snapshot():
    """(name, size, mtime) of every file directly in Client\\Data plus a count of files below it:
    used to prove that tests never write into the client."""
    snap = []
    for root, dirs, files in os.walk(CFG.data_dir):
        for f in files:
            p = os.path.join(root, f)
            st = os.stat(p)
            snap.append((os.path.relpath(p, CFG.data_dir), st.st_size, int(st.st_mtime)))
    return sorted(snap)
