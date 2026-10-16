"""Headless skeleton / animation edits for the 0.10.0 in-game visual test (WoWDataTool\\tests\\ingame), modelled on the app's own
blender_addon\\tests\\studio_rig_edit.py (the user's path: File > Import > WoW 1.12 model, edit, File > Export > WoW 1.12 model for
WoW Data Studio, which also writes wowdata_rig.json):

    blender -b --factory-startup --python ingame_rig_edit.py -- ADDON_ROOT REPORT.json IN.gltf OUT.gltf EDIT.json

EDIT.json (every part optional; applied in this order):
  "report_only": true      list bones (wow index, parent, key bone, vertices, mean / top of their vertices), actions, attachment
                           and event empties; nothing is exported
  "retime": [{"action": "Walk", "factor": 1.5}]
                           scale the action's keys (and handles) and its markers in time (the manual's "Retime a walk cycle")
  "spike": {"name": "bone_spike", "parent_wow_bone": 21, "height_factor": 2.0, "radius_factor": 0.15, "sides": 8}
                           a new bone on top of the parent bone's vertices (pointing up) and a new cone mesh there, joined into the
                           part (mesh object) that holds most of the parent's vertices and weighted 1.0 to the new bone
                           (the manual's "Add a bone and weight a new part to it")
  "new_action": {"from": "Stand", "name": "Stand [4]", "chance": 100,
                 "rotate": [{"bone_wow": 21, "axis": "X", "degrees": 35}], "spike_degrees": 45}
                           "New animation from this one" (a copy of the action) with extra rotation keys at its start / middle /
                           end; the spike bone (if any) swings in it; its WoW chance set (the WoW panel's "Chance (%)")
  "scale_keys": [{"actions": ["AttackUnarmed", "AttackUnarmed [2]"], "bone": "auto_stinger" | "<bone name>" | <wow index>,
                  "factor": 2.5}]
                           scale keys (1 -> factor -> 1 at start / middle / end) on one bone in these actions; "auto_stinger" =
                           the leaf bone whose vertices are highest (the scorpion's stinger)
  "move_attachment": {"id": 1, "world_offset": [0, 0, 0.6]}
                           move the attachment empty ATT_<id>_* by this offset (Blender world axes, Z up) at the rest pose
"""
import json
import math
import sys
import traceback

import bpy
from mathutils import Euler, Vector

argv = sys.argv[sys.argv.index('--') + 1:]
addon_root, report_path, src, dst, edit_path = argv[0], argv[1], argv[2], argv[3], argv[4]
sys.path.insert(0, addon_root)
import wowdata_blender  # noqa: E402
from wowdata_blender import rig as wrig  # noqa: E402

with open(edit_path, 'r', encoding='utf-8') as f:
    edit = json.load(f)
out = {'blender': bpy.app.version_string, 'ok': False, 'done': []}


def bone_by_wow(arm, idx):
    return next((b for b in arm.data.bones if b.get('wow_bone') == idx), None)


def action_named(name):
    for a in bpy.data.actions:
        if a.name == name or wrig.strip_suffix(a.name) == name:
            return a
    raise KeyError('no action named %s (actions: %s)' % (name, ', '.join(sorted(a.name for a in bpy.data.actions))))


def mesh_objects():
    return [o for o in bpy.data.objects if o.type == 'MESH']


def group_vertices(bone_name, min_weight=0.5):
    """(object, [world positions]) of the vertices weighted to this bone (rest pose: the mesh's own coordinates)."""
    res = []
    for o in mesh_objects():
        g = o.vertex_groups.get(bone_name)
        if g is None:
            continue
        pts = []
        for v in o.data.vertices:
            for vg in v.groups:
                if vg.group == g.index and vg.weight >= min_weight:
                    pts.append(o.matrix_world @ v.co)
                    break
        if pts:
            res.append((o, pts))
    return res


def key_rotation(arm, pb, act, frames_degrees, axis):
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = act
    pb.rotation_mode = 'QUATERNION'
    # keep the bone's own animation: the extra rotation is applied on top of what the action has at that frame
    for fr, deg in frames_degrees:
        bpy.context.scene.frame_set(int(round(fr)))
        base = pb.rotation_quaternion.copy()
        e = [0.0, 0.0, 0.0]
        e['XYZ'.index(axis)] = math.radians(deg)
        pb.rotation_quaternion = base @ Euler(e).to_quaternion()
        pb.keyframe_insert('rotation_quaternion', frame=fr)


def key_scale(arm, pb, act, frames_factors):
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = act
    for fr, s in frames_factors:
        bpy.context.scene.frame_set(int(round(fr)))
        pb.scale = (s, s, s)
        pb.keyframe_insert('scale', frame=fr)


try:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    try:
        wowdata_blender.register()
    except ValueError:
        pass
    out['import'] = wowdata_blender.import_wow_gltf(bpy.context, src)
    # the importer turns the model to face Blender's front view (a rotation on the root): world matrices are only current after an
    # update (without it matrix_world is the identity and a part made at a WORLD position lands 90 degrees off, found 2026-10-13)
    bpy.context.view_layer.update()
    root, man = wrig.wow_rig_root(bpy.context)
    arm = wrig.armature_of(root, bpy.context)
    if arm is None:
        raise RuntimeError('no armature: was the package exported with "Skeleton and animations (for editing)"?')
    scene = bpy.context.scene
    fps = wrig.fps_of(scene)
    out['fps'] = fps

    if edit.get('report_only'):
        bones = []
        children = {}
        for b in arm.data.bones:
            if b.parent:
                children.setdefault(b.parent.name, []).append(b.name)
        for b in arm.data.bones:
            gv = group_vertices(b.name, 0.01)
            pts = [p for _, lst in gv for p in lst]
            e = {'name': b.name, 'wow_bone': b.get('wow_bone'), 'parent': b.parent.name if b.parent else None,
                 'key_bone': b.get('wow_key_bone_id'), 'leaf': b.name not in children, 'vertices': len(pts),
                 'head': list(arm.matrix_world @ b.head_local)}
            if pts:
                e['mean'] = list(sum(pts, Vector()) / len(pts))
                e['top'] = max(p.z for p in pts)
            bones.append(e)
        out['bones'] = bones
        out['actions'] = [{'name': a.name, 'range': list(a.frame_range), 'seq': a.get('wow_sequence'), 'chance': a.get('wow_chance'),
                           'move_speed': a.get('wow_move_speed'), 'markers': [m.name for m in a.pose_markers]} for a in bpy.data.actions]
        out['empties'] = [{'name': o.name, 'parent_bone': o.parent_bone, 'world': list(o.matrix_world.translation)}
                          for o in bpy.data.objects if o.type == 'EMPTY']
        out['meshes'] = [{'name': o.name, 'submesh': o.get('wow_submesh'), 'vertices': len(o.data.vertices),
                          'uv': [u.name for u in o.data.uv_layers], 'materials': [m.name for m in o.data.materials if m]} for o in mesh_objects()]
        out['ok'] = True
    else:
        for e in edit.get('retime', []):
            act = action_named(e['action'])
            f = float(e['factor'])
            r0 = tuple(act.frame_range)
            for fc in act.fcurves:
                for k in fc.keyframe_points:
                    k.co.x *= f
                    k.handle_left.x *= f
                    k.handle_right.x *= f
                fc.update()
            for pm in act.pose_markers:
                pm.frame = int(round(pm.frame * f))
            out['done'].append('retime %s x%s: frames %s -> %s' % (act.name, f, r0, tuple(act.frame_range)))

        spike_bone = None
        if 'spike' in edit:
            e = edit['spike']
            parent = bone_by_wow(arm, e['parent_wow_bone'])
            if parent is None:
                raise RuntimeError('no bone with wow_bone %s' % e['parent_wow_bone'])
            # Bone references die with every mode switch (Blender rebuilds arm.data.bones): keep the NAME, not the Bone
            parent_name = parent.name
            gv = group_vertices(parent_name)
            if not gv:
                raise RuntimeError('bone %s has no vertices' % parent_name)
            target, _ = max(gv, key=lambda x: len(x[1]))
            target_name = target.name
            pts = [p for _, lst in gv for p in lst]
            zmin, zmax = min(p.z for p in pts), max(p.z for p in pts)
            hgt = max(zmax - zmin, 1e-3)
            # the middle of the part (x, y of the mean of its vertices) and the surface there (the highest of its vertices near
            # that middle): a spike on the forehead, not on the neck where the highest weighted vertices are
            cx = sum(p.x for p in pts) / len(pts)
            cy = sum(p.y for p in pts) / len(pts)
            ext = max(max(p.x for p in pts) - min(p.x for p in pts), max(p.y for p in pts) - min(p.y for p in pts))
            near = [p for p in pts if math.hypot(p.x - cx, p.y - cy) < 0.25 * ext] or pts
            surface = max(p.z for p in near)
            depth = float(e.get('height_factor', 2.0)) * hgt
            radius = float(e.get('radius_factor', 0.15)) * depth
            base = Vector((cx, cy, surface - 0.1 * depth))     # sunk a little into the head
            # the bone (armature space), child of the parent bone
            bpy.context.view_layer.objects.active = arm
            bpy.ops.object.mode_set(mode='EDIT')
            inv = arm.matrix_world.inverted()
            eb = arm.data.edit_bones.new(e['name'])
            eb.head = inv @ base
            eb.tail = inv @ (base + Vector((0.0, 0.0, depth)))
            eb.parent = arm.data.edit_bones[parent_name]
            bpy.ops.object.mode_set(mode='OBJECT')
            spike_bone = arm.data.bones[e['name']]
            # the cone (world space), joined into the part with most of the parent's vertices
            bpy.ops.object.select_all(action='DESELECT')
            bpy.ops.mesh.primitive_cone_add(vertices=int(e.get('sides', 8)), radius1=radius, radius2=0.0, depth=depth,
                                            location=base + Vector((0.0, 0.0, depth / 2.0)))
            cone = bpy.context.active_object
            cone.name = 'spike_part'
            n_new = len(cone.data.vertices)
            if target.data.uv_layers.active is not None and cone.data.uv_layers.active is not None:
                cone.data.uv_layers.active.name = target.data.uv_layers.active.name
            vg = cone.vertex_groups.new(name=e['name'])
            vg.add([v.index for v in cone.data.vertices], 1.0, 'REPLACE')
            before = len(target.data.vertices)
            bpy.ops.object.select_all(action='DESELECT')
            cone.select_set(True)
            target.select_set(True)
            bpy.context.view_layer.objects.active = target
            bpy.ops.object.join()
            out['spike'] = {'part': target_name, 'submesh': target.get('wow_submesh'), 'new_vertices': len(target.data.vertices) - before,
                            'cone_vertices': n_new, 'depth': depth, 'radius': radius, 'base': list(base), 'parent': parent_name}
            out['done'].append('spike: bone %s under %s, %d new vertices in %s' % (e['name'], parent_name, len(target.data.vertices) - before, target_name))

        if 'new_action' in edit:
            e = edit['new_action']
            src_act = action_named(e['from'])
            new = src_act.copy()                      # what the WoW panel's "New animation from this one" does
            new.name = e['name']
            new.use_fake_user = True
            if 'chance' in e:
                new['wow_chance'] = float(e['chance'])
            f0, f1 = new.frame_range
            mid = (f0 + f1) / 2.0
            for r in e.get('rotate', []):
                b = bone_by_wow(arm, r['bone_wow'])
                key_rotation(arm, arm.pose.bones[b.name], new, [(f0, 0.0), (mid, float(r['degrees'])), (f1, 0.0)], r.get('axis', 'X'))
            if spike_bone is not None and e.get('spike_degrees'):
                d = float(e['spike_degrees'])
                q1, q3 = f0 + (f1 - f0) * 0.25, f0 + (f1 - f0) * 0.75
                key_rotation(arm, arm.pose.bones[spike_bone.name], new, [(f0, 0.0), (q1, d), (mid, 0.0), (q3, -d), (f1, 0.0)], 'X')
            out['done'].append('new_action %s from %s (frames %s, chance %s)' % (new.name, src_act.name, tuple(new.frame_range), new.get('wow_chance')))

        for e in edit.get('scale_keys', []):
            bone = e['bone']
            if bone == 'auto_stinger':
                parents = {b.parent.name for b in arm.data.bones if b.parent}
                best = None
                for b in arm.data.bones:
                    if b.name in parents:
                        continue
                    gv = group_vertices(b.name)
                    pts = [p for _, lst in gv for p in lst]
                    if len(pts) < 8:
                        continue
                    mz = sum(p.z for p in pts) / len(pts)
                    if best is None or mz > best[1]:
                        best = (b, mz)
                bname = best[0].name
            elif isinstance(bone, int):
                bname = bone_by_wow(arm, bone).name
            else:
                bname = bone
            pb = arm.pose.bones[bname]
            for an in e['actions']:
                act = action_named(an)
                f0, f1 = act.frame_range
                fac = float(e['factor'])
                key_scale(arm, pb, act, [(f0, 1.0), ((f0 + f1) / 2.0, fac), (f1, 1.0)])
                out['done'].append('scale_keys %s on %s: 1 -> %s -> 1' % (act.name, bname, fac))
            out['scaled_bone'] = bname

        if 'move_attachment' in edit:
            e = edit['move_attachment']
            o = next((x for x in bpy.data.objects if x.type == 'EMPTY' and x.get('wow_attachment_id') == e['id']), None)
            if o is None:
                o = next(x for x in bpy.data.objects if x.type == 'EMPTY' and x.name.startswith('ATT_%02d_' % e['id']))
            pose_position = arm.data.pose_position
            arm.data.pose_position = 'REST'
            bpy.context.view_layer.update()
            w0 = o.matrix_world.translation.copy()
            mw = o.matrix_world.copy()
            mw.translation = mw.translation + Vector(e['world_offset'])
            o.matrix_world = mw
            bpy.context.view_layer.update()
            w1 = o.matrix_world.translation.copy()
            arm.data.pose_position = pose_position
            bpy.context.view_layer.update()
            out['attachment'] = {'name': o.name, 'parent_bone': o.parent_bone, 'world_before': list(w0), 'world_after': list(w1)}
            out['done'].append('move_attachment %s by %s (world, rest pose)' % (o.name, e['world_offset']))

        # play Stand again (as the add-on leaves it) before exporting
        stand = next((a for a in bpy.data.actions if a.get('wow_anim_id') == 0 and a.get('wow_variation', 0) == 0 and a.name == wrig.strip_suffix(a.name)), None)
        if stand is not None and arm.animation_data is not None:
            arm.animation_data.action = stand
        out['export'] = wowdata_blender.export_for_studio(bpy.context, dst)
        out['ok'] = True
except Exception:  # noqa: BLE001
    out['error'] = traceback.format_exc()
with open(report_path, 'w', encoding='utf-8') as f:
    json.dump(out, f, indent=1, default=str)
