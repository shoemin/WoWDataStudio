"""Paths and settings.

Defaults assume the layout of this machine (`WoWDataTool` next to `Client` and `Server`).
Override any key in `wowdata.json` at the tool root (see `wowdata.example.json`) or with the
environment variable `WOWDATA_<KEY>` (e.g. `WOWDATA_CLIENT_DIR`).
"""
import json
import os

TOOL_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_PARENT = os.path.dirname(TOOL_ROOT)

DEFAULTS = {
    'client_dir': os.path.join(_PARENT, 'Client'),
    'server_dbc_dir': os.path.join(_PARENT, 'Server', 'data', '5875', 'dbc'),
    'mysql_exe': os.path.join(_PARENT, 'Server', 'mysql5', 'bin', 'mysql.exe'),
    'mysql_host': '127.0.0.1',
    'mysql_user': 'root',
    'mysql_password': 'root',
    'mysql_database': 'mangos',
    'staging_dir': os.path.join(TOOL_ROOT, 'staging'),
    'export_dir': os.path.join(TOOL_ROOT, 'work', 'export'),
    'extract_dir': os.path.join(TOOL_ROOT, 'work', 'extract'),
    'build_dir': os.path.join(TOOL_ROOT, 'build'),
    'backup_dir': os.path.join(TOOL_ROOT, 'backups'),
    'cache_dir': os.path.join(TOOL_ROOT, 'cache'),
    'state_dir': os.path.join(TOOL_ROOT, 'state'),
    'patch_name': 'patch-3.MPQ',
}


class Config(dict):
    def __getattr__(self, key):
        try:
            return self[key]
        except KeyError:
            raise AttributeError(key)

    @property
    def data_dir(self):
        return os.path.join(self['client_dir'], 'Data')

    @property
    def index_path(self):
        return os.path.join(self['cache_dir'], 'asset_index.sqlite')


def load(path=None):
    cfg = Config(DEFAULTS)
    path = path or os.path.join(TOOL_ROOT, 'wowdata.json')
    if os.path.exists(path):
        with open(path, 'r', encoding='utf-8') as f:
            user = json.load(f)
        for k, v in user.items():
            if not k.startswith('_'):
                cfg[k] = v
    for k in list(cfg):
        env = os.environ.get('WOWDATA_' + k.upper())
        if env:
            cfg[k] = env
    return cfg
