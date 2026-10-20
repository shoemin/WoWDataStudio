"""make_gallery_designers.py RUN_DIR : RUN_DIR\\index.html for the designers' in-game test (run_designers.ps1; 0.12 / 0.13).
The style of make_gallery_v010.py. Inputs: RUN_DIR\\shots\\<view>_<tag>.png (the run being judged) and, when present,
RUN_DIR\\run1\\shots\\ (an earlier run in the same folder, kept for the parts it alone has or for its faults);
<shots>\\burst_<tag>\\<series>_NNN.jpg (+ <series>_times.tsv); <shots>\\logs\\*_wowcode.log (scenario notes);
RUN_DIR\\app_report.json; RUN_DIR\\judgement.json ({item: {"verdict": {tag: "..."}, "why": "...", "expected": "...",
"best": [paths]}, "_intro": "..."}). Frame-series strips go to RUN_DIR\\gallery\\."""
import glob
import html
import json
import os
import re
import sys

from PIL import Image, ImageDraw

run = sys.argv[1]
gal = os.path.join(run, 'gallery')
os.makedirs(gal, exist_ok=True)
app = {r['Id']: r for r in json.load(open(os.path.join(run, 'app_report.json'), encoding='utf-8'))}
jpath = os.path.join(run, 'judgement.json')
judge = json.load(open(jpath, encoding='utf-8')) if os.path.exists(jpath) else {}
TAGS = ['before', 'srvonly', 'loose', 'patch3', 'afterundo']
RUNS = [('shots', 'run 4'), (os.path.join('run3', 'shots'), 'run 3'), (os.path.join('run2', 'shots'), 'run 2'), (os.path.join('run1', 'shots'), 'run 1')]
OPEN = {'run 2', 'run 4'}
ITEMS = [
    ('S1', ['q1_B_offered', 'q1_B_reward', 'q1_B_reward_tip', 'q1_spellbook', 'q1_s1_icon', 'c1_wide', 'c1_cast1_a', 'c1_cast1_b', 'c1_cast1_c',
            'c1_cast2_a', 'c1_cast2_b', 'c1_cast2_c', 'c1_cast3_a', 'c1_cast3_b', 'c1_cast3_c', 'c1_combatlog', 'c1_tooltip',
            't1_trainer_npc', 't1_trainer_window', 't1_trainer_bought', 't1_spellbook'],
     ['cast1', 'cast2', 'cast3'], r'S1|spell book|tooltip|after cast|trainer|service|learned|reward spell|cast '),
    ('T1', ['k1_tree', 'k1_tip_new', 'k1_tip_moved'], [], r'talent|Shadowburn|tab '),
    ('Q1', ['q1_giver_ui', 'q1_giver_clean', 'q1_A_detail', 'q1_A_nodetail', 'q1_A_log', 'q1_A_boarview', 'q1_A_kill_1', 'q1_A_kill_2', 'q1_A_tracked',
            'q1_A_progress', 'q1_A_reward', 'q1_B_offered', 'q1_B_notoffered', 'q1_B_reward', 'q1_bags', 'q1_spellbook'],
     [], r'detail|board|reward|window|before|after|GM|try|giver|log text|progress|B in'),
]


def esc(s):
    return html.escape(str(s))


def strip(shots, series, tag, label, n=12):
    """n frames spread over a frame series, with their capture times, as one picture."""
    fs = sorted(glob.glob(os.path.join(run, shots, f'burst_{tag}', f'{series}_*.jpg')))
    if not fs:
        return None
    times = {}
    tp = os.path.join(run, shots, f'burst_{tag}', f'{series}_times.tsv')
    if os.path.exists(tp):
        for line in open(tp, encoding='utf-8-sig'):
            parts = line.strip().split('\t')
            if len(parts) == 2 and parts[0].isdigit():
                times[int(parts[0])] = int(parts[1])
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
    out = os.path.join(gal, f'{series}_{tag}_{label.replace(" ", "")}.jpg')
    sheet.save(out, quality=85)
    return os.path.relpath(out, run).replace('\\', '/'), len(fs)


def notes(shots, tag, pattern):
    res = []
    for lf in sorted(glob.glob(os.path.join(run, shots, 'logs', f'{tag}_*_wowcode.log'))):
        for line in open(lf, encoding='utf-8', errors='replace'):
            m = re.search(r'scenario (note|step FAIL): (.*)', line)
            if m and (m.group(1) == 'step FAIL' or re.search(pattern, m.group(2))):
                res.append(f'{os.path.basename(lf).split("_wowcode")[0]}: {"FAIL " if m.group(1) == "step FAIL" else ""}{m.group(2).strip()[:400]}')
    return res


