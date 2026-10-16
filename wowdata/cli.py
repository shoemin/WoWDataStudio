"""wowdata command line. Run `wowdata -h` or `wowdata <command> -h`."""
import argparse
import json
import os
import sys

from . import __version__, config


def _client(cfg, include_unmounted=False, overlays=()):
    from .vfs import ClientData
    if not os.path.isdir(cfg.data_dir):
        sys.exit('client Data folder not found: %s (set client_dir in wowdata.json)' % cfg.data_dir)
    return ClientData(cfg.data_dir, include_unmounted=include_unmounted, overlays=overlays, cache_dir=cfg.cache_dir)


def _index(cfg, client=None, rebuild=False):
    from . import index
    if rebuild or not os.path.exists(cfg.index_path):
        print('building the search index (%s) ...' % cfg.index_path)
        index.build(client or _client(cfg), cfg.index_path, cfg)
    return index.Index(cfg.index_path)


def _arch(a):
    return '[%s]' % a if a else '[MISSING]'


# ---------------------------------------------------------------------- commands


def cmd_info(cfg, args):
    client = _client(cfg, include_unmounted=True)
    print('wowdata %s' % __version__)
    print('client Data : %s' % cfg.data_dir)
    print('archives in lookup order (first wins):')
    for i, (n, a, mounted) in enumerate(client.archives):
        flag = '' if mounted else '   (not mounted by the game: installer/launcher files)'
        print('  %2d. %-14s v%d  %6d files  %s%s' % (i + 1, n, a.format_version, len(a.listfile()), '', flag))
    print('staging     : %s' % cfg.staging_dir)
    print('index       : %s%s' % (cfg.index_path, '' if os.path.exists(cfg.index_path) else ' (not built yet)'))
    if os.path.exists(cfg.index_path):
        from .index import Index
        print('index meta  : %s' % Index(cfg.index_path).meta().get('counts'))


def cmd_index(cfg, args):
    from . import index
    client = _client(cfg)
    print('building %s' % cfg.index_path)
    c = index.build(client, cfg.index_path, cfg, use_server=not args.no_server)
    print('done in %.1f s' % c['seconds'])


def _print_files(files, indent):
    for f in files:
        print('%s%-22s %s %s' % (indent, f['role'], f['path'], _arch(f['archive'])))


def _print_display(d, indent='    '):
    if not d or d.get('missing'):
        print('%sdisplay %s: not in CreatureDisplayInfo' % (indent, d.get('id') if d else '?'))
        return
    print('%sdisplay %d  model %s  scale %.2f' % (indent, d['id'], d['model'] or '-', d['scale']))
    _print_files(d['files'], indent + '  ')
    var = d.get('variations') or []
    if len(var) > 1:
        skins = sorted({s for v in var for s in v['skins']})
        ndisp = sum(len(v['displays']) for v in var)
        print('%s  texture variations of this model: %d displays, %d skins: %s' % (indent, ndisp, len(skins), ', '.join(skins)))


