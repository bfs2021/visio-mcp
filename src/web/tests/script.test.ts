import { describe, expect, it } from 'vitest';
import type { Operation, PreparedAssetRef } from '../src/contracts/generated';
import { compile, MAX_OPERATIONS, validateScript, type CompileContext } from '../src/script/compiler';
import { lexLine } from '../src/script/lexer';
import { parse } from '../src/script/parser';
import { canonicalSerialize } from '../src/model/canonical';
import { createTestEngine, exactElementHashes, sequenceIds } from './support/engine';
import { byId, id, run } from './support/ops';

const MM = 72 / 25.4;
type T = ReturnType<typeof createTestEngine>;

/** The seed plus a shape alias `logo` and, separately, an image alias `logoimg`, so both kinds of target exist. */
async function scene(): Promise<T> {
  const t = createTestEngine();
  await run(t, [
    { op: 'createShape', id: id(960), alias: 'logo', bounds: { x: 10, y: 10, width: 80, height: 40 } },
    { op: 'createImage', id: id(961), alias: 'logoimg', bounds: { x: 10, y: 80, width: 40, height: 40 }, assetId: 'asset:logo' },
  ]);
  return t;
}

const ctxFor = (t: T, extra: Partial<CompileContext> = {}): CompileContext => ({ snapshot: t.store.snapshot(), newUuid: sequenceIds('7'), ...extra });
const ops = (t: T, source: string, extra: Partial<CompileContext> = {}): Operation[] => {
  const r = compile(source, ctxFor(t, extra));
  if (!r.ok) throw new Error(`${r.error.message}`);
  return r.value.operations;
};
const bad = (t: T, source: string, extra: Partial<CompileContext> = {}) => {
  const r = compile(source, ctxFor(t, extra));
  if (r.ok) throw new Error('expected a compile error');
  return r.error;
};
const U = (n: number) => `00000000-0000-4000-8000-7${String(n).padStart(11, '0')}`;

describe('lexer and parser', () => {
  it('keeps # inside quotes and treats it as a comment outside', () => {
    const cmds = parse('add shape id=a text="#1 # not a comment" # but this is\n# whole line\n\nset a fill="#FFFFFF"');
    expect(cmds).toHaveLength(2);
    expect(cmds[0]!.named.find(n => n.key === 'text')!.value).toMatchObject({ type: 'string', value: '#1 # not a comment' });
    expect(cmds[1]!.span.line).toBe(4);
  });

  it('reads JSON escapes in strings', () => {
    const [cmd] = parse('set a text="line1\\nline2 \\"quoted\\" \\u00e9 \\\\"');
    expect(cmd!.named[0]!.value).toMatchObject({ value: 'line1\nline2 "quoted" é \\' });
  });

  it('reports bad strings and stray characters with line and column', () => {
    expect(() => lexLine('set a text="oops', 3)).toThrow(/not closed/);
    expect(() => lexLine('set a text="bad \\q"', 1)).toThrow(/invalid escape/);
    try { lexLine('add shape @@', 7); } catch (e) { expect((e as { span: { line: number; column: number } }).span).toMatchObject({ line: 7, column: 11 }); }
    expect(() => parse('set a x= 3mm')).toThrow(/no value/);
    expect(() => lexLine('set a =3', 1)).toThrow(/equals sign/);
  });

  it('numbers carry their unit and words may contain dots and hyphens', () => {
    const [cmd] = parse('set-gap vertical a.b c-d 20mm');
    expect(cmd!.verb).toBe('set-gap');
    expect(cmd!.positionals.map(p => (p.type === 'word' ? p.text : p.type === 'number' ? `${p.value}${p.unit}` : '?'))).toEqual(['vertical', 'a.b', 'c-d', '20mm']);
  });

  it('rejects duplicate fields', () => {
    expect(() => parse('add shape x=1mm x=2mm')).toThrow(/given twice/);
  });
});

