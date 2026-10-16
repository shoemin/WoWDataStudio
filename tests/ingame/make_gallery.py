"""make_gallery.py RUN_DIR : builds RUN_DIR\\gallery\\ (renamed copies NN_item_view_before|after_loose|after_patch3.png) and RUN_DIR\\index.html
from RUN_DIR\\shots\\*.png (session.ps1 output, tags before / loose / patch3), RUN_DIR\\app_report.json and RUN_DIR\\judgement.json
({item id: {"loose": "PASS|FAIL|...", "patch3": ..., "why": "...", "views": {...}}} written by hand after looking at the pictures)."""
import html
import json
import os
import shutil
import sys

run = sys.argv[1]
shots = os.path.join(run, 'shots')
gal = os.path.join(run, 'gallery')
os.makedirs(gal, exist_ok=True)
app = {r['Id']: r for r in json.load(open(os.path.join(run, 'app_report.json'), encoding='utf-8'))}
judge = json.load(open(os.path.join(run, 'judgement.json'), encoding='utf-8'))

ITEMS = [
    ('01', 'boar_skin', ['s2_boars_wide', 's2_boar_near']),
    ('02', 'scorpid_mesh', ['s3_idle1', 's3_move', 's3_attack2', 's3_attack3']),
    ('03', 'tree_texture', ['s1_tree', 's1_den']),
    ('04', 'bush_mesh', ['s1_bush', 's1_pan1', 's1_den']),
    ('05', 'ground_texture', []),
    ('05b', 'ground_texture_specular', ['s1_den', 's1_mound', 's2_boars_wide']),
    ('06', 'terrain_mound', ['s1_mound', 's1_pan6', 's1_bush']),
    ('07', 'den_wmo_texture', ['s1_den', 's1_pan0']),
    ('08', 'robe_item_texture', ['s4_dressup']),
    ('09', 'shadow_bolt_effect', ['s2_sb1_b', 's2_sb2_b']),
    ('10', 'interface_textures', ['s4_ui_front']),
    ('11', 'character_skin', ['s4_front_clean', 's4_ui_front']),
]
TAGS = [('before', 'before'), ('loose', 'after_loose'), ('patch3', 'after_patch3')]
for iid, slug, views in ITEMS:
    j = judge.get(iid, {})
    if j.get('views'):
        views = j['views']
    for v in views:
        for tag, suffix in TAGS:
            src = os.path.join(shots, f'{v}_{tag}.png')
            if os.path.exists(src):
                shutil.copyfile(src, os.path.join(gal, f'{iid}_{slug}_{v}_{suffix}.png'))
    for extra in j.get('extra', []):        # burst frames etc.: [path relative to run, caption]
        pass


def esc(s):
    return html.escape(str(s))


def cls(v):
    v = (v or '').upper()
    return 'pass' if v.startswith('PASS') else 'fail' if v.startswith('FAIL') else 'na'


rows = []
for iid, slug, _ in ITEMS:
    a, j = app.get(iid, {}), judge.get(iid, {})
    rows.append(f"<tr><td><a href='#i{iid}'>{iid}</a></td><td>{esc(a.get('Title', slug))}</td>"
                f"<td class='{cls('PASS' if a.get('Ok') else 'FAIL')}'>{'PASS' if a.get('Ok') else 'FAIL'}</td>"
                f"<td class='{cls(j.get('loose'))}'>{esc(j.get('loose', '-'))}</td><td class='{cls(j.get('patch3'))}'>{esc(j.get('patch3', '-'))}</td></tr>")
