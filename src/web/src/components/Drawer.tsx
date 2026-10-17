import type { EditorModel } from '../editor/EditorModel';
import { ScriptDrawer } from './ScriptDrawer';
import { TransactionDrawer } from './TransactionDrawer';
import { useModel } from './useModel';

/** The optional bottom drawer: a script editor and a transparent record of every transaction. There is no built-in chat. */
export function Drawer({ model }: { model: EditorModel }) {
  useModel(model);
  if (!model.drawerOpen) return null;
  const tab = (id: 'script' | 'transactions', label: string) => (
    <button type="button" role="tab" aria-selected={model.drawerTab === id} className={model.drawerTab === id ? 'active' : ''} data-testid={`drawer-tab-${id}`} onClick={() => model.toggleDrawer(id)}>{label}</button>
  );
  return (
    <section className="drawer" data-testid="drawer" aria-label="Script and transactions">
      <div className="drawer-tabs" role="tablist">
        {tab('script', 'Script')}
        {tab('transactions', 'Transactions')}
        <button type="button" className="drawer-close" data-testid="drawer-close" aria-label="Close drawer" onClick={() => model.toggleDrawer()}>×</button>
      </div>
      {model.drawerTab === 'script' ? <ScriptDrawer model={model} /> : <TransactionDrawer model={model} />}
    </section>
  );
}
