# picdiff.py -- how much two game pictures (PNG, same size) differ: the share of pixels whose colour changed by more than a small
# threshold (the camera and the creatures move a little between BEFORE and AFTER, so the number is compared with the gallery, not alone).
#   python -I picdiff.py before.png after.png      ->  changed=12.3% meanabs=7.1 size=1920x1080
import struct
import sys
import zlib


def load_png(path):
    data = open(path, 'rb').read()
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise SystemExit('not a PNG: ' + path)
    pos, idat, w = 8, b'', 0
    while pos < len(data):
        n, kind = struct.unpack('>I4s', data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + n]
        if kind == b'IHDR':
            w, h, depth, ctype = struct.unpack('>IIBB', body[:10])
            if depth != 8 or ctype not in (2, 6):
                raise SystemExit('only 8-bit RGB / RGBA PNGs: ' + path)
            bpp = 3 if ctype == 2 else 4
        elif kind == b'IDAT':
            idat += body
        pos += 12 + n
    raw = zlib.decompress(idat)
    stride = w * bpp
    rows, prev, p = [], bytearray(stride), 0
    for _y in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, bpp, rows


def main():
    w1, h1, b1, r1 = load_png(sys.argv[1])
    w2, h2, b2, r2 = load_png(sys.argv[2])
    if (w1, h1) != (w2, h2):
        print('size differs: %dx%d vs %dx%d' % (w1, h1, w2, h2))
        return
    changed = total = 0
    absum = 0
    for y in range(0, h1, 2):
        a, b = r1[y], r2[y]
        for x in range(0, w1, 2):
            d = sum(abs(a[x * b1 + k] - b[x * b2 + k]) for k in range(3))
            absum += d
            total += 1
            if d > 60:
                changed += 1
    print('changed=%.2f%% meanabs=%.1f size=%dx%d' % (100.0 * changed / max(total, 1), absum / max(total, 1) / 3, w1, h1))


if __name__ == '__main__':
    main()
