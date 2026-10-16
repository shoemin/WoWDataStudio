"""Skeleton and animation editing for WoW Data Studio (add-on 0.5.0).

Import (after Blender's glTF importer, when the package manifest has a "rig"):
  * every action is tagged with its WoW sequence (custom properties: wow_sequence, the animation, loop, chance, blend
    time, move speed, repeats) and remembers a fingerprint of its keys, so an action you do not touch goes back
    unchanged, key for key;
  * the importer's NLA tracks are removed (every action keeps a fake user) and "Stand" is made the active action;
  * animation events become pose markers of their action ($FSD, $CSL...); their exact times are remembered;
  * the global sequences are actions of their own ("Global sequence 1 (2.00 s)").

Export (File > Export > WoW 1.12 model for WoW Data Studio) writes wowdata_rig.json next to the glTF: the bones (with
their original numbers), every action (unchanged ones only by name, edited or new ones sampled as the bone's local
animation matrix), the markers and the attachment / event empties, all in the model's own (M2) coordinates.

Panels (View3D > Sidebar > WoW): play an animation, its WoW properties, "New animation from this one", "Check for WoW";
Bone properties > WoW bone: key bone; Object properties > WoW attachment point / event.
"""
import hashlib
import json
import math
import re

import bpy
from bpy.props import BoolProperty, EnumProperty, FloatProperty, IntProperty, StringProperty
from mathutils import Matrix, Quaternion, Vector

RIG_FILE = 'wowdata_rig.json'
SUFFIX = re.compile(r'(\.\d{3})+$')
VARIATION = re.compile(r'\s*(\[\d+\]|#\d+)\s*$')
CHANNELS = ('translation', 'rotation', 'scale')


# ---------------------------------------------------------------------- helpers

def base_name(action_name):
    s = SUFFIX.sub('', action_name.strip())
    for _ in range(3):
        s = VARIATION.sub('', s)
    return s.strip()


def manifest_of(obj):
    t = obj.get('wow_manifest') if obj is not None else None
    if isinstance(t, str) and t.startswith('{'):
        try:
            return json.loads(t)
        except ValueError:
            return None
    return None


def wow_rig_root(context):
    for o in context.scene.objects:
        m = manifest_of(o)
        if m is not None and m.get('rig') is not None:
            return o, m
    return None, None


def armature_of(root, context):
    if root is not None and root.type == 'ARMATURE':
        return root
    for o in context.scene.objects:
        if o.type == 'ARMATURE':
            return o
    return None


def bone_of_path(path):
    m = re.match(r'pose\.bones\["(.+?)"\]\.(\w+)', path)
    return (m.group(1), m.group(2)) if m else (None, None)


def action_fingerprint(action):
    """A fingerprint of an action's keys (paths, keyframes, handles, interpolation, modifiers, frame range)."""
    h = hashlib.sha1()
    for fc in sorted(action.fcurves, key=lambda f: (f.data_path, f.array_index)):
        h.update(('%s|%d|%d|' % (fc.data_path, fc.array_index, len(fc.modifiers))).encode())
        for k in fc.keyframe_points:
            h.update(('%.6f,%.9g,%s,%.6f,%.9g,%.6f,%.9g;' % (k.co[0], k.co[1], k.interpolation,
                                                           k.handle_left[0], k.handle_left[1],
                                                           k.handle_right[0], k.handle_right[1])).encode())
    fr = action.frame_range
    h.update(('range %.6f %.6f %s' % (fr[0], fr[1], action.use_frame_range)).encode())
    return h.hexdigest()


def fps_of(scene):
    return scene.render.fps / (scene.render.fps_base or 1.0)


def strip_suffix(name):
    return SUFFIX.sub('', name)


# ---------------------------------------------------------------------- import

