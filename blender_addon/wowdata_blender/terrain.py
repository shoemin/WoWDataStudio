"""WoW 1.12 terrain in Blender: imports the terrain package written by WoW Data Studio's "Open in Blender" for map tiles
(wowdata_terrain.json), and exports the edited heights back for "Import edited terrain...".

What the import builds:
  * one object per tile, named "Tile <x>_<y>", in its own collection, with its own viewport colour (Solid view, colour
    by object) and a label (a text object above the tile; "Show labels" hides them);
  * the tile's ground as one mesh: 129 x 129 outer + 128 x 128 inner vertices, 4 triangles around every inner vertex,
    holes left out (their vertices stay), every vertex tagged with its grid id (point attribute wow_vid) and its height
    at import (wow_orig_z);
  * the edge vertices in vertex groups EDGE_N, EDGE_E, EDGE_S, EDGE_W;
  * a material that blends the tile's ground textures like the game (each texture repeated 8 times per chunk, weighted
    by its alpha-map weights, stored as images: 3 textures per RGB weight image);
  * optionally the doodads and buildings as references (not imported back).

Rules, as the game's terrain is a fixed grid: only heights (Z) come back. "Sync shared edges" copies the heights of an
edited edge onto the neighbouring tile's matching edge (shared vertices must be identical in the game); "Check edge
seams" reports the edges that differ.
"""
import array
import json
import math
import os

import bpy
from bpy.props import StringProperty
from bpy_extras.io_utils import ImportHelper
from mathutils import Vector

MANIFEST_NAME = 'wowdata_terrain.json'
EDIT_NAME = 'wowdata_terrain_edit.json'
OUTER = 129
INNER = 128
N_OUTER = OUTER * OUTER
N_INNER = INNER * INNER
COUNT = N_OUTER + N_INNER
EDGES = ('N', 'E', 'S', 'W')
REF_COLLECTION = 'WoW references (ignored on import)'
LABEL_PREFIX = 'Label '


def outer_id(r, c):
    return r * OUTER + c


def inner_id(r, c):
    return N_OUTER + r * INNER + c


def _version():
    from . import bl_info
    return '.'.join(str(x) for x in bl_info['version'])


# ---------------------------------------------------------------------- import

def _read_heights(path):
    with open(path, 'rb') as f:
        data = f.read()
    h = array.array('f')
    h.frombytes(data[:COUNT * 4])
    holes = data[COUNT * 4:COUNT * 4 + N_INNER]
    return h, holes


def _collection(name, parent):
    c = bpy.data.collections.get(name)
    if c is None or c.name not in parent.children:
        c = bpy.data.collections.new(name)
        parent.children.link(c)
    return c


def _tile_mesh(name, heights, holes, unit):
    verts = []
    for r in range(OUTER):
        for c in range(OUTER):
            verts.append((c * unit, -r * unit, heights[outer_id(r, c)]))
    for r in range(INNER):
        for c in range(INNER):
            verts.append(((c + 0.5) * unit, -(r + 0.5) * unit, heights[inner_id(r, c)]))
    faces = []
    for r in range(INNER):
        for c in range(INNER):
            if holes and holes[r * INNER + c]:
                continue
            o00, o01 = outer_id(r, c), outer_id(r, c + 1)
            o10, o11 = outer_id(r + 1, c), outer_id(r + 1, c + 1)
            m = inner_id(r, c)
            faces.extend(((o00, m, o01), (o01, m, o11), (o11, m, o10), (o10, m, o00)))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    # tile UVs (0..1 over the tile, north up), per loop from the vertex
    uv = me.uv_layers.new(name='TileUV')
    vuv = [((c) / 128.0, 1.0 - r / 128.0) for r in range(OUTER) for c in range(OUTER)] + \
          [((c + 0.5) / 128.0, 1.0 - (r + 0.5) / 128.0) for r in range(INNER) for c in range(INNER)]
    vi = array.array('i', [0]) * len(me.loops)
    me.loops.foreach_get('vertex_index', vi)
    flat = array.array('f', [0.0]) * (len(me.loops) * 2)
    for i, v in enumerate(vi):
        flat[i * 2], flat[i * 2 + 1] = vuv[v]
    uv.data.foreach_set('uv', flat)
    ids = me.attributes.new('wow_vid', 'INT', 'POINT')
    ids.data.foreach_set('value', array.array('i', range(1, COUNT + 1)))
    orig = me.attributes.new('wow_orig_z', 'FLOAT', 'POINT')
    orig.data.foreach_set('value', heights[:COUNT] if isinstance(heights, array.array) else array.array('f', heights[:COUNT]))
    for p in me.polygons:
        p.use_smooth = True
    me.update()
    return me


