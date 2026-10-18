import type { TextBlock } from '../contracts/generated';

export type TextWidthMeasurer = (text: string, font: { family: string; sizePt: number; bold: boolean; italic: boolean }) => number;
export class TextMeasurementLimitError extends Error {}
export interface TextMeasurement {
  lines: number;
  availableWidthPt: number;
  availableHeightPt: number;
  requiredWidthPt: number;
  requiredHeightPt: number;
  contentWidthPt: number;
  contentHeightPt: number;
  fits: boolean;
}

/** Shared by layout inspection and fitting. Height is a 1.2-em estimate; advance widths come from the supplied font measurer. */
export function measureTextBox(text: TextBlock, width: number, height: number, measureWidth: TextWidthMeasurer, target: 'editor' | 'visio' = 'editor'): TextMeasurement {
  // This is an explicit conservative policy, not native Visio font measurement.
  const scale = target === 'visio' ? 1.1 : 1;
  const inset = text.paddingPt + (target === 'visio' ? 2 : 0);
  const availableWidthPt = Math.max(0, width - 2 * inset), availableHeightPt = Math.max(0, height - 2 * inset);
  if (text.value.length === 0) return { lines: 0, availableWidthPt, availableHeightPt, requiredWidthPt: 0, requiredHeightPt: 0, contentWidthPt: 0, contentHeightPt: 0, fits: true };
  const font = { family: text.fontFamily, sizePt: text.fontSizePt, bold: text.bold, italic: text.italic };
  const advance = (s: string) => {
    let width = measureWidth(s, font);
    // Visio can substitute a symbol font with a full-em advance where the browser
    // uses a half-em glyph (observed with paired arrows in the reference diagram).
    if (target === 'visio') for (const symbol of s.match(/\p{S}/gu) ?? []) width += Math.max(0, font.sizePt - measureWidth(symbol, font));
    return width * scale;
  };
  let lines = 0, maxWidth = 0, longestWord = 0;
  for (const paragraph of text.value.split(/\r\n|\r|\n/)) {
    if (!text.wrap) { maxWidth = Math.max(maxWidth, advance(paragraph)); lines++; continue; }
    let line = '', space = '', lineWidth = 0;
    // NBSP, narrow NBSP and figure space are deliberately not legal wrap points.
    const breakable = /^[ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+$/u;
    const tokens = paragraph.match(/[^ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+|[ \t\u000B\u000C\u1680\u2000-\u2006\u2008-\u200A\u205F\u3000\u200B]+/gu) ?? [];
    for (const token of tokens) {
      if (breakable.test(token)) { space += token; continue; }
      const wordWidth = advance(token), spaceWidth = advance(space);
      longestWord = Math.max(longestWord, wordWidth);
      if (line && lineWidth + spaceWidth + wordWidth > availableWidthPt) {
        maxWidth = Math.max(maxWidth, advance(line)); lines++; line = token; lineWidth = wordWidth;
      } else { line += space + token; lineWidth += spaceWidth + wordWidth; }
      space = '';
    }
    maxWidth = Math.max(maxWidth, advance(line + space)); lines++;
  }
  const contentWidthPt = text.wrap ? Math.max(longestWord, maxWidth) : maxWidth;
  const contentHeightPt = lines * text.fontSizePt * 1.2 * scale;
  const result = {
    lines, availableWidthPt, availableHeightPt, contentWidthPt, contentHeightPt,
    requiredWidthPt: contentWidthPt + 2 * inset, requiredHeightPt: contentHeightPt + 2 * inset,
    fits: contentWidthPt <= availableWidthPt + 0.01 && contentHeightPt <= availableHeightPt + 0.01,
  };
  if (Object.values(result).some(n => typeof n === 'number' && !Number.isFinite(n))) throw new TextMeasurementLimitError('Text dimensions exceed finite measurement limits');
  return result;
}
