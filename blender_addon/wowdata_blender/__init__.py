"""WoW 1.12 models in Blender: imports the glTF written by WoW Data Studio ("Export for Blender") or by
`wowdata model export`, fixes it up, and exports it back for WoW Data Studio's "Import edited model...".

Install: Edit > Preferences > Add-ons > (drop-down) Install from Disk... > choose wowdata_blender.zip
(WoW Data Studio: Tools > Install the Blender add-on..., or `python blender_addon\\build_zip.py`), then tick
"Import-Export: WoW 1.12 model (wowdata)".

What the import does on top of Blender's own glTF importer:
  * materials: blend mode per the M2 render flags (opaque / alpha key / alpha / additive / modulate), back-face
    culling unless the render flags say two-sided;
  * hides the geosets the game hides by default (characters: one hairstyle, no armour pieces) and the collision mesh;
  * orientation and scale on the top-level object only (the mesh and bone data keep M2 coordinates);
  * tags every part with its id as a custom property, so matching survives renames when you export back:
    meshes (wow_submesh, wow_mesh_id), bones (wow_bone), materials (wow_material_id, wow_submesh, wow_texture_id)
    and images (wow_texture_id, wow_game_path);
  * keeps the package manifest (wowdata_manifest.json, also embedded in the glTF) or the command line's sidecar
    (<Name>.wowm2.json) as custom properties on the armature ("wow_manifest" / "wow_sidecar").

Export back (File > Export > WoW 1.12 model for WoW Data Studio): the scene as glTF with every custom property as
glTF extras, the skins as KHR_materials_variants, the textures as PNG, plus wowdata_manifest.json and
wowdata_blender.json (image file -> game texture). Then "Import edited model..." in WoW Data Studio.

Skeleton and animations (0.5.0, rig.py): with a package exported with "Skeleton and animations", every action is
tagged with its WoW animation, events become pose markers, the WoW panel (View3D > Sidebar > WoW) plays animations and
edits their properties, and the export also writes wowdata_rig.json (bones, edited or new actions sampled, markers,
attachment and event empties) for "Import edited model...".

Terrain (0.4.0, terrain.py): File > Import > WoW 1.12 terrain imports the map tiles of WoW Data Studio's "Open in
Blender" (one object per tile, edge vertex groups, the ground textures); the WoW Terrain panel (N) has "Sync shared
edges", "Check edge seams" and "Export terrain for WoW Data Studio".
"""
bl_info = {
    'name': 'WoW 1.12 model (wowdata)',
    'author': 'WoWDataTool',
    'version': (0, 5, 0),
    'blender': (4, 2, 0),
    'location': 'File > Import > WoW 1.12 model / terrain; File > Export > WoW 1.12 model / terrain for WoW Data Studio; View3D > Sidebar > WoW / WoW Terrain',
    'description': 'Import WoW 1.12 models and terrain exported by WoW Data Studio or wowdata and export them back for WoW Data Studio',
    'category': 'Import-Export',
}

import json
import math
import os
import subprocess

import bpy
from bpy.props import BoolProperty, EnumProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from . import terrain  # noqa: E402  (WoW terrain: import, edge sync, seam check, export)
from . import rig  # noqa: E402  (0.5.0: skeleton and animations)

SIDECAR_SUFFIX = '.wowm2.json'
MANIFEST_NAME = 'wowdata_manifest.json'
BLENDER_MAP_NAME = 'wowdata_blender.json'
ADDON_VERSION = '.'.join(str(x) for x in bl_info['version'])


# ---------------------------------------------------------------------- helpers

def sidecar_path(gltf_path):
    base = os.path.splitext(gltf_path)[0]
    return base + SIDECAR_SUFFIX


def load_sidecar(gltf_path):
    p = sidecar_path(gltf_path)
    if not os.path.exists(p):
        return None, None
    with open(p, 'r', encoding='utf-8') as f:
        text = f.read()
    return json.loads(text), text


def load_manifest(gltf_path, objects=()):
    """The package manifest: embedded in the root's extras (custom property wow_manifest) or next to the file."""
    for o in objects:
        t = o.get('wow_manifest')
        if isinstance(t, str) and t.startswith('{'):
            try:
                return json.loads(t), t
            except ValueError:
                pass
    d = os.path.dirname(os.path.abspath(gltf_path))
    for cand in (os.path.join(d, MANIFEST_NAME), os.path.join(os.path.dirname(d), MANIFEST_NAME)):
        if os.path.exists(cand):
            with open(cand, 'r', encoding='utf-8') as f:
                text = f.read()
            try:
                return json.loads(text), text
            except ValueError:
                pass
    return None, None


