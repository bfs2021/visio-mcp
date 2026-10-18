const MM = 72 / 25.4;
/** Size presets in points, width by height. */
export const PAGE_PRESETS: Array<{ id: string; label: string; widthPt: number; heightPt: number }> = [
  { id: 'a4-landscape', label: 'A4 landscape', widthPt: 297 * MM, heightPt: 210 * MM },
  { id: 'a4-portrait', label: 'A4 portrait', widthPt: 210 * MM, heightPt: 297 * MM },
  { id: 'a3-landscape', label: 'A3 landscape', widthPt: 420 * MM, heightPt: 297 * MM },
  { id: 'a3-portrait', label: 'A3 portrait', widthPt: 297 * MM, heightPt: 420 * MM },
  { id: 'letter-landscape', label: 'Letter landscape', widthPt: 792, heightPt: 612 },
  { id: 'letter-portrait', label: 'Letter portrait', widthPt: 612, heightPt: 792 },
];

