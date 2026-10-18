import type { Bounds, ShapeStyle, TextBlock, TextElement } from '../contracts/generated';
import { iconCatalog } from '../icons/catalog';
import { measureTextBox, type TextWidthMeasurer } from './textMeasurement';
import { STRUCTURE_KEY } from './swimlane';

export type TemplateId = 'repository-card' | 'labelled-container' | 'environment-columns' | 'pipeline' | 'swimlane';
export interface Content { title: string; subtitle?: string; items?: string[]; footer?: string; iconId?: string; columns?: { title: string; items?: string[]; iconId?: string }[]; steps?: { title: string; detail?: string }[];
  /** swimlane: the rows (top to bottom) and the columns (left to right). */
  lanes?: { title: string; heightPt?: number; colour?: string }[]; phases?: { title: string }[]; laneHeaderWidthPt?: number }
export interface Settings { widthPt: number; paddingPt: number; gapPt: number; align: 'left' | 'center' | 'right'; target: 'editor' | 'visio' }
export interface Part { role: string; kind: 'shape' | 'text'; bounds: Bounds; text?: TextBlock; geometry?: { preset?: 'rounded-rectangle' | 'rectangle'; cornerRadiusPt?: number; svgPath?: string }; style?: ShapeStyle; metadata?: Record<string, string> }
const boxStyle = (colour = '#ffffff'): ShapeStyle => ({ fill: { kind: 'solid', colour, opacity: 1 }, stroke: { kind: 'solid', colour: '#cbd5e1', opacity: 1 }, strokeWidthPt: 1, dash: 'solid', lineCap: 'round', lineJoin: 'round' });
export const samples: Record<TemplateId, Content> = {
  'repository-card': { title: 'Infrastructure repository', subtitle: 'Owned by Sensical', items: ['Bicep modules', 'Environment parameters', 'Network, security and identities', 'Infrastructure release manifests'], footer: 'Validate → Plan → Deploy → Verify', iconId: 'lucide:git-branch' },
  'labelled-container': { title: 'Client Azure tenancy', items: ['Independent infrastructure and application deployments', 'Separate identities and configuration'], iconId: 'lucide:cloud' },
  'environment-columns': { title: 'Client Azure tenancy', columns: [{ title: 'DEV', items: ['Infrastructure vX', 'Application vY', 'Rapid iteration'] }, { title: 'TEST', items: ['Infrastructure vX', 'Application vY', 'Integration / UAT'] }, { title: 'PROD', items: ['Infrastructure vX', 'Application vY', 'Approved releases'] }], iconId: 'lucide:cloud' },
  swimlane: { title: 'Release process', lanes: [{ title: 'Developer' }, { title: 'Pipeline' }, { title: 'Operations' }], phases: [{ title: 'Day 1' }, { title: 'Day 2' }, { title: 'Day 3' }] },
  pipeline: { title: 'Application release flow', steps: [{ title: 'Application feature branch', detail: 'Code changes and automated tests' }, { title: 'Build one container image', detail: 'Unit tests · Image scan · SBOM' }, { title: 'Infrastructure compatibility check', detail: 'Does the platform satisfy the release contract?' }, { title: 'Record application deployment', detail: 'Release · Image digest · Test results' }], iconId: 'lucide:git-branch' },
};
const stringSchema = { type: 'string', maxLength: 512 };
const itemSchema = { type: 'array', maxItems: 8, items: stringSchema };
const iconSchema = { type: 'string', enum: iconCatalog.icons.map(i => i.id) };
export const componentCatalog = {
  version: 'layout-components-1', units: 'points', limits: { textCharacters: 16384, parts: 120, measurementCharacters: 1000000 },
  layoutSchema: { type: 'object', additionalProperties: false, properties: { widthPt: { type: 'number', minimum: 120, maximum: 2000 }, paddingPt: { type: 'number', minimum: 0, maximum: 100 }, gapPt: { type: 'number', minimum: 0, maximum: 100 }, align: { enum: ['left', 'center', 'right'] }, target: { enum: ['editor', 'visio'], default: 'visio' } } },
  components: (Object.keys(samples) as TemplateId[]).map(id => ({ id, name: id.replaceAll('-', ' '), example: samples[id], contentSchema: { type: 'object', additionalProperties: false, ...(id === 'swimlane' ? { required: ['lanes'] } : id === 'pipeline' ? { required: ['steps'] } : id === 'environment-columns' ? { required: ['columns'] } : {}), properties: {
    title: stringSchema, subtitle: stringSchema, iconId: iconSchema,
    ...(id === 'repository-card' || id === 'labelled-container' ? { items: itemSchema, footer: stringSchema } : {}),
    ...(id === 'environment-columns' ? { columns: { type: 'array', minItems: 1, maxItems: 6, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema, items: itemSchema, iconId: iconSchema } } } } : {}),
    ...(id === 'swimlane' ? {
      lanes: { type: 'array', minItems: 1, maxItems: 10, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema, heightPt: { type: 'number', minimum: 30, maximum: 600, default: 90 }, colour: { type: 'string', pattern: '^#[0-9a-fA-F]{6}$', description: 'Lane header fill. Defaults to a palette colour.' } } } },
      phases: { type: 'array', maxItems: 8, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema } }, description: 'Optional columns across all lanes (days, stages). Omit for lanes only.' },
      laneHeaderWidthPt: { type: 'number', minimum: 40, maximum: 300, default: 96 },
    } : {}),
    ...(id === 'pipeline' ? { steps: { type: 'array', minItems: 1, maxItems: 10, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema, detail: stringSchema } } } } : {}),
  } } })),
  guidance: 'A swimlane is a frame with a title bar, an optional row of phase columns and lanes with header cells; place other shapes on top of it and the document summary reports each one\'s lane and phase. Create with templateId and optional content; omitted content uses the example. Reflow with componentId only, using current text/styles. Explicit width/padding/gap/alignment reflows the group, without changing font size. target visio is a conservative estimate; verify native output. Every proposal is read-only; apply operations separately at baseRevision. Ungrouping removes component reflow support.',
};

