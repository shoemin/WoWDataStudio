"""Read-only access to the server's world database (VMaNGOS schema, database `mangos`) via mysql.exe.

Only SELECT statements are ever sent (`query` refuses anything else). The mysql command line client is
used instead of a Python driver so that nothing has to be installed.

VMaNGOS keeps one template row per content patch (`patch` column, 0..10); the index uses the row with
the highest patch for every entry, which is what a 1.12 server loads.
"""
import re
import subprocess

from . import config as _config


class ServerDBError(Exception):
    pass


_SELECT = re.compile(r'^\s*(SELECT|SHOW)\b', re.I)


def _unescape(v):
    if v == 'NULL':
        return None
    if '\\' not in v:
        return v
    return v.replace('\\t', '\t').replace('\\n', '\n').replace('\\0', '\0').replace('\\\\', '\\')


def query(sql, cfg=None, timeout=300):
    """Rows (lists of strings / None) of one SELECT."""
    cfg = cfg or _config.load()
    for stmt in [s for s in sql.split(';') if s.strip()]:
        if not _SELECT.match(stmt):
            raise ServerDBError('only SELECT/SHOW statements are allowed: %r' % stmt.strip()[:60])
    cmd = [cfg.mysql_exe, '--host=%s' % cfg.mysql_host, '-u%s' % cfg.mysql_user, '-p%s' % cfg.mysql_password,
           '--default-character-set=utf8', '-N', '-B', cfg.mysql_database, '-e', sql]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise ServerDBError('mysql not available: %s' % e)
    if r.returncode != 0:
        err = r.stderr.decode('utf-8', 'replace').replace('Warning: Using a password on the command line interface can be insecure.', '').strip()
        raise ServerDBError('mysql failed: %s' % err)
    out = r.stdout.decode('utf-8', 'replace')
    rows = []
    for line in out.split('\n'):
        if line.endswith('\r'):
            line = line[:-1]
        if not line:
            continue
        rows.append([_unescape(v) for v in line.split('\t')])
    return rows


def available(cfg=None):
    try:
        query('SELECT 1', cfg, timeout=20)
        return True
    except ServerDBError:
        return False


def _latest(table, cols):
    return ('SELECT %s FROM %s t JOIN (SELECT entry, MAX(patch) p FROM %s GROUP BY entry) m '
            'ON t.entry = m.entry AND t.patch = m.p' % (', '.join('t.' + c for c in cols), table, table))


def creatures(cfg=None):
    cols = ['entry', 'name', 'subname', 'level_min', 'level_max', 'display_id1', 'display_id2', 'display_id3', 'display_id4']
    return query(_latest('creature_template', cols), cfg)


def creature_spawns(cfg=None):
    """(entry, map, count) for every spawned creature."""
    return query('SELECT id, map, COUNT(*) FROM creature GROUP BY id, map', cfg)


def gameobjects(cfg=None):
    return query(_latest('gameobject_template', ['entry', 'name', 'type', 'displayId']), cfg)


def gameobject_spawns(cfg=None):
    return query('SELECT id, map, COUNT(*) FROM gameobject GROUP BY id, map', cfg)


def items(cfg=None):
    return query(_latest('item_template', ['entry', 'name', 'display_id', 'inventory_type', 'quality', 'class', 'subclass']), cfg)
