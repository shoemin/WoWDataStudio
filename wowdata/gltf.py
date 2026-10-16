"""M2 -> glTF 2.0 (.gltf + .bin + PNG textures) plus a sidecar JSON with everything glTF cannot hold.

Coordinates: the M2 is right-handed with Z up (yards). glTF is right-handed with Y up. We map
    glTF (x, y, z) = M2 (x, z, -y)
i.e. a -90 degree rotation about X. Blender's glTF importer applies the inverse (its "+Y up" conversion), so in
Blender every coordinate equals the M2's again: Z up, model facing +X, 1 Blender unit = 1 yard. Vectors
(positions, normals, pivots, translations) use the mapping above; quaternions (x, y, z, w) -> (x, z, -y, w);
scales (sx, sy, sz) -> (sx, sz, sy).

Skeleton: one glTF node per M2 bone, named bone_<index>[_<key bone name>]. A bone's rest pose is its pivot
(M2 bones have no rest rotation): node translation = pivot - parent pivot, inverse bind matrix = T(-pivot).
The client's bone matrix T(pivot) * T(t) * R * S * T(-pivot) (composed with the parent's) then equals the glTF
skin matrix exactly, with node translation = pivot - parent pivot + t, rotation = R, scale = S.

Meshes: view 0 (the highest detail), one glTF mesh + node per submesh (named <model>_<geoset>_<submesh>), skinned
with up to 4 joints/weights per vertex (the M2's own 8-bit weights, kept exactly). The material is the first
texture unit of the submesh; further texture units (extra passes / layers) are listed in the mesh extras and the
sidecar. Geosets hidden by default (characters) carry extras.wow_default_visible = false.

Animations: one glTF animation per sequence, named after AnimationData (e.g. Stand, Walk, Attack1H; a variant
gets _<sub id>), times in seconds from the sequence start. See m2/anim.py for how keys are taken per sequence.
"""
import base64
import datetime
import hashlib
import io
import json
import os
import re
import struct

import numpy as np
from PIL import Image

from .m2 import M2
from .m2.anim import animation_names, sequence_keys
from .m2.schema import ATTACHMENT_NAMES, BLEND_MODES, KEY_BONES, RENDER_FLAG_BITS, TEXTURE_TYPES
from .m2.validate import validate

C = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], np.float64)   # glTF = C @ m2 (rows: x, z, -y)
SIDECAR_SUFFIX = '.wowm2.json'
ALPHA_KEY_CUTOFF = 224.0 / 255.0     # M2Scene_SetBatchBlendState: alpha ref = alpha * 224

FLOAT, UBYTE, USHORT, UINT = 5126, 5121, 5123, 5125
ARRAY_BUFFER, ELEMENT_ARRAY_BUFFER = 34962, 34963
REPEAT, CLAMP = 10497, 33071


def to_gltf_vec(v):
    v = np.asarray(v, np.float64)
    return np.stack([v[..., 0], v[..., 2], -v[..., 1]], -1)


def to_gltf_quat(q):
    q = np.asarray(q, np.float64)
    return np.stack([q[..., 0], q[..., 2], -q[..., 1], q[..., 3]], -1)


def to_gltf_scale(s):
    s = np.asarray(s, np.float64)
    return np.stack([s[..., 0], s[..., 2], s[..., 1]], -1)


def safe_name(s):
    return re.sub(r'[^A-Za-z0-9_.-]+', '_', s).strip('_') or 'model'