def setup_rig(context, objects, actions, manifest):
    """Tag the imported actions, markers, bones and empties of a package with a rig. Returns a small report."""
    rig = manifest.get('rig') if manifest else None
    if not rig:
        return {'rig': False}
    scene = context.scene
    fps = fps_of(scene)
    arm = next((o for o in objects if o.type == 'ARMATURE'), None)
    by_action = {s['action']: s for s in rig.get('sequences', [])}
    globals_by_action = {g['action']: g for g in rig.get('global_sequences', [])}
    tagged = 0
    for act in actions:
        s = by_action.get(act.name) or by_action.get(strip_suffix(act.name))
        if s is not None:
            act['wow_sequence'] = int(s['index'])
            act['wow_anim_id'] = int(s['anim_id'])
            act['wow_anim_name'] = s.get('anim_name', '')
            act['wow_variation'] = int(s.get('variation', 0))
            act['wow_loop'] = bool(s.get('loop', True))
            act['wow_chance'] = float(s.get('chance', 100.0))
            act['wow_blend_ms'] = int(s.get('blend_ms', 150))
            act['wow_move_speed'] = float(s.get('move_speed', 0.0))
            act['wow_repeat_min'] = int(s.get('repeat_min', 0))
            act['wow_repeat_max'] = int(s.get('repeat_max', 0))
            act['wow_duration_ms'] = int(s.get('duration_ms', 0))
            act['wow_flags'] = int(s.get('flags', 0))
            tagged += 1
        g = globals_by_action.get(act.name) or globals_by_action.get(strip_suffix(act.name))
        if g is not None:
            act['wow_global_sequence'] = int(g['index'])
            act['wow_duration_ms'] = int(g['duration_ms'])
        act.use_fake_user = True
    # events -> pose markers (their exact times are kept on the action)
    seq_action = {}
    for act in actions:
        if 'wow_sequence' in act:
            seq_action.setdefault(int(act['wow_sequence']), act)
    markers = 0
    exact = {}
    for e in rig.get('events', []):
        for seq, ms in e.get('triggers', []):
            act = seq_action.get(int(seq))
            if act is None:
                continue
            frame = int(round(ms / 1000.0 * fps))
            pm = act.pose_markers.new(e['marker'])
            pm.frame = frame
            exact.setdefault(act.name, []).append([e['marker'], int(ms), frame])
            markers += 1
    for name, lst in exact.items():
        bpy.data.actions[name]['wow_markers'] = json.dumps(lst)
    # fingerprints of the keys as imported
    for act in actions:
        act['wow_import_hash'] = action_fingerprint(act)
    # the importer puts every action on an NLA track: keep the actions, drop the tracks, play Stand
    if arm is not None and arm.animation_data is not None:
        ad = arm.animation_data
        for t in list(ad.nla_tracks):
            ad.nla_tracks.remove(t)
        stand = next((a for a in actions if a.get('wow_anim_id') == 0 and a.get('wow_variation', 0) == 0), None)
        first = stand or next((a for a in actions if 'wow_sequence' in a), None)
        if first is not None:
            ad.action = first
            fr = first.frame_range
            scene.frame_start, scene.frame_end = int(math.floor(fr[0])), int(math.ceil(fr[1]))
    if arm is not None:
        arm['wow_fps'] = fps
    return {'rig': True, 'actions_tagged': tagged, 'markers': markers}


# ---------------------------------------------------------------------- export

def _space(root, arm):
    """Armature space -> M2 space (the root's import rotation / scale undone)."""
    rw = root.matrix_world if root is not None else Matrix.Identity(4)
    return rw.inverted() @ arm.matrix_world


def _basis(pb, fcs, frame):
    """The pose bone's basis matrix (T R S) from its fcurves at a frame; channels without curves keep the rest value."""
    loc = Vector((0.0, 0.0, 0.0))
    scale = Vector((1.0, 1.0, 1.0))
    quat = Quaternion((1.0, 0.0, 0.0, 0.0))
    euler = [0.0, 0.0, 0.0]
    axis_angle = [0.0, 0.0, 1.0, 0.0]
    for (prop, idx), fc in fcs.items():
        v = fc.evaluate(frame)
        if prop == 'location':
            loc[idx] = v
        elif prop == 'scale':
            scale[idx] = v
        elif prop == 'rotation_quaternion':
            quat[idx] = v
        elif prop == 'rotation_euler':
            euler[idx] = v
        elif prop == 'rotation_axis_angle':
            axis_angle[idx] = v
    mode = pb.rotation_mode
    if mode == 'QUATERNION':
        rot = quat.normalized().to_matrix().to_4x4() if quat.magnitude > 1e-12 else Matrix.Identity(4)
    elif mode == 'AXIS_ANGLE':
        rot = Matrix.Rotation(axis_angle[0], 4, Vector(axis_angle[1:4]))
    else:
        from mathutils import Euler
        rot = Euler(euler, mode).to_matrix().to_4x4()
    return Matrix.Translation(loc) @ rot @ Matrix.Diagonal((scale[0], scale[1], scale[2], 1.0))