def fig(path, cap):
    return f"<figure><a href='{esc(path)}'><img src='{esc(path)}' loading='lazy'></a><figcaption>{esc(cap)}</figcaption></figure>"


lst = lambda xs: ''.join(f'<li>{esc(x)}</li>' for x in xs)  # noqa: E731
secs, rows = [], []
for iid, views, series, pattern in ITEMS:
    a, j = app.get(iid, {}), judge.get(iid, {})
    verdicts = j.get('verdict', {})
    rows.append(f"<tr><td><a href='#i{iid}'>{iid}</a></td><td>{esc(a.get('Title', ''))}</td><td>{'PASS' if a.get('Ok') else 'FAIL'}</td>"
                + ''.join(f"<td>{esc(verdicts.get(t, '-'))}</td>" for t in TAGS) + f"<td>{esc(j.get('overall', '-'))}</td></tr>")
    body = []
    best = j.get('best', [])
    if best:
        body.append("<h3>Best pictures</h3><div class='row'>" + ''.join(fig(b, b) for b in best if os.path.exists(os.path.join(run, b))) + '</div>')
    for shots, label in RUNS:
        part = []
        for v in views:
            cells = [fig(f'{shots}/{v}_{t}.png'.replace('\\', '/'), f'{label} {t}') for t in TAGS if os.path.exists(os.path.join(run, shots, f'{v}_{t}.png'))]
            if cells:
                part.append(f"<h4>{esc(v)} ({label})</h4><div class='row'>{''.join(cells)}</div>")
        for s in series:
            for t in TAGS:
                r = strip(shots, s, t, label)
                if r:
                    part.append(f"<h4>frame series {esc(s)} [{t}] ({r[1]} frames, {label})</h4>" + fig(r[0], f'{s} {t} {label}'))
        text = ''.join(f"<h4>logged in game [{t}, {label}]</h4><ul>{lst(notes(shots, t, pattern))}</ul>" for t in TAGS if notes(shots, t, pattern))
        if part or text:
            body.append(f"<details{' open' if label in OPEN else ''}><summary><b>{esc(label)}</b></summary>{''.join(part)}{text}</details>")
    secs.append(f"""<section id='i{iid}'><h2>{iid}. {esc(a.get('Title', ''))}</h2>
<p>App: <b>{'PASS' if a.get('Ok') else 'FAIL'}</b> - {esc(a.get('Verdict', ''))}</p>
<p><b>Expected in game:</b> {esc(j.get('expected', ''))}</p><p><b>Judgement ({esc(j.get('overall', 'not judged'))}):</b> {esc(j.get('why', 'not judged yet'))}</p>
<details><summary>How it was made, validation, compare, change sets</summary><p>{esc(a.get('How', ''))}</p>
<h4>Validation</h4><ul>{lst(a.get('Validation', []))}</ul><h4>Compare</h4><ul>{lst(a.get('Compare', []))}</ul>
<h4>Change sets</h4><ul>{lst(a.get('ChangeSets', []))}</ul><h4>Notes</h4><ul>{lst(a.get('Notes', []))}</ul></details>
{''.join(body)}</section>""")
page = f"""<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Designers In-Game Test</title><style>
:root {{ --bg:#f6f6f4; --fg:#1d1d1f; --mut:#666; --card:#fff; --line:#ddd; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#17181a; --fg:#e8e8e8; --mut:#9a9a9a; --card:#222327; --line:#3a3b40; }} }}
body {{ background:var(--bg); color:var(--fg); font:15px/1.45 system-ui,Segoe UI,sans-serif; margin:0 auto; max-width:1500px; padding:16px; }}
table {{ border-collapse:collapse; width:100%; background:var(--card); }} td,th {{ border:1px solid var(--line); padding:6px 8px; text-align:left; vertical-align:top; }}
section {{ background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 16px; margin:18px 0; }}
.row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(280px,1fr)); gap:8px; }}
figure {{ margin:0; }} figure img {{ width:100%; border-radius:4px; display:block; }} figcaption {{ color:var(--mut); font-size:13px; }}
ul {{ font-size:13px; word-break:break-word; }} summary {{ cursor:pointer; margin:8px 0; }}
</style></head><body><h1>WoW Data Studio 0.12 / 0.13 designers: in-game test</h1><p>{esc(judge.get('_intro', ''))}</p>
<div style='overflow-x:auto'><table><tr><th>#</th><th>Change</th><th>App</th>{''.join(f'<th>{t}</th>' for t in TAGS)}<th>Overall</th></tr>{''.join(rows)}</table></div>
{''.join(secs)}</body></html>"""
open(os.path.join(run, 'index.html'), 'w', encoding='utf-8').write(page)
print('index.html written;', len(os.listdir(gal)), 'gallery pictures')