describe('compiler: every source command family', () => {
  it('page use, add shape/text/image, set and connect from the specification example', async () => {
    const t = await scene();
    await run(t, [{ op: 'addPage', id: id(800), name: 'Architecture' }, { op: 'registerAsset', asset: { id: 'asset:corona-energy-logo', name: 'Corona', mimeType: 'image/png', sha256: 'e'.repeat(64), tags: [] } }]);
    const source = `page use "Architecture"

add shape id=elexon type=roundedRect x=90mm y=25mm w=70mm h=20mm
set elexon text="Elexon"
set elexon fill="#FFFFFF" stroke="#53606F" strokeWidth=1pt

add shape id=dip type=roundedRect x=90mm y=65mm w=70mm h=25mm
set dip text="Data Integration Platform (DIP)"

add image id=corona asset="asset:corona-energy-logo" x=25mm y=125mm w=45mm h=20mm fit=contain

connect id=elexon_dip from=elexon.south to=dip.north route=orthogonal endArrow=triangle

align hcenter elexon dip
set-gap vertical elexon dip 20mm`;
    const result = ops(t, source);
    const page = id(800);
    expect(result).toEqual([
      { op: 'createShape', id: U(1), pageId: page, alias: 'elexon', bounds: { x: 90 * MM, y: 25 * MM, width: 70 * MM, height: 20 * MM }, geometry: { preset: 'rounded-rectangle' } },
      { op: 'set', target: U(1), patch: { text: { value: 'Elexon' } } },
      { op: 'set', target: U(1), patch: { style: { fill: { kind: 'solid', colour: '#ffffff', opacity: 1 }, stroke: { kind: 'solid', colour: '#53606f', opacity: 1 }, strokeWidthPt: 1 } } },
      { op: 'createShape', id: U(2), pageId: page, alias: 'dip', bounds: { x: 90 * MM, y: 65 * MM, width: 70 * MM, height: 25 * MM }, geometry: { preset: 'rounded-rectangle' } },
      { op: 'set', target: U(2), patch: { text: { value: 'Data Integration Platform (DIP)' } } },
      { op: 'createImage', id: U(3), pageId: page, alias: 'corona', bounds: { x: 25 * MM, y: 125 * MM, width: 45 * MM, height: 20 * MM }, assetId: 'asset:corona-energy-logo', fit: 'contain' },
      { op: 'createConnector', id: U(4), pageId: page, alias: 'elexon_dip', from: { elementId: U(1), port: 'south', glue: 'static' }, to: { elementId: U(2), port: 'north', glue: 'static' }, route: 'orthogonal', style: { endArrow: 'block' } },
      { op: 'align', targets: [U(1), U(2)], edge: 'center' },
      { op: 'setGap', targets: [U(1), U(2)], axis: 'vertical', gapPt: 20 * MM },
    ]);
  });

  it('the whole script runs on the page it names and leaves the other page alone', async () => {
    const t = await scene();
    await run(t, [{ op: 'addPage', id: id(800), name: 'Second' }]);
    const before = t.store.snapshot().document.pages[0]!;
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7100), baseRevision: t.engine.revision, script: 'page use "Second"\nadd shape id=n x=10mm y=10mm w=20mm h=10mm\nset n text="Hi"' }, (src, snap, c) => {
      const out = compile(src, { snapshot: snap, newUuid: sequenceIds('7'), ...c });
      return out;
    });
    expect(r.ok).toBe(true);
    const doc = t.store.snapshot().document;
    expect(doc.pages[0]).toBe(before);
    expect(doc.pages[1]!.elements).toHaveLength(1);
    expect(doc.pages[1]!.elements[0]!.alias).toBe('n');
  });

  it('modify commands: set, move, resize, rotate, delete, duplicate', async () => {
    const t = await scene();
    expect(ops(t, 'move logo x=5mm y=6mm')).toEqual([{ op: 'resize', target: id(960), bounds: { x: 5 * MM, y: 6 * MM } }]);
    expect(ops(t, 'move logo dx=3mm dy=0mm')).toEqual([{ op: 'move', target: id(960), delta: { xPt: 3 * MM, yPt: 0 } }]);
    expect(ops(t, 'resize logo w=50mm')).toEqual([{ op: 'resize', target: id(960), bounds: { width: 50 * MM } }]);
    expect(ops(t, 'rotate logo angle=45deg')).toEqual([{ op: 'rotate', target: id(960), angleDeg: 45 }]);
    expect(ops(t, 'rotate logo by=-10deg')).toEqual([{ op: 'rotate', target: id(960), deltaDeg: -10 }]);
    expect(ops(t, 'delete logo withConnectors=true')).toEqual([{ op: 'delete', target: id(960), withConnectors: true }]);
    expect(ops(t, 'duplicate logo as=twin dx=5mm')).toEqual([{ op: 'duplicate', targets: [id(960)], as: 'twin', id: U(1), offset: { xPt: 5 * MM, yPt: 0 } }]);
    expect(ops(t, 'set logoimg asset="asset:logo-v2"\nmove logoimg dx=3mm dy=0mm')).toEqual([
      { op: 'set', target: id(961), patch: { assetId: 'asset:logo-v2' } },
      { op: 'move', target: id(961), delta: { xPt: 3 * MM, yPt: 0 } },
    ]);
  });

  it('the copy made by duplicate can be named later in the same script', async () => {
    const t = await scene();
    const r = ops(t, 'duplicate logo as=twin dx=30mm\nset twin text="copy"');
    expect(r[1]).toEqual({ op: 'set', target: U(1), patch: { text: { value: 'copy' } } });
  });

  it('arrange: align, distribute, set-gap, z; layers; groups', async () => {
    const t = await scene();
    await run(t, [{ op: 'group', targets: ['s0', 's1'], id: id(970), alias: 'pair' }]);
    expect(ops(t, 'align left s2 s3')).toEqual([{ op: 'align', targets: [t.ids.shapes[2], t.ids.shapes[3]], edge: 'left' }]);
    expect(ops(t, 'distribute horizontal s2 s3 s4')).toEqual([{ op: 'distribute', targets: [2, 3, 4].map(i => t.ids.shapes[i]), axis: 'horizontal' }]);
    expect(ops(t, 'z front s2 s3')).toEqual([{ op: 'reorder', targets: [2, 3].map(i => t.ids.shapes[i]), to: 'front' }]);
    expect(ops(t, 'layer add "Notes" visible=false\nlayer assign "Notes" s2 s3 mode=add\nlayer set "Notes" locked=true')).toEqual([
      { op: 'addLayer', pageId: t.ids.page, id: U(1), name: 'Notes', visible: false },
      { op: 'assignLayers', targets: [2, 3].map(i => t.ids.shapes[i]), layerIds: [U(1)], mode: 'add' },
      { op: 'setLayer', layerId: U(1), patch: { locked: true } },
    ]);
    expect(ops(t, 'group id=trio s2 s3 s4')[0]).toEqual({ op: 'group', targets: [2, 3, 4].map(i => t.ids.shapes[i]), id: U(1), alias: 'trio' });
    expect(ops(t, 'rotate pair by=30deg pivotX=50mm pivotY=40mm')).toEqual([{ op: 'rotate', target: id(970), deltaDeg: 30, pivot: { xPt: 50 * MM, yPt: 40 * MM } }]);
    expect(ops(t, 'delete pair children=ungroup')).toEqual([{ op: 'delete', target: id(970), children: 'ungroup' }]);
  });

  it('pages: add, rename, size', async () => {
    const t = await scene();
    const r = ops(t, 'page add "Detail" w=200mm h=100mm\npage rename "Detail" name="Details"\npage size "Details" h=120mm\npage size w=300mm');
    expect(r).toEqual([
      { op: 'addPage', id: U(1), name: 'Detail', widthPt: 200 * MM, heightPt: 100 * MM },
      { op: 'setPage', pageId: U(1), patch: { name: 'Details' } },
      { op: 'setPage', pageId: U(1), patch: { heightPt: 120 * MM } },
      { op: 'setPage', pageId: t.ids.page, patch: { widthPt: 300 * MM } },
    ]);
  });

  it('assets: import prepared bytes, replace on images, replace everywhere', async () => {
    const t = await scene();
    const asset = { id: 'asset:fresh', name: 'Fresh', mimeType: 'image/png' as const, sha256: 'f'.repeat(64), tags: ['x'] };
    const ref: PreparedAssetRef = { preparationId: 'prep-1', expiresAt: '2030-01-01T00:00:00Z', asset };
    const r = ops(t, 'asset import prepared=up1\nadd image id=fresh asset="asset:fresh" x=0mm y=0mm w=10mm h=10mm\nasset replace logoimg to="asset:fresh"\nasset replace-all from="asset:logo-v2" to="asset:fresh"', { preparedAssetRefs: { up1: ref } });
    expect(r[0]).toEqual({ op: 'registerAsset', asset });
    expect(r[1]).toMatchObject({ op: 'createImage', assetId: 'asset:fresh' });
    expect(r[2]).toEqual({ op: 'replaceAsset', from: 'asset:logo', to: 'asset:fresh', targets: [id(961)] });
    expect(r[3]).toEqual({ op: 'replaceAsset', from: 'asset:logo-v2', to: 'asset:fresh' });
  });

  it('assets: a library picture whose id the document pins to other bytes is registered as a separate version, and the script means that version', async () => {
    const t = await scene();
    const asset = { id: 'asset:logo', name: 'Logo', mimeType: 'image/png' as const, sha256: 'e'.repeat(64), tags: [] as string[] };
    const ref: PreparedAssetRef = { preparationId: 'prep-2', expiresAt: '2030-01-01T00:00:00Z', asset };
    const versionId = `asset:logo~${'e'.repeat(64)}`;
    const r = ops(t, ['asset import prepared=lib', 'add image id=new asset="asset:logo" x=0mm y=0mm w=10mm h=10mm', 'asset replace logoimg to="asset:logo"'].join('\n'), { preparedAssetRefs: { lib: ref } });
    expect(r[0]).toEqual({ op: 'registerAsset', asset: { ...asset, id: versionId } });
    expect(r[1]).toMatchObject({ op: 'createImage', assetId: versionId });
    expect(r[2]).toEqual({ op: 'replaceAsset', from: 'asset:logo', to: versionId, targets: [id(961)] });
  });
});