def cmd_find(cfg, args):
    ix = _index(cfg)
    kinds = args.type.split(',') if args.type else None
    res = ix.find(args.text, kinds, args.limit)
    if args.json:
        print(json.dumps(res, indent=1, default=str))
        return
    if not ix.has_server():
        print('(the index has no server data: creatures, game objects and items are not searchable; run `wowdata index build` with the server DB running)')
    shown = 0
    if res.get('creature', {}).get('rows'):
        r = res['creature']
        print('\nCREATURES (%d match%s%s)' % (r['total'], 'es' if r['total'] != 1 else '', ', first %d' % args.limit if r['total'] > args.limit else ''))
        for c in r['rows']:
            sp = ', '.join('%d on map %d %s' % (s['count'], s['map'], s['map_name'] or '') for s in c['spawns'][:3]) or 'not spawned'
            sub = ' <%s>' % c['subname'] if c['subname'] else ''
            print('  %d %s%s  lvl %s-%s  (%s)' % (c['entry'], c['name'], sub, c['level_min'], c['level_max'], sp))
            for d in c['displays']:
                _print_display(d)
        shown += 1
    if res.get('gameobject', {}).get('rows'):
        r = res['gameobject']
        print('\nGAME OBJECTS (%d match%s)' % (r['total'], 'es' if r['total'] != 1 else ''))
        for g in r['rows']:
            sp = sum(s['count'] for s in g['spawns'])
            print('  %d %s  type %s  display %s  (%d spawns)' % (g['entry'], g['name'], g['type'], g['display_id'], sp))
            _print_files(g['files'], '      ')
        shown += 1
    if res.get('item', {}).get('rows'):
        r = res['item']
        print('\nITEMS (%d match%s)' % (r['total'], 'es' if r['total'] != 1 else ''))
        for it in r['rows']:
            print('  %d %s  display %s' % (it['entry'], it['name'], it['display_id']))
            files = it['files']
            if len(files) > 12 and not args.all_files:
                _print_files(files[:12], '      ')
                print('      ... %d more (use --all-files)' % (len(files) - 12))
            else:
                _print_files(files, '      ')
        shown += 1
    if res.get('race', {}).get('rows'):
        print('\nPLAYER RACES')
        for r in res['race']['rows']:
            print('  race %d %s %s' % (r['race'], r['name'], r['sex']))
            _print_display(r['display'])
            print('    character sections (%d rows):' % r['section_rows'])
            for sec, texs in r['sections'].items():
                flat = [t for row in texs for t in row]
                print('      %-12s %3d textures, e.g. %s' % (sec, len(flat), flat[0] if flat else '-'))
        shown += 1
    if res.get('model', {}).get('rows'):
        r = res['model']
        print('\nMODELS BY PATH (%d)' % r['total'])
        for m in r['rows']:
            print('  %s %s  used by %d %s display(s)' % (m['path'], _arch(m['archive']), m['users'], m['kinds']))
            for v in (m.get('variations') or [])[:12]:
                print('      skins %-40s displays %s' % (', '.join(v['skins']) or '-', ', '.join(map(str, v['displays'][:10])) + (' ...' if len(v['displays']) > 10 else '')))
        shown += 1
    if res.get('file', {}).get('rows'):
        r = res['file']
        print('\nFILES BY PATH (%d%s)' % (r['total'], ', first %d' % len(r['rows']) if r['total'] > len(r['rows']) else ''))
        for f in r['rows']:
            print('  %s %s' % (f['path'], _arch(f['archive'])))
        shown += 1
    if not shown:
        print('nothing found for %r' % args.text)
    print('\n(map tiles are not indexed yet: world doodads such as trees are found by their file path; see ROADMAP.md)')


def cmd_extract(cfg, args):
    from .resolve import Catalog
    from .targets import resolve, out_path, TargetError
    client = _client(cfg, include_unmounted=args.all_archives)
    cat = Catalog(client)
    out_dir = args.out or cfg.extract_dir
    n = 0
    for t in args.target:
        try:
            files = resolve(t, cat, cfg)
        except TargetError as e:
            sys.exit(str(e))
        for role, p, a in files:
            data = client.read(p)
            if data is None:
                print('  missing   %s' % p)
                continue
            dest = out_path(out_dir, p)
            with open(dest, 'wb') as f:
                f.write(data)
            n += 1
            print('  %-22s %s %s -> %s' % (role, p, _arch(a), dest))
    print('%d file(s) written under %s' % (n, out_dir))


def cmd_ls(cfg, args):
    client = _client(cfg, include_unmounted=args.all_archives)
    names = client.glob(args.pattern)
    for p in names[:args.limit]:
        src = client.where(p)
        print('%-90s %s' % (p, ' > '.join(s.name + ('' if s.kind in ('archive', 'loose') else ' (%s)' % s.kind) for s in src)))
    if len(names) > args.limit:
        print('... %d more' % (len(names) - args.limit))


