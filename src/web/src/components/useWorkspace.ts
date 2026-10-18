import { useSyncExternalStore } from 'react';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';

/** Re-renders when the tabs change: opened, closed, switched, renamed, or marked unsaved. */
export function useWorkspace(ws: WorkspaceModel): number {
  return useSyncExternalStore(ws.subscribe, ws.getVersion);
}