def _flat(m):
    return [m[r][c] for r in range(4) for c in range(4)]


def _sample_times(fcurves_of_bone, frame_start, frame_end):
    frames = {frame_start, frame_end}
    smooth = False
    for fc in fcurves_of_bone:
        kps = fc.keyframe_points
        for i, k in enumerate(kps):
            if frame_start <= k.co[0] <= frame_end:
                frames.add(k.co[0])
            if i + 1 < len(kps) and k.interpolation != 'LINEAR':
                smooth = True
        if len(fc.modifiers) > 0:
            smooth = True
    if smooth:
        f = math.floor(frame_start)
        while f <= frame_end:
            if f >= frame_start:
                frames.add(float(f))
            f += 1
    return sorted(frames)


def _uses_constraints(arm):
    return any(c.enabled for pb in arm.pose.bones for c in pb.constraints) or (
        arm.animation_data is not None and len(arm.animation_data.drivers) > 0)


def export_rig(context, root, manifest, out_path):
    """Write wowdata_rig.json for the scene's armature."""
    scene = context.scene
    arm = armature_of(root, context)
    rig = manifest.get('rig') or {}
    if arm is None:
        info = {'format': 'wowdata-rig', 'format_version': 1, 'bones': [], 'actions': [], 'globals': [], 'points': [],
                'notes': ['The scene has no armature.']}
        with open(out_path, 'w', encoding='utf-8') as f:
            json.dump(info, f)
        return {'bones': 0, 'actions': 0, 'sampled': 0}
    fps = fps_of(scene)
    C = _space(root, arm)
    Ci = C.inverted()
    bones = []
    rest_changed = set()
    for b in arm.data.bones:
        head = C @ b.head_local
        e = {'name': b.name, 'parent': b.parent.name if b.parent else None, 'head': [head.x, head.y, head.z]}
        if 'wow_bone' in b:
            e['wow_bone'] = int(b['wow_bone'])
        if 'wow_key_bone_id' in b:
            e['key_bone_id'] = int(b['wow_key_bone_id'])
        if 'wow_flags' in b:
            e['flags'] = int(b['wow_flags'])
        piv = b.get('wow_pivot')
        if piv is not None and (Vector(tuple(piv)) - head).length > 1e-4:
            rest_changed.add(b.name)
        bones.append(e)
    gs_channels = {}
    for b in arm.data.bones:
        g = b.get('wow_global_channels')
        if g is not None:
            try:
                gs_channels[b.name] = {k: int(v) for k, v in g.items()}
            except AttributeError:
                pass
    # actions that animate this armature
    names = {b.name for b in arm.data.bones}
    actions = []
    for act in bpy.data.actions:
        paths = [bone_of_path(fc.data_path)[0] for fc in act.fcurves]
        if 'wow_sequence' in act or 'wow_global_sequence' in act or any(p in names for p in paths):
            actions.append(act)
    constraints = _uses_constraints(arm)
    out_actions, out_globals = [], []
    sampled = 0
    saved_action = arm.animation_data.action if arm.animation_data else None
    saved_frame = scene.frame_current
    for act in actions:
        fr = act.frame_range
        f0, f1 = float(fr[0]), float(fr[1])
        dur = (f1 - f0) / fps * 1000.0
        is_global = 'wow_global_sequence' in act
        props = {}
        for k in ('wow_anim_id', 'wow_loop', 'wow_chance', 'wow_blend_ms', 'wow_move_speed', 'wow_repeat_min', 'wow_repeat_max',
                  'wow_flags', 'wow_next_alias'):
            if k in act:
                v = act[k]
                props[k[4:]] = bool(v) if k == 'wow_loop' else v
        # keys per bone
        per_bone = {}
        for fc in act.fcurves:
            bn, prop = bone_of_path(fc.data_path)
            if bn in names:
                per_bone.setdefault(bn, {})[(prop, fc.array_index)] = fc
        touched_rest = any(bn in rest_changed for bn in per_bone)
        unchanged = act.get('wow_import_hash') == action_fingerprint(act) and not touched_rest
        e = {'name': act.name, 'unchanged': unchanged, 'duration_ms': dur, 'props': props}
        if 'wow_sequence' in act:
            e['wow_sequence'] = int(act['wow_sequence'])
        if is_global:
            e['global_sequence'] = int(act['wow_global_sequence'])
        # markers with their exact times
        exact = {}
        try:
            for name, ms, frame in json.loads(act.get('wow_markers', '[]')):
                exact.setdefault((name, int(frame)), []).append(int(ms))
        except (ValueError, TypeError):
            pass
        markers = []
        for pm in act.pose_markers:
            lst = exact.get((pm.name, int(pm.frame)))
            ms = lst.pop(0) if lst else (pm.frame - f0) / fps * 1000.0
            markers.append({'name': pm.name, 'time_ms': ms})
        e['markers'] = markers
        if not unchanged:
            sampled += 1
            bones_out = {}
            if constraints:
                if arm.animation_data is None:
                    arm.animation_data_create()
                arm.animation_data.action = act
            for bn, fcs in per_bone.items():
                pb = arm.pose.bones.get(bn)
                if pb is None:
                    continue
                gch = gs_channels.get(bn, {})
                if is_global:
                    # a global sequence's action: only the channels that run on it
                    skip = {ch for ch in CHANNELS if gch.get(ch, -1) != int(act['wow_global_sequence'])}
                else:
                    # a sequence's action: the channels that run on a global sequence are edited in that one's action
                    skip = set(gch.keys())
                chans = set()
                for (prop, _i) in fcs:
                    ch = {'location': 'translation', 'scale': 'scale'}.get(prop, 'rotation')
                    if ch not in skip:
                        chans.add(ch)
                if not chans:
                    continue
                times = _sample_times(fcs.values(), f0, f1)
                rest = pb.bone.matrix_local
                rest_i = rest.inverted_safe()
                mats = []
                for fr_ in times:
                    if constraints:
                        scene.frame_set(int(math.floor(fr_)), subframe=fr_ - math.floor(fr_))
                        s = pb.matrix @ rest_i
                        if pb.parent is not None:
                            sp = pb.parent.matrix @ pb.parent.bone.matrix_local.inverted()
                            L = sp.inverted_safe() @ s     # a parent scaled to 0 has no exact inverse
                        else:
                            L = s
                    else:
                        L = rest @ _basis(pb, fcs, fr_) @ rest_i
                    mats.append(_flat(C @ L @ Ci))
                bones_out[bn] = {'times_ms': [(t - f0) / fps * 1000.0 for t in times], 'matrices': mats, 'channels': sorted(chans)}
            e['bones'] = bones_out
        (out_globals if is_global else out_actions).append(e)
    if arm.animation_data is not None:
        arm.animation_data.action = saved_action
    scene.frame_set(saved_frame)
    # attachment and event empties, at the rest pose
    points = []
    pose_position = arm.data.pose_position
    arm.data.pose_position = 'REST'
    context.view_layer.update()
    rw_inv = (root.matrix_world if root is not None else Matrix.Identity(4)).inverted()
    for o in context.scene.objects:
        if o.type != 'EMPTY':
            continue
        is_att = 'wow_attachment' in o or 'wow_attachment_id' in o or o.name.startswith('ATT_')
        is_evt = 'wow_event' in o or 'wow_event_code' in o or o.name.startswith('EVT_')
        if not (is_att or is_evt):
            continue
        p = rw_inv @ o.matrix_world.translation
        e = {'name': o.name, 'kind': 'attachment' if is_att else 'event', 'position': [p.x, p.y, p.z],
             'bone': o.parent_bone if o.parent is not None and o.parent_type == 'BONE' and o.parent_bone else None}
        if is_att:
            if 'wow_attachment' in o:
                e['wow_index'] = int(o['wow_attachment'])
            m = re.match(r'ATT_(\d+)', o.name)
            if 'wow_attachment_id' in o:
                e['id'] = int(o['wow_attachment_id'])
            elif m:
                e['id'] = int(m.group(1))
        else:
            if 'wow_event' in o:
                e['wow_index'] = int(o['wow_event'])
            m = re.match(r'EVT_\d+_(.{1,4})', o.name)
            e['code'] = o.get('wow_event_code') or (m.group(1) if m else None)
        points.append(e)
    arm.data.pose_position = pose_position
    context.view_layer.update()
    from . import ADDON_VERSION
    info = {'format': 'wowdata-rig', 'format_version': 1, 'addon': ADDON_VERSION, 'blender': bpy.app.version_string, 'fps': fps,
            'space': 'M2 coordinates; matrices row by row in column-vector form (M @ v)',
            'bones': bones, 'actions': out_actions, 'globals': out_globals, 'points': points}
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(info, f, separators=(',', ':'))
    return {'bones': len(bones), 'actions': len(out_actions), 'globals': len(out_globals), 'sampled': sampled, 'points': len(points),
            'constraints': constraints}