describe('compiler: typing, syntax rules and diagnostics', () => {
  it('colours, booleans, dimensions, angles and units are typed', async () => {
    const t = await scene();
    expect(compile('set logo stroke="#333333"', ctxFor(t)).ok).toBe(true);
    expect(ops(t, 'set logo fill="#11223380" locked=true bold=true')[0]).toEqual({
      op: 'set', target: id(960), patch: { style: { fill: { kind: 'solid', colour: '#112233', opacity: 0.502 } }, text: { bold: true }, locked: true },
    });
    expect(ops(t, 'set logo fill=none')[0]).toMatchObject({ patch: { style: { fill: { kind: 'none' } } } });
    expect(bad(t, 'set logo fill=#FFFFFF').details).toBeDefined(); // unquoted # starts a comment, leaving nothing
    expect(bad(t, 'set logo fill="red"').message).toMatch(/#RRGGBB/);
    expect(bad(t, 'set logo locked=yes').message).toMatch(/true or false/);
    expect(bad(t, 'move logo dx=10 dy=0mm').message).toMatch(/needs a unit/);
    expect(bad(t, 'move logo dx=10furlongs').message).toMatch(/unknown unit/);
    expect(bad(t, 'rotate logo angle=45').message).toMatch(/deg/);
    expect(bad(t, 'set logo strokeWidth=hairline').message).toMatch(/dimension/);
  });

  it('mixed absolute and delta moves, mixed angle and by, half pivots and unknown fields are rejected', async () => {
    const t = await scene();
    await run(t, [{ op: 'group', targets: ['s0', 's1'], id: id(970), alias: 'pair' }]);
    expect(bad(t, 'move logo x=1mm dx=2mm').message).toMatch(/not both/);
    expect(bad(t, 'rotate logo angle=1deg by=2deg').message).toMatch(/not both/);
    expect(bad(t, 'rotate pair by=5deg pivotX=1mm').message).toMatch(/both pivotX/);
    expect(bad(t, 'rotate logo by=5deg pivotX=1mm pivotY=1mm').message).toMatch(/only to a group/);
    expect(bad(t, 'rotate pair angle=10deg').details?.hint).toMatch(/by=/);
    expect(bad(t, 'move logo sideways=3mm').message).toMatch(/unknown field "sideways"/);
    expect(bad(t, 'set logo').message).toMatch(/at least one/);
    expect(bad(t, 'frobnicate logo').message).toMatch(/unknown command/);
    expect(bad(t, 'delete pair').message).toMatch(/children=/);
    expect(bad(t, 'set logo asset="asset:logo"').message).toMatch(/images only/);
  });

  it('set applies only what it names and only to what it can', async () => {
    const t = await scene();
    expect(ops(t, 'set logo stroke="#333333"')).toEqual([{ op: 'set', target: id(960), patch: { style: { stroke: { kind: 'solid', colour: '#333333', opacity: 1 } } } }]);
    expect(bad(t, 'set logoimg stroke="#333333"').message).toMatch(/no line or fill/);
    expect(bad(t, 'set logoimg text="x"').message).toMatch(/no text/);
  });

  it('hcenter is a synonym for center', async () => {
    const t = await scene();
    expect(ops(t, 'align hcenter s1 s2')[0]).toMatchObject({ edge: 'center' });
    expect(ops(t, 'align center s1 s2')[0]).toMatchObject({ edge: 'center' });
    expect(ops(t, 'align vcenter s1 s2')[0]).toMatchObject({ edge: 'middle' });
  });

  it('errors carry the original line and column', async () => {
    const t = await scene();
    const e = bad(t, 'set logo fill="#FFFFFF"\nmove logo dx=10 dy=0mm\nset logo locked=true');
    expect(e.details?.line).toBe(2);
    expect(e.details?.column).toBe(14);
    expect(e.message).toMatch(/line 2, column 14/);
    const e1 = bad(t, '\n\nadd shape id=a x=1mm');
    expect(e1.details?.line).toBe(3);
  });

  it('a name or alias that does not resolve says so and ambiguity lists candidates', async () => {
    const t = await scene();
    await run(t, [{ op: 'set', target: 's2', patch: { name: 'Twin' } }, { op: 'set', target: 's3', patch: { name: 'Twin' } }]);
    expect(bad(t, 'move nothing dx=1mm dy=1mm').message).toMatch(/nothing is called/);
    const e = bad(t, 'move "Twin" dx=1mm dy=1mm');
    expect(e.message).toMatch(/matches 2 elements/);
    expect(e.details?.hint).toMatch(new RegExp(t.ids.shapes[2]!));
  });

  it('a script cannot reuse an alias it or the page already has', async () => {
    const t = await scene();
    expect(bad(t, 'add shape id=logo x=1mm y=1mm w=5mm h=5mm').message).toMatch(/already used/);
    expect(bad(t, 'add shape id=a x=1mm y=1mm w=5mm h=5mm\nadd shape id=a x=1mm y=1mm w=5mm h=5mm').details?.line).toBe(2);
  });

  it('dotted aliases: the whole alias first, then alias.port; an ambiguous token must be spelled out', async () => {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(980), alias: 'srv', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(981), alias: 'srv.db', bounds: { x: 0, y: 220, width: 10, height: 10 } },
      { op: 'createShape', id: id(982), alias: 'solo.x', bounds: { x: 0, y: 240, width: 10, height: 10 } },
    ]);
    // srv.db is an alias of its own and srv exists too: ambiguous
    const e = bad(t, 'connect from=srv.db to=logo');
    expect(e.message).toMatch(/could be the alias/);
    expect(e.details?.hint).toMatch(/fromAlias=/);
    // spelled out both ways
    expect(ops(t, 'connect fromAlias="srv.db" to=logo')[0]).toMatchObject({ from: { elementId: id(981), glue: 'dynamic' } });
    expect(ops(t, 'connect fromAlias="srv" fromPort="db" to=logo')[0]).toMatchObject({ from: { elementId: id(980), port: 'db', glue: 'static' } });
    expect(ops(t, 'connect from="srv.db" to=logo')[0]).toMatchObject({ from: { elementId: id(981), glue: 'dynamic' } }); // a quoted token is one whole alias
    // arrowhead sizes are plain fields on a connector
    expect(ops(t, 'connect from=solo.x to=logo.east endArrowSize=large startArrow=open startArrowSize=small')[0]).toMatchObject({ style: { endArrowSize: 'large', startArrow: 'open', startArrowSize: 'small' } });
    expect(bad(t, 'connect from=solo.x to=logo.east endArrowSize=huge').message).toMatch(/endArrowSize/);
    // only one reading exists: no ambiguity
    expect(ops(t, 'connect from=solo.x to=logo.east')[0]).toMatchObject({ from: { elementId: id(982), glue: 'dynamic' }, to: { elementId: id(960), port: 'east', glue: 'static' } });
    expect(ops(t, 'connect from=logo.south toX=10mm toY=20mm')[0]).toMatchObject({ to: { glue: 'none', point: { x: 10 * MM, y: 20 * MM } } });
  });

  it('page use must name an existing page, and a many-page document needs one', async () => {
    const t = await scene();
    await run(t, [{ op: 'addPage', id: id(800), name: 'Second' }]);
    expect(bad(t, 'page use "Nope"').message).toMatch(/no page called/);
    expect(bad(t, 'add shape id=q x=1mm y=1mm w=5mm h=5mm').message).toMatch(/more than one page/);
    expect(ops(t, 'page use "Page 1"\nadd shape id=q x=1mm y=1mm w=5mm h=5mm')[0]).toMatchObject({ pageId: t.ids.page });
  });

  it('every operation carries the span of its own command', async () => {
    const t = await scene();
    const r = compile('# header\nset logo locked=true\n\nmove logo dx=1mm dy=0mm', ctxFor(t));
    expect(r.ok && r.value.sourceSpans.map(s => [s.line, s.command])).toEqual([[2, 'set logo locked=true'], [4, 'move logo dx=1mm dy=0mm']]);
  });

  it('1001_operations_reject and the byte limit apply before anything runs', async () => {
    const t = await scene();
    const many = Array.from({ length: MAX_OPERATIONS + 1 }, (_, i) => `add shape id=s_${i} x=1mm y=1mm w=5mm h=5mm`).join('\n');
    const e = bad(t, many);
    expect(e.message).toMatch(/at most 1000/);
    expect(e.details?.line).toBe(MAX_OPERATIONS + 1);
    const exactly = Array.from({ length: MAX_OPERATIONS }, (_, i) => `add shape id=s_${i} x=1mm y=1mm w=5mm h=5mm`).join('\n');
    expect(compile(exactly, ctxFor(t)).ok).toBe(true);
    const huge = compile('#'.padEnd(1024 * 1024 + 1, 'x'), ctxFor(t));
    expect(!huge.ok && huge.error.code).toBe('limit_exceeded');
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7200), baseRevision: t.engine.revision, script: many }, (s, snap, c) => compile(s, { snapshot: snap, newUuid: sequenceIds('7'), ...c }));
    expect(r.ok).toBe(false);
    expect(t.store.snapshot().document.pages[0]!.elements).toHaveLength(22);
  });
});

