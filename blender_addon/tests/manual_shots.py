"""Screenshots of the terrain workflow in Blender for the user manual (docs\\manual\\terrain-in-blender.md):

    blender --factory-startup EMPTY.blend --python manual_shots.py -- OUT_DIR wowdata_terrain.json

(EMPTY.blend: any saved .blend, e.g. the startup file saved once; opening a file keeps Blender's splash screen away.)

Opens Blender with its window (screenshots need the user interface), registers the add-on from this folder, imports the
package and walks through the steps, saving the whole window after each:
  71_blender_tiles           the tiles in their own colours with their labels, the WoW Terrain panel (N)
  72_blender_ground_textures Material Preview: the ground textures blended as in the game
  73_blender_seam_check      the edge of the centre tile raised; "Check edge seams" selects the vertices that differ
  74_blender_synced          after "Sync shared edges": the neighbour's edge follows, the check finds nothing
then exports the terrain (as "Export terrain for WoW Data Studio") and quits. Writes OUT_DIR\\manual_shots.json.
"""
import json
import math
import os
import sys
import traceback

import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import wowdata_blender  # noqa: E402
from wowdata_blender import terrain  # noqa: E402

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, MANIFEST = argv[0], argv[1]
os.makedirs(OUT, exist_ok=True)
log = {'ok': False, 'shots': []}


def view3d():
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == 'VIEW_3D':
                region = next(r for r in area.regions if r.type == 'WINDOW')
                return win, area, region, area.spaces.active
    return None, None, None, None


def look(target, distance, pitch, yaw):
    win, area, region, space = view3d()
    r3d = space.region_3d
    r3d.view_perspective = 'PERSP'
    r3d.view_location = Vector(target)
    r3d.view_distance = distance
    r3d.view_rotation = Euler((math.radians(90 - pitch), 0, math.radians(yaw)), 'XYZ').to_quaternion()
    space.clip_end = 30000.0
    space.clip_start = 1.0


def shot(name):
    win, area, region, space = view3d()
    path = os.path.join(OUT, name + '.png')
    with bpy.context.temp_override(window=win, area=area, region=region):
        bpy.ops.screen.screenshot(filepath=path)
    log['shots'].append(path)


def tile(name):
    return next(o for o in bpy.data.objects if o.get('wow_tile_id') == name)


def raise_east(name, rows=(40, 72), height=9.0, width=8):
    o = tile(name)
    me = o.data
    vid = me.attributes['wow_vid'].data
    for v in me.vertices:
        gid = vid[v.index].value - 1
        if gid < 129 * 129:
            r, c = divmod(gid, 129)
        else:
            r, c = divmod(gid - 129 * 129, 128)
            r, c = r + 0.5, c + 0.5
        depth = 128 - c
        if rows[0] <= r <= rows[1] and depth <= width:
            v.co.z += height * (1.0 - depth / (width + 1.0))
    me.update()


STEP = [0]
TS = 1600.0 / 3.0


def run():
    try:
        s = STEP[0]
        STEP[0] += 1
        win, area, region, space = view3d()
        if s == 0:
            for o in list(bpy.data.objects):
                bpy.data.objects.remove(o, do_unlink=True)
            try:
                wowdata_blender.register()
            except ValueError:
                pass
            log['import'] = terrain.import_wow_terrain(bpy.context, MANIFEST, references=False)
            space.show_region_ui = True
            space.overlay.show_wireframes = False
            look((TS / 2, -TS / 2, 120), 2600, 50, 20)
            return 1.0
        if s == 1:
            for r in area.regions:      # the WoW Terrain tab of the sidebar (known once the sidebar was drawn)
                if r.type == 'UI':
                    try:
                        r.active_panel_category = 'WoW Terrain'
                    except (AttributeError, TypeError) as e:
                        log['panel'] = str(e)
            area.tag_redraw()
            return 1.0
        if s == 2:
            shot('71_blender_tiles')
            terrain._set_shading(bpy.context, 'TEXTURES')
            look((TS / 2, -TS / 2, 120), 1500, 42, 25)
            return 12.0          # EEVEE compiles the ground materials
        if s == 3:
            shot('72_blender_ground_textures')
            terrain._set_shading(bpy.context, 'OBJECT')
            raise_east('Tile 32_48')
            issues = terrain.check_seams(bpy.context, select=True)
            bpy.context.scene['wow_seam_report'] = '%d edge(s) differ: %s' % (len(issues), '; '.join(
                '%s / %s (%s): %d vertices, up to %.2f' % (i['a'], i['b'], i['edge'], i['vertices'], i['max']) for i in issues))
            log['seams_after_edit'] = issues
            o = tile('Tile 32_48')
            bpy.context.view_layer.objects.active = o
            for x in bpy.data.objects:
                x.select_set(x == o or x == tile('Tile 33_48'))
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.object.mode_set(mode='EDIT')
            space.overlay.show_wireframes = True
            look((TS * 0.97, -TS * 56 / 128, 150), 1000, 38, 35)
            return 1.5
        if s == 4:
            shot('73_blender_seam_check')
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.object.mode_set(mode='OBJECT')
            rep = terrain.sync_edges(bpy.context, tile('Tile 32_48'))
            issues = terrain.check_seams(bpy.context, select=True)
            bpy.context.scene['wow_seam_report'] = 'Synced %d vertices on %d edges. All shared edges match.' % (rep['synced'], len(rep['edges'])) if not issues else str(issues)
            log['sync'] = rep
            log['seams_after_sync'] = issues
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.object.mode_set(mode='EDIT')
            return 1.5
        if s == 5:
            shot('74_blender_synced')
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.object.mode_set(mode='OBJECT')
            log['export'] = terrain.export_terrain(bpy.context, terrain.default_export_path(bpy.context))
            log['ok'] = True
    except Exception:  # noqa: BLE001
        log['error'] = traceback.format_exc()
    with open(os.path.join(OUT, 'manual_shots.json'), 'w', encoding='utf-8') as f:
        json.dump(log, f, indent=1)
    bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(run, first_interval=1.0)