def cmd_dbc(cfg, args):
    from . import dbc
    client = _client(cfg)
    t = dbc.load(client, args.table)
    print('%s: %d rows, %d fields%s' % (args.table, t.count, t.field_count, '' if t.schema else ' (no layout: values guessed)'))
    rows = range(min(args.limit, t.count)) if not args.ids else None
    if t.schema:
        recs = [t.get(i) for i in args.ids] if args.ids else [t.rows()[i] for i in rows]
        for r in recs:
            print(json.dumps(r, default=str) if r else 'missing')
    else:
        ids = set(args.ids or [])
        for i in range(t.count):
            if ids and t.raw(i, 0) not in ids:
                continue
            if not ids and i >= args.limit:
                break
            print(dbc.guess_row(t, i))


def cmd_tex_export(cfg, args):
    from .resolve import Catalog
    from .targets import resolve, TargetError
    from .texture import export_one, TextureError
    client = _client(cfg)
    cat = Catalog(client)
    out_dir = args.out or cfg.export_dir
    n = 0
    for t in args.target:
        try:
            files = resolve(t, cat, cfg)
        except TargetError as e:
            sys.exit(str(e))
        blps = [(role, p, a) for role, p, a in files if p.lower().endswith('.blp')]
        if not blps:
            print('%s: no textures' % t)
        for role, p, a in blps:
            if not a:
                print('  missing   %s (%s)' % (p, role))
                continue
            try:
                png, side = export_one(client, p, out_dir, {'target': t, 'role': role})
            except TextureError as e:
                print('  error     %s: %s' % (p, e))
                continue
            n += 1
            print('  %-22s %s [%s] %s -> %s' % (role, p, a, side['blp']['format'], png))
    print('%d texture(s) exported under %s (each PNG has a .blp.json sidecar with the original format)' % (n, out_dir))


def cmd_tex_import(cfg, args):
    from .texture import import_one, TextureError
    client = _client(cfg)
    staging = args.staging or cfg.staging_dir
    for png in args.png:
        try:
            rep = import_one(client, png, staging, target=args.as_path, fmt=args.format, palette_mode=args.palette, template=args.template, full=args.full)
        except TextureError as e:
            sys.exit('%s: %s' % (png, e))
        print('%s\n  -> %s' % (png, rep['staged']))
        print('  format %s, %d bytes, %s' % (rep['format'], rep['bytes'], 'BYTE-IDENTICAL to the original' if rep.get('identical') else 'changed'))
        if rep.get('full_reencode'):
            print('  full re-encode: same format %s, same mip layout %s' % (rep.get('same_format'), rep.get('same_layout')))
            if 'vs_original_psnr_db' in rep:
                print('  vs the original texture: max channel diff %s, mean %s, PSNR %s dB' % (
                    rep['vs_original_max_diff'], rep['vs_original_mean_diff'], rep['vs_original_psnr_db']))
        if 'changed_pixels' in rep:
            print('  edited pixels: %d' % rep['changed_pixels'])
        for lv in rep.get('per_level', []):
            unit = 'blocks' if 'blocks' in lv else 'pixels'
            if lv['re_encoded']:
                print('    level %d (%s): %d of %d %s re-encoded' % (lv['level'], lv['size'], lv['re_encoded'], lv[unit], unit))
        print('  game view vs PNG: %d pixels differ, max channel diff %d, mean %.3f%s' % (
            rep['pixels_differing'], rep['max_pixel_diff'], rep['mean_abs_diff'],
            ' (DXT is lossy: re-encoded blocks are approximations)' if rep['pixels_differing'] else ''))
        for w in rep.get('warnings', []):
            print('  warning: %s' % w)
    print('staged in %s (nothing was written to the client). Next: `wowdata loose install` or `wowdata pack --install`.' % staging)


def cmd_tex_tint(cfg, args):
    from .texture import tint
    color = tuple(int(v) for v in args.color.split(','))
    print('wrote %s' % tint(args.png, args.out, color, args.strength))


