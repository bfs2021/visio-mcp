import json, uuid, math
D = json.load(open('source-shapes.json'))
SH = D['shapes']
kids = {}
for s in SH:
    kids.setdefault(s['gid'], []).append(s)
HH = 8.26771653543307
PW = 11.69291338582677
PT = 72
P = str(uuid.uuid4())
LC, LS = str(uuid.uuid4()), str(uuid.uuid4())
ops = [
    {"op": "addPage", "id": P, "name": "Lego Infrastructure", "widthPt": round(PW * PT, 2), "heightPt": round(HH * PT, 2)},
    {"op": "setPage", "pageId": P, "patch": {"background": {"kind": "solid", "colour": "#FFFFFF", "opacity": 1}, "grid": {"visible": False, "snap": False}}},
    {"op": "addLayer", "pageId": P, "id": LC, "name": "Connector"},
    {"op": "addLayer", "pageId": P, "id": LS, "name": "symbols"},
]


def X(x): return round(x * PT, 2)
def Y(y): return round((HH - y) * PT, 2)


def fl(c, k, d=None):
    try:
        return float(c[k])
    except Exception:
        return d


def paint(c, op=1):
    return {"kind": "none"} if c is None else {"kind": "solid", "colour": c, "opacity": op}


def rot(x, y, a):
    ca, sa = math.cos(a), math.sin(a)
    return x * ca - y * sa, x * sa + y * ca


def frame(s):
    c = s['c']
    w, h = s['w'], s['h']
    lx, ly = fl(c, 'LocPinX', w / 2), fl(c, 'LocPinY', h / 2)
    a = s['ang']
    dx, dy = rot(w / 2 - lx, h / 2 - ly, a)
    return s['cx'] - dx, s['cy'] - dy, lx, ly, a


def fillof(s):
    c = s['c']
    if c.get('FillPattern') == '0':
        return None
    return c.get('FillForegnd')


def lineof(s):
    c = s['c']
    if c.get('LinePattern') == '0':
        return None, 1, 'solid'
    col = c.get('LineColor') or '#000000'
    wt = fl(c, 'LineWeight')
    wt = round(wt * PT, 2) if wt else 0.75
    pat = c.get('LinePattern')
    dash = 'dashed' if pat in ('23', '2', '4', '9') else ('dotted' if pat == '3' else 'solid')
    return col, wt, dash


def textblock(s):
    ch = s.get('char') or {}
    size = fl(ch, 'Size')
    size = round(size * PT, 1) if size else 10
    st = int(float(ch.get('Style', '0')))
    col = ch.get('Color') or '#000000'
    return {"value": s['text'], "fontFamily": "Calibri", "fontSizePt": size, "bold": bool(st & 1), "italic": bool(st & 2),
            "underline": bool(st & 4), "colour": col, "horizontalAlign": "center", "verticalAlign": "middle", "wrap": True, "paddingPt": 1}


def layer_of(s, override=None):
    lm = override if override is not None else s['c'].get('LayerMember', '')
    return [LC] if lm == '0' else ([LS] if lm == '1' else [])


def bounds_of(s):
    return {"x": X(s['cx'] - s['w'] / 2), "y": Y(s['cy'] + s['h'] / 2), "width": round(abs(s['w']) * PT, 2), "height": round(abs(s['h']) * PT, 2)}


def emit_shape(s, preset=None, svg=None, fill=..., layer=None, text=True, stroke=...):
    f = fillof(s) if fill is ... else fill
    col, wt, dash = lineof(s) if stroke is ... else stroke
    g = {"svgPath": svg} if svg else {"preset": preset or "rectangle"}
    o = {"op": "createShape", "pageId": P, "bounds": bounds_of(s), "geometry": g,
         "style": {"fill": paint(f, round(1 - (fl(s["c"], "FillForegndTrans", 0) or 0), 2)), "stroke": paint(col), "strokeWidthPt": wt, "dash": dash}}
    if s['ang']:
        o["rotationDeg"] = round(-s['ang'] * 180 / math.pi, 2)
    if s['text'] and text:
        o["text"] = textblock(s)
    lids = layer_of(s, layer)
    if lids:
        o["layerIds"] = lids
    o["name"] = s['id']
    ops.append(o)


