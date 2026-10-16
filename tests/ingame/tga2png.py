"""Convert screenshots: stdin lines "SRC|DST" (TGA or BMP -> PNG, or JPEG when DST ends in .jpg).
-delete  remove the sources after conversion
-burst   external capture series: keep every 2nd frame (about 15 per second), 960 px wide JPEG (a 5 s series was 0.4 GB as PNG)
-burstall  the same, but every frame is kept (animation timing: the 0.10.0 test's walk cycles)"""
import os
import re
import sys

from PIL import Image

delete = '-delete' in sys.argv
burstall = '-burstall' in sys.argv
burst = '-burst' in sys.argv or burstall
for line in sys.stdin:
    line = line.strip().lstrip(chr(0xFEFF))       # PowerShell's pipe into a native program starts with a BOM
    if not line or '|' not in line:
        continue
    src, dst = line.split('|', 1)
    try:
        if burst:
            m = re.search(r'_(\d+)\.bmp$', src)
            if m and int(m.group(1)) % 2 == 0 and not burstall:
                os.remove(src)
                continue
        im = Image.open(src).convert('RGB')
        if burst:
            im = im.resize((960, int(960 * im.height / im.width)), Image.LANCZOS)
            im.save(dst, quality=82)
        else:
            im.save(dst, optimize=True)
        if delete:
            os.remove(src)
        print('converted', os.path.basename(dst))
    except Exception as e:  # noqa: BLE001
        print('FAILED', src, e)