describe('scripts and the engine', () => {
  const compileWith = (ids = sequenceIds('7')) => (src: string, snap: Parameters<NonNullable<Parameters<ReturnType<typeof createTestEngine>['engine']['executeScript']>[1]>>[1], c: { pageId?: string; preparedAssetRefs?: Record<string, PreparedAssetRef> }) =>
    compile(src, { snapshot: snap, newUuid: ids, ...c });

  it('script_equals_typed_operations', async () => {
    const a = await scene(), b = await scene();
    const source = 'add shape id=n type=ellipse x=10mm y=10mm w=30mm h=20mm\nset n text="Node" fill="#FFEEDD"\nmove logo dx=2mm dy=3mm\nalign left logo n';
    const typed = ops(b, source);
    const viaScript = await a.engine.executeScript({ ...a.scope(), transactionId: id(7300), baseRevision: a.engine.revision, script: source }, compileWith());
    const viaApply = await b.engine.execute(b.request(typed), 'mcp');
    expect(viaScript.ok && viaApply.ok).toBe(true);
    expect(canonicalSerialize({ ...a.store.snapshot().document, revision: 0 })).toBe(canonicalSerialize({ ...b.store.snapshot().document, revision: 0 }));
    expect(viaScript.ok && viaScript.value.created).toEqual(viaApply.ok ? viaApply.value.created : []);
  });

  it('one_batch_one_undo', async () => {
    const t = await scene();
    const before = exactElementHashes(t.store.snapshot());
    const r0 = t.engine.revision;
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7301), baseRevision: r0, script: 'add shape id=a x=1mm y=1mm w=5mm h=5mm\nadd shape id=b x=20mm y=1mm w=5mm h=5mm\nconnect from=a to=b\nset logo locked=true' }, compileWith());
    expect(r.ok).toBe(true);
    expect(t.engine.revision).toBe(r0 + 1);
    const u = await t.engine.undo({ ...t.scope(), transactionId: id(7302), baseRevision: t.engine.revision });
    expect(u.ok).toBe(true);
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
  });

  it('a failing line rolls the whole script back and reports that line', async () => {
    const t = await scene();
    await run(t, [{ op: 'set', target: 's5', patch: { locked: true } }]);
    const snapshot = t.store.snapshot();
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7303), baseRevision: t.engine.revision, script: 'add shape id=a x=1mm y=1mm w=5mm h=5mm\n\nmove s5 dx=1mm dy=1mm\nadd shape id=b x=1mm y=1mm w=5mm h=5mm' }, compileWith());
    expect(r.ok).toBe(false);
    if (r.ok) return;
    expect(r.error.code).toBe('locked_target');
    expect(r.error.details?.line).toBe(3);
    expect(r.error.message).toMatch(/line 3/);
    expect(t.store.snapshot().document).toBe(snapshot.document);
    expect(t.store.snapshot().revision).toBe(snapshot.revision);
  });

  it('duplicate_script_create_retries_once', async () => {
    const t = await scene();
    const req = { ...t.scope(), transactionId: id(7304), baseRevision: t.engine.revision, script: 'add shape id=once x=1mm y=1mm w=5mm h=5mm' };
    let compiles = 0;
    const counting = (src: string, snap: Parameters<ReturnType<typeof compileWith>>[1], c: Parameters<ReturnType<typeof compileWith>>[2]) => { compiles++; return compile(src, { snapshot: snap, newUuid: () => crypto.randomUUID(), ...c }); };
    const first = await t.engine.executeScript(req, counting);
    const retry = await t.engine.executeScript(req, counting);
    expect(first.ok && retry.ok).toBe(true);
    expect(retry).toEqual(first); // the original result, not a second creation
    expect(compiles).toBe(1); // and the compiler never ran again, so no fresh ids were allocated
    expect(t.store.snapshot().document.pages[0]!.elements.filter(e => e.alias === 'once')).toHaveLength(1);
    const clash = await t.engine.executeScript({ ...req, script: 'add shape id=other x=1mm y=1mm w=5mm h=5mm' }, counting);
    expect(!clash.ok && clash.error.code).toBe('transaction_id_conflict');
  });

  it('page_use_is_context_only', async () => {
    const t = await scene();
    await run(t, [{ op: 'addPage', id: id(800), name: 'Second' }]);
    const snapshot = t.store.snapshot();
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7305), baseRevision: t.engine.revision, script: 'page use "Second"' }, compileWith());
    expect(r.ok && r.value.noChange).toBe(true);
    expect(t.store.snapshot().document).toBe(snapshot.document); // no revision, no history entry, nothing that selects a page for the GUI
    expect(t.store.snapshot().revision).toBe(snapshot.revision);
    expect(t.engine.undoHead()?.description).toBe('addPage');
  });

  it('asset_prepare_precedes_commit', async () => {
    const t = await scene();
    const asset = { id: 'asset:fresh', name: 'Fresh', mimeType: 'image/png' as const, sha256: 'f'.repeat(64), tags: [] };
    const ref: PreparedAssetRef = { preparationId: 'prep-1', expiresAt: '2030-01-01T00:00:00Z', asset };
    const good = 'asset import prepared=up1\nadd image id=fresh asset="asset:fresh" x=0mm y=0mm w=10mm h=10mm';
    // not prepared: refused at compile, nothing registered
    const missing = await t.engine.executeScript({ ...t.scope(), transactionId: id(7306), baseRevision: t.engine.revision, script: good }, compileWith());
    expect(!missing.ok && missing.error.message).toMatch(/no prepared asset/);
    expect(t.store.snapshot().document.assets.some(a => a.id === 'asset:fresh')).toBe(false);
    // prepared, but a later line fails: the registration rolls back with everything else
    const failing = await t.engine.executeScript({ ...t.scope(), transactionId: id(7307), baseRevision: t.engine.revision, script: `${good}\nmove nowhere dx=1mm dy=1mm`, preparedAssetRefs: { up1: ref } }, compileWith());
    expect(failing.ok).toBe(false);
    expect(t.store.snapshot().document.assets.some(a => a.id === 'asset:fresh')).toBe(false);
    // prepared and valid: registered and used in one commit
    const r0 = t.engine.revision;
    const ok = await t.engine.executeScript({ ...t.scope(), transactionId: id(7308), baseRevision: r0, script: good, preparedAssetRefs: { up1: ref } }, compileWith());
    expect(ok.ok && ok.value.changedAssetIds).toEqual(['asset:fresh']);
    expect(t.engine.revision).toBe(r0 + 1);
  });

  it('validate never commits', async () => {
    const t = await scene();
    const before = exactElementHashes(t.store.snapshot());
    const snapshot = t.store.snapshot();
    const good = validateScript('add shape id=v x=1mm y=1mm w=5mm h=5mm\nset logo text="Hi"', ctxFor(t));
    expect(good.ok && [good.value.operationCount, good.value.created, good.value.changed, good.value.revision]).toEqual([2, 1, 1, snapshot.revision]);
    const bad1 = validateScript('set logo locked=true\nmove nothing dx=1mm dy=1mm', ctxFor(t));
    expect(!bad1.ok && bad1.error.details?.line).toBe(2);
    // a plan-time failure (not a compile error) is also mapped back to its line
    await run(t, [{ op: 'set', target: 's6', patch: { locked: true } }]);
    const locked = validateScript('set logo text="x"\nmove s6 dx=1mm dy=1mm', ctxFor(t));
    expect(!locked.ok && locked.error.details?.line).toBe(2);
    expect(exactElementHashes(t.store.snapshot())).toEqual({ ...before, [t.ids.shapes[6]!]: expect.any(String) });
    expect(byId(t, id(960)).locked).toBe(false);
  });

  it('only the transaction at the head of the stack can be undone', async () => {
    const t = await scene();
    const r = await t.engine.executeScript({ ...t.scope(), transactionId: id(7309), baseRevision: t.engine.revision, script: 'add shape id=a x=1mm y=1mm w=5mm h=5mm' }, compileWith());
    expect(r.ok && t.engine.undoHead()?.transactionId).toBe(id(7309));
    await run(t, [{ op: 'move', target: 's1', delta: { xPt: 4, yPt: 0 } }]);
    expect(t.engine.undoHead()?.transactionId).not.toBe(id(7309)); // a later change now sits above the script's
  });
});

