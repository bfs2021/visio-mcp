import { findElement } from '../canvas/intents';
import type { EditorModel } from './EditorModel';

export interface ArrangeAction {
  id: string;
  label: string;
  shortcut?: string;
  disabled: boolean;
  run: () => void;
  /** Actions in one section are shown together, with a separator between sections. */
  section: 'group' | 'order' | 'align' | 'distribute';
}

/** The one list of structural actions. The Arrange menu, the context menu and the Arrange panel all render it, so they cannot disagree. */
export function arrangeActions(model: EditorModel): ArrangeAction[] {
  const targets = model.targets;
  const n = targets.length;
  const hasGroup = targets.some(t => findElement(model.snapshot, t)?.kind === 'group');
  const act = (id: string, label: string, section: ArrangeAction['section'], enabled: boolean, run: () => Promise<void>, shortcut?: string): ArrangeAction =>
    ({ id, label, section, disabled: !enabled, run: () => void run(), ...(shortcut ? { shortcut } : {}) });
  return [
    act('group', 'Group', 'group', n >= 2, () => model.group(), 'Ctrl+G'),
    act('ungroup', 'Ungroup', 'group', hasGroup, () => model.ungroup(), 'Ctrl+Shift+G'),
    act('front', 'Bring to front', 'order', n >= 1, () => model.reorder('front'), 'Ctrl+Shift+F'),
    act('forward', 'Bring forward', 'order', n >= 1, () => model.reorder('forward')),
    act('backward', 'Send backward', 'order', n >= 1, () => model.reorder('backward')),
    act('back', 'Send to back', 'order', n >= 1, () => model.reorder('back'), 'Ctrl+Shift+B'),
    act('align-left', 'Align left', 'align', n >= 2, () => model.align('left')),
    act('align-center', 'Align centre', 'align', n >= 2, () => model.align('center')),
    act('align-right', 'Align right', 'align', n >= 2, () => model.align('right')),
    act('align-top', 'Align top', 'align', n >= 2, () => model.align('top')),
    act('align-middle', 'Align middle', 'align', n >= 2, () => model.align('middle')),
    act('align-bottom', 'Align bottom', 'align', n >= 2, () => model.align('bottom')),
    act('distribute-h', 'Distribute horizontally', 'distribute', n >= 3, () => model.distribute('horizontal')),
    act('distribute-v', 'Distribute vertically', 'distribute', n >= 3, () => model.distribute('vertical')),
  ];
}