def _principled(mat):
    for n in mat.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            return n
    return None


def _output(mat):
    for n in mat.node_tree.nodes:
        if n.type == 'OUTPUT_MATERIAL' and n.is_active_output:
            return n
    for n in mat.node_tree.nodes:
        if n.type == 'OUTPUT_MATERIAL':
            return n
    return None


def _base_color_source(mat):
    """The socket feeding the Principled base colour (the image texture), or None."""
    p = _principled(mat)
    if p is None or not p.inputs['Base Color'].is_linked:
        return None
    return p.inputs['Base Color'].links[0].from_socket


def _base_image(mat):
    """The image of the image-texture node behind the base colour (directly or through other nodes)."""
    if mat is None or not mat.use_nodes or mat.node_tree is None:
        return None
    p = _principled(mat)
    seen = set()
    stack = [p.inputs['Base Color']] if p is not None else []
    while stack:
        sock = stack.pop()
        for link in sock.links:
            n = link.from_node
            if n in seen:
                continue
            seen.add(n)
            if n.type == 'TEX_IMAGE' and n.image is not None:
                return n.image
            stack.extend(i for i in n.inputs if i.is_linked)
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE' and n.image is not None:
            return n.image
    return None


def _set_render_method(mat, method):
    if hasattr(mat, 'surface_render_method'):        # Blender 4.2+
        mat.surface_render_method = method
    if hasattr(mat, 'blend_method'):                  # older / legacy EEVEE property
        try:
            mat.blend_method = {'DITHERED': 'HASHED', 'BLENDED': 'BLEND'}.get(method, 'OPAQUE')
        except TypeError:
            pass


def fix_material(mat):
    """Apply the M2 blend mode and culling stored in the material's glTF extras (wow_blend, wow_render_flags)."""
    if mat is None or 'wow_blend' not in mat or mat.get('wow_fixed'):
        return None
    blend = int(mat['wow_blend'])
    flags = int(mat.get('wow_render_flags', 0))
    mat.use_backface_culling = not (flags & 4)
    if blend == 0:
        _set_render_method(mat, 'DITHERED')
    elif blend == 1:
        _set_render_method(mat, 'DITHERED')            # alpha key: the importer already built the alpha clip
    elif blend == 2:
        _set_render_method(mat, 'BLENDED')
    elif mat.use_nodes:
        # 3/4 additive, 5/6 modulate: replace the shader with an emission + transparent combination
        _set_render_method(mat, 'BLENDED')
        nt = mat.node_tree
        src = _base_color_source(mat)
        out = _output(mat)
        if out is not None:
            tr = nt.nodes.new('ShaderNodeBsdfTransparent')
            tr.location = (out.location.x - 300, out.location.y - 200)
            if blend in (3, 4):
                em = nt.nodes.new('ShaderNodeEmission')
                em.location = (out.location.x - 300, out.location.y + 100)
                add = nt.nodes.new('ShaderNodeAddShader')
                add.location = (out.location.x - 150, out.location.y)
                if src is not None:
                    nt.links.new(src, em.inputs['Color'])
                nt.links.new(em.outputs[0], add.inputs[0])
                nt.links.new(tr.outputs[0], add.inputs[1])
                nt.links.new(add.outputs[0], out.inputs['Surface'])
            else:
                if src is not None:
                    if blend == 6:     # mod2x
                        mul = nt.nodes.new('ShaderNodeMix')
                        mul.data_type = 'RGBA'
                        mul.blend_type = 'MULTIPLY'
                        mul.inputs['Factor'].default_value = 1.0
                        mul.inputs[7].default_value = (2.0, 2.0, 2.0, 1.0)
                        nt.links.new(src, mul.inputs[6])
                        nt.links.new(mul.outputs[2], tr.inputs['Color'])
                    else:
                        nt.links.new(src, tr.inputs['Color'])
                nt.links.new(tr.outputs[0], out.inputs['Surface'])
    mat['wow_fixed'] = True
    return blend


def _top_level(objs):
    return [o for o in objs if o.parent is None or o.parent not in objs]