def cmd_tex_info(cfg, args):
    from . import blp
    if os.path.exists(args.path):
        with open(args.path, 'rb') as f:
            data = f.read()
    else:
        data = _client(cfg).read(args.path)
        if data is None:
            sys.exit('%s not found on disk or in the archives' % args.path)
    b = blp.BLP(data)
    print(json.dumps(b.info(), indent=1))
    probs = blp.validate(data)
    print('client compatibility: %s' % ('OK' if not probs else '; '.join(probs)))


def cmd_staging(cfg, args):
    from .deploy import staging_files, check_staging
    files = staging_files(args.staging or cfg.staging_dir)
    for rel, full in files.items():
        print('  %s (%d bytes)' % (rel, os.path.getsize(full)))
    print('%d file(s) in %s' % (len(files), args.staging or cfg.staging_dir))
    for p in check_staging(files):
        print('  PROBLEM: %s' % p)


def cmd_loose(cfg, args):
    from . import deploy
    try:
        if args.action == 'install':
            acts = deploy.loose_install(cfg, args.staging, force=args.force, dry_run=args.dry_run)
            for rel, src, dest, action in acts:
                print('  %-20s %s' % (action, dest))
            print('%d file(s) %s. They are used by the game only with the loose-file client patch '
                  '(docs/LOOSE_FILES_PATCH.md); restart the game to see them.' % (len(acts), 'would be installed' if args.dry_run else 'installed'))
        elif args.action == 'remove':
            for p, what in deploy.loose_remove(cfg, force=args.force):
                print('  %-45s %s' % (what, p))
        else:
            st = deploy.loose_status(cfg)
            for p, state, dest in st:
                print('  %-8s %s' % (state, dest))
            print('%d loose file(s) installed by wowdata' % len(st))
    except deploy.DeployError as e:
        sys.exit(str(e))


def cmd_pack(cfg, args):
    from . import deploy
    try:
        s = deploy.pack(cfg, args.staging, args.out, install=args.install, name=args.name)
    except deploy.DeployError as e:
        sys.exit(str(e))
    print('built %s: %d file(s), %d bytes, verified by read-back' % (s['path'], s['files'], s['size']))
    for c in s['contents']:
        print('  %s' % c)
    if s.get('backup'):
        print('previous archive backed up to %s' % s['backup'])
    if s.get('installed'):
        print('installed as %s (restart the game)' % s['installed'])
    else:
        print('not installed (add --install to copy it to %s)' % os.path.join(cfg.data_dir, args.name or cfg.patch_name))
    if s.get('note'):
        print('note: ' + s['note'])


def _model_ctx(cfg, args, client):
    from .resolve import Catalog
    from .modelctx import resolve_model
    from .targets import TargetError
    cat = Catalog(client)
    choices = {k: getattr(args, k) for k in ('skin', 'face', 'hair_style', 'hair_color', 'facial_hair')
               if getattr(args, k, None) is not None}
    try:
        return resolve_model(args.target, cat, cfg, choices)
    except TargetError as e:
        sys.exit(str(e))


def _print_export(rep):
    vis = [x for x in rep['meshes'] if x['visible']]
    print('  %s' % rep['gltf'])
    print('  %d bones, %d animations (of %d sequences), %d vertices, %d submeshes (%d visible by default), %d materials' % (
        rep['bones'], rep['animations'], rep['sequences'], rep['vertices'], len(rep['meshes']), len(vis), len(rep['materials'])))
    for t in rep['textures']:
        print('  texture %d (%s): %s' % (t['texture'], t['type_name'], t['resolved']))
    for n in rep['notes']:
        print('  note: %s' % n)
    for e in rep['errors']:
        print('  VALIDATION ERROR %s: %s' % (e['where'], e['message']))
    print('  sidecar %s' % rep['sidecar'])


