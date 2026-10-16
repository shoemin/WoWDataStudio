"""extras.py RUN_DIR : the gallery's extra pictures (crops and burst strips) into RUN_DIR\\gallery\\extras\\, chosen after looking at the shots."""
import glob
import os
import sys

from PIL import Image, ImageDraw

run = sys.argv[1]
shots = os.path.join(run, 'shots')
out = os.path.join(run, 'gallery', 'extras')
os.makedirs(out, exist_ok=True)
TAGS = ['before', 'loose', 'patch3']


def label(im, text):
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, 8 + 7 * len(text), 16), fill=(0, 0, 0))
    d.text((4, 2), text, fill=(255, 255, 255))
    return im


def row(images, h=None):
    h = h or min(i.height for i in images)
    images = [i.resize((int(i.width * h / i.height), h)) for i in images]
    W = Image.new('RGB', (sum(i.width for i in images), h))
    x = 0
    for i in images:
        W.paste(i, (x, 0))
        x += i.width
    return W


def save(im, name):
    im.save(os.path.join(out, name), quality=88) if name.endswith('.jpg') else im.save(os.path.join(out, name))
    print(name, im.size)


# 10: player + target frames (top left) and the action bar (bottom left), per state
frames = [label(Image.open(f'{shots}/s4_ui_front_{t}.png').crop((0, 0, 600, 180)), t) for t in TAGS]
save(row(frames), '10_ui_frames_before_loose_patch3.png')
bars = [label(Image.open(f'{shots}/s4_ui_front_{t}.png').crop((150, 830, 560, 900)).resize((820, 140)), t) for t in TAGS]
W = Image.new('RGB', (820, 420))
for k, b in enumerate(bars):
    W.paste(b, (0, k * 140))
save(W, '10_action_bar_before_loose_patch3.png')

# 9: the missile in flight (external capture, 960 px frames): baseline, loose, patch-3, and the first attempt (plain tint) with its square
mis = [('burst_before/sb1_057.jpg', 'before'), ('burst_loose/sb1_065.jpg', 'after loose'), ('burst_patch3/sb1_071.jpg', 'after patch-3'),
       ('pass1_s2/burst_loose/sb1_069.jpg', 'first attempt (plain tint): additive glow became a square')]
save(row([label(Image.open(f'{shots}/{f}').crop((380, 180, 960, 420)), t) for f, t in mis]), '09_missile_in_flight.png')
hand = [('burst_before/sb1_055.jpg', 'before'), ('burst_loose/sb1_063.jpg', 'after loose'), ('burst_patch3/sb1_067.jpg', 'after patch-3')]
save(row([label(Image.open(f'{shots}/{f}').crop((300, 160, 800, 420)), t) for f, t in hand]), '09_missile_release.png')

# 1: a boar next to the character (it ran in after the Shadow Bolt hit a neighbour) in the burst frames
boar = [('burst_before/sb2_063.jpg', 'before'), ('burst_loose/sb2_055.jpg', 'after loose'), ('burst_patch3/sb2_061.jpg', 'after patch-3')]
save(row([label(Image.open(f'{shots}/{f}').crop((300, 160, 800, 420)), t) for f, t in boar]), '01_boar_close.png')

# 2: the scorpid's attack animation: 6 frames spread over the 4 s capture, per state
for t in TAGS:
    fs = sorted(glob.glob(f'{shots}/burst_{t}/scorpid_*.jpg'))
    if not fs:
        continue
    pick = [fs[int(i * (len(fs) - 1) / 5)] for i in range(6)]
    strip = row([label(Image.open(f).crop((240, 120, 720, 420)), os.path.basename(f)) for f in pick], 220)
    save(strip, f'02_scorpid_attack_frames_{t}.jpg')
for t in TAGS:
    im = Image.open(f'{shots}/s3_attack3_{t}.png').crop((560, 360, 1100, 800))
    save(label(im, t), f'02_scorpid_attack3_crop_{t}.png')

# 5 / 5b: the same view (S2, over the boars) with only DurotarDirt.blp edited (pass 1) and with DurotarDirt_s.blp edited too (pass 3)
for name, files in [('05_ground_DurotarDirt_only', ['s2_boars_wide_before.png', 'pass1_s2/s2_boars_wide_loose.png', 'pass1_s2/s2_boars_wide_patch3.png']),
                    ('05b_ground_with_DurotarDirt_s', ['s2_boars_wide_before.png', 's2_boars_wide_loose.png', 's2_boars_wide_patch3.png'])]:
    ims = [label(Image.open(f'{shots}/{f}').resize((800, 450)), t) for f, t in zip(files, ['before', 'after loose', 'after patch-3'])]
    save(row(ims), name + '.jpg')