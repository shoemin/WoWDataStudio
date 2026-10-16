"""Getting files into the game: the staging folder, loose-file delivery and patch-3.MPQ.

Safety rules (enforced here, not just documented):
  * The stock archives are never opened for writing, never replaced and never deleted.
  * Every command writes to the staging folder (inside WoWDataTool) unless told to install.
  * `loose install` writes only the staged files into Client\\Data\\<archive path>, records each one (with
    its SHA-256) in state\\loose_manifest.json, and refuses to replace a file it did not install unless
    --force (the replaced file is then backed up). `loose remove` deletes only files it installed and that
    are unchanged since.
  * `pack --install` writes only Client\\Data\\patch-<one character>.MPQ (default patch-3.MPQ), never a
    stock archive name, and first copies an existing file of that name to backups\\.
"""
import datetime
import hashlib
import json
import os
import re
import shutil

from . import blp, mpqwrite
from .vfs import STOCK_ARCHIVES

_PATCH_NAME = re.compile(r'^patch-[3-9a-z]\.mpq$', re.I)


class DeployError(Exception):
    pass


def _sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def _stamp():
    return datetime.datetime.now().strftime('%Y%m%d_%H%M%S')


def staging_files(staging_dir):
    """{archive path: file on disk} of everything in the staging folder."""
    out = {}
    if not os.path.isdir(staging_dir):
        return out
    for root, dirs, files in os.walk(staging_dir):
        dirs[:] = [d for d in dirs if not d.startswith('.')]
        for fn in files:
            if fn.startswith('.') or fn.endswith('.tmp'):
                continue
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, staging_dir).replace('/', '\\')
            out[rel] = full
    return dict(sorted(out.items(), key=lambda kv: kv[0].upper()))


def check_staging(files):
    """Problems that must block delivery (BLPs the client could not load, archive names...)."""
    problems = []
    for rel, full in files.items():
        low = rel.lower()
        if low.endswith('.mpq'):
            problems.append('%s: archives cannot be staged' % rel)
        if low.endswith('.blp'):
            with open(full, 'rb') as f:
                for p in blp.validate(f.read()):
                    problems.append('%s: %s' % (rel, p))
    return problems


# ---------------------------------------------------------------------- loose files


def _manifest_path(cfg):
    return os.path.join(cfg.state_dir, 'loose_manifest.json')


def _load_manifest(cfg):
    p = _manifest_path(cfg)
    if os.path.exists(p):
        with open(p, 'r', encoding='utf-8') as f:
            return json.load(f)
    return {}


def _save_manifest(cfg, man):
    os.makedirs(cfg.state_dir, exist_ok=True)
    p = _manifest_path(cfg)
    with open(p + '.tmp', 'w', encoding='utf-8') as f:
        json.dump(man, f, indent=2, sort_keys=True)
    os.replace(p + '.tmp', p)


def loose_install(cfg, staging_dir=None, force=False, dry_run=False):
    staging_dir = staging_dir or cfg.staging_dir
    files = staging_files(staging_dir)
    if not files:
        raise DeployError('the staging folder %s is empty' % staging_dir)
    problems = check_staging(files)
    if problems:
        raise DeployError('staging has problems:\n  ' + '\n  '.join(problems))
    man = _load_manifest(cfg)
    data_dir = cfg.data_dir
    actions = []
    for rel, src in files.items():
        dest = os.path.join(data_dir, *rel.split('\\'))
        key = rel.upper()
        mine = man.get(key)
        if os.path.exists(dest):
            if mine and _sha(dest) == mine['sha256']:
                action = 'update'
            elif force:
                action = 'replace (backed up)'
            else:
                raise DeployError('%s exists and was not installed by wowdata; use --force to back it up and replace it' % dest)
        else:
            action = 'add'
        actions.append((rel, src, dest, action))
    if dry_run:
        return actions
    backup_root = os.path.join(cfg.backup_dir, 'loose_' + _stamp())
    for rel, src, dest, action in actions:
        key = rel.upper()
        entry = man.get(key, {'path': rel})
        if action.startswith('replace'):
            b = os.path.join(backup_root, *rel.split('\\'))
            os.makedirs(os.path.dirname(b), exist_ok=True)
            shutil.copy2(dest, b)
            entry['backup'] = b
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        shutil.copyfile(src, dest + '.wowdata_tmp')
        os.replace(dest + '.wowdata_tmp', dest)
        entry['sha256'] = _sha(dest)
        entry['installed'] = datetime.datetime.now().isoformat(timespec='seconds')
        entry['dest'] = dest
        man[key] = entry
    _save_manifest(cfg, man)
    return actions


