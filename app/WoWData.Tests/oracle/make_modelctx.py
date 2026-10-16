"""Test oracle for the C# model looks (WoWData.Core.M2.ModelResolver): the Python wowdata/modelctx.py results for a few
character looks, NPC displays and creature displays, written into <outdir> (app/.testcache/oracle/modelctx/; it holds
data derived from Blizzard's files: keep it out of source control).

Usage:
    python make_modelctx.py <outdir>

Files written (done.txt last):
    looks.json            [{race, sex, look, body: file, size, hair, extra, geosets, parts}] and creature displays
    body_<n>.rgba         the composited 256x256 body texture of look n (raw RGBA)
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
for p in (os.getcwd(), TOOL):
    if p not in sys.path:
        sys.path.insert(0, p)

from wowdata import config, vfs                  # noqa: E402
from wowdata.modelctx import character_textures, character_geosets, _creature_display   # noqa: E402
from wowdata.resolve import Catalog              # noqa: E402

LOOKS = [
    (2, 0, (0, 0, 0, 0, 0)),     # orc male, default
    (2, 0, (3, 2, 3, 2, 3)),
    (2, 0, (6, 4, 5, 6, 6)),
    (6, 1, (1, 1, 2, 1, 0)),     # tauren female
    (1, 1, (2, 3, 4, 5, 0)),     # human female
    (4, 0, (1, 2, 3, 4, 5)),     # night elf male
]
DISPLAYS = [503, 4713, 2450]


def main(out):
    os.makedirs(out, exist_ok=True)
    cfg = config.load()
    client = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)
    cat = Catalog(client)
    rows = []
    for n, (race, sex, look) in enumerate(LOOKS):
        skin, face, hs, hc, fh = look
        texs, _ = character_textures(cat, race, sex, skin, face, hs, hc, fh)
        body = texs.get(1)
        row = {'race': race, 'sex': sex, 'look': list(look), 'geosets': sorted(character_geosets(cat, race, sex, hs, fh)),
               'hair': texs[6].path if 6 in texs else None, 'extra': texs[8].path if 8 in texs else None}
        if body is not None:
            fn = 'body_%d.rgba' % n
            with open(os.path.join(out, fn), 'wb') as fh_:
                fh_.write(body.rgba.tobytes())
            row['body'] = fn
            row['size'] = [int(body.rgba.shape[1]), int(body.rgba.shape[0])]
            row['source'] = body.source
        rows.append(row)
    disp = []
    for d in DISPLAYS:
        ctx = _creature_display(cat, d)
        disp.append({'display': d, 'model': ctx.model_path, 'slots': {str(k): t.path for k, t in ctx.slots.items()}})
    with open(os.path.join(out, 'looks.json'), 'w', encoding='utf-8') as f:
        json.dump({'looks': rows, 'displays': disp}, f, indent=1)
    with open(os.path.join(out, 'done.txt'), 'w') as f:
        f.write('%d looks\n' % len(rows))


if __name__ == '__main__':
    main(sys.argv[1])
