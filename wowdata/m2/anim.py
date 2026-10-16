"""Animation helpers with the 1.12 client's semantics.

* All sequences share one timeline (milliseconds). Sequence i plays from sequences[i].start to .end.
* A track's ranges[i] = {first, last} (inclusive) are the keys sequence i uses (M2Model_FindTrackKeys @00713d50);
  a track without ranges uses all its keys for every sequence. The keys of a range may lie outside the
  sequence's own [start, end] window (Blizzard's files do that for constant channels): the client then
  interpolates between them, so the value at the window edges is an interpolated one.
* A track with a global sequence (global_sequence != -1) runs on its own clock that loops over
  global_sequences[gs] milliseconds, independently of the playing sequence.
* Interpolation 0 = step (hold the previous key), anything else = linear: M2Model_EvalQuatTrack @00713ea0 lerps
  the 4 quaternion components, M2Model_EvalFloatTrack @0071af20 lerps floats. (Type 2 only occurs in camera
  spline tracks.)
"""
import numpy as np


def animation_names(client):
    """{AnimationData id: name}."""
    from .. import dbc
    try:
        t = dbc.load(client, 'AnimationData')
    except dbc.DBCError:
        return {}
    return {t.raw(i, 0): t.string(t.raw(i, 1)) for i in range(t.count)}


def evaluate(times, values, t, interpolation, quaternion=False):
    """Value of a key list at time t (clamped at both ends)."""
    times = np.asarray(times, np.float64)
    values = np.asarray(values, np.float64)
    if len(times) == 0:
        return None
    if t <= times[0] or len(times) == 1:
        return values[0].copy()
    if t >= times[-1]:
        return values[-1].copy()
    i = int(np.searchsorted(times, t, side='right')) - 1
    if interpolation == 0 or times[i + 1] == times[i]:
        return values[i].copy()
    f = (t - times[i]) / (times[i + 1] - times[i])
    v = values[i] + (values[i + 1] - values[i]) * f
    if quaternion:
        n = np.linalg.norm(v)
        if n > 0:
            v = v / n
    return v


def sequence_keys(track, seq_index, seq_start, seq_end, global_sequences, quaternion=False):
    """(times in ms relative to the sequence start, values) of `track` while sequence `seq_index` plays, or None
    when the track has no keys for it. Keys outside the window are folded into interpolated keys at its edges;
    global-sequence tracks are unrolled over the sequence length."""
    ts = track.timestamps
    if ts is None or len(ts) == 0:
        return None
    vals = np.asarray(track.values)
    ranges = track.ranges
    if len(ranges):
        if seq_index >= len(ranges):
            return None
        a, b = (int(x) for x in ranges[seq_index])
        if b < a or a >= len(ts):
            return None
        b = min(b, len(ts) - 1)
    else:
        a, b = 0, len(ts) - 1
    kt = ts[a:b + 1].astype(np.float64)
    kv = vals[a:b + 1].astype(np.float64)
    interp = track.interpolation
    length = float(seq_end) - float(seq_start)
    gs = track.global_sequence
    if gs != -1 and gs < len(global_sequences):
        period = float(global_sequences[gs])
        if period <= 0 or len(kt) == 1:
            return np.array([0.0]), kv[:1]
        kt = kt - kt[0] if kt[0] < 0 else kt
        out_t, out_v = [], []
        k = 0
        while True:
            base = k * period
            if base > length:
                break
            for t, v in zip(kt, kv):
                if t <= period and base + t <= length:
                    out_t.append(base + t)
                    out_v.append(v)
            k += 1
            if k > 10000:
                break
        if not out_t or out_t[0] > 0:
            out_t.insert(0, 0.0)
            out_v.insert(0, evaluate(kt, kv, 0.0, interp, quaternion))
        if out_t[-1] < length:
            out_t.append(length)
            out_v.append(evaluate(kt, kv, length % period if period else 0.0, interp, quaternion))
        return _dedupe(np.array(out_t), np.array(out_v))
    start, end = float(seq_start), float(seq_end)
    inside = (kt > start) & (kt < end)
    out_t = [0.0]
    out_v = [evaluate(kt, kv, start, interp, quaternion)]
    out_t += list(kt[inside] - start)
    out_v += list(kv[inside])
    if end > start:
        out_t.append(end - start)
        out_v.append(evaluate(kt, kv, end, interp, quaternion))
    return _dedupe(np.array(out_t), np.array(out_v))


def _dedupe(t, v):
    """Drop keys with the same time as the previous key (keep the later one, as a step does)."""
    if len(t) < 2:
        return t, v
    keep = np.ones(len(t), bool)
    keep[:-1] = np.diff(t) > 0
    return t[keep], v[keep]