def loose_remove(cfg, force=False):
    man = _load_manifest(cfg)
    done = []
    keep = {}
    for key, e in man.items():
        dest = e['dest']
        if not os.path.exists(dest):
            done.append((e['path'], 'already gone'))
            continue
        if _sha(dest) != e['sha256'] and not force:
            keep[key] = e
            done.append((e['path'], 'KEPT: changed since install (use --force)'))
            continue
        os.remove(dest)
        if e.get('backup') and os.path.exists(e['backup']):
            shutil.copy2(e['backup'], dest)
            done.append((e['path'], 'removed, original restored from backup'))
        else:
            done.append((e['path'], 'removed'))
        # prune folders we emptied (never the Data folder itself)
        d = os.path.dirname(dest)
        while os.path.normcase(os.path.abspath(d)) != os.path.normcase(os.path.abspath(cfg.data_dir)):
            try:
                os.rmdir(d)
            except OSError:
                break
            d = os.path.dirname(d)
    _save_manifest(cfg, keep)
    return done


def loose_status(cfg):
    man = _load_manifest(cfg)
    out = []
    for key, e in sorted(man.items()):
        dest = e['dest']
        state = 'missing' if not os.path.exists(dest) else ('ok' if _sha(dest) == e['sha256'] else 'changed')
        out.append((e['path'], state, dest))
    return out


# ---------------------------------------------------------------------- patch MPQ


def pack(cfg, staging_dir=None, out=None, install=False, name=None):
    """Build an MPQ from the staging folder; with install=True copy it to Client\\Data. Returns a summary."""
    staging_dir = staging_dir or cfg.staging_dir
    name = name or cfg.patch_name
    if not _PATCH_NAME.match(name) or name.lower() in [n.lower() for n in STOCK_ARCHIVES]:
        raise DeployError('%s: the archive must be named patch-<3..9 or A..Z>.MPQ (the client mounts Data\\patch-?.MPQ, '
                          'highest name wins; patch.MPQ and patch-2.MPQ are stock)' % name)
    files = staging_files(staging_dir)
    if not files:
        raise DeployError('the staging folder %s is empty' % staging_dir)
    problems = check_staging(files)
    if problems:
        raise DeployError('staging has problems:\n  ' + '\n  '.join(problems))
    contents = {}
    for rel, full in files.items():
        with open(full, 'rb') as f:
            contents[rel] = f.read()
    os.makedirs(cfg.build_dir, exist_ok=True)
    out = out or os.path.join(cfg.build_dir, name)
    if os.path.normcase(os.path.dirname(os.path.abspath(out))) == os.path.normcase(os.path.abspath(cfg.data_dir)):
        raise DeployError('build into the build folder; use --install to put the archive into Client\\Data')
    summary = mpqwrite.build(contents, out, overwrite=True)
    problems = mpqwrite.verify(out, contents)
    if problems:
        raise DeployError('the built archive failed verification:\n  ' + '\n  '.join(problems))
    summary['verified'] = True
    summary['contents'] = list(contents)
    if install:
        dest = os.path.join(cfg.data_dir, name)
        if os.path.exists(dest):
            os.makedirs(cfg.backup_dir, exist_ok=True)
            b = os.path.join(cfg.backup_dir, '%s.%s.bak' % (name, _stamp()))
            shutil.copy2(dest, b)
            summary['backup'] = b
        tmp = dest + '.wowdata_tmp'
        try:
            shutil.copyfile(out, tmp)
            os.replace(tmp, dest)
        except PermissionError:
            if os.path.exists(tmp):
                os.remove(tmp)
            raise DeployError('could not write %s (is the game running?)' % dest)
        summary['installed'] = dest
        if _load_manifest(cfg):
            summary['note'] = ('loose files installed by wowdata are still in Client\\Data; with the loose-file patch they '
                               'win over every MPQ (run `wowdata loose remove` to test the archive alone)')
    return summary
