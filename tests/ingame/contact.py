"""contact.py OUT.png COLS WIDTH FILE... : a contact sheet (for quick looks at a series of screenshots), file names printed under each."""
import sys

from PIL import Image, ImageDraw

out, cols, width = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
files = sys.argv[4:]
ims = [Image.open(f).convert('RGB') for f in files]
h = int(width * ims[0].height / ims[0].width)
rows = (len(ims) + cols - 1) // cols
sheet = Image.new('RGB', (cols * width, rows * (h + 16)), (30, 30, 30))
d = ImageDraw.Draw(sheet)
for i, (f, im) in enumerate(zip(files, ims)):
    x, y = (i % cols) * width, (i // cols) * (h + 16)
    sheet.paste(im.resize((width, h)), (x, y))
    d.text((x + 4, y + h + 2), f.replace('\\', '/').split('/')[-1], fill=(255, 255, 255))
sheet.save(out)
print(out, sheet.size)