def _ground_material(name, tile, manifest, root):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    out.location = (900, 0)
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled')
    bsdf.location = (650, 0)
    bsdf.inputs['Roughness'].default_value = 1.0
    for key in ('Specular IOR Level', 'Specular'):
        if key in bsdf.inputs:
            bsdf.inputs[key].default_value = 0.0
            break
    nt.links.new(bsdf.outputs[0], out.inputs['Surface'])
    uvn = nt.nodes.new('ShaderNodeUVMap')
    uvn.uv_map = 'TileUV'
    uvn.location = (-900, 0)
    mapping = nt.nodes.new('ShaderNodeMapping')
    mapping.location = (-700, 200)
    mapping.inputs['Scale'].default_value = (128.0, 128.0, 1.0)       # 8 repeats per chunk, 16 chunks
    nt.links.new(uvn.outputs['UV'], mapping.inputs['Vector'])
    weights = {}
    total = None
    y = 400
    for k, use in enumerate(tile.get('textures', [])):
        tex = manifest['textures'][use['texture']]
        tn = nt.nodes.new('ShaderNodeTexImage')
        tn.location = (-400, y)
        tn.label = os.path.basename(tex.get('game_path', ''))
        if tex.get('png'):
            img = bpy.data.images.load(os.path.join(root, tex['png']), check_existing=True)
            img['wow_game_path'] = tex.get('game_path', '')
            tn.image = img
        nt.links.new(mapping.outputs['Vector'], tn.inputs['Vector'])
        wf = use['weight_image']
        if wf not in weights:
            wn = nt.nodes.new('ShaderNodeTexImage')
            wn.location = (-650, y - 120)
            wimg = bpy.data.images.load(os.path.join(root, wf), check_existing=True)
            try:
                wimg.colorspace_settings.name = 'Non-Color'
            except TypeError:
                pass
            wn.image = wimg
            wn.extension = 'EXTEND'
            wn.interpolation = 'Linear'
            nt.links.new(uvn.outputs['UV'], wn.inputs['Vector'])
            sep = nt.nodes.new('ShaderNodeSeparateColor')
            sep.location = (-420, y - 160)
            nt.links.new(wn.outputs['Color'], sep.inputs['Color'])
            weights[wf] = sep
        sep = weights[wf]
        scale = nt.nodes.new('ShaderNodeVectorMath')
        scale.operation = 'SCALE'
        scale.location = (-150, y)
        scale.label = 'x weight of %s' % os.path.basename(tex.get('game_path', ''))
        nt.links.new(tn.outputs['Color'], scale.inputs[0])
        nt.links.new(sep.outputs[use['channel']], scale.inputs['Scale'])
        if total is None:
            total = scale
        else:
            add = nt.nodes.new('ShaderNodeVectorMath')
            add.operation = 'ADD'
            add.location = (100 + 40 * k, y)
            nt.links.new(total.outputs[0], add.inputs[0])
            nt.links.new(scale.outputs[0], add.inputs[1])
            total = add
        y -= 260
    if total is not None:
        nt.links.new(total.outputs[0], bsdf.inputs['Base Color'])
    else:
        bsdf.inputs['Base Color'].default_value = (0.45, 0.5, 0.35, 1.0)
    mat['wow_terrain_material'] = True
    return mat


def _label(text, tile, unit, collection, obj):
    cu = bpy.data.curves.new(LABEL_PREFIX + text, 'FONT')
    cu.body = text
    cu.size = 48.0
    cu.align_x = 'CENTER'
    cu.align_y = 'CENTER'
    lab = bpy.data.objects.new(LABEL_PREFIX + text, cu)
    lab.location = (obj.location.x + 64 * unit, obj.location.y - 64 * unit, float(tile.get('height_max', 0.0)) + 40.0)
    lab.color = obj.color
    lab['wow_terrain_label'] = text
    lab.hide_select = True
    collection.objects.link(lab)
    return lab


