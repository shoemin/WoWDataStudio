"""Oracle for the DBC editor (0.7.0): what the Python reference reads from every DBC of the client.

Writes <out>/dbc.json: per table the header (records, fields, record size, string block size) and, for the tables that
wowdata.dbc has a typed layout for, the value of every typed field of every row, keyed by the file field index (strings
as text, floats as their 32-bit pattern so the comparison is exact). The C# tests read the same files with the editor's
layouts and compare.

Usage (working directory: the WoWDataTool folder): python app/WoWData.Tests/oracle/make_dbc.py <out dir>
"""
import json
import os
import struct
import sys

for p in (os.getcwd(), os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))):
    if p not in sys.path:
        sys.path.insert(0, p)
from wowdata import config, dbc, vfs  # noqa: E402


def main(out):
    cfg = config.load()
    client = vfs.ClientData(cfg.data_dir, cache_dir=cfg.cache_dir)
    tables = {}
    for path in sorted(client.glob('DBFilesClient\\*.dbc'), key=str.lower):
        name = os.path.splitext(os.path.basename(path))[0]
        data = client.read(path)
        d = dbc.DBC(data, name)
        entry = {'count': d.count, 'fields': d.field_count, 'record_size': d.record_size, 'string_size': d.string_size}
        if d.schema is not None and name in dbc.SCHEMAS:
            cols = {}
            col = 0
            for f in d.schema:
                for _ in range(f.count):
                    vals = []
                    for r in range(d.count):
                        v = d.raw(r, col)
                        if f.kind in ('s', 'loc'):
                            vals.append(d.string(v))
                        elif f.kind == 'f':
                            vals.append(struct.unpack('<I', struct.pack('<f', d.raw_float(r, col)))[0])
                        elif f.kind == 'u':
                            vals.append(v & 0xFFFFFFFF)
                        else:
                            vals.append(v)
                    cols[str(col)] = {'kind': f.kind, 'name': f.name, 'values': vals}
                    col += dbc.LOCALES + 1 if f.kind == 'loc' else 1
            entry['typed'] = cols
        tables[name] = entry
    with open(os.path.join(out, 'dbc.json'), 'w', encoding='utf-8') as fh:
        json.dump(tables, fh)
    with open(os.path.join(out, 'done.txt'), 'w') as fh:
        fh.write('ok\n')


if __name__ == '__main__':
    main(sys.argv[1])