/** Pure finite templates. All coordinates remain ordinary editable canonical page geometry. */
export function layoutParts(template: TemplateId, content: Content, settings: Settings, x: number, y: number, measureWidth: TextWidthMeasurer, existing = new Map<string, TextElement>(), explicitAlign = false): Part[] {
  if (template === 'swimlane') return swimlaneParts(content, settings, x, y, measureWidth, existing);
  const parts: Part[] = [], { widthPt: width, paddingPt: pad, gapPt: gap } = settings;
  const inner = width - 2 * pad;
  if (inner < 40) throw new Error('Padding leaves less than 40pt for content; increase width or reduce padding');
  const shape = (role: string, bx: number, by: number, w: number, h: number, colour?: string) => parts.push({ role, kind: 'shape', bounds: { x: bx, y: by, width: w, height: h }, geometry: { preset: 'rounded-rectangle', cornerRadiusPt: 6 }, style: boxStyle(colour) });
  const text = (role: string, value: string, bx: number, by: number, w: number, bold = false): number => {
    const old = existing.get(role)?.text;
    const tb: TextBlock = old ? { ...old, ...(explicitAlign ? { horizontalAlign: settings.align } : {}) } : { value, fontFamily: 'Arial', fontSizePt: bold ? 15 : 12, bold, italic: false, underline: false, colour: '#1e293b', horizontalAlign: settings.align, verticalAlign: 'top', wrap: true, paddingPt: 0 };
    const m = measureTextBox(tb, w, 10000, measureWidth, settings.target);
    if (m.requiredWidthPt > w + .01) throw new Error(`${role}: text cannot fit this width without changing its font or wrapping; use a wider component`);
    const h = Math.max(1, Math.ceil(m.requiredHeightPt * 1000) / 1000);
    parts.push({ role, kind: 'text', bounds: { x: bx, y: by, width: w, height: h }, text: tb }); return h;
  };
  const icon = (role: string, id: string, bx: number, by: number, size = 24) => {
    const found = iconCatalog.icons.find(i => i.id === id); if (!found) throw new Error(`Unknown iconId ${id}; use search_icons`);
    parts.push({ role, kind: 'shape', bounds: { x: bx, y: by, width: size, height: size }, geometry: { svgPath: found.path }, style: { ...boxStyle(), fill: { kind: 'none' }, stroke: { kind: 'solid', colour: '#334155', opacity: 1 }, strokeWidthPt: size / 12 }, metadata: { 'icon.id': id, 'icon.catalogVersion': iconCatalog.version, 'icon.geometrySha256': found.sha256, 'icon.sourceCommit': iconCatalog.sourceCommit, 'icon.license': iconCatalog.licenceText } });
  };
  // The frame is first, ensuring it remains behind all named content parts.
  shape('frame', x, y, width, 1);
  let cursor = y + pad;
  icon('title.icon', content.iconId ?? samples[template].iconId!, x + pad, cursor);
  const heading = text('title', content.title, x + pad + 24 + gap, cursor, inner - 24 - gap, true);
  cursor += Math.max(24, heading) + gap;
  if (content.subtitle !== undefined) cursor += text('subtitle', content.subtitle, x + pad, cursor, inner) + gap;
  if (template === 'environment-columns') {
    const columns = content.columns!; const cw = (inner - gap * (columns.length - 1)) / columns.length;
    if (cw < 40) throw new Error('Columns need at least 40pt each; increase component width');
    const start = cursor, heights: number[] = [];
    columns.forEach((c, i) => {
      const cx = x + pad + i * (cw + gap); let cy = start + gap;
      shape(`column.${i}.frame`, cx, start, cw, 1, '#f8fafc');
      cy += text(`column.${i}.title`, c.title, cx + gap, cy, cw - 2 * gap, true) + gap;
      icon(`column.${i}.icon`, c.iconId ?? 'lucide:container', cx + (cw - 24) / 2, cy); cy += 24 + gap;
      c.items?.forEach((value, j) => { cy += text(`column.${i}.item.${j}`, value, cx + gap, cy, cw - 2 * gap) + gap; });
      heights.push(cy - start);
    });
    const h = Math.max(1, ...heights); for (const p of parts) if (/^column\.\d+\.frame$/.test(p.role)) p.bounds.height = h;
    cursor += h;
  } else if (template === 'pipeline') {
    content.steps!.forEach((s, i) => {
      const start = cursor; shape(`step.${i}.frame`, x + pad, start, inner, 1, '#f8fafc'); cursor += gap;
      cursor += text(`step.${i}.title`, s.title, x + pad + gap, cursor, inner - 2 * gap, true);
      if (s.detail !== undefined) cursor += gap + text(`step.${i}.detail`, s.detail, x + pad + gap, cursor + gap, inner - 2 * gap);
      cursor += gap; parts.find(p => p.role === `step.${i}.frame`)!.bounds.height = Math.max(1, cursor - start);
      if (i < content.steps!.length - 1) { cursor += gap; icon(`step.${i}.arrow`, 'lucide:arrow-down', x + (width - 16) / 2, cursor, 16); cursor += 16 + gap; }
    });
  } else {
    content.items?.forEach((value, i) => { cursor += text(`item.${i}`, value, x + pad, cursor, inner) + gap; });
    if (content.footer !== undefined) {
      const top = cursor; shape('footer.frame', x + pad, top, inner, 1, '#f1f5f9'); cursor += gap;
      cursor += text('footer', content.footer, x + pad + gap, cursor, inner - 2 * gap) + gap;
      parts.find(p => p.role === 'footer.frame')!.bounds.height = Math.max(1, cursor - top);
    }
  }
  parts[0]!.bounds.height = Math.max(1, cursor - y + pad);
  if (parts.length > 120 || parts[0]!.bounds.height > 10000 || parts.some(p => Object.values(p.bounds).some(n => !Number.isFinite(n)) || p.bounds.width <= 0 || p.bounds.height <= 0)) throw new Error('Component exceeds finite geometry limits');
  return parts;
}

