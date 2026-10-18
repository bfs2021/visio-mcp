import type { Bounds, ShapeStyle, TextBlock, TextElement } from '../contracts/generated';
import { iconCatalog } from '../icons/catalog';
import { measureTextBox, type TextWidthMeasurer } from './textMeasurement';

export type TemplateId = 'repository-card' | 'labelled-container' | 'environment-columns' | 'pipeline';
export interface Content { title: string; subtitle?: string; items?: string[]; footer?: string; iconId?: string; columns?: { title: string; items?: string[]; iconId?: string }[]; steps?: { title: string; detail?: string }[] }
export interface Settings { widthPt: number; paddingPt: number; gapPt: number; align: 'left' | 'center' | 'right'; target: 'editor' | 'visio' }
export interface Part { role: string; kind: 'shape' | 'text'; bounds: Bounds; text?: TextBlock; geometry?: { preset?: 'rounded-rectangle' | 'rectangle'; cornerRadiusPt?: number; svgPath?: string }; style?: ShapeStyle; metadata?: Record<string, string> }
const boxStyle = (colour = '#ffffff'): ShapeStyle => ({ fill: { kind: 'solid', colour, opacity: 1 }, stroke: { kind: 'solid', colour: '#cbd5e1', opacity: 1 }, strokeWidthPt: 1, dash: 'solid', lineCap: 'round', lineJoin: 'round' });
export const samples: Record<TemplateId, Content> = {
  'repository-card': { title: 'Infrastructure repository', subtitle: 'Owned by Sensical', items: ['Bicep modules', 'Environment parameters', 'Network, security and identities', 'Infrastructure release manifests'], footer: 'Validate → Plan → Deploy → Verify', iconId: 'lucide:git-branch' },
  'labelled-container': { title: 'Client Azure tenancy', items: ['Independent infrastructure and application deployments', 'Separate identities and configuration'], iconId: 'lucide:cloud' },
  'environment-columns': { title: 'Client Azure tenancy', columns: [{ title: 'DEV', items: ['Infrastructure vX', 'Application vY', 'Rapid iteration'] }, { title: 'TEST', items: ['Infrastructure vX', 'Application vY', 'Integration / UAT'] }, { title: 'PROD', items: ['Infrastructure vX', 'Application vY', 'Approved releases'] }], iconId: 'lucide:cloud' },
  pipeline: { title: 'Application release flow', steps: [{ title: 'Application feature branch', detail: 'Code changes and automated tests' }, { title: 'Build one container image', detail: 'Unit tests · Image scan · SBOM' }, { title: 'Infrastructure compatibility check', detail: 'Does the platform satisfy the release contract?' }, { title: 'Record application deployment', detail: 'Release · Image digest · Test results' }], iconId: 'lucide:git-branch' },
};
const stringSchema = { type: 'string', maxLength: 512 };
const itemSchema = { type: 'array', maxItems: 8, items: stringSchema };
const iconSchema = { type: 'string', enum: iconCatalog.icons.map(i => i.id) };
export const componentCatalog = {
  version: 'layout-components-1', units: 'points', limits: { textCharacters: 16384, parts: 120, measurementCharacters: 1000000 },
  layoutSchema: { type: 'object', additionalProperties: false, properties: { widthPt: { type: 'number', minimum: 120, maximum: 2000 }, paddingPt: { type: 'number', minimum: 0, maximum: 100 }, gapPt: { type: 'number', minimum: 0, maximum: 100 }, align: { enum: ['left', 'center', 'right'] }, target: { enum: ['editor', 'visio'], default: 'visio' } } },
  components: (Object.keys(samples) as TemplateId[]).map(id => ({ id, name: id.replaceAll('-', ' '), example: samples[id], contentSchema: { type: 'object', additionalProperties: false, ...(id === 'pipeline' ? { required: ['steps'] } : id === 'environment-columns' ? { required: ['columns'] } : {}), properties: {
    title: stringSchema, subtitle: stringSchema, iconId: iconSchema,
    ...(id === 'repository-card' || id === 'labelled-container' ? { items: itemSchema, footer: stringSchema } : {}),
    ...(id === 'environment-columns' ? { columns: { type: 'array', minItems: 1, maxItems: 6, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema, items: itemSchema, iconId: iconSchema } } } } : {}),
    ...(id === 'pipeline' ? { steps: { type: 'array', minItems: 1, maxItems: 10, items: { type: 'object', required: ['title'], additionalProperties: false, properties: { title: stringSchema, detail: stringSchema } } } } : {}),
  } } })),
  guidance: 'Create with templateId and optional content; omitted content uses the example. Reflow with componentId only, using current text/styles. Explicit width/padding/gap/alignment reflows the group, without changing font size. target visio is a conservative estimate; verify native output. Every proposal is read-only; apply operations separately at baseRevision. Ungrouping removes component reflow support.',
};

/** Pure finite templates. All coordinates remain ordinary editable canonical page geometry. */
export function layoutParts(template: TemplateId, content: Content, settings: Settings, x: number, y: number, measureWidth: TextWidthMeasurer, existing = new Map<string, TextElement>(), explicitAlign = false): Part[] {
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

export function componentWarningText(warning: string): string {
  if (warning === 'COMPONENT_OUTSIDE_PAGE') return 'This component extends beyond the page.';
  if (warning.startsWith('VISIO_TEXT_ESTIMATE:')) return 'Text fitting is estimated for Visio. Check the exported diagram.';
  return warning;
}