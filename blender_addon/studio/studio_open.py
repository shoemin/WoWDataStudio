"""Start-up script of WoW Data Studio's "Open in Blender": loads the wowdata_blender add-on and imports a package.

    blender --python studio_open.py -- MODEL.gltf ADDON_ROOT ADDON_VERSION
    blender --python studio_open.py -- wowdata_terrain.json ADDON_ROOT ADDON_VERSION     (map tiles, 0.6.0)

The add-on installed in Blender is used when it is at least ADDON_VERSION; otherwise the copy shipped with WoW Data
Studio (ADDON_ROOT\\wowdata_blender) is loaded for this session only (nothing is installed).
"""
import os
import sys

import addon_utils
import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
GLTF, ROOT, WANT = argv[0], argv[1], tuple(int(x) for x in argv[2].split('.'))


def _load_addon():
    installed = next((m for m in addon_utils.modules() if m.__name__ == 'wowdata_blender'), None)
    if installed is not None and tuple(installed.bl_info.get('version', (0, 0, 0))) >= WANT:
        addon_utils.enable('wowdata_blender', default_set=False)
        return sys.modules['wowdata_blender'], 'installed'
    if 'wowdata_blender' in sys.modules:
        try:
            addon_utils.disable('wowdata_blender', default_set=False)
        except Exception:  # noqa: BLE001
            pass
        del sys.modules['wowdata_blender']
    sys.path.insert(0, ROOT)
    import wowdata_blender  # noqa: E402
    wowdata_blender.register()
    return wowdata_blender, 'from WoW Data Studio'


def _run():
    mod, where = _load_addon()
    for o in list(bpy.data.objects):          # the start-up scene's cube, camera and light
        bpy.data.objects.remove(o, do_unlink=True)
    if GLTF.lower().endswith('.json'):
        rep = mod.terrain.import_wow_terrain(bpy.context, GLTF)
    else:
        rep = mod.import_wow_gltf(bpy.context, GLTF)
    print('WoW Data Studio: imported %s with the add-on (%s): %s' % (GLTF, where, rep))
    # frame the model in every 3D view
    try:
        for win in bpy.context.window_manager.windows:
            for area in win.screen.areas:
                if area.type == 'VIEW_3D':
                    region = next(r for r in area.regions if r.type == 'WINDOW')
                    with bpy.context.temp_override(window=win, area=area, region=region):
                        bpy.ops.view3d.view_all()
    except Exception:  # noqa: BLE001
        pass
    # tests: WOWDATA_OPEN_REPORT=<file> writes what was imported and closes Blender
    report = os.environ.get('WOWDATA_OPEN_REPORT')
    if report:
        import json
        with open(report, 'w', encoding='utf-8') as f:
            json.dump({'addon': where, 'version': '.'.join(str(x) for x in mod.bl_info['version']), 'import': rep,
                       'objects': len(bpy.data.objects), 'background': bpy.app.background,
                       'object_names': sorted(o.name for o in bpy.data.objects)}, f)
        bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(_run, first_interval=0.2)