def import_wow_terrain(context, filepath, references=True):
    """Build the tile objects of a terrain package (wowdata_terrain.json or its folder)."""
    if os.path.isdir(filepath):
        filepath = os.path.join(filepath, MANIFEST_NAME)
    with open(filepath, 'r', encoding='utf-8') as f:
        text = f.read()
    man = json.loads(text)
    if man.get('format') != 'wowdata-terrain-package':
        raise RuntimeError('%s is not a terrain package of WoW Data Studio' % filepath)
    root = os.path.dirname(os.path.abspath(filepath))
    unit = float(man['grid']['unit'])
    scene = context.scene
    top = _collection('WoW terrain %s %d_%d' % (man['map']['directory'], man['center'][0], man['center'][1]), scene.collection)
    report = {'tiles': [], 'objects': 0, 'labels': 0, 'references': 0}
    for t in man['tiles']:
        heights, holes = _read_heights(os.path.join(root, t['heights_file']))
        name = t['id']
        col = _collection(name, top)
        me = _tile_mesh(name, heights, holes, unit)
        obj = bpy.data.objects.new(name, me)
        obj.location = (t['location'][0], t['location'][1], 0.0)
        obj.color = (t['color'][0], t['color'][1], t['color'][2], 1.0)
        obj['wow_terrain_tile'] = '%d_%d' % (t['x'], t['y'])
        obj['wow_tile_id'] = name
        obj['wow_tile_x'] = int(t['x'])
        obj['wow_tile_y'] = int(t['y'])
        obj['wow_game_path'] = t['game_path']
        obj['wow_sha256'] = t['sha256']
        col.objects.link(obj)
        # edge vertex groups
        for e in EDGES:
            vg = obj.vertex_groups.new(name='EDGE_' + e)
            if e == 'N':
                ids = [outer_id(0, c) for c in range(OUTER)]
            elif e == 'S':
                ids = [outer_id(128, c) for c in range(OUTER)]
            elif e == 'W':
                ids = [outer_id(r, 0) for r in range(OUTER)]
            else:
                ids = [outer_id(r, 128) for r in range(OUTER)]
            vg.add(ids, 1.0, 'REPLACE')
        mat = _ground_material('Ground ' + name, t, man, root)
        me.materials.append(mat)
        _label(name, t, unit, col, obj)
        report['tiles'].append({'name': obj.name, 'collection': col.name, 'color': list(obj.color), 'vertices': len(me.vertices),
                                'faces': len(me.polygons), 'groups': [g.name for g in obj.vertex_groups],
                                'textures': len(t.get('textures', []))})
        report['objects'] += 1
        report['labels'] += 1
    scene['wow_terrain_manifest'] = os.path.abspath(filepath)
    scene['wow_terrain_package'] = root
    scene['wow_terrain_map'] = man['map']['directory']
    refs = man.get('references')
    if references and refs and os.path.exists(os.path.join(root, refs['file'])):
        before = set(bpy.data.objects)
        res = bpy.ops.import_scene.gltf(filepath=os.path.join(root, refs['file']), import_shading='FLAT')
        if 'FINISHED' in res:
            rc = _collection(REF_COLLECTION, top)
            for o in [o for o in bpy.data.objects if o not in before]:
                for c in list(o.users_collection):
                    c.objects.unlink(o)
                rc.objects.link(o)
                o.hide_select = True
                o['wow_reference'] = True
                report['references'] += 1
    # Solid view, colour by object: the tiles are obviously separate pieces
    _set_shading(context, 'OBJECT')
    return report


def _view3d_spaces(context):
    wm = getattr(context, 'window_manager', None)
    if wm is None:
        return
    for win in wm.windows:
        for area in win.screen.areas:
            if area.type == 'VIEW_3D':
                for space in area.spaces:
                    if space.type == 'VIEW_3D':
                        yield space


