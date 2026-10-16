"""`wowdata map ...` commands: validate and diff 1.12 map files (on disk, in the client archives, or both)."""
import json
import os
import re
import sys

from . import validate as V
from . import diff as D

MAP_EXT = ('.adt', '.wdt', '.wdl')
_WM = re.compile(r'(world[\\/]+maps[\\/].*)$', re.I)


def archive_path_of(path):
    """'...\\World\\Maps\\Azeroth\\Azeroth_32_48.adt' -> 'World\\Maps\\Azeroth\\Azeroth_32_48.adt' (or None)."""
    m = _WM.search(path.replace('/', '\\'))
    return m.group(1).replace('/', '\\') if m else None


def _disk_items(path):
    """(name, loader) for every map file under a disk path; grouped by directory."""
    groups = {}
    if os.path.isfile(path):
        groups.setdefault(os.path.dirname(path), []).append(path)
    else:
        for root, dirs, files in os.walk(path):
            for f in sorted(files):
                if f.lower().endswith(MAP_EXT):
                    groups.setdefault(root, []).append(os.path.join(root, f))
    out = []
    for d in sorted(groups):
        out.append([(p, (lambda p=p: open(p, 'rb').read())) for p in groups[d]])
    return out


def _archive_items(client, pattern):
    names = client.glob(pattern)
    if not names and client.exists(pattern):
        names = [pattern]
    groups = {}
    for n in names:
        if n.lower().endswith(MAP_EXT):
            groups.setdefault(n.rsplit('\\', 1)[0].upper(), []).append(n)
    return [[(n, (lambda n=n: client.read(n))) for n in groups[g]] for g in sorted(groups)]


def cmd_validate(cfg, args, client_factory):
    report = V.Report()
    client = None
    targets = list(args.paths)
    if args.all_client:
        targets.append('World\\Maps\\*')
    if not targets:
        sys.exit('nothing to validate: give a file, a folder, an archive path/wildcard, or --all-client')
    for t in targets:
        if os.path.exists(t):
            groups = _disk_items(t)
        else:
            client = client or client_factory(cfg)
            groups = _archive_items(client, t)
        if not groups:
            print('no map files found for %s' % t)
        for items in groups:
            V.validate_map_set(items, report)
    findings = sorted(report.findings, key=lambda f: (V.SEVERITY_ORDER[f.severity], f.file, f.code))
    if not args.show_info:
        shown = [f for f in findings if f.severity != V.INFO]
    else:
        shown = findings
    if args.json:
        print(json.dumps({'files': report.files, 'errors': report.count(V.ERROR), 'warnings': report.count(V.WARNING),
                          'info': report.count(V.INFO), 'findings': [f.as_dict() for f in shown]}, indent=1))
    else:
        for f in shown[:args.limit]:
            print(f)
        if len(shown) > args.limit:
            print('... %d more (use --limit or --json)' % (len(shown) - args.limit))
        print('%d file(s): %d error(s), %d warning(s), %d info' % (report.files, report.count(V.ERROR),
                                                                    report.count(V.WARNING), report.count(V.INFO)))
        print('RESULT: %s' % ('PASS' if report.ok else 'FAIL'))
    return 0 if report.ok else 1


def _load(spec, cfg, client_holder, client_factory):
    """Bytes of a disk file, or of an archive path read through the client's archive order."""
    if os.path.isfile(spec):
        with open(spec, 'rb') as f:
            return f.read(), spec
    if client_holder[0] is None:
        client_holder[0] = client_factory(cfg)
    data = client_holder[0].read(spec)
    if data is None:
        sys.exit('not found on disk or in the client archives: %s' % spec)
    return data, '%s [client]' % spec


def cmd_diff(cfg, args, client_factory):
    holder = [None]
    pairs = []
    if os.path.isdir(args.a):
        # every map file in a folder against its counterpart (folder B, or the client archives)
        for items in _disk_items(args.a):
            for p, _ in items:
                rel = os.path.relpath(p, args.a)
                if args.b:
                    if os.path.isdir(args.b):
                        pairs.append((p, os.path.join(args.b, rel)))
                else:
                    old = archive_path_of(p)
                    if old:
                        pairs.append((old, p))   # client copy (OLD) -> folder file (NEW)
    elif args.b:
        pairs.append((args.a, args.b))
    else:
        old = archive_path_of(args.a)
        if not old:
            sys.exit('give B, or an A path containing World\\Maps\\... to compare with the client copy')
        pairs.append((old, args.a))
    differing = 0
    out = []
    for a, b in pairs:
        ab, an = _load(a, cfg, holder, client_factory)
        bb, bn = _load(b, cfg, holder, client_factory)
        d = D.diff_bytes(ab, bb, b, an, bn)
        if not d.same_content:
            differing += 1
        if args.json:
            out.append(d.as_dict())
            continue
        print('%s\n  -> %s: %s' % (d.a_name, d.b_name, d.summary()))
        for s, m in d.lines[:args.limit]:
            print('    %-18s %s' % (s, m))
        if len(d.lines) > args.limit:
            print('    ... %d more' % (len(d.lines) - args.limit))
        if args.layout:
            for s, m in d.layout[:args.limit]:
                print('    (layout) %-18s %s' % (s, m))
    if args.json:
        print(json.dumps(out, indent=1))
    else:
        print('%d file pair(s), %d with content differences' % (len(pairs), differing))
    return 1 if differing else 0


def add_parser(sub, client_factory):
    m = sub.add_parser('map', help='1.12 map files (ADT/WDT/WDL): validate against the client loaders, semantic diff')
    ms = m.add_subparsers(dest='mcmd', required=True)
    s = ms.add_parser('validate', help='check ADT/WDT/WDL files against what the 1.12 client loaders read')
    s.add_argument('paths', nargs='*', help='file or folder on disk, or an archive path / wildcard '
                   '(e.g. World\\Maps\\Azeroth\\*)')
    s.add_argument('--all-client', action='store_true', help='validate every map in the client archives')
    s.add_argument('--show-info', action='store_true', help='also list info findings')
    s.add_argument('--json', action='store_true')
    s.add_argument('--limit', type=int, default=200)
    s.set_defaults(fn=lambda cfg, a: sys.exit(cmd_validate(cfg, a, client_factory)))
    s = ms.add_parser('diff', help='semantic chunk-level diff of two map files (or folder vs client/folder)')
    s.add_argument('a', help='OLD file, folder or archive path. Alone: the NEW file/folder (e.g. a Noggit project '
                   'World\\Maps\\<map> folder), compared with its client archive copy as OLD')
    s.add_argument('b', nargs='?', help='NEW file, folder or archive path (differences are listed OLD -> NEW)')
    s.add_argument('--layout', action='store_true', help='also list offset/size-only differences')
    s.add_argument('--json', action='store_true')
    s.add_argument('--limit', type=int, default=40)
    s.set_defaults(fn=lambda cfg, a: sys.exit(cmd_diff(cfg, a, client_factory)))