def polyline(s, layer=None):
    c = s['c']
    px, py, lx, ly, a = frame(s)
    w, h = s['w'], s['h']
    pts = []
    cur = (0, 0)
    for rt, rc in s['geom'][0]:
        x = fl(rc, 'X', cur[0])
        y = fl(rc, 'Y', cur[1])
        cur = (x, y)
        pts.append(cur)
    if s['geom'][0][0][0] == 'LineTo':
        pts = [(0, 0)] + pts
    if len(pts) < 2:
        return False
    out = []
    for x, y in pts:
        if c.get('FlipX') == '1':
            x = w - x
        if c.get('FlipY') == '1':
            y = h - y
        dx, dy = rot(x - lx, y - ly, a)
        out.append((px + dx, py + dy))
    ba, ea = c.get('BeginArrow', '0'), c.get('EndArrow', '0')
    col = c.get('LineColor') or '#000000'
    wt = fl(c, 'LineWeight')
    wt = round(wt * PT, 2) if wt else 0.75
    o = {"op": "createConnector", "pageId": P, "name": s['id'],
         "from": {"point": {"x": X(out[0][0]), "y": Y(out[0][1])}, "glue": "none"},
         "to": {"point": {"x": X(out[-1][0]), "y": Y(out[-1][1])}, "glue": "none"},
         "route": "straight", "waypoints": [{"x": X(x), "y": Y(y)} for x, y in out[1:-1]],
         "style": {"colour": col, "widthPt": wt, "dash": "solid", "startArrow": "block" if ba != '0' else "none", "endArrow": "block" if ea != '0' else "none"}}
    lids = layer_of(s, layer)
    if lids:
        o["layerIds"] = lids
    ops.append(o)
    return True


HOP = "M 0 100 C 0 -33 100 -33 100 100"


def emit(s):
    gt = s['geom'][0] if s['geom'] else []
    kinds = ''.join(t[0][0] for t in gt)
    types = [t[0] for t in gt]
    if kinds and kinds[0] == 'R':
        emit_shape(s)
    elif kinds == 'E':
        emit_shape(s, "ellipse")
    elif any(t.endswith('ArcTo') for t in types):
        emit_shape(s, svg=HOP, fill=None, stroke=('#000000', 1.0, 'solid'))
    elif types and set(types) <= {'MoveTo', 'LineTo'}:
        if not polyline(s):
            print('skip poly', s['id'])
    else:
        print('unhandled', s['id'], types)


def special(g):
    ch = kids.get(g['id'], [])
    for k in ch:
        if (k['master'] or '').startswith('Database'):
            emit_shape(dict(k, text='', ang=0), "cylinder", fill='#2a7bc4', layer='1', stroke=('#1f5f9b', 1, 'solid'))
            for t in ch:
                if t['text']:
                    emit_shape(t, fill=None, stroke=(None, 1, 'solid'))
            return True
        if k['master'] == 'Cloud':
            emit_shape(dict(k, text='', ang=0), "cloud", fill='#a5a5a5', layer='1', stroke=('#7f7f7f', 1, 'solid'))
            for t in ch:
                if t['text']:
                    emit_shape(t, fill=None, stroke=(None, 1, 'solid'))
            return True
        if (k['master'] or '').startswith('Azure'):
            box = [t for t in ch if t is not k and t['geom']][0]
            emit_shape(box)
            emit_shape(dict(k, text='', ang=0), svg="M 50 0 L 100 50 L 50 100 L 0 50 Z", fill='#0070c0', layer='1', stroke=(None, 1, 'solid'))
            lab = dict(box, text='Azure\nActive Directory', cy=box['cy'] - 0.2, h=0.3, c=dict(box['c'], FillPattern='0'), char={'Color': '#0070c0', 'Size': '0.1111111111111111'})
            emit_shape(lab, fill=None, stroke=(None, 1, 'solid'))
            return True
    return False


def rec(g):
    for k in kids.get(g['id'], []):
        if k['type'] == 'Group':
            rec(k)
        else:
            emit(k)


for s in SH:
    if s['depth'] != 0:
        continue
    if s['type'] == 'Group':
        if not special(s):
            rec(s)
    else:
        emit(s)
json.dump(ops, open('ops.json', 'w'))
print(len(ops))
