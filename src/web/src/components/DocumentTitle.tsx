import { useEffect } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { documentTitle } from '../editor/documentTitle';
import { useModel } from './useModel';

/** Keeps the page title, and through it the window and taskbar title, in step with the file and its unsaved state. */
export function DocumentTitle({ model }: { model: EditorModel }) {
  useModel(model);
  const title = documentTitle(model.engine.status());
  useEffect(() => { document.title = title; }, [title]);
  return null;
}