def _materials_of(objs):
    mats = set()
    for o in objs:
        if o.type != 'MESH':
            continue
        for m in o.data.materials:
            if m is not None:
                mats.add(m)
        for v in getattr(o.data, 'gltf2_variant_mesh_data', []):
            if v.material is not None:
                mats.add(v.material)
    return mats


def tag_ids(objs, manifest):
    """Custom properties with the export ids on meshes, materials and images (bones and materials get theirs from
    the glTF extras). They survive renames and come back as glTF extras when exporting."""
    tagged = {'meshes': 0, 'materials': 0, 'images': 0}
    tex = {t['id']: t for t in (manifest or {}).get('textures', [])}
    for o in objs:
        if o.type != 'MESH' or 'wow_submesh' not in o:
            continue
        if 'wow_mesh_id' not in o:
            o['wow_mesh_id'] = 's%02d' % int(o['wow_submesh'])
        o.data['wow_submesh'] = int(o['wow_submesh'])
        o.data['wow_mesh_id'] = o['wow_mesh_id']
        tagged['meshes'] += 1
    for m in _materials_of(objs):
        if 'wow_material_id' not in m:
            continue
        tagged['materials'] += 1
        tid = m.get('wow_texture_id')
        img = _base_image(m)
        if img is not None and isinstance(tid, str):
            img['wow_texture_id'] = tid
            t = tex.get(tid)
            if t is not None and t.get('game_path'):
                img['wow_game_path'] = t['game_path']
            tagged['images'] += 1
    return tagged


# ---------------------------------------------------------------------- import

def import_wow_gltf(context, filepath, orientation='FRONT', scale=1.0, hide_default_hidden=True, bone_heuristic='TEMPERANCE'):
    before = set(bpy.data.objects)
    actions_before = set(bpy.data.actions)
    res = bpy.ops.import_scene.gltf(filepath=filepath, import_shading='NORMALS', bone_heuristic=bone_heuristic,
                                    guess_original_bind_pose=False, merge_vertices=False, import_scene_extras=True)
    if 'FINISHED' not in res:
        raise RuntimeError('the glTF importer failed for %s' % filepath)
    new = [o for o in bpy.data.objects if o not in before]
    new_actions = [a for a in bpy.data.actions if a not in actions_before]
    side, side_text = load_sidecar(filepath)
    report = {'objects': len(new), 'actions': len(new_actions), 'materials_fixed': 0, 'hidden': 0, 'sidecar': side is not None}

    # materials (including the materials of other skins: KHR_materials_variants)
    for m in _materials_of(new):
        if fix_material(m) is not None:
            report['materials_fixed'] += 1

    # geosets hidden by default + collision
    for o in new:
        if o.type != 'MESH':
            continue
        vis = o.get('wow_default_visible', True)
        if (not vis or o.get('wow_collision')) and hide_default_hidden:
            o.hide_set(True)
            o.hide_render = True
            report['hidden'] += 1
        if o.get('wow_collision'):
            o.display_type = 'WIRE'

    arm = next((o for o in new if o.type == 'ARMATURE'), None)
    holder = arm or next((o for o in new if o.parent is None), None) or (new[0] if new else None)
    manifest, manifest_text = load_manifest(filepath, new)
    report['manifest'] = manifest is not None
    if manifest is not None and holder is not None:
        holder['wow_manifest'] = manifest_text
        holder['wow_archive_path'] = manifest.get('model', {}).get('archive_path', '')
        holder['wow_source_sha256'] = manifest.get('model', {}).get('sha256', '')
        holder['wow_package_dir'] = os.path.dirname(os.path.abspath(filepath))
        holder['wow_gltf_file'] = os.path.basename(filepath)
        context.scene['wow_model'] = holder['wow_archive_path']
    report['tagged'] = tag_ids(new, manifest)
    if manifest is not None and manifest.get('rig'):
        report['rig'] = rig.setup_rig(context, new, new_actions, manifest)

    # the command line's sidecar -> custom properties
    if side is not None and holder is not None:
        holder['wow_sidecar'] = side_text
        src = side.get('source') or {}
        holder['wow_archive_path'] = src.get('archive_path', '')
        holder['wow_source_sha256'] = src.get('sha256', '')
        holder['wow_m2_version'] = int(src.get('m2_version', 0))
        holder['wow_coordinates'] = side.get('coordinates', {}).get('mapping', '')
        bones = side.get('m2', {}).get('bones', [])
        if arm is not None:
            for i, b in enumerate(bones):
                name = side['export']['bone_nodes'][i]
                bb = arm.data.bones.get(name) or next((x for x in arm.data.bones if x.name.startswith(name)), None)
                if bb is None:
                    continue
                bb['wow_bone'] = i
                bb['wow_key_bone_id'] = int(b.get('key_bone_id', -1))
                bb['wow_flags'] = int(b.get('flags', 0))
                bb['wow_parent'] = int(b.get('parent', -1))
                bb['wow_pivot'] = [float(x) for x in b.get('pivot', [0, 0, 0])]
                bb['wow_tracks'] = json.dumps({k: b[k] for k in ('translation', 'rotation', 'scale') if k in b})
        anims = {a['name']: a for a in side.get('export', {}).get('animations', [])}
        seqs = side.get('m2', {}).get('sequences', [])
        for act in new_actions:
            a = anims.get(act.name)
            if a is None:   # the importer may suffix action names with the object name
                a = next((v for k, v in anims.items() if act.name.startswith(k)), None)
            if a is None:
                continue
            act['wow_sequence'] = a['sequence']
            act['wow_anim_id'] = a['anim_id']
            act['wow_sub_id'] = a['sub_id']
            if a['sequence'] < len(seqs):
                act['wow_sequence_fields'] = json.dumps(seqs[a['sequence']])
    for act in new_actions:
        act.use_fake_user = True        # keep every sequence even when no object uses it
        if 'wow_import_hash' not in act:
            act['wow_import_hash'] = rig.action_fingerprint(act)

    # orientation / scale on the top-level object(s) only
    rot = {'FRONT': -math.pi / 2, 'WOW': 0.0}[orientation]
    for o in _top_level(new):
        o.rotation_mode = 'XYZ'
        o.rotation_euler[2] += rot
        o.scale = (o.scale[0] * scale, o.scale[1] * scale, o.scale[2] * scale)
        o['wow_import_rotation_z'] = rot
        o['wow_import_scale'] = scale
    report['armature'] = arm.name if arm else None
    report['variants'] = len(getattr(context.scene, 'gltf2_KHR_materials_variants_variants', []) or [])
    return report


