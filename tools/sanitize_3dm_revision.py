"""Neutralize the revision-history usernames in the shipped .3dm resources (AUD-REL-005).

openNURBS stamps the OS username into ON_3dmRevisionHistory (created by /
last edited by) on every save, and rhino3dm exposes the fields read-only -
so the 2026-07-17 sanitize rebuild itself wrote the maintainer's login into
the files it was cleaning. This script patches the strings in place:

- locates the TCODE_PROPERTIES_REVISIONHISTORY chunk (0x20008021) inside the
  properties table (0x10000014) by walking the 3dm chunk stream (V50+ layout:
  uint32 typecode + int64 length; short chunks carry the 0x80000000 bit),
- replaces every UTF-16LE occurrence of the username with an equal-length
  neutral string inside that chunk only,
- recomputes the chunk's trailing CRC (standard zlib crc32 over the payload
  minus its last 4 bytes - verified against the stored value before patching),
- validates the result with rhino3dm: readable, identical object count and
  per-object bounding boxes, neutralized CreatedBy/LastEditedBy.

Usage:  python sanitize_3dm_revision.py <file.3dm> <username> [replacement]
The replacement must have the same character count (default: 'private').
The original file is replaced only when every validation passes; a .bak
copy is left next to it.
"""
import struct
import sys
import zlib

import rhino3dm

TCODE_SHORT = 0x80000000
TCODE_PROPERTIES_TABLE = 0x10000014
TCODE_REVISION_HISTORY = 0x20008021


def walk_chunks(buf, base, end):
    pos = base
    while pos + 12 <= end:
        tc, ln = struct.unpack_from('<Iq', buf, pos)
        if tc in (0, 0xFFFFFFFF):
            return
        yield pos, tc, ln
        pos += 12 + (0 if tc & TCODE_SHORT else max(ln, 0))


def summarize(path):
    f = rhino3dm.File3dm.Read(path)
    if f is None:
        return None
    boxes = []
    for o in f.Objects:
        bb = o.Geometry.GetBoundingBox()
        boxes.append(tuple(round(v, 9) for v in
                     (bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z)))
    return {'count': len(list(f.Objects)), 'boxes': sorted(boxes),
            'createdBy': f.CreatedBy, 'lastEditedBy': f.LastEditedBy}


def patch(path, username, replacement):
    old = username.encode('utf-16-le')
    new = replacement.encode('utf-16-le')
    if len(old) != len(new):
        raise SystemExit('replacement must have the same character count as the username')

    before = summarize(path)
    if before is None:
        raise SystemExit(f'{path}: rhino3dm cannot read the original file')

    data = bytearray(open(path, 'rb').read())
    table = next((p for p, tc, ln in walk_chunks(data, 32, len(data))
                  if tc == TCODE_PROPERTIES_TABLE), None)
    if table is None:
        raise SystemExit(f'{path}: no properties table chunk')
    tlen = struct.unpack_from('<q', data, table + 4)[0]
    rev = next(((p, ln) for p, tc, ln in walk_chunks(data, table + 12, table + 12 + tlen)
                if tc == TCODE_REVISION_HISTORY), None)
    if rev is None:
        raise SystemExit(f'{path}: no revision-history chunk')
    rpos, rlen = rev
    body_start, body_end = rpos + 12, rpos + 12 + rlen - 4
    stored = struct.unpack_from('<I', data, body_end)[0]
    body = bytes(data[body_start:body_end])
    if zlib.crc32(body) & 0xFFFFFFFF != stored:
        raise SystemExit(f'{path}: stored chunk CRC does not match zlib.crc32 - unknown layout, aborting')

    hits = body.count(old)
    if hits == 0:
        print(f'{path}: username not present in the revision history - nothing to do')
        return
    patched = body.replace(old, new)
    data[body_start:body_end] = patched
    struct.pack_into('<I', data, body_end, zlib.crc32(patched) & 0xFFFFFFFF)
    if old in data:
        raise SystemExit(f'{path}: username still present outside the revision chunk - aborting')

    tmp = path + '.sanitized'
    open(tmp, 'wb').write(data)
    after = summarize(tmp)
    ok = (after is not None
          and after['count'] == before['count']
          and after['boxes'] == before['boxes']
          and username not in (after['createdBy'] + after['lastEditedBy']))
    if not ok:
        raise SystemExit(f'{path}: validation failed after patch '
                         f'(readable={after is not None}) - original untouched, see {tmp}')

    import shutil, os
    shutil.copy2(path, path + '.bak')
    os.replace(tmp, path)
    print(f'{path}: {hits} occurrence(s) replaced; createdBy={after["createdBy"]!r} '
          f'lastEditedBy={after["lastEditedBy"]!r}; geometry identical '
          f'({after["count"]} objects); backup at {path}.bak')


if __name__ == '__main__':
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    patch(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 'private')