def cmd_model_export(cfg, args):
    from . import gltf
    client = _client(cfg)
    ctx = _model_ctx(cfg, args, client)
    out = args.out or os.path.join(cfg.export_dir, 'models')
    print('%s -> %s (%s)' % (args.target, ctx.model_path, ctx.kind))
    rep = gltf.export(client, ctx, out, view_index=args.view, animations=not args.no_anims)
    _print_export(rep)


def cmd_model_view(cfg, args):
    from . import gltf, viewer
    client = _client(cfg)
    ctx = _model_ctx(cfg, args, client)
    out = args.out or os.path.join(cfg.export_dir, 'models')
    print('%s -> %s (%s)' % (args.target, ctx.model_path, ctx.kind))
    rep = gltf.export(client, ctx, out, view_index=args.view, animations=not args.no_anims, glb=True)
    _print_export(rep)
    page = os.path.join(rep['folder'], rep['name'] + '.html')
    viewer.write_html(page, gltf.glb_base64(rep['glb']), rep['name'], '%s  |  %s' % (ctx.model_path, ctx.label))
    print('viewer page: %s (%.1f MB; three.js %s is loaded from cdn.jsdelivr.net)' % (page, os.path.getsize(page) / 1e6, viewer.THREE_VERSION))
    if not args.no_open:
        try:
            os.startfile(page)
        except (AttributeError, OSError) as e:
            print('open it in a browser (%s)' % e)