class Builder:
    """Accumulates glTF JSON and one binary buffer."""

    def __init__(self):
        self.g = {'asset': {'version': '2.0', 'generator': 'wowdata (WoWDataTool)'}, 'scene': 0,
                  'scenes': [{'nodes': []}], 'nodes': [], 'meshes': [], 'materials': [], 'textures': [],
                  'images': [], 'samplers': [], 'skins': [], 'animations': [], 'accessors': [], 'bufferViews': [],
                  'buffers': []}
        self.bin = bytearray()
        self.images = []          # (name, png bytes)
        self._dedupe = {}
        self.extensions = set()

    def view(self, data, target=None):
        while len(self.bin) % 4:
            self.bin.append(0)
        bv = {'buffer': 0, 'byteOffset': len(self.bin), 'byteLength': len(data)}
        if target:
            bv['target'] = target
        self.bin.extend(data)
        self.g['bufferViews'].append(bv)
        return len(self.g['bufferViews']) - 1

    def accessor(self, arr, ctype, atype, target=None, normalized=False, minmax=False, dedupe=False):
        arr = np.ascontiguousarray(arr)
        data = arr.tobytes()
        key = None
        if dedupe:
            key = (hashlib.sha1(data).hexdigest(), ctype, atype, normalized)
            if key in self._dedupe:
                return self._dedupe[key]
        count = arr.shape[0]
        acc = {'bufferView': self.view(data, target), 'componentType': ctype, 'count': int(count), 'type': atype}
        if normalized:
            acc['normalized'] = True
        if minmax:
            flat = arr.reshape(count, -1)
            acc['min'] = [float(x) for x in flat.min(0)]
            acc['max'] = [float(x) for x in flat.max(0)]
        self.g['accessors'].append(acc)
        idx = len(self.g['accessors']) - 1
        if key:
            self._dedupe[key] = idx
        return idx

    def node(self, node):
        self.g['nodes'].append(node)
        return len(self.g['nodes']) - 1

    def image(self, name, rgba):
        buf = io.BytesIO()
        mode = 'RGBA' if (rgba[..., 3] < 255).any() else 'RGB'
        Image.fromarray(np.ascontiguousarray(rgba if mode == 'RGBA' else rgba[..., :3]), mode).save(buf, 'PNG')
        self.images.append((name, buf.getvalue()))
        self.g['images'].append({'name': name})
        return len(self.g['images']) - 1

    def finish(self, mode, bin_name=None, image_dir='textures'):
        """mode 'gltf': (json dict, bin bytes, [(file name, png)]) with external files;
        mode 'glb': GLB bytes with everything embedded."""
        g = json.loads(json.dumps(self.g))
        for k in [k for k, v in g.items() if isinstance(v, list) and not v]:
            del g[k]
        if self.extensions:
            g['extensionsUsed'] = sorted(self.extensions)
        files = []
        bin_ = bytearray(self.bin)
        if mode == 'glb':
            for i, (name, png) in enumerate(self.images):
                while len(bin_) % 4:
                    bin_.append(0)
                g.setdefault('bufferViews', []).append({'buffer': 0, 'byteOffset': len(bin_), 'byteLength': len(png)})
                bin_.extend(png)
                g['images'][i] = {'name': name, 'bufferView': len(g['bufferViews']) - 1, 'mimeType': 'image/png'}
        else:
            for i, (name, png) in enumerate(self.images):
                fn = '%s/%s.png' % (image_dir, name)
                g['images'][i] = {'name': name, 'uri': fn}
                files.append((fn, png))
        while len(bin_) % 4:
            bin_.append(0)
        g['buffers'] = [{'byteLength': len(bin_)}]
        if mode == 'glb':
            js = json.dumps(g, separators=(',', ':')).encode('utf-8')
            js += b' ' * (-len(js) % 4)
            out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bin_))
            out += struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(bin_), 0x004E4942) + bytes(bin_)
            return out
        g['buffers'][0]['uri'] = bin_name
        return g, bytes(bin_), files


def _matrix_translate(t):
    m = np.eye(4)
    m[:3, 3] = t
    return m


def _jsonable(x):
    """numpy / bytes -> JSON types (floats from float32 stay exact when parsed back as float32)."""
    if isinstance(x, (bytes, np.bytes_)):
        s = bytes(x)
        return s.rstrip(b'\0').decode('latin-1')
    if isinstance(x, np.ndarray):
        if x.dtype.names:
            return [_jsonable(r) for r in x]
        return x.tolist()
    if isinstance(x, np.void):
        return {n: _jsonable(x[n]) for n in x.dtype.names}
    if isinstance(x, np.generic):
        return x.item()
    return x