describe('compiler: route modes (R20)', () => {
  it('connect mode=, set mode= and reroute compile to the route operations', async () => {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(970), alias: 'p', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(971), alias: 'q', bounds: { x: 200, y: 200, width: 10, height: 10 } },
      { op: 'createConnector', id: id(972), alias: 'wire', from: { elementId: id(970), glue: 'dynamic' }, to: { elementId: id(971), glue: 'dynamic' }, route: 'orthogonal' },
    ]);
    const made = ops(t, ['connect id=w2 from=p to=q route=orthogonal mode=frozen', 'set wire mode=manual', 'reroute', 'reroute wire'].join(String.fromCharCode(10)));
    expect(made[0]).toMatchObject({ op: 'createConnector', route: 'orthogonal', routing: { mode: 'frozen' } });
    expect(made[1]).toMatchObject({ op: 'set', patch: { routing: { mode: 'manual' } } });
    expect(made[2]).toEqual({ op: 'reroute' });
    expect(made[3]).toMatchObject({ op: 'reroute', targets: [id(972)] });
    expect(bad(t, 'set wire mode=sideways')).toBeDefined();
    expect(bad(t, 'set p mode=frozen')).toBeDefined();
  });
});

describe('compiler: things riding on a connector', () => {
  it('on= at= offset= attach a shape or text box, labelAt= labelOffset= place a connector label', async () => {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(970), alias: 'p', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(971), alias: 'q', bounds: { x: 200, y: 200, width: 10, height: 10 } },
    ]);
    const made = ops(t, 'connect id=wire from=p to=q text="feed" labelAt=25% labelOffset=-4pt\nadd shape id=tag x=0mm y=0mm w=10mm h=5mm on=wire at=0.75 offset=6pt\nadd text id=note x=0mm y=0mm w=10mm h=5mm text="Report" on=wire');
    expect(made[0]).toMatchObject({ op: 'createConnector', label: { value: 'feed' }, labelAnchor: { position: 0.25, offsetPt: -4 } });
    expect(made[1]).toMatchObject({ op: 'createShape', attachment: { connector: U(1), position: 0.75, offsetPt: 6 } });
    expect(made[2]).toMatchObject({ op: 'createText', attachment: { connector: U(1) } });
    expect((made[2] as { attachment: object }).attachment).not.toHaveProperty('position');
  });

  it('set moves an attachment along (keeping the connector), detaches with on=none, and refuses nonsense', async () => {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(970), alias: 'p', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(971), alias: 'q', bounds: { x: 200, y: 200, width: 10, height: 10 } },
      { op: 'createConnector', id: id(972), alias: 'wire', from: { elementId: id(970), glue: 'dynamic' }, to: { elementId: id(971), glue: 'dynamic' } },
      { op: 'createShape', id: id(973), alias: 'tag', bounds: { x: 0, y: 0, width: 20, height: 10 }, attachment: { connector: 'wire', position: 0.5 } },
    ]);
    expect(ops(t, 'set tag at=0.9')[0]).toMatchObject({ op: 'set', patch: { attachment: { connector: id(972), position: 0.9, offsetPt: 0 } } });
    expect(ops(t, 'set tag on=none')[0]).toMatchObject({ patch: { attachment: null } });
    expect(ops(t, 'set wire labelAt=0.1')[0]).toMatchObject({ patch: { labelAnchor: { position: 0.1 } } });
    expect(bad(t, 'set tag on=p').message).toMatch(/is a shape/);
    expect(bad(t, 'set wire on=wire').message).toMatch(/apply to shapes/);
    expect(bad(t, 'set tag labelAt=0.1').message).toMatch(/apply to connectors/);
    expect(bad(t, 'set p at=0.5').message).toMatch(/go with on=/);
  });

  it('move-segment compiles to the moveSegment operation and needs exactly one of to= and by=', async () => {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(970), alias: 'p', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(971), alias: 'q', bounds: { x: 200, y: 300, width: 10, height: 10 } },
      { op: 'createConnector', id: id(972), alias: 'wire', from: { elementId: id(970), glue: 'dynamic' }, to: { elementId: id(971), glue: 'dynamic' }, route: 'orthogonal' },
    ]);
    const made = ops(t, ['move-segment wire segment=1 by=-10pt', 'move-segment wire segment=2 to=120pt'].join(String.fromCharCode(10)));
    expect(made[0]).toEqual({ op: 'moveSegment', target: id(972), segment: 1, by: -10 });
    expect(made[1]).toEqual({ op: 'moveSegment', target: id(972), segment: 2, to: 120 });
    expect(bad(t, 'move-segment wire segment=1')).toBeDefined();
    expect(bad(t, 'move-segment wire segment=1 by=1pt to=2pt')).toBeDefined();
    expect(bad(t, 'move-segment p segment=1 by=1pt')).toBeDefined();
    const pins = ops(t, ['pin-segment wire segment=1', 'unpin-segment wire segment=1'].join(String.fromCharCode(10)));
    expect(pins[0]).toEqual({ op: 'pinSegment', target: id(972), segment: 1, pinned: true });
    expect(pins[1]).toEqual({ op: 'pinSegment', target: id(972), segment: 1, pinned: false });
  });
});