def _set_shading(context, mode):
    """'OBJECT': Solid view coloured per tile; 'TEXTURES': Material Preview with the ground textures."""
    for space in _view3d_spaces(context):
        if mode == 'OBJECT':
            space.shading.type = 'SOLID'
            space.shading.color_type = 'OBJECT'
        else:
            space.shading.type = 'MATERIAL'
        space.clip_end = max(space.clip_end, 20000.0)


# ---------------------------------------------------------------------- tiles, edges, seams

def terrain_objects(context):
    return {(int(o['wow_tile_x']), int(o['wow_tile_y'])): o for o in context.scene.objects
            if o.type == 'MESH' and 'wow_terrain_tile' in o}


class _Tile:
    """Fast access to one tile object's vertices by grid id."""

    def __init__(self, obj):
        self.obj = obj
        me = obj.data
        n = len(me.vertices)
        self.co = array.array('f', [0.0]) * (n * 3)
        me.vertices.foreach_get('co', self.co)
        self.index = {}
        att = me.attributes.get('wow_vid')
        if att is not None:
            vids = array.array('i', [0]) * n
            att.data.foreach_get('value', vids)
            for i, v in enumerate(vids):
                if 0 < v <= COUNT and (v - 1) not in self.index:
                    self.index[v - 1] = i
        self.orig = None
        oa = me.attributes.get('wow_orig_z')
        if oa is not None:
            self.orig = array.array('f', [0.0]) * n
            oa.data.foreach_get('value', self.orig)
        self.mw = obj.matrix_world.copy()
        self.mwi = self.mw.inverted()
        self.dirty = False

    def world(self, gid):
        i = self.index.get(gid)
        if i is None:
            return None
        return self.mw @ Vector((self.co[i * 3], self.co[i * 3 + 1], self.co[i * 3 + 2]))

    def edited(self, gid):
        i = self.index.get(gid)
        if i is None or self.orig is None:
            return False
        return abs(self.co[i * 3 + 2] - self.orig[i]) > 1e-4

    def change_from_orig(self, gid):
        i = self.index.get(gid)
        if i is None or self.orig is None:
            return 0.0
        return abs(self.co[i * 3 + 2] - self.orig[i])

    def set_world_z(self, gid, z):
        i = self.index.get(gid)
        if i is None:
            return
        w = self.world(gid)
        w.z = z
        loc = self.mwi @ w
        self.co[i * 3 + 2] = loc.z
        self.dirty = True

    def write(self):
        if self.dirty:
            self.obj.data.vertices.foreach_set('co', self.co)
            self.obj.data.update()


def shared_pairs():
    """(dx, dy, edge name, [(id here, id in the neighbour)]): east, south and the two lower corners (each pair once)."""
    yield 1, 0, 'east', [(outer_id(r, 128), outer_id(r, 0)) for r in range(OUTER)]
    yield 0, 1, 'south', [(outer_id(128, c), outer_id(0, c)) for c in range(OUTER)]
    yield 1, 1, 'south-east corner', [(outer_id(128, 128), outer_id(0, 0))]
    yield -1, 1, 'south-west corner', [(outer_id(128, 0), outer_id(0, 128))]


def _with_object_mode(context, fn):
    mode = context.object.mode if context.object is not None else 'OBJECT'
    if mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    try:
        return fn()
    finally:
        if mode == 'EDIT':
            bpy.ops.object.mode_set(mode='EDIT')


