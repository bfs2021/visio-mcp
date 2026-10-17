import type { Point, Snapshot } from '../contracts/generated';
import type { Result } from '../model/result';
import type { GestureKind } from './GestureController';
import type { Intent } from './intents';

export interface GestureNotice { phase: 'begin' | 'commit' | 'cancel'; kind: GestureKind; ids: string[] }

export interface ViewportInfo {
  scale: number;          // CSS pixels per point
  scrollLeft: number;
  scrollTop: number;
  marginPt: number;
  pageWidthPt: number;
  pageHeightPt: number;
}

export type Tool = { kind: 'select' } | { kind: 'connector' } | { kind: 'place'; what: string };

/** What the canvas tells the editor. It never carries maxGraph cells, and nothing here edits the document. */
export interface AdapterCallbacks {
  onIntent(intent: Intent): void;
  onSelection(ids: string[]): void;
  onGesture(notice: GestureNotice): void;
  onViewport(info: ViewportInfo): void;
  /** A click on empty canvas with a placement tool armed, in point coordinates. */
  onPlace(what: string, at: Point): void;
}

export interface CanvasAdapter {
  /** Shows the committed snapshot. Programmatic updates are guarded so they never become new edits. */
  project(snapshot: Snapshot, pageId?: string): Result<void>;
  setSelection(ids: string[]): void;
  getSelection(): string[];
  setTool(tool: Tool): void;
  setZoom(scale: number, anchorClient?: { x: number; y: number }): void;
  zoomToFit(): void;
  getViewport(): ViewportInfo;
  dispose(): void;
}