describe('compiler: branches that join a connector, and hops', () => {
  async function bus() {
    const t = await scene();
    await run(t, [
      { op: 'createShape', id: id(980), alias: 'p', bounds: { x: 0, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(981), alias: 'q', bounds: { x: 200, y: 200, width: 10, height: 10 } },
      { op: 'createShape', id: id(982), alias: 'r', bounds: { x: 100, y: 400, width: 10, height: 10 } },
      { op: 'createConnector', id: id(983), alias: 'bus', from: { elementId: id(980), glue: 'dynamic' }, to: { elementId: id(981), glue: 'dynamic' } },
    ]);
    return t;
  }

  it('toJoin= toAt= make the end a branch on a connector, in connect and in set', async () => {
    const t = await bus();
    expect(ops(t, 'connect id=tap from=r toJoin=bus toAt=25% hops=arc')[0]).toMatchObject({
      op: 'createConnector', to: { glue: 'none', join: { connector: id(983), position: 0.25 } }, hops: 'arc',
    });
    expect(ops(t, 'connect id=tap from=r toJoin=bus')[0]).toMatchObject({ to: { glue: 'none', join: { connector: id(983) } } });
    expect(ops(t, 'connect id=tap fromJoin=bus to=r')[0]).toMatchObject({ from: { glue: 'none', join: { connector: id(983) } } });
    await run(t, [{ op: 'createConnector', id: id(984), alias: 'tap', from: { elementId: id(982), glue: 'dynamic' }, to: { glue: 'none', point: { x: 5, y: 5 } } }]);
    expect(ops(t, 'set tap toJoin=bus toAt=0.8')[0]).toMatchObject({ op: 'set', patch: { to: { glue: 'none', join: { connector: id(983), position: 0.8 } } } });
    expect(ops(t, 'set tap hops=arc')[0]).toMatchObject({ patch: { hops: 'arc' } });
  });

  it('refuses a join to a shape, a join with another end form, a stray At=, and hops on a shape', async () => {
    const t = await bus();
    expect(bad(t, 'connect from=r toJoin=p').message).toMatch(/takes a connector/);
    expect(bad(t, 'connect from=r to=q toJoin=bus').message).toMatch(/alternatives/);
    expect(bad(t, 'connect from=r to=q toAt=0.5').message).toMatch(/goes with toJoin/);
    expect(bad(t, 'set p hops=arc').message).toMatch(/applies to connectors/);
    expect(bad(t, 'set bus toAt=0.5').message).toMatch(/goes with toJoin/);
  });
});