def check_seams(context, tolerance=1e-3, select=False):
    """The shared edges between the tile objects whose heights differ: [{a, b, edge, vertices, max}]."""
    objs = terrain_objects(context)
    tiles = {k: _Tile(o) for k, o in objs.items()}
    issues = []
    for (x, y), ta in tiles.items():
        for dx, dy, edge, pairs in shared_pairs():
            tb = tiles.get((x + dx, y + dy))
            if tb is None:
                continue
            n, worst, bad = 0, 0.0, []
            for ia, ib in pairs:
                wa, wb = ta.world(ia), tb.world(ib)
                if wa is None or wb is None:
                    continue
                d = abs(wa.z - wb.z)
                if d > tolerance:
                    n += 1
                    worst = max(worst, d)
                    bad.append((ia, ib))
            if n:
                issues.append({'a': ta.obj.name, 'b': tb.obj.name, 'edge': edge, 'vertices': n, 'max': worst, 'pairs': bad})
    if select:
        for t in tiles.values():
            me = t.obj.data
            me.vertices.foreach_set('select', [False] * len(me.vertices))
            me.edges.foreach_set('select', [False] * len(me.edges))
            me.polygons.foreach_set('select', [False] * len(me.polygons))
        for i in issues:
            for ia, ib in i['pairs']:
                for name, gid in ((i['a'], ia), (i['b'], ib)):
                    t = next(t for t in tiles.values() if t.obj.name == name)
                    idx = t.index.get(gid)
                    if idx is not None:
                        t.obj.data.vertices[idx].select = True
    for i in issues:
        i.pop('pairs', None)
    return issues


def sync_edges(context, prefer=None):
    """Copy the heights of edited edge vertices onto the neighbour's matching vertices (the edited side wins; when
    both sides were edited, the object `prefer` (the active one) wins, else the side that moved further).
    Returns {'synced': vertices, 'edges': [...], 'conflicts': n}."""
    objs = terrain_objects(context)
    tiles = {k: _Tile(o) for k, o in objs.items()}
    synced, conflicts, edges = 0, 0, []
    for (x, y), ta in tiles.items():
        for dx, dy, edge, pairs in shared_pairs():
            tb = tiles.get((x + dx, y + dy))
            if tb is None:
                continue
            n = 0
            for ia, ib in pairs:
                wa, wb = ta.world(ia), tb.world(ib)
                if wa is None or wb is None or abs(wa.z - wb.z) <= 1e-5:
                    continue
                ea, eb = ta.edited(ia), tb.edited(ib)
                if not ea and not eb:
                    continue        # an old difference of the game's own tiles: left alone
                if ea and eb:
                    conflicts += 1
                    if prefer is not None and prefer in (ta.obj, tb.obj):
                        from_a = prefer is ta.obj
                    else:
                        from_a = ta.change_from_orig(ia) >= tb.change_from_orig(ib)
                else:
                    from_a = ea
                if from_a:
                    tb.set_world_z(ib, wa.z)
                else:
                    ta.set_world_z(ia, wb.z)
                n += 1
            if n:
                edges.append({'a': ta.obj.name, 'b': tb.obj.name, 'edge': edge, 'vertices': n})
                synced += n
    for t in tiles.values():
        t.write()
    return {'synced': synced, 'edges': edges, 'conflicts': conflicts}


# ---------------------------------------------------------------------- export back

def default_export_path(context):
    root = context.scene.get('wow_terrain_package')
    if not root:
        return ''
    return os.path.join(root, 'blender_export', EDIT_NAME)


def export_terrain(context, filepath):
    """Write wowdata_terrain_edit.json and one .bin per tile object (wow_vid int32[n], then local x y z float32[n * 3])
    for WoW Data Studio's "Import edited terrain...". Modifiers are applied (the evaluated mesh)."""
    if os.path.isdir(filepath) or not filepath.lower().endswith('.json'):
        filepath = os.path.join(filepath if os.path.isdir(filepath) else os.path.dirname(filepath), EDIT_NAME)
    out_dir = os.path.dirname(os.path.abspath(filepath))
    os.makedirs(out_dir, exist_ok=True)
    depsgraph = context.evaluated_depsgraph_get()
    tiles = []
    notes = []
    for (x, y), obj in sorted(terrain_objects(context).items(), key=lambda kv: (kv[0][1], kv[0][0])):
        ev = obj.evaluated_get(depsgraph)
        me = ev.to_mesh()
        try:
            n = len(me.vertices)
            co = array.array('f', [0.0]) * (n * 3)
            me.vertices.foreach_get('co', co)
            att = me.attributes.get('wow_vid')
            vids = None
            if att is not None and att.domain == 'POINT':
                vids = array.array('i', [0]) * n
                att.data.foreach_get('value', vids)
            else:
                notes.append('%s has no wow_vid attribute: the app matches its vertices by position.' % obj.name)
            fname = 'tile_%d_%d.bin' % (x, y)
            with open(os.path.join(out_dir, fname), 'wb') as f:
                if vids is not None:
                    vids.tofile(f)
                co.tofile(f)
            mw = obj.matrix_world
            tiles.append({'id': obj.get('wow_tile_id', obj.name), 'object': obj.name, 'x': x, 'y': y, 'file': fname, 'vertices': n,
                          'has_vid': vids is not None, 'matrix_world': [mw[r][c] for r in range(4) for c in range(4)]})
        finally:
            ev.to_mesh_clear()
    info = {'format': 'wowdata-terrain-edit', 'format_version': 1, 'addon': _version(), 'blender': bpy.app.version_string,
            'manifest': context.scene.get('wow_terrain_manifest', ''), 'tiles': tiles, 'notes': notes}
    with open(filepath, 'w', encoding='utf-8') as f:
        json.dump(info, f, indent=1)
    return {'file': filepath, 'tiles': len(tiles)}


