import type { Bounds, Element, GroupElement, Operation, Snapshot, TextElement } from '../contracts/generated';
import { effectiveState } from '../canvas/cellMapping';
import { ancestorsOf, parentMap } from '../commands/groups';
import { planPatch } from '../commands/planPatch';
import { applyDiff } from '../commands/diff';
import { iconCatalog } from '../icons/catalog';
import { fail, ok, type Result } from '../model/result';
import { TextMeasurementLimitError, type TextWidthMeasurer } from './textMeasurement';
import { componentCatalog, layoutParts, samples, type Content, type Settings, type TemplateId } from './componentTemplates';
export { componentCatalog } from './componentTemplates';

export interface ComponentProposal {
  documentId: string; sessionId: string; baseRevision: number; pageId: string; groupId: string; templateId: TemplateId;
  parts: Record<string, string>; bounds: Bounds; operations: Operation[]; warnings: string[];
}
const uuid = /^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i;
const record = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const known = (o: Record<string, unknown>, keys: string[]) => { if (Object.keys(o).some(k => !keys.includes(k))) throw new Error('Unknown component/content/layout option'); };
function contentValue(value: unknown, template: TemplateId): Content {
  if (value === undefined) return structuredClone(samples[template]);
  if (!record(value)) throw new Error('content must be an object');
  known(value, ['title', 'subtitle', 'iconId', ...(template === 'environment-columns' ? ['columns'] : template === 'pipeline' ? ['steps'] : ['items', 'footer'])]);
  const str = (v: unknown): string => { if (typeof v !== 'string' || v.length > 512) throw new Error('Text fields must be strings of at most 512 characters'); return v; };
  const icon = (v: unknown): string => { const s = str(v); if (!iconCatalog.icons.some(i => i.id === s)) throw new Error('Unknown iconId; use search_icons'); return s; };
  const items = (v: unknown): string[] => { if (!Array.isArray(v) || v.length > 8) throw new Error('items must be at most 8 strings'); return v.map(str); };
  const c: Content = { title: value.title === undefined ? samples[template].title : str(value.title) };
  if (value.subtitle !== undefined) c.subtitle = str(value.subtitle);
  if (value.footer !== undefined) c.footer = str(value.footer);
  if (value.iconId !== undefined) c.iconId = icon(value.iconId);
  if (value.items !== undefined) c.items = items(value.items);
  if (template === 'environment-columns') {
    if (!Array.isArray(value.columns) || value.columns.length < 1 || value.columns.length > 6) throw new Error('environment-columns needs 1–6 columns');
    c.columns = value.columns.map(v => { if (!record(v)) throw new Error('column must be an object'); known(v, ['title', 'items', 'iconId']); return { title: str(v.title), ...(v.items === undefined ? {} : { items: items(v.items) }), ...(v.iconId === undefined ? {} : { iconId: icon(v.iconId) }) }; });
  }
  if (template === 'pipeline') {
    if (!Array.isArray(value.steps) || value.steps.length < 1 || value.steps.length > 10) throw new Error('pipeline needs 1–10 steps');
    c.steps = value.steps.map(v => { if (!record(v)) throw new Error('step must be an object'); known(v, ['title', 'detail']); return { title: str(v.title), ...(v.detail === undefined ? {} : { detail: str(v.detail) }) }; });
  }
  if (JSON.stringify(c).length > 16384) throw new Error('Component content exceeds 16384 characters');
  return c;
}
/** Keep structure and icon IDs, never stale text copies. Current text and styling are read from named children on every reflow. */
function structure(v: unknown, key = ''): unknown {
  if (typeof v === 'string') return key === 'iconId' ? v : '';
  if (Array.isArray(v)) return v.map(x => structure(x));
  if (record(v)) return Object.fromEntries(Object.entries(v).map(([k, x]) => [k, structure(x, k)]));
  return v;
}