# ---------------------------------------------------------------------- UI

class WOWDATA_OT_play_action(bpy.types.Operator):
    """Play this animation: make it the armature's action and set the scene's frame range to it"""
    bl_idname = 'wowdata.play_action'
    bl_label = 'Play'
    action: StringProperty()

    def execute(self, context):
        root, _ = wow_rig_root(context)
        arm = armature_of(root, context)
        act = bpy.data.actions.get(self.action)
        if arm is None or act is None:
            return {'CANCELLED'}
        if arm.animation_data is None:
            arm.animation_data_create()
        arm.animation_data.action = act
        fr = act.frame_range
        context.scene.frame_start, context.scene.frame_end = int(math.floor(fr[0])), int(math.ceil(fr[1]))
        context.scene.frame_set(context.scene.frame_start)
        return {'FINISHED'}


class WOWDATA_OT_new_animation(bpy.types.Operator):
    """Copy the current animation into a new one (a new variation, or rename it to another animation)"""
    bl_idname = 'wowdata.new_animation'
    bl_label = 'New animation from this one'
    name: StringProperty(name='Name', description='An animation name from the game (Stand, EmoteDance...), "[2]" etc. for a variation')

    def invoke(self, context, event):
        root, _ = wow_rig_root(context)
        arm = armature_of(root, context)
        act = arm.animation_data.action if arm and arm.animation_data else None
        if act is not None:
            base = base_name(act.name)
            n = 2
            while bpy.data.actions.get('%s [%d]' % (base, n)) is not None:
                n += 1
            self.name = '%s [%d]' % (base, n)
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        root, _ = wow_rig_root(context)
        arm = armature_of(root, context)
        act = arm.animation_data.action if arm and arm.animation_data else None
        if act is None:
            self.report({'ERROR'}, 'Play an animation first (WoW panel).')
            return {'CANCELLED'}
        new = act.copy()
        new.name = self.name
        new.use_fake_user = True
        arm.animation_data.action = new
        self.report({'INFO'}, 'New animation %s (a copy of %s). Change its keys, then export.' % (new.name, act.name))
        return {'FINISHED'}


