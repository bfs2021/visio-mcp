export interface PrintMargins { leftPt: number; topPt: number; rightPt: number; bottomPt: number }
export interface PrintMedia { id: string; label: string; widthPt: number; heightPt: number; minimumMargins: PrintMargins }
export interface PrintDevice { id: string; label: string; media: PrintMedia[] }
export interface EditorPrintOptions {
  expected: { documentId: string; sessionId: string; baseRevision: number };
  pageRange: string;
  printerId: string;
  mediaId: string;
  orientation: 'portrait' | 'landscape';
  layout: 'fit' | 'tile';
  margins: PrintMargins;
}
export interface PrintSheetPreview {
  sheetNumber: number;
  paperWidthPt: number;
  paperHeightPt: number;
  sourcePageNumbers: number[];
  svg: string;
}
export interface PreparedPrintPreview {
  jobId: string;
  sheets: PrintSheetPreview[];
  settings: unknown;
  marginWarnings: string[];
  expiresAt: string;
  sourcePageCount: number;
}
export interface EditorPrintResponse {
  devices?: PrintDevice[];
  jobId?: string;
  sheets?: PrintSheetPreview[];
  settings?: unknown;
  marginWarnings?: string[];
  expiresAt?: string;
  sourcePageCount?: number;
  path?: string;
  bytes?: number;
  printed?: boolean;
  cancelled?: boolean;
  [key: string]: unknown;
}