def cmd_model_info(cfg, args):
    from .m2 import M2, M2Error
    from .m2.validate import validate
    from .m2.schema import TEXTURE_TYPES, BLEND_MODES
    client = _client(cfg)
    if os.path.exists(args.target):
        with open(args.target, 'rb') as f:
            data = f.read()
        label = args.target
    else:
        ctx = _model_ctx(cfg, args, client)
        data = client.read(ctx.model_path)
        label = ctx.model_path
    try:
        m = M2.parse(data)
    except M2Error as e:
        sys.exit('%s: %s' % (label, e))
    print('%s: M2 version %#x "%s", %d bytes' % (label, m.version, m.model_name, len(data)))
    counts = {k: v for k, v in m.counts().items() if v}
    print('  ' + ', '.join('%s %d' % kv for kv in counts.items()))
    for i, v in enumerate(m.views):
        print('  view %d: bone limit %d, %d triangles, %d submeshes, %d texture units' % (
            i, v.bone_limit, len(v.triangles) // 3, len(v.submeshes), len(v.texture_units)))
    for i in range(len(m.textures)):
        t = int(m.textures['type'][i])
        print('  texture %d: type %d (%s) flags %d %s' % (i, t, TEXTURE_TYPES.get(t, '?'), m.textures['flags'][i], m.texture_name(i)))
    for i in range(len(m.render_flags)):
        print('  render flags %d: flags %#x blend %s' % (i, m.render_flags['flags'][i], BLEND_MODES.get(int(m.render_flags['blend'][i]))))
    same = m.serialize() == data
    print('  re-serialised: %s' % ('byte-identical' if same else 'DIFFERENT'))
    issues = validate(m)
    for lvl in ('error', 'warning', 'note'):
        sel = [i for i in issues if i.level == lvl]
        print('  %d %s(s)' % (len(sel), lvl))
        for i in sel[:args.limit]:
            print('    %s: %s' % (i.where, i.msg))


def cmd_model_check(cfg, args):
    """Parse, re-serialise and validate every .m2 matching a pattern (default: all)."""
    import collections
    import time
    from .m2 import M2, M2Error
    from .m2.validate import validate
    client = _client(cfg)
    names = client.glob(args.pattern)
    names = [n for n in names if n.lower().endswith('.m2')]
    t = time.time()
    st = collections.Counter()
    issues = collections.Counter()
    for p in names:
        data = client.read(p)
        try:
            m = M2.parse(data)
        except M2Error as e:
            st['parse errors'] += 1
            print('  PARSE ERROR %s: %s' % (p, e))
            continue
        st['parsed'] += 1
        if m.serialize() == data:
            st['byte-identical'] += 1
        else:
            st['re-serialised differently'] += 1
            print('  DIFFERENT %s' % p)
        for i in validate(m):
            issues[i.level] += 1
            if i.level == 'error':
                print('  ERROR %s %s: %s' % (p, i.where, i.msg))
    print('%d model(s) in %.1f s: %s; issues %s' % (len(names), time.time() - t, dict(st), dict(issues)))


# ---------------------------------------------------------------------- parser


def build_parser():
    p = argparse.ArgumentParser(prog='wowdata', description='WoW 1.12 data tool: find, extract, convert and repack client assets.')
    p.add_argument('--version', action='version', version='wowdata ' + __version__)
    p.add_argument('--config', help='settings file (default: wowdata.json next to the tool)')
    sub = p.add_subparsers(dest='cmd', required=True)

    s = sub.add_parser('info', help='archives in lookup order, paths, index state')
    s.set_defaults(fn=cmd_info)

    s = sub.add_parser('index', help='(re)build the search index')
    s.add_argument('action', choices=['build'])
    s.add_argument('--no-server', action='store_true', help='DBCs and archives only (no creature/item/object names)')
    s.set_defaults(fn=cmd_index)

    s = sub.add_parser('find', help='find assets by how the game uses them: creatures, objects, items, races, model and file paths')
    s.add_argument('text')
    s.add_argument('--type', help='comma list of: creature,gameobject,item,race,model,file')
    s.add_argument('--limit', type=int, default=10)
    s.add_argument('--all-files', action='store_true', help='do not shorten long file lists')
    s.add_argument('--json', action='store_true')
    s.set_defaults(fn=cmd_find)

    s = sub.add_parser('extract', help='copy files out of the archives (path, wildcard, creature:/display:/item:/go:/race: id)')
    s.add_argument('target', nargs='+')
    s.add_argument('--out', help='output folder (default work\\extract)')
    s.add_argument('--all-archives', action='store_true', help='also read base.MPQ and backup.MPQ (not mounted by the game)')
    s.set_defaults(fn=cmd_extract)

    s = sub.add_parser('ls', help='list archive paths matching a wildcard, with every archive that has them')
    s.add_argument('pattern')
    s.add_argument('--limit', type=int, default=200)
    s.add_argument('--all-archives', action='store_true')
    s.set_defaults(fn=cmd_ls)

    s = sub.add_parser('dbc', help='print rows of a client DBC table')
    s.add_argument('table')
    s.add_argument('ids', nargs='*', type=int)
    s.add_argument('--limit', type=int, default=10)
    s.set_defaults(fn=cmd_dbc)

    t = sub.add_parser('texture', help='texture round trip: export to PNG, import back to BLP').add_subparsers(dest='tcmd', required=True)
    s = t.add_parser('export', help='write PNGs + .blp.json sidecars for a path / creature:/display:/item:/go:/race: target')
    s.add_argument('target', nargs='+')
    s.add_argument('--out', help='output folder (default work\\export)')
    s.set_defaults(fn=cmd_tex_export)
    s = t.add_parser('import', help='encode edited PNGs back to BLP (same format as the original) into the staging folder')
    s.add_argument('png', nargs='+')
    s.add_argument('--as', dest='as_path', help='archive path to write instead of the original one (e.g. a new skin name)')
    s.add_argument('--template', help='archive path of the BLP whose format to copy (default: the original)')
    s.add_argument('--format', choices=['dxt1', 'dxt1a', 'dxt3', 'dxt5', 'pal', 'pal1', 'pal4', 'pal8'], help='force another format')
    s.add_argument('--palette', choices=['auto', 'keep', 'new'], default='auto', help='palette textures: keep the original palette or build a new one')
    s.add_argument('--full', action='store_true', help='re-encode everything (encoder test) instead of only the edited blocks')
    s.add_argument('--staging', help='staging folder (default staging\\)')
    s.set_defaults(fn=cmd_tex_import)
    s = t.add_parser('tint', help='make a visibly tinted copy of a PNG (for in-game tests)')
    s.add_argument('png')
    s.add_argument('out')
    s.add_argument('--color', default='255,64,64', help='R,G,B to blend toward (default 255,64,64)')
    s.add_argument('--strength', type=float, default=0.5)
    s.set_defaults(fn=cmd_tex_tint)
    s = t.add_parser('info', help='BLP header, mip layout and client compatibility of a file or archive path')
    s.add_argument('path')
    s.set_defaults(fn=cmd_tex_info)

    mo = sub.add_parser('model', help='M2 models: export to glTF for Blender, quick-look viewer, info, check').add_subparsers(dest='mcmd', required=True)

    def model_target(s):
        s.add_argument('target', help='an .m2/.mdx path, display:N, creature:N, item:N, itemdisplay:N, go:N, godisplay:N or race:orc[:female]')
        g = s.add_argument_group('character appearance (CharSections indexes; default 0)')
        for k in ('skin', 'face', 'hair-style', 'hair-color', 'facial-hair'):
            g.add_argument('--' + k, type=int, dest=k.replace('-', '_'))

    s = mo.add_parser('export', help='write <out>\\<Name>\\<Name>.gltf + .bin + textures\\*.png + <Name>.wowm2.json (sidecar)')
    model_target(s)
    s.add_argument('--out', help='output folder (default work\\export\\models)')
    s.add_argument('--view', type=int, default=0, help='which of the 4 views (LODs) to export (default 0 = full detail)')
    s.add_argument('--no-anims', action='store_true', help='skip the animations')
    s.set_defaults(fn=cmd_model_export)
    s = mo.add_parser('view', help='export, then write a self-contained HTML viewer page (three.js) and open it')
    model_target(s)
    s.add_argument('--out', help='output folder (default work\\export\\models)')
    s.add_argument('--view', type=int, default=0)
    s.add_argument('--no-anims', action='store_true')
    s.add_argument('--no-open', action='store_true', help='do not open the page in the browser')
    s.set_defaults(fn=cmd_model_view)
    s = mo.add_parser('info', help='parse a model (target or a file on disk): counts, views, textures, round trip, validation')
    model_target(s)
    s.add_argument('--limit', type=int, default=20, help='issues shown per level')
    s.set_defaults(fn=cmd_model_info)
    s = mo.add_parser('check', help='parse + re-serialise + validate every model matching a wildcard')
    s.add_argument('pattern', nargs='?', default='*.m2')
    s.set_defaults(fn=cmd_model_check)

    s = sub.add_parser('staging', help='list the staging folder and check it')
    s.add_argument('--staging')
    s.set_defaults(fn=cmd_staging)

    s = sub.add_parser('loose', help='copy the staging folder into Client\\Data as loose files (needs the loose-file client patch)')
    s.add_argument('action', choices=['install', 'remove', 'status'])
    s.add_argument('--staging')
    s.add_argument('--force', action='store_true')
    s.add_argument('--dry-run', action='store_true')
    s.set_defaults(fn=cmd_loose)

    s = sub.add_parser('pack', help='build patch-3.MPQ from the staging folder (install with --install)')
    s.add_argument('--staging')
    s.add_argument('--out', help='archive to write (default build\\patch-3.MPQ)')
    s.add_argument('--name', help='archive name in Client\\Data (default patch-3.MPQ)')
    s.add_argument('--install', action='store_true', help='copy it to Client\\Data (an existing one is backed up first)')
    s.set_defaults(fn=cmd_pack)

    from .maps import cli as map_cli
    map_cli.add_parser(sub, _client)
    return p


def main(argv=None):
    args = build_parser().parse_args(argv)
    cfg = config.load(args.config)
    args.fn(cfg, args)


if __name__ == '__main__':
    main()