# ---------------------------------------------------------------------- export back to WoW Data Studio

def wow_root(context):
    """The object holding the package manifest (the armature of the imported model)."""
    for o in context.scene.objects:
        if isinstance(o.get('wow_manifest'), str):
            return o
    for o in context.scene.objects:
        if 'wow_archive_path' in o or 'wow_model' in o:
            return o
    return None


def default_export_path(context):
    r = wow_root(context)
    if r is None or not r.get('wow_package_dir'):
        return ''
    name = os.path.splitext(r.get('wow_gltf_file') or (r.name + '.gltf'))[0]
    return os.path.join(r['wow_package_dir'], 'blender_export', name + '.gltf')


def export_for_studio(context, filepath):
    """Export the scene for WoW Data Studio's "Import edited model...": glTF with extras, variants and PNG textures,
    plus wowdata_manifest.json (the original export's) and wowdata_blender.json (image file -> game texture)."""
    if not filepath.lower().endswith(('.gltf', '.glb')):
        filepath += '.gltf'
    out_dir = os.path.dirname(os.path.abspath(filepath))
    os.makedirs(out_dir, exist_ok=True)
    root = wow_root(context)
    manifest_text = root.get('wow_manifest') if root is not None else None
    # export the skins (KHR_materials_variants) when the scene has them
    try:
        prefs = context.preferences.addons['io_scene_gltf2'].preferences
        if len(getattr(context.scene, 'gltf2_KHR_materials_variants_variants', []) or []) > 0 and not prefs.KHR_materials_variants_ui:
            prefs.KHR_materials_variants_ui = True
    except (KeyError, AttributeError):
        pass
    glb = filepath.lower().endswith('.glb')
    kw = dict(filepath=filepath, export_format='GLB' if glb else 'GLTF_SEPARATE', export_extras=True, export_yup=True,
              export_animations=False, export_skins=True, export_texcoords=True, export_normals=True, export_tangents=False,
              export_materials='EXPORT', export_image_format='AUTO', export_cameras=False, export_lights=False,
              use_selection=False, use_visible=False, export_apply=False, export_morph=False)
    if not glb:
        kw['export_texture_dir'] = 'textures'
    res = bpy.ops.export_scene.gltf(**kw)
    if 'FINISHED' not in res:
        raise RuntimeError('the glTF exporter failed')
    # image file -> game texture, from the images' custom properties (the glTF exporter writes no image extras)
    images = {}
    by_name = {i.name: i for i in bpy.data.images}
    if not glb:
        with open(filepath, 'r', encoding='utf-8') as f:
            g = json.load(f)
        changed = False
        for im in g.get('images', []):
            bi = by_name.get(im.get('name', ''))
            if bi is None:
                continue
            tid = bi.get('wow_texture_id')
            if isinstance(tid, str):
                images[im.get('uri', im.get('name', ''))] = {'texture_id': tid, 'image': bi.name, 'game_path': bi.get('wow_game_path', '')}
                im.setdefault('extras', {})['wow_texture_id'] = tid
                changed = True
        if changed:
            with open(filepath, 'w', encoding='utf-8') as f:
                json.dump(g, f, separators=(',', ':'))
    else:
        for bi in bpy.data.images:
            tid = bi.get('wow_texture_id')
            if isinstance(tid, str):
                images[bi.name] = {'texture_id': tid, 'image': bi.name, 'game_path': bi.get('wow_game_path', '')}
    objects = {o.name: {'submesh': int(o['wow_submesh']) if 'wow_submesh' in o else None}
               for o in context.scene.objects if o.type == 'MESH'}
    info = {'format': 'wowdata-blender-export', 'format_version': 1, 'addon': ADDON_VERSION, 'blender': bpy.app.version_string,
            'gltf': os.path.basename(filepath), 'model': root.get('wow_archive_path', '') if root else '',
            'import_rotation_z': float(root.get('wow_import_rotation_z', 0.0)) if root else 0.0,
            'import_scale': float(root.get('wow_import_scale', 1.0)) if root else 1.0,
            'images': images, 'objects': objects}
    with open(os.path.join(out_dir, BLENDER_MAP_NAME), 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    if isinstance(manifest_text, str):
        with open(os.path.join(out_dir, MANIFEST_NAME), 'w', encoding='utf-8') as f:
            f.write(manifest_text)
    rig_report = None
    man = rig.manifest_of(root)
    if man is not None and man.get('rig'):
        rig_report = rig.export_rig(context, root, man, os.path.join(out_dir, rig.RIG_FILE))
    return {'file': filepath, 'images': len(images), 'objects': len(objects), 'manifest': isinstance(manifest_text, str), 'rig': rig_report}


# ---------------------------------------------------------------------- operators

class WOWDATA_OT_import_gltf(bpy.types.Operator, ImportHelper):
    """Import a WoW 1.12 model exported by WoW Data Studio or wowdata (glTF)"""
    bl_idname = 'wowdata.import_gltf'
    bl_label = 'Import WoW model (wowdata glTF)'
    bl_options = {'REGISTER', 'UNDO'}

    filename_ext = '.gltf'
    filter_glob: StringProperty(default='*.gltf;*.glb', options={'HIDDEN'})
    orientation: EnumProperty(name='Orientation', default='FRONT', items=[
        ('FRONT', 'Face the front view (-Y)', 'Rotate the top-level object -90 degrees about Z (data unchanged)'),
        ('WOW', 'As in the game (+X)', 'No rotation: Blender coordinates equal the M2 coordinates')])
    scale: FloatProperty(name='Scale', default=1.0, min=0.0001, description='Object scale (1 Blender unit = 1 yard at 1.0; 0.9144 for metres)')
    hide_default_hidden: BoolProperty(name='Hide non-default geosets', default=True,
                                      description='Hide the geosets the game hides by default and the collision mesh')

    def execute(self, context):
        try:
            rep = import_wow_gltf(context, self.filepath, self.orientation, self.scale, self.hide_default_hidden)
        except Exception as e:  # noqa: BLE001
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, 'WoW model: %d objects, %d actions, %d materials fixed, %d hidden%s' % (
            rep['objects'], rep['actions'], rep['materials_fixed'], rep['hidden'],
            '' if rep['sidecar'] or rep['manifest'] else ' (no manifest or sidecar found)'))
        return {'FINISHED'}


