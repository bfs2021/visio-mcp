import type { TextBlock } from '../contracts/generated';

export type TextWidthMeasurer = (text: string, font: { family: string; sizePt: number; bold: boolean; italic: boolean }) => number;
export class TextMeasurementLimitError extends Error {}
export interface TextMeasurement {
  lines: number; availableWidthPt: number; availableHeightPt: number; requiredWidthPt: number; requiredHeightPt: number;
  contentWidthPt: number; contentHeightPt: number; fits: boolean;
}

/** Shared plain-text line breaking for measurement and SVG projection. NBSP/narrow NBSP/figure space stay unbroken. */
export function wrapTextLines(value: string, width: number, advance: (value: string) => number, wrap = true) {
  const lines: string[] = []; let maxWidth = 0, longestWord = 0;
  const append = (s: string) => { lines.push(s); maxWidth = Math.max(maxWidth, advance(s)); };
  if (!value.length) return { lines, maxWidth, longestWord };
  for (const paragraph of value.split(/\r\n|\r|\n/)) {
    if (!wrap) { append(paragraph); continue; }
    let line = '', space = '', lineWidth = 0;
    const breakable = /^[ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+$/u;
    const tokens = paragraph.match(/[^ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+|[ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+/gu) ?? [];
    for (const token of tokens) {
      if (breakable.test(token)) { space += token; continue; }
      const wordWidth = advance(token), spaceWidth = advance(space); longestWord = Math.max(longestWord, wordWidth);
      if (line && lineWidth + spaceWidth + wordWidth > width) { append(line); line = token; lineWidth = wordWidth; }
      else { line += space + token; lineWidth += spaceWidth + wordWidth; }
      space = '';
    }
    append(line + space);
  }
  return { lines, maxWidth, longestWord };
}

/** The margins that apply: per-side insets when given, else the uniform padding. Sides are in the text's own frame: for vertical text, left is the bottom of the shape and top is its left side. */
export function textInsets(text: TextBlock): { top: number; right: number; bottom: number; left: number } {
  return text.insetsPt ?? { top: text.paddingPt, right: text.paddingPt, bottom: text.paddingPt, left: text.paddingPt };
}

/** Height is a 1.2-em estimate; advances and line breaks share their implementation with the SVG projection. */
export function measureTextBox(text: TextBlock, width: number, height: number, measureWidth: TextWidthMeasurer, target: 'editor' | 'visio' = 'editor'): TextMeasurement {
  const scale = target === 'visio' ? 1.1 : 1;
  const extra = target === 'visio' ? 2 : 0, sides = textInsets(text);
  // vertical text flows along the shape's height: its lines run top-to-bottom of the frame and stack left-to-right
  const vertical = text.orientation === 'vertical';
  const [flowWidth, flowHeight] = vertical ? [height, width] : [width, height];
  const along = sides.left + sides.right, across = sides.top + sides.bottom; // margins are in the text's own frame, as in Visio: for vertical text 'left' is where the line starts (the bottom)
  const availableWidthPt = Math.max(0, flowWidth - along - 2 * extra), availableHeightPt = Math.max(0, flowHeight - across - 2 * extra);
  if (text.value.length === 0) return { lines: 0, ...(vertical ? { availableWidthPt: availableHeightPt, availableHeightPt: availableWidthPt } : { availableWidthPt, availableHeightPt }), requiredWidthPt: 0, requiredHeightPt: 0, contentWidthPt: 0, contentHeightPt: 0, fits: true };
  const font = { family: text.fontFamily, sizePt: text.fontSizePt, bold: text.bold, italic: text.italic };
  const advance = (s: string) => {
    let width = measureWidth(s, font);
    if (target === 'visio') for (const symbol of s.match(/\p{S}/gu) ?? []) width += Math.max(0, font.sizePt - measureWidth(symbol, font));
    return width * scale;
  };
  const wrapped = wrapTextLines(text.value, availableWidthPt, advance, text.wrap);
  const contentWidthPt = text.wrap ? Math.max(wrapped.longestWord, wrapped.maxWidth) : wrapped.maxWidth;
  const contentHeightPt = wrapped.lines.length * text.fontSizePt * 1.2 * scale;
  const flow = { lines: wrapped.lines.length, availableWidthPt, availableHeightPt, contentWidthPt, contentHeightPt,
    requiredWidthPt: contentWidthPt + along + 2 * extra, requiredHeightPt: contentHeightPt + across + 2 * extra,
    fits: contentWidthPt <= availableWidthPt + .01 && contentHeightPt <= availableHeightPt + .01 };
  // report in the shape's frame: for vertical text the flow's width is the shape's height
  const result = vertical ? { ...flow, availableWidthPt: flow.availableHeightPt, availableHeightPt: flow.availableWidthPt, contentWidthPt: flow.contentHeightPt, contentHeightPt: flow.contentWidthPt,
    requiredWidthPt: flow.requiredHeightPt, requiredHeightPt: flow.requiredWidthPt } : flow;
  if (Object.values(result).some(n => typeof n === 'number' && !Number.isFinite(n))) throw new TextMeasurementLimitError('Text dimensions exceed finite measurement limits');
  return result;
}
