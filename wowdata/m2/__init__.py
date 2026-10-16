"""M2 models (1.12: MD20 version 0x100, views embedded).

  header.py    M2Header: a fast minimal reader (texture names, counts) used by the search index
  schema.py    every record layout (from the decompile; see its docstring)
  model.py     M2.parse / M2.serialize: the full structure, byte-identical round trip
  validate.py  the client's bounds checks plus cross-reference and sanity checks
  anim.py      track evaluation and per-sequence key extraction (client semantics)
"""
from .header import M2Header, ARRAYS  # noqa: F401
from .model import M2, M2Error, RecArray, Track, load  # noqa: F401
from .schema import TEXTURE_TYPES, BLEND_MODES, RENDER_FLAG_BITS  # noqa: F401