class WOWDATA_OT_import_target(bpy.types.Operator):
    """Run `wowdata model export <target>` and import the result"""
    bl_idname = 'wowdata.import_target'
    bl_label = 'Import from WoW (wowdata)'
    bl_options = {'REGISTER', 'UNDO'}

    target: StringProperty(name='Target', description='display:503, creature:3098, race:orc, item:12282 or an .m2 path',
                           default='display:503')

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        prefs = context.preferences.addons[__name__].preferences
        tool = bpy.path.abspath(prefs.tool_dir)
        out = os.path.join(tool, 'work', 'export', 'models')
        try:
            r = subprocess.run([prefs.python_exe, '-m', 'wowdata', 'model', 'export', self.target, '--out', out],
                               cwd=tool, capture_output=True, text=True, timeout=600)
        except OSError as e:
            self.report({'ERROR'}, 'could not run %s: %s' % (prefs.python_exe, e))
            return {'CANCELLED'}
        gl = None
        for line in r.stdout.splitlines():
            line = line.strip()
            if line.lower().endswith('.gltf'):
                gl = line
        if r.returncode != 0 or not gl:
            self.report({'ERROR'}, (r.stderr or r.stdout).strip()[-400:] or 'wowdata failed')
            return {'CANCELLED'}
        rep = import_wow_gltf(context, gl, prefs.orientation, prefs.scale, True)
        self.report({'INFO'}, '%s: %d objects, %d actions' % (self.target, rep['objects'], rep['actions']))
        return {'FINISHED'}