def dump_recarray(arr, skip_tracks=False, skip=()):
    """A RecArray as a list of dicts (plain fields, sub arrays, tracks as {interpolation, global_sequence, ranges,
    timestamps, values}); with skip_tracks only the track headers and key counts."""
    out = []
    if arr is None or len(arr) == 0:
        return out
    rec = arr.rec
    track_names = [t for t, _ in rec.tracks]
    for i in range(len(arr)):
        it = arr[i]
        d = {}
        for n in rec.plain:
            if n in skip:
                continue
            d[n] = _jsonable(arr.flat[n][i])
        for a, elem in rec.arrays:
            base = a.split('.')[0]
            if base in track_names or a in skip:
                continue
            v = arr.sub[a][i]
            d[a] = dump_recarray(v) if hasattr(v, 'rec') else _jsonable(v)
        for t in it.tracks():
            td = {'interpolation': t.interpolation, 'global_sequence': t.global_sequence, 'keys': len(t)}
            if not skip_tracks:
                td['ranges'] = _jsonable(t.ranges)
                td['timestamps'] = _jsonable(t.timestamps)
                if t.values is not None:
                    td['values'] = _jsonable(t.values)
            else:
                td['ranges'] = len(t.ranges)
            d[t.name] = td
        out.append(d)
    return out


def m2_dump(m):
    """Everything in the M2 except the vertices and the bone keyframes (both are in the glTF, and the exact bytes
    stay in the original file, identified by its SHA-256)."""
    h = m.header
    d = {'version': m.version, 'name': m.model_name, 'global_flags': int(m.global_flags)}
    for n in h.rec.plain:
        if n not in ('magic', 'version', 'global_flags'):
            d[n] = _jsonable(h.flat[n][0])
    for a, elem in h.rec.arrays:
        if a in ('name', 'vertices'):
            continue
        v = h.sub[a][0]
        if a == 'bones':
            d[a] = dump_recarray(v, skip_tracks=True)
        elif a == 'views':     # index lists are rebuilt from the mesh on re-import; counts kept here
            d[a] = dump_recarray(v, skip=('indices', 'triangles', 'properties'))
            for k, vw in enumerate(d[a]):
                vw['counts'] = {x: len(v.sub[x][k]) for x in ('indices', 'triangles', 'properties')}
        elif hasattr(v, 'rec'):
            d[a] = dump_recarray(v)
        else:
            d[a] = _jsonable(v)
    d['vertex_count'] = len(m.vertices)
    return d