export function planComponent(snapshot: Snapshot, ctx: { measureWidth: TextWidthMeasurer }, p: Record<string, unknown>): Result<ComponentProposal> {
  try {
    known(p, ['pageId', 'templateId', 'componentId', 'content', 'layout', 'x', 'y']);
    if (typeof p.pageId !== 'string' || !uuid.test(p.pageId)) throw new Error('pageId must be a UUID');
    const page = snapshot.document.pages.find(x => x.id === p.pageId); if (!page) return fail('not_found', 'pageId is not in the document');
    let group: GroupElement | undefined, template = p.templateId, content = p.content;
    const oldParts = new Map<string, Element>(); let saved: Partial<Settings> = {};
    if (p.componentId !== undefined) {
      if (typeof p.componentId !== 'string' || !uuid.test(p.componentId)) throw new Error('componentId must be a group UUID');
      if (p.templateId !== undefined || p.content !== undefined) throw new Error('Reflow takes componentId and layout only; edit existing text/style through ordinary operations');
      const el = page.elements.find(e => e.id === p.componentId);
      if (!el || el.kind !== 'group') return fail('not_found', 'componentId is not a group on this page');
      group = el;
      if (el.metadata['layout.version'] !== componentCatalog.version) throw new Error('Unknown or unsupported component version');
      template = el.metadata['layout.template'];
      content = JSON.parse(el.metadata['layout.recipe'] ?? 'null');
      saved = JSON.parse(el.metadata['layout.settings'] ?? 'null');
      if (!record(saved)) throw new Error('Missing component settings');
      const parents = parentMap(page.elements);
      const members = [el.id, ...el.childIds, ...ancestorsOf(parents, el.id)];
      for (const id of members) {
        const child = page.elements.find(e => e.id === id);
        if (!child || effectiveState(child, page.layers).locked) throw new Error('A component part or ancestor is missing or locked');
        if (child.rotationDeg !== 0) throw new Error('Rotated components/parts require manual layout; no reflow proposed');
      }
      for (const id of el.childIds) {
        const child = page.elements.find(e => e.id === id)!; const role = child.metadata['layout.role'];
        if (!role || oldParts.has(role) || (child.kind !== 'text' && child.kind !== 'shape')) throw new Error('Component membership/roles changed; repair manually or ungroup');
        oldParts.set(role, child);
      }
    }
    if (typeof template !== 'string' || !Object.hasOwn(samples, template)) return fail('not_found', 'Unknown templateId; call list_layout_components');
    const templateId = template as TemplateId, c = contentValue(content, templateId);
    if (p.layout !== undefined && !record(p.layout)) throw new Error('layout must be an object');
    const options = (p.layout ?? {}) as Record<string, unknown>; known(options, ['widthPt', 'paddingPt', 'gapPt', 'align', 'target']);
    known(saved as Record<string, unknown>, ['widthPt', 'paddingPt', 'gapPt', 'align', 'target']);
    const frame = oldParts.get('frame');
    const defaults: Settings = { widthPt: templateId === 'environment-columns' ? 600 : 400, paddingPt: 16, gapPt: 10, align: templateId === 'repository-card' || templateId === 'labelled-container' ? 'left' : 'center', target: 'visio' };
    const settings = { ...defaults, ...saved, ...(frame ? { widthPt: frame.bounds.width } : {}), ...options } as Settings;
    for (const [name, low, high] of [['widthPt', 120, 2000], ['paddingPt', 0, 100], ['gapPt', 0, 100]] as const) {
      const n = settings[name]; if (typeof n !== 'number' || !Number.isFinite(n) || n < low || n > high) throw new Error(`${name} must be ${low}–${high} points`);
    }
    if (!['left', 'center', 'right'].includes(settings.align) || !['editor', 'visio'].includes(settings.target)) throw new Error('Invalid alignment or target');
    const x = p.x === undefined ? frame?.bounds.x ?? 0 : p.x, y = p.y === undefined ? frame?.bounds.y ?? 0 : p.y;
    if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y) || Math.abs(x) > 1e6 || Math.abs(y) > 1e6) throw new Error('x/y must be finite points within ±1000000');
    const textParts = new Map([...oldParts].filter((pair): pair is [string, TextElement] => pair[1].kind === 'text'));
    if ([...textParts.values()].reduce((n, e) => n + e.text.value.length, 0) > 16384) throw new Error('Component text exceeds 16384 characters');
    let work = 0;
    const measured: TextWidthMeasurer = (s, f) => { work += s.length; if (work > 1000000) throw new TextMeasurementLimitError('Component measurement exceeds 1000000 characters'); return ctx.measureWidth(s, f); };
    const parts = layoutParts(templateId, c, settings, x, y, measured, textParts, Object.hasOwn(options, 'align'));
    if (group && (oldParts.size !== parts.length || parts.some(part => oldParts.get(part.role)?.kind !== part.kind))) throw new Error('Component structure changed; no destructive reconstruction proposed');
    const operations: Operation[] = [], ids: Record<string, string> = {};
    for (const part of parts) {
      const id = oldParts.get(part.role)?.id ?? crypto.randomUUID(); ids[part.role] = id;
      if (group) {
        operations.push({ op: 'resize', target: id, bounds: part.bounds });
        if (part.kind === 'text' && Object.hasOwn(options, 'align')) operations.push({ op: 'set', target: id, patch: { text: { horizontalAlign: settings.align } } });
      } else {
        if (part.kind === 'text') operations.push({ op: 'createText', id, pageId: page.id, name: part.role, bounds: part.bounds, text: part.text! });
        else operations.push({ op: 'createShape', id, pageId: page.id, name: part.role, bounds: part.bounds, geometry: part.geometry!, style: part.style! });
        operations.push({ op: 'set', target: id, patch: { metadata: { ...part.metadata, 'layout.role': part.role } } });
      }
    }
    const groupId = group?.id ?? crypto.randomUUID();
    if (!group) operations.push({ op: 'group', id: groupId, targets: Object.values(ids), name: c.title });
    const recipe = JSON.stringify(structure(c)); if (recipe.length > 4096) throw new Error('Component structure exceeds metadata limits');
    operations.push({ op: 'set', target: groupId, patch: { metadata: { 'layout.template': templateId, 'layout.version': componentCatalog.version, 'layout.recipe': recipe, 'layout.settings': JSON.stringify(settings) } } });
    const planned = planPatch(snapshot.document, operations, { pageId: page.id, newId: () => crypto.randomUUID() });
    if (!planned.ok) return planned;
    const bounds = parts[0]!.bounds;
    const warnings = settings.target === 'visio' ? ['VISIO_TEXT_ESTIMATE: verify native rendering; line heights/font substitutions may differ'] : [];
    if (x < 0 || y < 0 || x + bounds.width > page.widthPt || y + bounds.height > page.heightPt) warnings.push('COMPONENT_OUTSIDE_PAGE');
    return ok({ documentId: snapshot.documentId, sessionId: snapshot.sessionId, baseRevision: snapshot.revision, pageId: page.id, groupId, templateId, parts: ids, bounds, operations, warnings });
  } catch (e) {
    if (e instanceof TextMeasurementLimitError) return fail('limit_exceeded', e.message);
    return fail('invalid_request', e instanceof Error ? e.message : 'Invalid component request');
  }
}

export function componentPreview(snapshot: Snapshot, proposal: ComponentProposal): Result<Snapshot> {
  const plan = planPatch(snapshot.document, proposal.operations, { pageId: proposal.pageId, newId: () => crypto.randomUUID() });
  if (!plan.ok) return plan;
  const document = applyDiff(snapshot.document, plan.value.forward, snapshot.revision);
  const wanted = new Set([proposal.groupId, ...Object.values(proposal.parts)]);
  const page = document.pages.find(p => p.id === proposal.pageId)!;
  return ok({ ...snapshot, document: { ...document, assets: [], pages: [{ ...page, layers: [], guides: [], elements: page.elements.filter(e => wanted.has(e.id)).map(e => ({ ...e, layerIds: [] })), background: { kind: 'solid', colour: '#ffffff', opacity: 1 }, grid: { ...page.grid, visible: false } }] } });
}
