"""Install and enable the wowdata_blender add-on in this Blender, then save the preferences (run by WoW Data Studio's
"Install the Blender add-on...", headless):

    blender -b --python studio_install.py -- RESULT.json wowdata_blender.zip

Uses Blender's own add-on installer (the same as Edit > Preferences > Add-ons > Install from Disk...).
"""
import json
import sys
import traceback

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
result_path, zip_path = argv[0], argv[1]
out = {'ok': False, 'blender': bpy.app.version_string}
try:
    out['install'] = list(bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True))
    out['enable'] = list(bpy.ops.preferences.addon_enable(module='wowdata_blender'))
    out['save'] = list(bpy.ops.wm.save_userpref())
    import wowdata_blender  # noqa: E402
    out['module_file'] = wowdata_blender.__file__
    out['version'] = '.'.join(str(x) for x in wowdata_blender.bl_info['version'])
    out['operators'] = [n for n in ('import_gltf', 'import_target', 'export_for_studio') if n in dir(bpy.ops.wowdata)]
    out['terrain_operators'] = [n for n in ('import_terrain', 'sync_edges', 'check_seams', 'export_terrain') if n in dir(bpy.ops.wowdata)]
    out['user_scripts'] = bpy.utils.user_resource('SCRIPTS')
    out['ok'] = 'FINISHED' in out['install'] and 'FINISHED' in out['enable'] and len(out['operators']) == 3
    out['message'] = ('The add-on %s is installed and enabled in Blender %s.' % (out['version'], bpy.app.version_string)) if out['ok'] else \
        'Blender did not enable the add-on.'
except Exception as e:  # noqa: BLE001
    out['message'] = 'Blender reported an error: %s' % e
    out['error'] = traceback.format_exc()
with open(result_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1)