class WOWDATA_OT_check(bpy.types.Operator):
    """Check the armature, animations and markers against the WoW 1.12 limits (WoW Data Studio checks again on import)"""
    bl_idname = 'wowdata.check_rig'
    bl_label = 'Check for WoW'

    def execute(self, context):
        root, man = wow_rig_root(context)
        arm = armature_of(root, context)
        problems = []
        if arm is None:
            problems.append('No armature.')
        else:
            n = len(arm.data.bones)
            if n > 256:
                problems.append('%d bones: vertices can only use the first 256.' % n)
            names = set((man or {}).get('rig', {}).get('animation_names', {}).values())
            for act in bpy.data.actions:
                if 'wow_global_sequence' in act:
                    continue
                if names and base_name(act.name) not in names and 'wow_anim_id' not in act:
                    problems.append('Action "%s" is not named after a game animation.' % act.name)
                for pm in act.pose_markers:
                    if not pm.name.startswith('$'):
                        problems.append('Marker "%s" in %s is not an event ($FSD, $CSL...).' % (pm.name, act.name))
            for o in context.scene.objects:
                if o.type == 'MESH' and any(len([g for g in v.groups if g.weight > 0]) > 4 for v in o.data.vertices):
                    problems.append('%s has vertices with more than 4 bone weights (the strongest 4 are kept).' % o.name)
        if problems:
            for p in problems[:12]:
                self.report({'WARNING'}, p)
            return {'FINISHED'}
        self.report({'INFO'}, 'No problems found. WoW Data Studio checks everything again when you import.')
        return {'FINISHED'}


