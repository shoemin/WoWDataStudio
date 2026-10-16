"""Screenshots of the skeleton and animation workflow in Blender for the user manual
(docs\\manual\\skeleton-and-animation.md):

    blender --factory-startup EMPTY.blend --python manual_shots_rig.py -- OUT_DIR PACKAGE\\Boar.gltf

(a package made with "Skeleton and animations": wds models export Creature\\Boar\\Boar.m2 --with-skeleton). Opens Blender
with its window, registers the add-on from this folder, imports the package and saves the whole window:
  113_blender_wow_animations   the WoW panel (N): the animations, the playing one's WoW properties; the Action editor
                               with the walk's keys and its event markers ($FSD...)
  114_blender_wow_bone         pose mode, the head bone selected, Bone properties > WoW bone (key bone)
then quits. Writes OUT_DIR\\manual_shots_rig.json.
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
from wowdata_blender import rig as wrig  # noqa: E402

argv = sys.argv[sys.argv.index('--') + 1:]
OUT, GLTF = argv[0], argv[1]
os.makedirs(OUT, exist_ok=True)
log = {'ok': False, 'shots': []}


def area_of(kind):
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == kind:
                region = next(r for r in area.regions if r.type == 'WINDOW')
                return win, area, region, area.spaces.active
    return None, None, None, None


def shot(name):
    win, area, region, space = area_of('VIEW_3D')
    path = os.path.join(OUT, name + '.png')
    with bpy.context.temp_override(window=win, area=area, region=region):
        bpy.ops.screen.screenshot(filepath=path)
    log['shots'].append(path)


STEP = [0]


def run():
    try:
        s = STEP[0]
        STEP[0] += 1
        win, area, region, space = area_of('VIEW_3D')
        if s == 0:
            for o in list(bpy.data.objects):
                bpy.data.objects.remove(o, do_unlink=True)
            try:
                wowdata_blender.register()
            except ValueError:
                pass
            log['import'] = wowdata_blender.import_wow_gltf(bpy.context, GLTF)
            space.show_region_ui = True
            # the timeline becomes the Action editor (keys and markers of the playing animation)
            for a in win.screen.areas:
                if a.type == 'DOPESHEET_EDITOR' or a.type == 'TIMELINE':
                    a.type = 'DOPESHEET_EDITOR'
                    a.spaces.active.ui_mode = 'ACTION'
                    a.spaces.active.show_pose_markers = True      # the action's own markers (the events)
                    a.spaces.active.dopesheet.show_only_selected = False
                    log['dopesheet'] = True
                    break
            root, man = wrig.wow_rig_root(bpy.context)
            arm = wrig.armature_of(root, bpy.context)
            bpy.context.view_layer.objects.active = arm
            arm.select_set(True)
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.wowdata.play_action(action='Walk')
                bpy.ops.view3d.view_selected()
            r3d = space.region_3d
            r3d.view_rotation = Euler((math.radians(70), 0, math.radians(35)), 'XYZ').to_quaternion()
            r3d.view_distance = 9.0
            arm.data.display_type = 'STICK'
            # a taller Action editor, so the event markers show
            for a in win.screen.areas:
                if a.type == 'DOPESHEET_EDITOR':
                    try:
                        with bpy.context.temp_override(window=win, area=a):
                            bpy.ops.screen.area_move(x=a.x + a.width // 2, y=a.y + a.height, delta=220)
                    except (RuntimeError, TypeError) as e:
                        log['area_move'] = str(e)
            bpy.context.scene.frame_set(10)
            return 1.0
        if s == 1:
            for r in area.regions:
                if r.type == 'UI':
                    try:
                        r.active_panel_category = 'WoW'
                    except (AttributeError, TypeError) as e:
                        log['panel'] = str(e)
            for a in win.screen.areas:
                if a.type == 'DOPESHEET_EDITOR':
                    with bpy.context.temp_override(window=win, area=a, region=next(r for r in a.regions if r.type == 'WINDOW')):
                        try:
                            bpy.ops.action.view_all()
                        except RuntimeError as e:
                            log['view_all'] = str(e)
            area.tag_redraw()
            return 2.0
        if s == 2:
            shot('113_blender_wow_animations')
            root, man = wrig.wow_rig_root(bpy.context)
            arm = wrig.armature_of(root, bpy.context)
            with bpy.context.temp_override(window=win, area=area, region=region):
                bpy.ops.object.mode_set(mode='POSE')
            head = next((b for b in arm.data.bones if b.name.endswith('_Head')), None)
            if head is not None:
                for b in arm.data.bones:
                    b.select = b == head
                arm.data.bones.active = head
            for a in win.screen.areas:
                if a.type == 'PROPERTIES':
                    a.spaces.active.context = 'BONE'
            return 1.5
        if s == 3:
            shot('114_blender_wow_bone')
            log['ok'] = True
    except Exception:  # noqa: BLE001
        log['error'] = traceback.format_exc()
    with open(os.path.join(OUT, 'manual_shots_rig.json'), 'w', encoding='utf-8') as f:
        json.dump(log, f, indent=1)
    bpy.ops.wm.quit_blender()
    return None


bpy.app.timers.register(run, first_interval=1.0)
