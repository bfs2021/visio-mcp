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

/** Height is a 1.2-em estimate; advances and line breaks share their implementation with the SVG projection. */
export function measureTextBox(text: TextBlock, width: number, height: number, measureWidth: TextWidthMeasurer, target: 'editor' | 'visio' = 'editor'): TextMeasurement {
  const scale = target === 'visio' ? 1.1 : 1;
  const inset = text.paddingPt + (target === 'visio' ? 2 : 0);
  const availableWidthPt = Math.max(0, width - 2 * inset), availableHeightPt = Math.max(0, height - 2 * inset);
  if (text.value.length === 0) return { lines: 0, availableWidthPt, availableHeightPt, requiredWidthPt: 0, requiredHeightPt: 0, contentWidthPt: 0, contentHeightPt: 0, fits: true };
  const font = { family: text.fontFamily, sizePt: text.fontSizePt, bold: text.bold, italic: text.italic };
  const advance = (s: string) => {
    let width = measureWidth(s, font);
    if (target === 'visio') for (const symbol of s.match(/\p{S}/gu) ?? []) width += Math.max(0, font.sizePt - measureWidth(symbol, font));
    return width * scale;
  };
  const wrapped = wrapTextLines(text.value, availableWidthPt, advance, text.wrap);
  const contentWidthPt = text.wrap ? Math.max(wrapped.longestWord, wrapped.maxWidth) : wrapped.maxWidth;
  const contentHeightPt = wrapped.lines.length * text.fontSizePt * 1.2 * scale;
  const result = { lines: wrapped.lines.length, availableWidthPt, availableHeightPt, contentWidthPt, contentHeightPt,
    requiredWidthPt: contentWidthPt + 2 * inset, requiredHeightPt: contentHeightPt + 2 * inset,
    fits: contentWidthPt <= availableWidthPt + .01 && contentHeightPt <= availableHeightPt + .01 };
  if (Object.values(result).some(n => typeof n === 'number' && !Number.isFinite(n))) throw new TextMeasurementLimitError('Text dimensions exceed finite measurement limits');
  return result;
}
