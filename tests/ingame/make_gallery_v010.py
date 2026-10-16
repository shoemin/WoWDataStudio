"""make_gallery_v010.py RUN_DIR : RUN_DIR\\index.html for the 0.9.0 / 0.10.0 in-game test (run_v010.ps1).
Inputs: RUN_DIR\\shots\\<view>_<tag>.png (tags before / srvonly / loose / patch3 / afterundo), RUN_DIR\\shots\\burst_<tag>\\<series>_NNN.jpg
(+ <series>_times.tsv), RUN_DIR\\shots\\logs\\*_wowcode.log (the scenario notes: target names, merchant items, tooltip lines),
RUN_DIR\\app_report.json, RUN_DIR\\app\\renders\\*.png, RUN_DIR\\judgement.json (written by hand after looking at the pictures:
{item: {"verdict": {tag: "PASS ..."}, "why": "...", "expected": "..."}}; optional). Frame-series strips go to RUN_DIR\\gallery\\."""
import glob
import html
import json
import os
import re
import sys

from PIL import Image, ImageDraw

run = sys.argv[1]
shots = os.path.join(run, 'shots')
gal = os.path.join(run, 'gallery')
os.makedirs(gal, exist_ok=True)
app = {r['Id']: r for r in json.load(open(os.path.join(run, 'app_report.json'), encoding='utf-8'))}
jpath = os.path.join(run, 'judgement.json')
judge = json.load(open(jpath, encoding='utf-8')) if os.path.exists(jpath) else {}
TAGS = ['before', 'srvonly', 'loose', 'patch3', 'afterundo']
ITEMS = [
    ('A1', ['b1_wide', 'b1_side_1', 'b1_side_2'], ['herd_east', 'herd_side'], ['A1_walk_phases_original_top_edit_bottom']),
    ('A2', ['b1_boar_near', 'b1_east_1', 'b1_east_3'], ['herd_east', 'herd_west'], ['A2_stand_original_top_new_stand4_bottom']),
    ('A3', ['b1_boar_near', 'b1_side', 'b1_other_side'], [], ['A3_spike_edit_top_original_bottom']),
    ('A4', ['q1_idle', 'q1_idle_side', 'q1_attack2', 'q1_attack_front'], ['quil_attack'], ['A4_quilboar_original_edit']),
    ('A5', ['c1_attack2', 'c1_attack4', 'c2_attack2', 'c2_attack4'], ['scorp_attack', 'scorp_rh_attack'],
     ['A5_AttackUnarmed_original_top_edit_bottom', 'A5_AttackUnarmed2_original_top_edit_bottom']),
    ('S1', ['v1_merchant', 'v1_tip_blade', 'v1_link_blade', 'v1_icons'], [], []),
    ('S2', ['v1_vendor_ui', 'v1_vendor_clean', 'v1_vendor_side', 'v1_merchant'], [], []),
    ('S3', ['v1_tip_draught', 'v1_link_draught', 'v1_icons'], [], []),
]


def esc(s):
    return html.escape(str(s))