class Exporter:
    def __init__(self, client, m, ctx, anim_names=None, view_index=0, animations=True, max_sequences=None):
        self.client = client
        self.m = m
        self.ctx = ctx
        self.view_index = view_index
        self.with_anims = animations
        self.max_sequences = max_sequences
        self.anim_names = anim_names if anim_names is not None else animation_names(client)
        self.b = Builder()
        self.report = {'meshes': [], 'materials': [], 'textures': [], 'animations': [], 'notes': list(ctx.notes) if ctx else []}
        self.name = safe_name(m.model_name or os.path.splitext(os.path.basename(ctx.model_path))[0])

    # ------------------------------------------------------------------ textures / materials
    def _texture_image(self, ti):
        """(rgba, description, archive path) for M2 texture i, resolving replaceable slots from the context."""
        m = self.m
        typ = int(m.textures['type'][ti])
        if typ == 0:
            p = m.texture_name(ti)
            from .modelctx import load_blp
            img = load_blp(self.client, p) if p else None
            if img is not None:
                return img, p, p
            return None, 'missing file %s' % p, p
        t = self.ctx.slots.get(typ) if self.ctx else None
        if t is not None:
            return t.rgba, t.source, t.path
        return None, 'replaceable slot %d (%s) not resolved' % (typ, TEXTURE_TYPES.get(typ, '?')), None

    def _gltf_texture(self, ti):
        key = ti
        if key in self._tex_cache:
            return self._tex_cache[key]
        m = self.m
        rgba, desc, path = self._texture_image(ti)
        typ = int(m.textures['type'][ti])
        flags = int(m.textures['flags'][ti])
        if rgba is None:
            if typ == 0:     # a file the model names is missing: make it obvious
                rgba = np.full((8, 8, 4), (200, 0, 200, 255), np.uint8)
                rgba[::2, ::2] = (40, 40, 40, 255)
                what = 'magenta placeholder'
            else:            # an empty replaceable slot (e.g. no cape worn): neutral grey
                rgba = np.full((8, 8, 4), (128, 128, 128, 255), np.uint8)
                what = 'grey placeholder'
            self.report['notes'].append('texture %d: %s (%s)' % (ti, desc, what))
            name = 'missing_%d_type%d' % (ti, typ)
        else:
            name = safe_name(os.path.splitext(os.path.basename(path))[0]) if path else '%s_slot%d' % (self.name, typ)
        name = self._unique_image_name(name)
        img = self.b.image(name, rgba)
        samp = {'magFilter': 9729, 'minFilter': 9987, 'wrapS': REPEAT if flags & 1 else CLAMP, 'wrapT': REPEAT if flags & 2 else CLAMP}
        if samp not in self.b.g['samplers']:
            self.b.g['samplers'].append(samp)
        s_idx = self.b.g['samplers'].index(samp)
        self.b.g['textures'].append({'source': img, 'sampler': s_idx, 'name': name})
        idx = len(self.b.g['textures']) - 1
        self.report['textures'].append({'texture': ti, 'type': typ, 'type_name': TEXTURE_TYPES.get(typ, '?'), 'flags': flags,
                                        'file_in_m2': m.texture_name(ti), 'resolved': desc, 'png': name + '.png',
                                        'size': list(rgba.shape[:2])})
        self._tex_cache[key] = (idx, rgba)
        return idx, rgba

    def _unique_image_name(self, name):
        used = {n for n, _ in self.b.images}
        if name not in used:
            return name
        i = 2
        while '%s_%d' % (name, i) in used:
            i += 1
        return '%s_%d' % (name, i)

    def _static_weight(self, tu):
        """Static opacity of a texture unit: transparency weight and colour alpha at time 0 of sequence 0."""
        m = self.m
        w = 1.0
        tl = int(tu['transparency_lookup'])
        if tl < len(m.transparency_lookup):
            ti = int(m.transparency_lookup[tl])
            if ti < len(m.transparency):
                t = m.transparency[ti].weight
                if len(t):
                    w *= float(t.values[0]) / 32767.0
        c = int(tu['color'])
        if 0 <= c < len(m.colors):
            a = m.colors[c].alpha
            if len(a):
                w *= float(a.values[0]) / 32767.0
        return max(0.0, min(1.0, w))

    def material(self, tu):
        m = self.m
        rfi = int(tu['render_flags'])
        flags, blend = (int(m.render_flags['flags'][rfi]), int(m.render_flags['blend'][rfi])) if rfi < len(m.render_flags) else (0, 0)
        tl = int(tu['texture_lookup'])
        texs = [int(m.texture_lookup[tl + k]) for k in range(max(1, int(tu['texture_count']))) if tl + k < len(m.texture_lookup)]
        key = (rfi, tuple(texs), int(tu['color']), int(tu['transparency_lookup']))
        if key in self._mat_cache:
            return self._mat_cache[key]
        mat = {'name': '', 'pbrMetallicRoughness': {'metallicFactor': 0.0, 'roughnessFactor': 1.0}}
        base_name = ''
        if texs and texs[0] < len(m.textures):
            tidx, rgba = self._gltf_texture(texs[0])
            mat['pbrMetallicRoughness']['baseColorTexture'] = {'index': tidx}
            base_name = self.b.g['textures'][tidx]['name']
        weight = self._static_weight(tu)
        if blend == 0:
            mat['alphaMode'] = 'OPAQUE'
        elif blend == 1:
            mat['alphaMode'] = 'MASK'
            mat['alphaCutoff'] = ALPHA_KEY_CUTOFF
        else:
            mat['alphaMode'] = 'BLEND'
            if weight < 1.0:
                mat['pbrMetallicRoughness']['baseColorFactor'] = [1.0, 1.0, 1.0, weight]
        if flags & 4:
            mat['doubleSided'] = True
        if flags & 1:
            mat['extensions'] = {'KHR_materials_unlit': {}}
            self.b.extensions.add('KHR_materials_unlit')
        mat['name'] = '%s_mat%d_%s' % (self.name, len(self.b.g['materials']), base_name or 'untextured')
        mat['extras'] = {
            'wow_render_flags_index': rfi, 'wow_render_flags': flags,
            'wow_render_flag_names': [n for bit, n in RENDER_FLAG_BITS.items() if flags & bit],
            'wow_blend': blend, 'wow_blend_name': BLEND_MODES.get(blend, '?'),
            'wow_textures': texs, 'wow_static_opacity': round(weight, 4),
            'wow_color': int(tu['color']), 'wow_transparency_lookup': int(tu['transparency_lookup']),
            'wow_texture_transform_lookup': int(tu['texture_transform_lookup']),
        }
        if len(texs) > 1:
            mat['extras']['wow_note'] = 'second texture layer (%s) not shown: glTF has one base colour texture' % texs[1:]
        self.b.g['materials'].append(mat)
        idx = len(self.b.g['materials']) - 1
        self.report['materials'].append({'name': mat['name'], 'blend': BLEND_MODES.get(blend, blend), 'flags': mat['extras']['wow_render_flag_names'],
                                         'textures': texs})
        self._mat_cache[key] = idx
        return idx

    # ------------------------------------------------------------------ skeleton
    def skeleton(self, root):
        m = self.m
        nb = len(m.bones)
        self.bone_nodes = []
        if nb == 0:
            return None
        piv = m.bones['pivot'].astype(np.float64)
        par = m.bones['parent'].astype(np.int64)
        kb = m.bones['key_bone_id'].astype(np.int64)
        self.rest_t = np.zeros((nb, 3))
        for i in range(nb):
            p = par[i]
            local = piv[i] - (piv[p] if 0 <= p < nb else 0)
            self.rest_t[i] = to_gltf_vec(local)
            name = 'bone_%03d' % i + ('_' + KEY_BONES[kb[i]] if 0 <= kb[i] < len(KEY_BONES) else '')
            node = {'name': name, 'translation': [float(x) for x in self.rest_t[i]],
                    'extras': {'wow_bone': i, 'wow_key_bone_id': int(kb[i]), 'wow_flags': int(m.bones['flags'][i]),
                               'wow_parent': int(p), 'wow_submesh_id': int(m.bones['submesh_id'][i]),
                               'wow_pivot': [float(x) for x in piv[i]]}}
            self.bone_nodes.append(self.b.node(node))
        for i in range(nb):
            p = par[i]
            if 0 <= p < nb:
                self.b.g['nodes'][self.bone_nodes[p]].setdefault('children', []).append(self.bone_nodes[i])
            else:
                self.b.g['nodes'][root].setdefault('children', []).append(self.bone_nodes[i])
        ibm = np.zeros((nb, 16), np.float32)
        for i in range(nb):
            ibm[i] = _matrix_translate(-to_gltf_vec(piv[i])).T.reshape(16)     # column-major
        skin = {'name': self.name + '_skeleton', 'joints': self.bone_nodes, 'skeleton': root,
                'inverseBindMatrices': self.b.accessor(ibm.reshape(nb, 16), FLOAT, 'MAT4')}
        self.b.g['skins'].append(skin)
        return 0

    def attachments(self):
        m = self.m
        piv = m.bones['pivot'].astype(np.float64) if len(m.bones) else None
        for i in range(len(m.attachments)):
            a = m.attachments[i]
            bone = int(a.bone)
            aid = int(a.id)
            pos = np.asarray(a.position, np.float64)
            if piv is not None and bone < len(piv):
                parent = self.bone_nodes[bone]
                local = pos - piv[bone]
            else:
                parent, local = self.root, pos
            n = self.b.node({'name': 'ATT_%02d_%s' % (aid, ATTACHMENT_NAMES.get(aid, 'Attachment')),
                             'translation': [float(x) for x in to_gltf_vec(local)],
                             'extras': {'wow_attachment': i, 'wow_attachment_id': aid, 'wow_bone': bone}})
            self.b.g['nodes'][parent].setdefault('children', []).append(n)

    # ------------------------------------------------------------------ meshes
    def meshes(self, root, skin):
        m = self.m
        if not len(m.views):
            self.report['notes'].append('no views: nothing to draw')
            return
        view = m.views[min(self.view_index, len(m.views) - 1)]
        ind, tri = view.indices.astype(np.int64), view.triangles.astype(np.int64)
        subs, tus = view.submeshes, view.texture_units
        verts = m.vertices
        pos_all = to_gltf_vec(verts['position']).astype(np.float32)
        nrm_all = to_gltf_vec(verts['normal'])
        ln = np.linalg.norm(nrm_all, axis=1, keepdims=True)
        bad = ln[:, 0] < 1e-6
        nrm_all = np.where(bad[:, None], np.array([0.0, 1.0, 0.0]), nrm_all / np.where(ln > 1e-6, ln, 1)).astype(np.float32)
        uv_all = verts['uv'].astype(np.float32)
        joints_all = verts['bones'].astype(np.uint8)
        w_all = verts['weights'].astype(np.int64)
        s = w_all.sum(1)
        fix = s != 255
        if fix.any():   # glTF wants normalised weights summing to 1: rescale the few that do not (none in 1.12 files)
            for k in np.nonzero(fix)[0]:
                if s[k] == 0:
                    w_all[k] = (255, 0, 0, 0)
                else:
                    w = np.floor(w_all[k] * 255.0 / s[k]).astype(np.int64)
                    w[np.argmax(w)] += 255 - w.sum()
                    w_all[k] = w
        weights_all = w_all.astype(np.uint8)
        tus_by_sub = {}
        for ti in range(len(tus)):
            tus_by_sub.setdefault(int(tus['submesh'][ti]), []).append(ti)
        vis = self.ctx.geosets if self.ctx else None
        for si in range(len(subs)):
            sm = subs[si]
            vs, vc, ts, tc = int(sm.vertex_start), int(sm.vertex_count), int(sm.index_start), int(sm.index_count)
            if tc == 0:
                continue
            t = tri[ts:ts + tc]
            view_verts = np.unique(np.concatenate([np.arange(vs, vs + vc), t]))
            local = np.searchsorted(view_verts, t).astype(np.uint32)
            model_verts = ind[view_verts]
            attrs = {
                'POSITION': self.b.accessor(pos_all[model_verts], FLOAT, 'VEC3', ARRAY_BUFFER, minmax=True),
                'NORMAL': self.b.accessor(nrm_all[model_verts], FLOAT, 'VEC3', ARRAY_BUFFER),
                'TEXCOORD_0': self.b.accessor(uv_all[model_verts], FLOAT, 'VEC2', ARRAY_BUFFER),
            }
            if skin is not None:
                attrs['JOINTS_0'] = self.b.accessor(joints_all[model_verts], UBYTE, 'VEC4', ARRAY_BUFFER)
                attrs['WEIGHTS_0'] = self.b.accessor(weights_all[model_verts], UBYTE, 'VEC4', ARRAY_BUFFER, normalized=True)
            idx_type = USHORT if len(view_verts) < 65536 else UINT
            prim = {'attributes': attrs, 'mode': 4,
                    'indices': self.b.accessor(local.astype(np.uint16 if idx_type == USHORT else np.uint32), idx_type, 'SCALAR', ELEMENT_ARRAY_BUFFER)}
            units = tus_by_sub.get(si, [])
            if units:
                prim['material'] = self.material(tus.flat[units[0]])
            geoset = int(sm.geoset_id)
            visible = vis is None or geoset in vis
            name = '%s_g%04d_s%02d' % (self.name, geoset, si)
            extras = {'wow_submesh': si, 'wow_geoset_id': geoset, 'wow_view': self.view_index,
                      'wow_texture_units': units, 'wow_default_visible': visible,
                      'wow_center_bone': int(sm.center_bone), 'wow_bone_count': int(sm.bone_count)}
            self.b.g['meshes'].append({'name': name, 'primitives': [prim], 'extras': extras})
            node = {'name': name, 'mesh': len(self.b.g['meshes']) - 1, 'extras': dict(extras)}
            if skin is not None:
                node['skin'] = skin
            n = self.b.node(node)
            self.b.g['nodes'][root].setdefault('children', []).append(n)
            self.report['meshes'].append({'node': name, 'submesh': si, 'geoset': geoset, 'vertices': int(len(view_verts)),
                                          'triangles': int(tc // 3), 'visible': visible, 'texture_units': units,
                                          'view_vertices': view_verts.tolist(), 'model_vertices': model_verts.tolist()})

    def collision(self, root):
        m = self.m
        ct, cv = m.collision_triangles, m.collision_vertices
        if not len(ct) or not len(cv):
            return
        pos = to_gltf_vec(cv).astype(np.float32)
        prim = {'attributes': {'POSITION': self.b.accessor(pos, FLOAT, 'VEC3', ARRAY_BUFFER, minmax=True)},
                'indices': self.b.accessor(ct.astype(np.uint16), USHORT, 'SCALAR', ELEMENT_ARRAY_BUFFER), 'mode': 4}
        extras = {'wow_collision': True, 'wow_default_visible': False}
        self.b.g['meshes'].append({'name': 'COL_' + self.name, 'primitives': [prim], 'extras': extras})
        n = self.b.node({'name': 'COL_' + self.name, 'mesh': len(self.b.g['meshes']) - 1, 'extras': extras})
        self.b.g['nodes'][root].setdefault('children', []).append(n)

    # ------------------------------------------------------------------ animations
    def animations(self):
        m = self.m
        nb = len(m.bones)
        if not nb or not len(m.sequences):
            return
        gseq = m.global_sequences
        animated = []
        for i in range(nb):
            b = m.bones[i]
            if len(b.translation) or len(b.rotation) or len(b.scale):
                animated.append(i)
        used = set()
        seqs = m.sequences
        count = len(seqs) if self.max_sequences is None else min(len(seqs), self.max_sequences)
        for si in range(count):
            aid, sub = int(seqs['anim_id'][si]), int(seqs['sub_id'][si])
            base = self.anim_names.get(aid, 'Anim%d' % aid)
            name = base if sub == 0 else '%s_%d' % (base, sub)
            if name in used:
                k = 2
                while '%s#%d' % (name, k) in used:
                    k += 1
                name = '%s#%d' % (name, k)
            used.add(name)
            start, end = int(seqs['start'][si]), int(seqs['end'][si])
            samplers, channels = [], []
            for bi in animated:
                b = m.bones[bi]
                for path, track in (('translation', b.translation), ('rotation', b.rotation), ('scale', b.scale)):
                    if not len(track):
                        continue
                    keys = sequence_keys(track, si, start, end, gseq, quaternion=(path == 'rotation'))
                    if keys is None:
                        if path == 'translation':
                            t, v = np.array([0.0]), np.zeros((1, 3))
                        elif path == 'rotation':
                            t, v = np.array([0.0]), np.array([[0.0, 0.0, 0.0, 1.0]])
                        else:
                            t, v = np.array([0.0]), np.ones((1, 3))
                        interp = 'STEP'
                    else:
                        t, v = keys
                        interp = 'STEP' if track.interpolation == 0 else 'LINEAR'
                    if path == 'translation':
                        out = to_gltf_vec(v) + self.rest_t[bi]
                    elif path == 'rotation':
                        out = to_gltf_quat(v)
                        n = np.linalg.norm(out, axis=1, keepdims=True)
                        out = np.where(n > 1e-9, out / np.where(n > 1e-9, n, 1), np.array([0, 0, 0, 1.0]))
                        for k in range(1, len(out)):          # shortest path for slerp
                            if np.dot(out[k], out[k - 1]) < 0:
                                out[k] = -out[k]
                    else:
                        out = to_gltf_scale(v)
                    inp = self.b.accessor((t / 1000.0).astype(np.float32).reshape(-1), FLOAT, 'SCALAR', minmax=True, dedupe=True)
                    outp = self.b.accessor(out.astype(np.float32), FLOAT, 'VEC4' if path == 'rotation' else 'VEC3', dedupe=True)
                    samplers.append({'input': inp, 'output': outp, 'interpolation': interp})
                    channels.append({'sampler': len(samplers) - 1, 'target': {'node': self.bone_nodes[bi], 'path': path}})
            if not channels:
                continue
            seq_extras = {k: _jsonable(seqs.flat[k][si]) for k in seqs.rec.plain}
            self.b.g['animations'].append({'name': name, 'samplers': samplers, 'channels': channels,
                                           'extras': {'wow_sequence': si, 'wow_anim_id': aid, 'wow_sub_id': sub,
                                                      'wow_anim_name': base, 'wow_sequence_fields': seq_extras}})
            self.report['animations'].append({'name': name, 'sequence': si, 'anim_id': aid, 'sub_id': sub,
                                              'duration_ms': end - start, 'channels': len(channels)})

    # ------------------------------------------------------------------ all
    def build(self):
        m = self.m
        root_extras = {'wow_model': self.ctx.model_path if self.ctx else m.model_name, 'wow_name': m.model_name,
                       'wow_coordinates': 'glTF (x, y, z) = M2 (x, z, -y); Blender import gives back M2 coordinates (Z up, faces +X)',
                       'wow_sidecar': self.name + SIDECAR_SUFFIX}
        self.root = self.b.node({'name': self.name, 'extras': root_extras})
        self.b.g['scenes'][0]['nodes'] = [self.root]
        self.b.g['scenes'][0]['name'] = self.name
        self._tex_cache, self._mat_cache = {}, {}
        skin = self.skeleton(self.root)
        self.meshes(self.root, skin)
        self.collision(self.root)
        if len(m.bones):
            self.attachments()
        if self.with_anims:
            self.animations()
        return self.b

    def sidecar(self, source):
        m = self.m
        return {
            'format': 'wowdata-m2-sidecar', 'format_version': 1,
            'created': datetime.datetime.now().isoformat(timespec='seconds'),
            'source': source,
            'context': self.ctx.describe() if self.ctx else None,
            'coordinates': {
                'm2': 'right-handed, Z up, model faces +X, units = yards',
                'gltf': 'right-handed, Y up',
                'mapping': 'glTF (x, y, z) = M2 (x, z, -y); quaternion (x, y, z, w) -> (x, z, -y, w); scale (sx, sy, sz) -> (sx, sz, sy)',
                'blender': "Blender's glTF importer converts Y-up back to Z-up, so Blender coordinates equal M2 coordinates",
            },
            'export': {
                'view': self.view_index,
                'bone_nodes': ['bone_%03d' % i for i in range(len(m.bones))],
                'meshes': self.report['meshes'],
                'materials': self.report['materials'],
                'textures': self.report['textures'],
                'animations': self.report['animations'],
                'notes': self.report['notes'],
                'not_in_gltf': ['second texture layers and extra texture-unit passes', 'colour / transparency / texture-transform '
                                'animation (static values only)', 'particles, ribbons, lights, cameras, events (data below)',
                                'views 1-3 (lower LODs; data below)', 'global sequences as independent clocks (unrolled into each animation)'],
            },
            'm2': m2_dump(m),
            'validation': [i.as_dict() for i in validate(m) if i.level != 'note'],
        }


def export(client, ctx, out_dir, view_index=0, animations=True, glb=False, max_sequences=None, anim_names=None):
    """Export a resolved model (modelctx.ModelContext) to <out_dir>\\<Name>\\. Returns a report dict."""
    data = client.read(ctx.model_path)
    if data is None:
        raise FileNotFoundError(ctx.model_path)
    m = M2.parse(data)
    ex = Exporter(client, m, ctx, anim_names=anim_names, view_index=view_index, animations=animations, max_sequences=max_sequences)
    b = ex.build()
    src = client.find(ctx.model_path)
    source = {'archive_path': ctx.model_path, 'archive': src.name if src else None, 'sha256': hashlib.sha256(data).hexdigest(),
              'size': len(data), 'm2_version': m.version, 'm2_name': m.model_name}
    folder = os.path.join(out_dir, ex.name)
    os.makedirs(os.path.join(folder, 'textures'), exist_ok=True)
    g, bin_, files = b.finish('gltf', bin_name=ex.name + '.bin')
    g['extras'] = {'wow_sidecar': ex.name + SIDECAR_SUFFIX, 'wow_source': source}
    gltf_path = os.path.join(folder, ex.name + '.gltf')
    with open(gltf_path, 'w', encoding='utf-8') as f:
        json.dump(g, f, separators=(',', ':'))
    with open(os.path.join(folder, ex.name + '.bin'), 'wb') as f:
        f.write(bin_)
    for fn, png in files:
        with open(os.path.join(folder, *fn.split('/')), 'wb') as f:
            f.write(png)
    side = ex.sidecar(source)
    side_path = os.path.join(folder, ex.name + SIDECAR_SUFFIX)
    with open(side_path, 'w', encoding='utf-8') as f:
        json.dump(side, f, indent=1)
    rep = {'gltf': gltf_path, 'sidecar': side_path, 'folder': folder, 'name': ex.name,
           'bones': len(m.bones), 'sequences': len(m.sequences), 'animations': len(ex.report['animations']),
           'meshes': ex.report['meshes'], 'materials': ex.report['materials'], 'textures': ex.report['textures'],
           'notes': ex.report['notes'], 'vertices': len(m.vertices),
           'errors': [i for i in side['validation'] if i['level'] == 'error']}
    if glb:
        rep['glb'] = b.finish('glb')
    return rep


def glb_base64(glb_bytes):
    return base64.b64encode(glb_bytes).decode('ascii')
