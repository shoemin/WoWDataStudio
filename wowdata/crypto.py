"""MPQ hashing and table encryption (the Storm algorithm used by every MPQ version).

The algorithm is the public MPQ format (documented on wowdev.wiki and in StormLib); this is an
independent Python implementation. It matches the client's own Storm code (SFile_HashString /
SFile_Decrypt in our decompile).
"""
import struct

HASH_TABLE_OFFSET = 0   # bucket index
HASH_NAME_A = 1
HASH_NAME_B = 2
HASH_FILE_KEY = 3


def _build_crypt_table():
    table = [0] * 0x500
    seed = 0x00100001
    for i in range(0x100):
        idx = i
        for _ in range(5):
            seed = (seed * 125 + 3) % 0x2AAAAB
            hi = (seed & 0xFFFF) << 16
            seed = (seed * 125 + 3) % 0x2AAAAB
            lo = seed & 0xFFFF
            table[idx] = hi | lo
            idx += 0x100
    return table


CRYPT_TABLE = _build_crypt_table()
_UPPER = bytes(range(256)).translate(bytes.maketrans(b'abcdefghijklmnopqrstuvwxyz/', b'ABCDEFGHIJKLMNOPQRSTUVWXYZ\\'))


def normalize(name):
    """Archive path form used for hashing: backslashes (the client uses them), case kept."""
    return name.replace('/', '\\')


def hash_string(name, hash_type):
    """Storm string hash. Case-insensitive, '/' == '\\'."""
    if isinstance(name, str):
        data = name.encode('latin-1', 'replace')
    else:
        data = name
    data = data.translate(_UPPER)
    seed1 = 0x7FED7FED
    seed2 = 0xEEEEEEEE
    base = hash_type << 8
    ct = CRYPT_TABLE
    for ch in data:
        seed1 = (ct[base + ch] ^ (seed1 + seed2)) & 0xFFFFFFFF
        seed2 = (ch + seed1 + seed2 + (seed2 << 5) + 3) & 0xFFFFFFFF
    return seed1


def decrypt(data, key):
    """Decrypt a byte string (only whole dwords are decrypted; a tail is copied)."""
    n = len(data) // 4
    if n == 0:
        return bytes(data)
    vals = list(struct.unpack('<%dI' % n, data[:n * 4]))
    seed = 0xEEEEEEEE
    ct = CRYPT_TABLE
    for i in range(n):
        seed = (seed + ct[0x400 + (key & 0xFF)]) & 0xFFFFFFFF
        v = vals[i] ^ ((key + seed) & 0xFFFFFFFF)
        vals[i] = v
        key = ((((~key) << 0x15) + 0x11111111) & 0xFFFFFFFF) | (key >> 0x0B)
        seed = (v + seed + (seed << 5) + 3) & 0xFFFFFFFF
    return struct.pack('<%dI' % n, *vals) + bytes(data[n * 4:])


def encrypt(data, key):
    """Inverse of decrypt()."""
    n = len(data) // 4
    if n == 0:
        return bytes(data)
    vals = list(struct.unpack('<%dI' % n, data[:n * 4]))
    seed = 0xEEEEEEEE
    ct = CRYPT_TABLE
    for i in range(n):
        seed = (seed + ct[0x400 + (key & 0xFF)]) & 0xFFFFFFFF
        plain = vals[i]
        vals[i] = plain ^ ((key + seed) & 0xFFFFFFFF)
        key = ((((~key) << 0x15) + 0x11111111) & 0xFFFFFFFF) | (key >> 0x0B)
        seed = (plain + seed + (seed << 5) + 3) & 0xFFFFFFFF
    return struct.pack('<%dI' % n, *vals) + bytes(data[n * 4:])


def file_key(name, block_pos=0, file_size=0, fix_key=False):
    """Encryption key of a file: hash of the base name; FIX_KEY variant mixes position and size."""
    base = normalize(name).split('\\')[-1]
    key = hash_string(base, HASH_FILE_KEY)
    if fix_key:
        key = ((key + block_pos) ^ file_size) & 0xFFFFFFFF
    return key


HASH_TABLE_KEY = hash_string('(hash table)', HASH_FILE_KEY)
BLOCK_TABLE_KEY = hash_string('(block table)', HASH_FILE_KEY)
