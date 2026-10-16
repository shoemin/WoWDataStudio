"""Build build\\wowdata_blender.zip for Blender's "Install from Disk..." (run: python blender_addon\\build_zip.py)."""
import os
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), 'build', 'wowdata_blender.zip')


def build(out=OUT):
    os.makedirs(os.path.dirname(out), exist_ok=True)
    src = os.path.join(HERE, 'wowdata_blender')
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(src):
            dirs[:] = [d for d in dirs if d != '__pycache__']
            for f in files:
                if f.endswith('.pyc'):
                    continue
                p = os.path.join(root, f)
                z.write(p, os.path.relpath(p, HERE))
    return out


if __name__ == '__main__':
    print('wrote', build())