def strip(series, tag, n=12):
    """n frames spread over a frame series, with their capture times, as one picture."""
    fs = sorted(glob.glob(os.path.join(shots, f'burst_{tag}', f'{series}_*.jpg')))
    if not fs:
        return None
    times = {}
    tp = os.path.join(shots, f'burst_{tag}', f'{series}_times.tsv')
    if os.path.exists(tp):
        for line in open(tp, encoding='utf-8-sig'):
            k, ms = line.strip().split('\t')
            times[int(k)] = int(ms)
    pick = [fs[int(i * (len(fs) - 1) / (n - 1))] for i in range(n)] if len(fs) >= n else fs
    ims = [Image.open(f).convert('RGB') for f in pick]
    w = 320
    h = int(w * ims[0].height / ims[0].width)
    cols = 6
    rows = (len(ims) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * w, rows * (h + 16)), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    for i, (f, im) in enumerate(zip(pick, ims)):
        x, y = (i % cols) * w, (i // cols) * (h + 16)
        sheet.paste(im.resize((w, h)), (x, y))
        k = int(re.search(r'_(\d+)\.jpg$', f).group(1))
        d.text((x + 4, y + h + 2), f'{tag} frame {k}' + (f' @ {times[k] / 1000:.2f} s' if k in times else ''), fill=(255, 255, 255))
    out = os.path.join(gal, f'{series}_{tag}.jpg')
    sheet.save(out, quality=85)
    return os.path.relpath(out, run).replace('\\', '/'), len(fs)


def notes(tag):
    res = []
    for lf in glob.glob(os.path.join(shots, 'logs', f'{tag}_*_wowcode.log')):
        for line in open(lf, encoding='utf-8', errors='replace'):
            m = re.search(r'scenario note: (.*)', line)
            if m and re.search(r'tooltip|merchant|GetItemInfo|target name|^target ', m.group(1)):
                res.append(f'{os.path.basename(lf).split("_wowcode")[0]}: {m.group(1).strip()}')
    return res


def fig(path, cap):
    return f"<figure><a href='{esc(path)}'><img src='{esc(path)}' loading='lazy'></a><figcaption>{esc(cap)}</figcaption></figure>"


secs, rows = [], []
for iid, views, series, renders in ITEMS:
    a, j = app.get(iid, {}), judge.get(iid, {})
    verdicts = j.get('verdict', {})
    rows.append(f"<tr><td><a href='#i{iid}'>{iid}</a></td><td>{esc(a.get('Title', ''))}</td><td>{'PASS' if a.get('Ok') else 'FAIL'}</td>"
                + ''.join(f"<td>{esc(verdicts.get(t, '-'))}</td>" for t in TAGS) + '</tr>')
    body = []
    for v in views:
        cells = [fig(f'shots/{v}_{t}.png', t) for t in TAGS if os.path.exists(os.path.join(shots, f'{v}_{t}.png'))]
        if cells:
            body.append(f"<h4>{esc(v)}</h4><div class='row'>{''.join(cells)}</div>")
    for s in series:
        for t in TAGS:
            r = strip(s, t)
            if r:
                body.append(f"<h4>frame series {esc(s)} [{t}] ({r[1]} frames)</h4>" + fig(r[0], f'{s} {t}'))
    app_imgs = ''.join(fig(f'app/renders/{r}.png', r) for r in renders if os.path.exists(os.path.join(run, 'app', 'renders', r + '.png')))
    lst = lambda xs: ''.join(f'<li>{esc(x)}</li>' for x in xs)  # noqa: E731
    text = ''
    if iid.startswith('S'):
        text = ''.join(f"<h4>logged in game [{t}]</h4><ul>{lst(notes(t))}</ul>" for t in TAGS if notes(t))
    secs.append(f"""<section id='i{iid}'><h2>{iid}. {esc(a.get('Title', ''))}</h2>
<p>App: <b>{'PASS' if a.get('Ok') else 'FAIL'}</b> - {esc(a.get('Verdict', ''))}</p>
<p><b>Expected in game:</b> {esc(j.get('expected', ''))}</p><p><b>Judgement:</b> {esc(j.get('why', 'not judged yet'))}</p>
<details><summary>How it was made, validation, compare, change sets</summary><p>{esc(a.get('How', ''))}</p>
<h4>Validation</h4><ul>{lst(a.get('Validation', []))}</ul><h4>Compare</h4><ul>{lst(a.get('Compare', []))}</ul>
<h4>Change sets</h4><ul>{lst(a.get('ChangeSets', []))}</ul><h4>Notes</h4><ul>{lst(a.get('Notes', []))}</ul></details>
<div class='row'>{app_imgs}</div>{''.join(body)}{text}</section>""")
page = f"""<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Data Studio 0.10 Test</title><style>
:root {{ --bg:#f6f6f4; --fg:#1d1d1f; --mut:#666; --card:#fff; --line:#ddd; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#17181a; --fg:#e8e8e8; --mut:#9a9a9a; --card:#222327; --line:#3a3b40; }} }}
body {{ background:var(--bg); color:var(--fg); font:15px/1.45 system-ui,Segoe UI,sans-serif; margin:0 auto; max-width:1500px; padding:16px; }}
table {{ border-collapse:collapse; width:100%; background:var(--card); }} td,th {{ border:1px solid var(--line); padding:6px 8px; text-align:left; vertical-align:top; }}
section {{ background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 16px; margin:18px 0; }}
.row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(280px,1fr)); gap:8px; }}
figure {{ margin:0; }} figure img {{ width:100%; border-radius:4px; display:block; }} figcaption {{ color:var(--mut); font-size:13px; }}
ul {{ font-size:13px; word-break:break-word; }}
</style></head><body><h1>WoW Data Studio 0.9.0 / 0.10.0: in-game visual test</h1><p>{esc(judge.get('_intro', ''))}</p>
<div style='overflow-x:auto'><table><tr><th>#</th><th>Change</th><th>App</th>{''.join(f'<th>{t}</th>' for t in TAGS)}</tr>{''.join(rows)}</table></div>
{''.join(secs)}</body></html>"""
open(os.path.join(run, 'index.html'), 'w', encoding='utf-8').write(page)
print('index.html written;', len(os.listdir(gal)), 'gallery pictures')
