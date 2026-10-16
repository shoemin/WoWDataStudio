"""Headless check that build\wowdata_blender.zip installs and enables through Blender's own add-on installer.
Run with BLENDER_USER_SCRIPTS pointing at a temporary folder so the user's Blender setup is not touched:

    blender-launcher.exe -b --factory-startup --python blender_install_test.py -- RESULT.json wowdata_blender.zip
"""
import json
import sys

import bpy

argv = sys.argv[sys.argv.index('--') + 1:]
result, zip_path = argv[0], argv[1]
out = {'install': list(bpy.ops.preferences.addon_install(filepath=zip_path, overwrite=True)),
       'enable': list(bpy.ops.preferences.addon_enable(module='wowdata_blender'))}
import wowdata_blender  # noqa: E402
out['module_file'] = wowdata_blender.__file__
out['operators'] = [n for n in ('import_gltf', 'import_target') if hasattr(bpy.types, 'WOWDATA_OT_' + n)]
out['user_scripts'] = bpy.utils.user_resource('SCRIPTS')
with open(result, 'w', encoding='utf-8') as f:
    json.dump(out, f)