class WOWDATA_PT_animations(bpy.types.Panel):
    bl_label = 'WoW animations'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'WoW'

    @classmethod
    def poll(cls, context):
        return wow_rig_root(context)[0] is not None

    def draw(self, context):
        root, man = wow_rig_root(context)
        arm = armature_of(root, context)
        lay = self.layout
        act = arm.animation_data.action if arm and arm.animation_data else None
        lay.label(text='Playing: %s' % (act.name if act else 'nothing'))
        if act is not None:
            box = lay.box()
            box.label(text='WoW properties of this animation')
            for k, label in (('wow_loop', 'Loops'), ('wow_chance', 'Chance (%)'), ('wow_blend_ms', 'Blend time (ms)'),
                             ('wow_move_speed', 'Move speed (yd/s)'), ('wow_repeat_min', 'Repeats (min)'),
                             ('wow_repeat_max', 'Repeats (max)')):
                if k in act:
                    box.prop(act, '["%s"]' % k, text=label)
            box.label(text='The action\'s name decides the animation (Stand, Walk, "Stand [2]"...).')
            lay.operator(WOWDATA_OT_new_animation.bl_idname, icon='DUPLICATE')
        lay.operator(WOWDATA_OT_check.bl_idname, icon='CHECKMARK')
        col = lay.column(align=True)
        for a in sorted(bpy.data.actions, key=lambda x: (x.get('wow_sequence', 10000), x.name)):
            if 'wow_sequence' not in a and 'wow_global_sequence' not in a and not (arm and any(
                    bone_of_path(fc.data_path)[0] in arm.data.bones for fc in a.fcurves)):
                continue
            row = col.row(align=True)
            op = row.operator(WOWDATA_OT_play_action.bl_idname, text=a.name, depress=(a == act))
            op.action = a.name


class WOWDATA_PT_bone(bpy.types.Panel):
    bl_label = 'WoW bone'
    bl_space_type = 'PROPERTIES'
    bl_region_type = 'WINDOW'
    bl_context = 'bone'

    @classmethod
    def poll(cls, context):
        return context.bone is not None or context.edit_bone is not None

    def draw(self, context):
        b = context.bone or context.edit_bone
        lay = self.layout
        lay.label(text='Original bone: %s' % (b.get('wow_bone', 'new bone')))
        lay.prop(context.window_manager, 'wowdata_key_bone', text='Key bone')
        lay.label(text='The game finds key bones (Head, hands...) by number.')


def _get_key(self):
    b = bpy.context.bone or bpy.context.edit_bone
    return int(b.get('wow_key_bone_id', -1)) + 1 if b is not None else 0


def _set_key(self, value):
    b = bpy.context.bone or bpy.context.edit_bone
    if b is not None:
        b['wow_key_bone_id'] = int(value) - 1


def _key_enum_items(self, context):
    root, man = wow_rig_root(context)
    keys = (man or {}).get('rig', {}).get('key_bones', []) if man else []
    return [('NONE', 'None', 'Not a key bone', 0)] + [('K%d' % i, '%s (%d)' % (n, i), '', i + 1) for i, n in enumerate(keys)]


class WOWDATA_PT_point(bpy.types.Panel):
    bl_label = 'WoW attachment point / event'
    bl_space_type = 'PROPERTIES'
    bl_region_type = 'WINDOW'
    bl_context = 'object'

    @classmethod
    def poll(cls, context):
        o = context.object
        return o is not None and o.type == 'EMPTY' and (o.name.startswith(('ATT_', 'EVT_')) or 'wow_attachment_id' in o or 'wow_event_code' in o)

    def draw(self, context):
        o = context.object
        lay = self.layout
        if 'wow_attachment_id' in o:
            lay.prop(o, '["wow_attachment_id"]', text='Attachment id')
        if 'wow_event_code' in o:
            lay.prop(o, '["wow_event_code"]', text='Event')
        lay.label(text='Move it in edit or object mode at the rest pose; parent it to a bone to change its bone.')


CLASSES = (WOWDATA_OT_play_action, WOWDATA_OT_new_animation, WOWDATA_OT_check, WOWDATA_PT_animations, WOWDATA_PT_bone, WOWDATA_PT_point)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.WindowManager.wowdata_key_bone = EnumProperty(name='Key bone', items=_key_enum_items, get=_get_key, set=_set_key)


def unregister():
    del bpy.types.WindowManager.wowdata_key_bone
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)