sections = []
for iid, slug, views in ITEMS:
    a, j = app.get(iid, {}), judge.get(iid, {})
    if j.get('views'):
        views = j['views']
    lst = lambda xs: ''.join(f'<li>{esc(x)}</li>' for x in xs)  # noqa: E731
    imgs = ''.join(f"<a href='{esc(p)}'><img src='{esc(p)}' loading='lazy'></a>" for p in [x.replace('\\', '/') for x in a.get('Images', [])])
    pairs = []
    for v in views:
        cells = []
        for tag, suffix in TAGS:
            f = f'gallery/{iid}_{slug}_{v}_{suffix}.png'
            if os.path.exists(os.path.join(run, f)):
                cells.append(f"<figure><a href='{f}'><img src='{f}' loading='lazy'></a><figcaption>{esc(suffix.replace('_', ' '))}</figcaption></figure>")
            else:
                cells.append(f"<figure class='missing'><div>no {esc(suffix.replace('_', ' '))} picture</div></figure>")
        pairs.append(f"<div class='view'><h4>{esc(v)}{(' - ' + esc(j.get('captions', {}).get(v))) if j.get('captions', {}).get(v) else ''}</h4><div class='row'>{''.join(cells)}</div></div>")
    extras = ''.join(f"<figure><a href='{esc(p)}'><img src='{esc(p)}' loading='lazy'></a><figcaption>{esc(c)}</figcaption></figure>" for p, c in j.get('extra', []))
    sections.append(f"""
<section id='i{iid}'>
<h2>{iid}. {esc(a.get('Title', slug))}</h2>
<p class='verdict'>App: <b class='{cls('PASS' if a.get('Ok') else 'FAIL')}'>{'PASS' if a.get('Ok') else 'FAIL'}</b> - {esc(a.get('Verdict', ''))}<br>
In game, loose files: <b class='{cls(j.get('loose'))}'>{esc(j.get('loose', '-'))}</b> &nbsp; patch-3.MPQ: <b class='{cls(j.get('patch3'))}'>{esc(j.get('patch3', '-'))}</b></p>
<p><b>Expected look:</b> {esc(j.get('expected', ''))}</p>
<p><b>Judgement:</b> {esc(j.get('why', ''))}</p>
<details><summary>How it was made, validation, compare data, change sets</summary>
<p>{esc(a.get('How', ''))}</p>
<h4>Validation / review</h4><ul>{lst(a.get('Validation', []))}</ul>
<h4>Compare</h4><ul>{lst(a.get('Compare', []))}</ul>
<h4>Change sets</h4><ul>{lst(a.get('ChangeSets', []))}</ul>
<h4>Notes</h4><ul>{lst(a.get('Notes', []))}</ul>
</details>
<div class='app'>{imgs}</div>
{''.join(pairs)}
{('<h4>More pictures</h4><div class="row">' + extras + '</div>') if extras else ''}
</section>""")

page = f"""<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Data Studio In-Game Test</title><style>
:root {{ --bg:#f6f6f4; --fg:#1d1d1f; --mut:#666; --card:#fff; --line:#ddd; --pass:#137333; --fail:#b3261e; --na:#8a6d00; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#17181a; --fg:#e8e8e8; --mut:#9a9a9a; --card:#222327; --line:#3a3b40; --pass:#5ccf7a; --fail:#ff7b72; --na:#e3b341; }} }}
body {{ background:var(--bg); color:var(--fg); font:15px/1.45 system-ui,Segoe UI,sans-serif; margin:0 auto; max-width:1500px; padding:16px; }}
table {{ border-collapse:collapse; width:100%; background:var(--card); }} td,th {{ border:1px solid var(--line); padding:6px 8px; text-align:left; vertical-align:top; }}
.pass {{ color:var(--pass); font-weight:600; }} .fail {{ color:var(--fail); font-weight:600; }} .na {{ color:var(--na); font-weight:600; }}
section {{ background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 16px; margin:18px 0; }}
.row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(280px,1fr)); gap:8px; }}
figure {{ margin:0; }} figure img, .app img {{ width:100%; border-radius:4px; display:block; }} figcaption {{ color:var(--mut); font-size:13px; }}
.app {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(130px,1fr)); gap:6px; margin:8px 0; }}
.missing div {{ border:1px dashed var(--line); padding:30px; text-align:center; color:var(--mut); }}
h2 {{ overflow-wrap:anywhere; }} details {{ margin:6px 0; }} ul {{ margin:4px 0; padding-left:20px; font-size:13px; word-break:break-word; }}
</style></head><body>
<h1>WoW Data Studio: in-game visual test</h1>
<p>{esc(judge.get('_intro', ''))}</p>
<div style='overflow-x:auto'><table><tr><th>#</th><th>Change</th><th>App steps</th><th>In game: loose files</th><th>In game: patch-3.MPQ</th></tr>{''.join(rows)}</table></div>
{''.join(sections)}
</body></html>"""
open(os.path.join(run, 'index.html'), 'w', encoding='utf-8').write(page)
print('index.html written;', len(os.listdir(gal)), 'gallery pictures')