class WOWDATA_OT_export_studio(bpy.types.Operator):
    """Export the edited model for WoW Data Studio (glTF with ids, skins and PNG textures)"""
    bl_idname = 'wowdata.export_for_studio'
    bl_label = 'Export for WoW Data Studio'
    bl_options = {'REGISTER'}

    filename_ext = '.gltf'
    filepath: StringProperty(subtype='FILE_PATH')
    filter_glob: StringProperty(default='*.gltf;*.glb', options={'HIDDEN'})

    def invoke(self, context, event):
        if not self.filepath:
            self.filepath = default_export_path(context) or bpy.path.ensure_ext(bpy.data.filepath or 'model', '.gltf')
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        if wow_root(context) is None:
            self.report({'ERROR'}, 'No WoW model in this scene: import one first (File > Import > WoW 1.12 model).')
            return {'CANCELLED'}
        try:
            rep = export_for_studio(context, self.filepath)
        except Exception as e:  # noqa: BLE001
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, 'Exported %s (%d textures). In WoW Data Studio: "Import edited model..." and pick this file or its folder.' % (
            os.path.basename(rep['file']), rep['images']))
        return {'FINISHED'}


class WOWDATA_Preferences(bpy.types.AddonPreferences):
    bl_idname = __name__
    tool_dir: StringProperty(name='WoWDataTool folder', subtype='DIR_PATH',
                             default=r'<your WoW Custom Client folder>\WoWDataTool')
    python_exe: StringProperty(name='Python (with numpy + Pillow)', default='python')
    orientation: EnumProperty(name='Orientation', default='FRONT', items=[('FRONT', 'Front view (-Y)', ''), ('WOW', 'As in the game (+X)', '')])
    scale: FloatProperty(name='Scale', default=1.0, min=0.0001)

    def draw(self, context):
        col = self.layout.column()
        col.label(text='WoW Data Studio: File > Import > WoW 1.12 model, edit, then File > Export > WoW 1.12 model for WoW Data Studio.')
        col.prop(self, 'tool_dir')
        col.prop(self, 'python_exe')
        col.prop(self, 'orientation')
        col.prop(self, 'scale')


def menu_import(self, context):
    self.layout.operator(WOWDATA_OT_import_gltf.bl_idname, text='WoW 1.12 model (.gltf from WoW Data Studio / wowdata)')
    self.layout.operator(WOWDATA_OT_import_target.bl_idname, text='WoW 1.12 model from the game data (wowdata)')


def menu_export(self, context):
    self.layout.operator(WOWDATA_OT_export_studio.bl_idname, text='WoW 1.12 model for WoW Data Studio (.gltf)')


CLASSES = (WOWDATA_OT_import_gltf, WOWDATA_OT_import_target, WOWDATA_OT_export_studio, WOWDATA_Preferences)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.types.TOPBAR_MT_file_export.append(menu_export)
    terrain.register()
    rig.register()


def unregister():
    rig.unregister()
    terrain.unregister()
    bpy.types.TOPBAR_MT_file_export.remove(menu_export)
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)
