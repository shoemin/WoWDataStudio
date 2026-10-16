"""M2 checks: the client's own bounds rules plus the cross-reference checks the client does not make.

M2Shared_FixupHeader @0071cdf0 only checks the magic, the version and that every array lies inside the file
(the parser enforces that). Everything below catches what would otherwise fail at render time or crash.

validate(m) -> [Issue]. Levels:
  'error'    the client would read out of bounds or use a missing record (a broken file);
  'warning'  legal but unusual data;
  'note'     harmless oddities found in Blizzard's own files (e.g. more key ranges than sequences).
"""
import numpy as np

NONE16 = 0xFFFF


class Issue:
    def __init__(self, level, where, msg):
        self.level, self.where, self.msg = level, where, msg

    def __repr__(self):
        return '%s %s: %s' % (self.level, self.where, self.msg)

    def as_dict(self):
        return {'level': self.level, 'where': self.where, 'message': self.msg}


def _u16(v):
    return int(v) & 0xFFFF


def validate(m):
    out = []

    def err(where, msg):
        out.append(Issue('error', where, msg))

    def warn(where, msg):
        out.append(Issue('warning', where, msg))

    def note(where, msg):
        out.append(Issue('note', where, msg))

    nseq = len(m.sequences)
    nbones = len(m.bones)
    nverts = len(m.vertices)
    ngs = len(m.global_sequences)
    ntex = len(m.textures)

    def idx(where, values, limit, allow_none=False, what='index'):
        a = np.asarray(values)
        v = a.astype(np.int64).reshape(-1)
        if a.dtype.itemsize == 2:
            v &= 0xFFFF          # int16 -1 and uint16 0xffff both mean "none"
        if allow_none:
            v = v[v != NONE16]
        bad = v[(v < 0) | (v >= limit)]
        if len(bad):
            err(where, '%d %s(es) out of range 0..%d (e.g. %d)' % (len(bad), what, limit - 1, int(bad[0])))

    def track(where, t):
        n = len(t)
        # bone-style tracks: {first, last} inclusive (M2Model_FindTrackKeys); events: [start, end) (M2Model_Update)
        exclusive = t.values is None
        vals = t.values
        if vals is not None and len(vals) != n:
            err(where, '%d values for %d timestamps' % (len(vals), n))
        if t.interpolation > 3:
            err(where, 'interpolation type %d' % t.interpolation)
        elif t.interpolation >= 2 and not (vals is not None and vals.ndim == 2 and vals.shape[1] in (3, 9) and where.startswith('cameras')):
            # camera tracks store spline keys (value + tangents) and use hermite; elsewhere the evaluators
            # (M2Model_EvalQuatTrack / EvalFloatTrack) only test "interpolation == 0" and otherwise lerp
            warn(where, 'interpolation type %d outside a camera track (the client lerps it)' % t.interpolation)
        gs = t.global_sequence
        if gs != -1 and not 0 <= gs < ngs:
            err(where, 'global sequence %d of %d' % (gs, ngs))
        r = t.ranges
        if len(r):
            if gs == -1 and len(r) < nseq:
                err(where, '%d ranges for %d sequences: the client reads ranges[sequence] past the end' % (len(r), nseq))
            elif gs == -1 and len(r) > nseq:
                note(where, '%d ranges for %d sequences (the extra ones are never read)' % (len(r), nseq))
            used = r[:nseq] if gs == -1 else r
            ok = used[(used[:, 0] < used[:, 1])]
            if n and len(ok) and (ok[:, 1] > (n if exclusive else n - 1)).any():
                err(where, 'a key range ends past the %d keys' % n)
            if n == 0 and (r != 0).any():
                warn(where, 'ranges without keys')
        if n > 1:
            ts = t.timestamps.astype(np.int64)
            if gs != -1 or not len(r):
                if (np.diff(ts) < 0).any():
                    warn(where, 'timestamps not sorted')
            else:
                for a, b in r:
                    if b > a and (np.diff(ts[a:b + 1]) < 0).any():
                        warn(where, 'timestamps not sorted inside range %d..%d' % (a, b))
                        break
        if vals is not None and len(vals) and vals.dtype.kind == 'f' and not np.isfinite(vals).all():
            err(where, 'non-finite values')

    def tracks_of(name, arr):
        for i in range(len(arr)):
            for t in arr[i].tracks():
                track('%s[%d].%s' % (name, i, t.name), t)

    # ---------------------------------------------------------------- sequences
    seq = m.sequences
    for i in range(nseq):
        s, e = int(seq['start'][i]), int(seq['end'][i])
        if e < s:
            err('sequences[%d]' % i, 'end %d before start %d' % (e, s))
        nv = int(seq['next_variant'][i])
        if nv != -1 and not 0 <= nv < nseq:
            err('sequences[%d]' % i, 'next_variant %d of %d' % (nv, nseq))
    idx('animation_lookup', m.animation_lookup, nseq, allow_none=True, what='sequence')
    if len(m.playable_lookup):
        fb = m.playable_lookup['fallback_id'].astype(np.int64)
        if (fb < 0).any():
            warn('playable_lookup', 'negative fallback animation ids')

    # ---------------------------------------------------------------- bones
    b = m.bones
    if nbones:
        par = b['parent'].astype(np.int64)
        for i, p in enumerate(par):
            if p != -1 and not 0 <= p < nbones:
                err('bones[%d]' % i, 'parent %d of %d' % (p, nbones))
            elif p >= i:
                warn('bones[%d]' % i, 'parent %d does not come before the bone' % p)
        kb = b['key_bone_id'].astype(np.int64)
        if ((kb < -1) | (kb > 26)).any():
            warn('bones', 'key bone ids outside -1..26')
        if not np.isfinite(b['pivot']).all():
            err('bones', 'non-finite pivots')
        tracks_of('bones', b)
        for i in range(nbones):
            q = b[i].rotation.values
            if q is not None and len(q):
                norm = np.linalg.norm(q.astype(np.float64), axis=1)
                if (np.abs(norm - 1) > 0.05).any():
                    warn('bones[%d].rotation' % i, 'quaternions not normalised (|q| %.3f..%.3f)' % (norm.min(), norm.max()))
    idx('key_bone_lookup', m.key_bone_lookup, max(nbones, 1), allow_none=True, what='bone')
    if len(m.key_bone_lookup) and nbones == 0:
        err('key_bone_lookup', 'entries but no bones')

    # ---------------------------------------------------------------- vertices
    v = m.vertices
    if nverts:
        if not (np.isfinite(v['position']).all() and np.isfinite(v['normal']).all() and np.isfinite(v['uv']).all()):
            err('vertices', 'non-finite position / normal / uv')
        w = v['weights'].astype(np.int64)
        sums = w.sum(1)
        if (sums != 255).any():
            warn('vertices', '%d vertices whose weights do not sum to 255 (%s)' % ((sums != 255).sum(), sorted(set(sums[sums != 255].tolist()))[:5]))
        used = v['bones'][w > 0]
        if nbones and len(used) and used.max() >= nbones:
            err('vertices', 'bone index %d of %d bones' % (used.max(), nbones))
    if nverts > 0xFFFF:
        err('vertices', '%d vertices: views index them with uint16' % nverts)

    # ---------------------------------------------------------------- views
    nbl = len(m.bone_lookup)
    nrf = len(m.render_flags)
    ntl = len(m.texture_lookup)
    for vi, view in enumerate(m.views):
        where = 'views[%d]' % vi
        ind, tri, props = view.indices, view.triangles, view.properties
        idx(where + '.indices', ind, max(nverts, 1), what='vertex')
        if len(tri) % 3:
            err(where + '.triangles', '%d indices is not a multiple of 3' % len(tri))
        idx(where + '.triangles', tri, max(len(ind), 1), what='view vertex')
        if len(props) != len(ind):
            err(where + '.properties', '%d entries for %d view vertices' % (len(props), len(ind)))
        subs = view.submeshes
        for si in range(len(subs)):
            s = subs[si]
            w2 = '%s.submeshes[%d]' % (where, si)
            vs, vc = int(s.vertex_start), int(s.vertex_count)
            ts, tc = int(s.index_start), int(s.index_count)
            if vs + vc > len(ind):
                err(w2, 'vertex range %d+%d past %d view vertices' % (vs, vc, len(ind)))
            if ts + tc > len(tri):
                err(w2, 'index range %d+%d past %d triangle indices' % (ts, tc, len(tri)))
            if tc % 3:
                err(w2, 'index count %d not a multiple of 3' % tc)
            if int(s.bone_start) + int(s.bone_count) > nbl:
                err(w2, 'bone palette %d+%d past %d bone_lookup entries' % (s.bone_start, s.bone_count, nbl))
            if int(s.level):
                warn(w2, 'level %d' % s.level)
            if tc and ts + tc <= len(tri):
                t = tri[ts:ts + tc].astype(np.int64)
                if len(t) and (t.min() < vs or t.max() >= vs + vc):
                    warn(w2, 'triangles use view vertices outside the submesh vertex range')
            if vc and vs + vc <= len(props) and int(s.bone_count):
                p = props[vs:vs + vc].astype(np.int64)
                vw = v['weights'][ind[vs:vs + vc]] if nverts else None
                if vw is not None and (p[vw > 0] >= int(s.bone_count)).any():
                    err(w2, 'vertex properties index past the submesh bone palette')
        tus = view.texture_units
        for ti in range(len(tus)):
            t = tus[ti]
            w2 = '%s.texture_units[%d]' % (where, ti)
            if int(t.submesh) >= len(subs):
                err(w2, 'submesh %d of %d' % (t.submesh, len(subs)))
            if int(t.render_flags) >= nrf:
                err(w2, 'render flags %d of %d' % (t.render_flags, nrf))
            if int(t.texture_lookup) + max(int(t.texture_count), 1) > ntl:
                err(w2, 'texture lookup %d+%d past %d' % (t.texture_lookup, t.texture_count, ntl))
            if int(t.texture_count) > 2:
                warn(w2, '%d texture layers (the client supports 2)' % t.texture_count)
            c = int(t.color)
            if c != -1 and not 0 <= c < len(m.colors):
                err(w2, 'color %d of %d' % (c, len(m.colors)))
            if int(t.transparency_lookup) >= len(m.transparency_lookup):
                err(w2, 'transparency lookup %d of %d' % (t.transparency_lookup, len(m.transparency_lookup)))
            ta = _u16(t.texture_transform_lookup)
            if ta != NONE16 and ta >= len(m.texture_transform_lookup):
                err(w2, 'texture transform lookup %d of %d' % (ta, len(m.texture_transform_lookup)))
            tl = _u16(t.tex_unit_lookup)
            if tl != NONE16 and tl >= len(m.tex_unit_lookup):
                err(w2, 'tex-unit lookup %d of %d' % (tl, len(m.tex_unit_lookup)))
    if len(m.views) == 0:
        warn('views', 'no views (nothing to draw)')
    idx('bone_lookup', m.bone_lookup, max(nbones, 1), what='bone')
    idx('texture_lookup', m.texture_lookup, max(ntex, 1), allow_none=True, what='texture')
    idx('transparency_lookup', m.transparency_lookup, max(len(m.transparency), 1), allow_none=True, what='transparency')
    idx('texture_transform_lookup', m.texture_transform_lookup, max(len(m.texture_transforms), 1), allow_none=True, what='transform')
    idx('replaceable_lookup', m.replaceable_lookup, max(ntex, 1), allow_none=True, what='texture')
    rf = m.render_flags
    if len(rf) and (rf['blend'] > 6).any():
        err('render_flags', 'blend mode > 6')

    # ---------------------------------------------------------------- textures, animated values
    for i in range(ntex):
        t = m.textures[i]
        if int(t.type) == 0 and not m.texture_name(i):
            warn('textures[%d]' % i, 'type 0 (file) without a file name')
    tracks_of('colors', m.colors)
    tracks_of('transparency', m.transparency)
    tracks_of('track_6c', m.track_6c)
    tracks_of('texture_transforms', m.texture_transforms)

    # ---------------------------------------------------------------- collision
    ct = m.collision_triangles
    if len(ct) % 3:
        err('collision_triangles', '%d indices is not a multiple of 3' % len(ct))
    idx('collision_triangles', ct, max(len(m.collision_vertices), 1), what='collision vertex')
    if len(m.collision_normals) and len(m.collision_normals) != len(ct) // 3:
        warn('collision_normals', '%d normals for %d triangles' % (len(m.collision_normals), len(ct) // 3))

    # ---------------------------------------------------------------- attachments, events, lights, cameras, emitters
    for name in ('attachments', 'events', 'lights', 'ribbons', 'particles'):
        arr = getattr(m, name)
        if len(arr) and nbones:
            bones = arr['bone'].astype(np.int64)
            bones = bones[bones != -1] if name == 'lights' else bones
            bad = bones[(bones < 0) | (bones >= nbones)]
            if len(bad):
                err(name, 'bone %d of %d' % (bad[0], nbones))
        tracks_of(name, arr)
    idx('attachment_lookup', m.attachment_lookup, max(len(m.attachments), 1), allow_none=True, what='attachment')
    tracks_of('cameras', m.cameras)
    idx('camera_lookup', m.camera_lookup, max(len(m.cameras), 1), allow_none=True, what='camera')
    for i in range(len(m.particles)):
        p = m.particles[i]
        if int(p.texture) >= max(ntex, 1):
            err('particles[%d]' % i, 'texture %d of %d' % (p.texture, ntex))
    for i in range(len(m.ribbons)):
        r = m.ribbons[i]
        idx('ribbons[%d].textures' % i, r.textures, max(ntex, 1), what='texture')
        idx('ribbons[%d].materials' % i, r.materials, max(nrf, 1), what='render flags')

    # ---------------------------------------------------------------- bounds
    for k in ('bounds_min', 'bounds_max', 'collision_min', 'collision_max'):
        if not np.isfinite(getattr(m, k)).all():
            err(k, 'not finite')
    return out


def errors(issues):
    return [i for i in issues if i.level == 'error']