# ---------------------------------------------------------------------- operators and panel

class WOWDATA_OT_import_terrain(bpy.types.Operator, ImportHelper):
    """Import WoW 1.12 terrain tiles exported by WoW Data Studio (wowdata_terrain.json)"""
    bl_idname = 'wowdata.import_terrain'
    bl_label = 'Import WoW terrain (WoW Data Studio)'
    bl_options = {'REGISTER', 'UNDO'}

    filename_ext = '.json'
    filter_glob: StringProperty(default='wowdata_terrain.json;*.json', options={'HIDDEN'})

    def execute(self, context):
        try:
            rep = import_wow_terrain(context, self.filepath)
        except Exception as e:  # noqa: BLE001
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, 'WoW terrain: %d tiles, %d references' % (rep['objects'], rep['references']))
        return {'FINISHED'}


class WOWDATA_OT_sync_edges(bpy.types.Operator):
    """Copy the heights of edited tile edges onto the neighbouring tile's matching edge (shared vertices must be identical in the game)"""
    bl_idname = 'wowdata.sync_edges'
    bl_label = 'Sync shared edges'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        if not terrain_objects(context):
            self.report({'ERROR'}, 'No WoW terrain tiles in this scene.')
            return {'CANCELLED'}
        rep = _with_object_mode(context, lambda: sync_edges(context, context.active_object))
        context.scene['wow_seam_report'] = 'Synced %d vertices on %d edges%s.' % (
            rep['synced'], len(rep['edges']), (' (%d edited on both sides: the active tile won)' % rep['conflicts']) if rep['conflicts'] else '')
        self.report({'INFO'}, context.scene['wow_seam_report'])
        return {'FINISHED'}


class WOWDATA_OT_check_seams(bpy.types.Operator):
    """Report (and select) the vertices of shared tile edges whose heights differ"""
    bl_idname = 'wowdata.check_seams'
    bl_label = 'Check edge seams'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        if not terrain_objects(context):
            self.report({'ERROR'}, 'No WoW terrain tiles in this scene.')
            return {'CANCELLED'}
        issues = _with_object_mode(context, lambda: check_seams(context, select=True))
        if not issues:
            text = 'All shared edges match.'
        else:
            text = '; '.join('%s / %s (%s): %d vertices, up to %.2f' % (i['a'], i['b'], i['edge'], i['vertices'], i['max']) for i in issues)
            text = '%d edge(s) differ: %s. Use "Sync shared edges".' % (len(issues), text)
        context.scene['wow_seam_report'] = text
        self.report({'WARNING'} if issues else {'INFO'}, text)
        return {'FINISHED'}


class WOWDATA_OT_terrain_shading(bpy.types.Operator):
    """Show the tiles in their colours (Solid view) or with their ground textures (Material Preview)"""
    bl_idname = 'wowdata.terrain_shading'
    bl_label = 'Terrain shading'
    mode: StringProperty(default='OBJECT')

    def execute(self, context):
        _set_shading(context, self.mode)
        return {'FINISHED'}