const LANE_COLOURS = ['#1d4e89', '#2e7d6b', '#8a5a00', '#7a3b8f', '#a1332f', '#3f5f8a', '#5b6770', '#4b7f2a', '#9a4f00', '#2c6e7f'];

/**
 * A swim-lane: a frame with a title bar, an optional row of phase columns, and lanes (rows) each with a header cell on the left. Structure
 * parts carry `swimlane.structure` so the inspector treats them as containment, not as shapes to collide with. Text is centred in its cell.
 */
function swimlaneParts(content: Content, settings: Settings, x: number, y: number, measureWidth: TextWidthMeasurer, existing: Map<string, TextElement>): Part[] {
  const lanes = content.lanes ?? [], phases = content.phases ?? [];
  const W = settings.widthPt, headerW = content.laneHeaderWidthPt ?? 96, cellPad = Math.min(settings.paddingPt, 16) / 2;
  const TITLE_H = 30, PHASE_H = phases.length ? 26 : 0;
  const colW = phases.length ? (W - headerW) / phases.length : W - headerW;
  if (W - headerW < 40 * Math.max(1, phases.length)) throw new Error('Phase columns need at least 40pt each; increase the width or reduce the lane header width');
  const heights = lanes.map(l => l.heightPt ?? 90);
  const total = TITLE_H + PHASE_H + heights.reduce((a, b) => a + b, 0);
  const parts: Part[] = [], texts: Part[] = [];
  const rect = (role: string, bx: number, by: number, w: number, h: number, fill: string, stroke: string | null = '#94a3b8') =>
    parts.push({ role, kind: 'shape', bounds: { x: bx, y: by, width: w, height: h }, geometry: { preset: 'rectangle' }, style: { ...boxStyle(fill), stroke: stroke ? { kind: 'solid', colour: stroke, opacity: 1 } : { kind: 'none' } }, metadata: { [STRUCTURE_KEY]: '1' } });
  const label = (role: string, value: string, bx: number, by: number, w: number, h: number, colour: string, bold: boolean, size: number, align: 'left' | 'center') => {
    const old = existing.get(role)?.text;
    const tb: TextBlock = old ?? { value, fontFamily: 'Arial', fontSizePt: size, bold, italic: false, underline: false, colour, horizontalAlign: align, verticalAlign: 'middle', wrap: true, paddingPt: 0 };
    const iw = w - 2 * cellPad;
    const m = measureTextBox(tb, iw, 10000, measureWidth, settings.target);
    if (m.requiredWidthPt > iw + .01) throw new Error(`${role}: text cannot fit this cell width without changing its font; widen the cell or shorten the text`);
    if (m.requiredHeightPt > h + .01) throw new Error(`${role}: text needs more height than its cell has; make the lane taller or the header wider`);
    const th = Math.max(1, Math.ceil(m.requiredHeightPt * 1000) / 1000);
    texts.push({ role, kind: 'text', bounds: { x: bx + cellPad, y: by + (h - th) / 2, width: iw, height: th }, text: tb });
  };

  rect('frame', x, y, W, total, '#ffffff');
  rect('title.bar', x, y, W, TITLE_H, '#0f2a43', '#0f2a43');
  if (phases.length) {
    rect('corner', x, y + TITLE_H, headerW, PHASE_H, '#cbd5e1');
    phases.forEach((_, i) => rect(`phase.${i}.header`, x + headerW + i * colW, y + TITLE_H, colW, PHASE_H, '#e2e8f0'));
  }
  let top = y + TITLE_H + PHASE_H;
  const bodyTop = top;
  lanes.forEach((_, i) => { rect(`lane.${i}.band`, x, top, W, heights[i]!, i % 2 ? '#f8fafc' : '#ffffff'); top += heights[i]!; });
  top = bodyTop;
  lanes.forEach((l, i) => {
    rect(`lane.${i}.header`, x, top, headerW, heights[i]!, l.colour && /^#[0-9a-fA-F]{6}$/.test(l.colour) ? l.colour : LANE_COLOURS[i % LANE_COLOURS.length]!);
    top += heights[i]!;
  });
  const bodyH = total - TITLE_H - PHASE_H;
  for (let i = 1; i < phases.length; i++) parts.push({ role: `phase.${i}.rule`, kind: 'shape', bounds: { x: x + headerW + i * colW - 0.5, y: bodyTop, width: 1, height: bodyH }, geometry: { preset: 'rectangle' }, style: { ...boxStyle('#94a3b8'), stroke: { kind: 'none' } }, metadata: { [STRUCTURE_KEY]: '1' } });

  label('title', content.title, x, y, W, TITLE_H, '#ffffff', true, 14, 'left');
  phases.forEach((p, i) => label(`phase.${i}.title`, p.title, x + headerW + i * colW, y + TITLE_H, colW, PHASE_H, '#1e293b', true, 12, 'center'));
  top = bodyTop;
  lanes.forEach((l, i) => { label(`lane.${i}.title`, l.title, x, top, headerW, heights[i]!, '#ffffff', true, 12, 'center'); top += heights[i]!; });

  const all = [...parts, ...texts];
  if (all.length > 120 || total > 10000 || all.some(p => Object.values(p.bounds).some(n => !Number.isFinite(n)) || p.bounds.width <= 0 || p.bounds.height <= 0)) throw new Error('Component exceeds finite geometry limits');
  return all;
}

export function componentWarningText(warning: string): string {
  if (warning === 'COMPONENT_OUTSIDE_PAGE') return 'This component extends beyond the page.';
  if (warning.startsWith('VISIO_TEXT_ESTIMATE:')) return 'Text fitting is estimated for Visio. Check the exported diagram.';
  return warning;
}