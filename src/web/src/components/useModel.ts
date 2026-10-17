import { useSyncExternalStore } from 'react';
import type { EditorModel } from '../editor/EditorModel';

/** Re-renders when anything the UI shows changes; the model object itself is the stable source of values. */
export function useModel(model: EditorModel): number {
  return useSyncExternalStore(model.subscribe, model.getVersion);
}