class WOWDATA_OT_toggle_labels(bpy.types.Operator):
    """Show or hide the tile labels"""
    bl_idname = 'wowdata.toggle_labels'
    bl_label = 'Show labels'

    def execute(self, context):
        labels = [o for o in context.scene.objects if 'wow_terrain_label' in o]
        hide = not all(o.hide_get() for o in labels) if labels else False
        for o in labels:
            o.hide_set(hide)
        return {'FINISHED'}


class WOWDATA_OT_export_terrain(bpy.types.Operator):
    """Export the edited terrain for WoW Data Studio's "Import edited terrain..." (heights only)"""
    bl_idname = 'wowdata.export_terrain'
    bl_label = 'Export terrain for WoW Data Studio'
    bl_options = {'REGISTER'}

    filename_ext = '.json'
    filepath: StringProperty(subtype='FILE_PATH')
    filter_glob: StringProperty(default='*.json', options={'HIDDEN'})

    def invoke(self, context, event):
        if not self.filepath:
            self.filepath = default_export_path(context) or os.path.join(os.path.dirname(bpy.data.filepath or ''), EDIT_NAME)
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        if not terrain_objects(context):
            self.report({'ERROR'}, 'No WoW terrain tiles in this scene: import them first (File > Import > WoW 1.12 terrain).')
            return {'CANCELLED'}
        try:
            rep = _with_object_mode(context, lambda: export_terrain(context, self.filepath or default_export_path(context)))
        except Exception as e:  # noqa: BLE001
            self.report({'ERROR'}, str(e))
            return {'CANCELLED'}
        self.report({'INFO'}, 'Exported %d tiles. In WoW Data Studio: Maps tab, "Import edited terrain..." and pick the package folder.' % rep['tiles'])
        return {'FINISHED'}


class WOWDATA_PT_terrain(bpy.types.Panel):
    bl_label = 'WoW Terrain'
    bl_idname = 'WOWDATA_PT_terrain'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'WoW Terrain'

    def draw(self, context):
        col = self.layout.column(align=False)
        objs = terrain_objects(context)
        if not objs:
            col.label(text='No WoW terrain in this scene.')
            col.operator(WOWDATA_OT_import_terrain.bl_idname, text='Import WoW terrain...')
            return
        col.label(text='%d tile(s): %s' % (len(objs), ', '.join(o.name for _, o in sorted(objs.items(), key=lambda kv: (kv[0][1], kv[0][0])))[:60]))
        box = col.box()
        box.label(text='Only heights (Z) go back to the game.')
        box.label(text='Edges shared by two tiles must match.')
        col.operator(WOWDATA_OT_sync_edges.bl_idname, icon='SNAP_EDGE')
        col.operator(WOWDATA_OT_check_seams.bl_idname, icon='VIEWZOOM')
        rep = context.scene.get('wow_seam_report')
        if rep:
            for line in [rep[i:i + 48] for i in range(0, min(len(rep), 240), 48)]:
                col.label(text=line)
        row = col.row(align=True)
        row.operator(WOWDATA_OT_terrain_shading.bl_idname, text='Tile colours').mode = 'OBJECT'
        row.operator(WOWDATA_OT_terrain_shading.bl_idname, text='Ground textures').mode = 'TEXTURES'
        col.operator(WOWDATA_OT_toggle_labels.bl_idname, icon='FONT_DATA')
        col.separator()
        col.operator(WOWDATA_OT_export_terrain.bl_idname, icon='EXPORT')


def menu_import(self, context):
    self.layout.operator(WOWDATA_OT_import_terrain.bl_idname, text='WoW 1.12 terrain (wowdata_terrain.json from WoW Data Studio)')


def menu_export(self, context):
    self.layout.operator(WOWDATA_OT_export_terrain.bl_idname, text='WoW 1.12 terrain for WoW Data Studio (.json)')


CLASSES = (WOWDATA_OT_import_terrain, WOWDATA_OT_sync_edges, WOWDATA_OT_check_seams, WOWDATA_OT_terrain_shading,
           WOWDATA_OT_toggle_labels, WOWDATA_OT_export_terrain, WOWDATA_PT_terrain)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.TOPBAR_MT_file_import.append(menu_import)
    bpy.types.TOPBAR_MT_file_export.append(menu_export)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(menu_export)
    bpy.types.TOPBAR_MT_file_import.remove(menu_import)
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)
