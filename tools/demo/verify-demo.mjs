// Verifies the spec section 23 demonstration from its saved evidence only: semantic snapshots and transaction reports,
// never pixels. Usage: node tools/demo/verify-demo.mjs [evidence-dir]
// Exit code 0 when every check passes, 1 when any fails. Steps the demo marked NOT RUN are listed, not counted as passes.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dir = process.argv[2] ?? join(here, '..', '..', 'docs', 'acceptance', 'evidence', 'DEMO');
const read = f => JSON.parse(readFileSync(join(dir, f), 'utf8'));

const canon = v => Array.isArray(v) ? `[${v.map(canon).join(',')}]`
  : v && typeof v === 'object' ? `{${Object.keys(v).sort().map(k => `${JSON.stringify(k)}:${canon(v[k])}`).join(',')}}`
  : JSON.stringify(v);
const elements = snap => new Map(snap.document.pages[0].elements.map(e => [e.id, e]));
const byAlias = (snap, alias) => [...elements(snap).values()].find(e => e.alias === alias);
const checks = [];
const check = (step, name, ok, detail = '') => checks.push({ step, name, ok: !!ok, detail: String(detail) });

/** Everything not named by the transaction must be byte-identical between two snapshots. */
function untouched(step, label, before, after, tx, allowExtra = []) {
  const b = elements(before), a = elements(after);
  const named = new Set([...tx.created, ...tx.changed, ...tx.deleted, ...allowExtra]);
  const drift = [...b.keys()].filter(id => !named.has(id) && (!a.has(id) || canon(a.get(id)) !== canon(b.get(id))));
  check(step, `${label}: every object the transaction did not name is byte-identical (${b.size - named.size} checked)`, drift.length === 0, drift.join(', '));
  const reallyChanged = [...b.keys()].filter(id => a.has(id) && canon(a.get(id)) !== canon(b.get(id)));
  const undeclared = reallyChanged.filter(id => !named.has(id));
  check(step, `${label}: nothing changed that the transaction did not declare`, undeclared.length === 0, undeclared.join(', '));
}

const transcript = read('transcript.json');
const file = (step, what) => transcript.find(t => t.step === step && (!what || t.what.includes(what)));

// ---- step 1
const s1 = read(file(1).snapshot);
const page = s1.document.pages[0];
check(1, 'blank A4 landscape page', Math.abs(page.widthPt - 841.89) < 0.5 && Math.abs(page.heightPt - 595.28) < 0.5 && page.elements.length === 0, `${page.widthPt} x ${page.heightPt}, ${page.elements.length} elements`);

// ---- step 2
const s2 = read(file(2).snapshot);
const e2 = [...elements(s2).values()];
const count = k => e2.filter(e => e.kind === k).length;
check(2, 'four labelled blocks, three connectors, annotations and a logo', count('shape') === 4 && count('connector') === 3 && count('text') === 2 && count('image') === 1, e2.map(e => e.kind).join(','));
check(2, 'every connector is glued to two existing elements', e2.filter(e => e.kind === 'connector').every(c => elements(s2).has(c.from.elementId) && elements(s2).has(c.to.elementId)));
check(2, 'every element has a stable UUID', e2.every(e => /^[0-9a-f-]{36}$/.test(e.id)));

// ---- step 5: align only the affected elements
{
  const before = read('step05-before.json'), after = read('step05-snapshot.json'), tx = read('step05-transaction.json');
  check(5, 'the defect existed before the correction', Math.abs(byAlias(before, 'erp').bounds.y - byAlias(before, 'crm').bounds.y) > 5);
  check(5, 'CRM and ERP now share one top edge', Math.abs(byAlias(after, 'erp').bounds.y - byAlias(after, 'crm').bounds.y) < 0.01);
  const touching = [...elements(after).values()].filter(e => e.kind === 'connector' && [e.from.elementId, e.to.elementId].includes(byAlias(after, 'erp').id)).map(e => e.id);
  check(5, 'the transaction changed only the misaligned shape', tx.changed.length === 1 && tx.changed[0] === byAlias(after, 'erp').id, tx.changed.join(','));
  untouched(5, 'alignment', before, after, tx, touching);
  check(5, 'the other row (Billing, Data) is exactly as it was', ['billing', 'data'].every(a => canon(byAlias(before, a)) === canon(byAlias(after, a))));
}

// ---- step 6: what the human did, from the snapshots on either side
const s5 = read('step05-snapshot.json'), s6 = read(file(6).snapshot);
{
  const dataBefore = byAlias(s5, 'data'), dataAfter = byAlias(s6, 'data');
  const billingBefore = byAlias(s5, 'billing'), billingAfter = byAlias(s6, 'billing');
  check(6, 'the human moved one element', dataAfter.bounds.x - dataBefore.bounds.x > 20 && Math.abs(dataAfter.bounds.y - dataBefore.bounds.y) < 40, `${(dataAfter.bounds.x - dataBefore.bounds.x).toFixed(1)} pt`);
  check(6, 'the human changed one fill colour', canon(billingBefore.style.fill) !== canon(billingAfter.style.fill) && billingAfter.style.fill.colour.toLowerCase() === '#fde68a');
  const changed = [...elements(s5).keys()].filter(id => canon(elements(s5).get(id)) !== canon(elements(s6).get(id)));
  const connectorsOfData = e => e.kind === 'connector' && [e.from.elementId, e.to.elementId].includes(dataAfter.id);
  const unexpected = changed.filter(id => id !== dataAfter.id && id !== billingAfter.id && !connectorsOfData(elements(s6).get(id)));
  check(6, 'only the two edited objects (and connectors glued to the moved one) differ', unexpected.length === 0, unexpected.join(','));
}

// ---- step 7: the agent preserved the human's work
{
  const before = read('step07-before.json'), after = read('step07-snapshot.json'), tx = read('step07-transaction.json'), changes = read('step07-changes.json');
  const data = byAlias(before, 'data').id, billing = byAlias(before, 'billing').id;
  check(7, 'get_changes reported the human edits as GUI transactions', changes.transactions.length >= 2 && changes.transactions.every(t => t.source === 'gui'), changes.transactions.map(t => t.source).join(','));
  check(7, 'get_changes named the dragged and the recoloured objects', [data, billing].every(id => changes.transactions.some(t => t.changed.includes(id))));
  check(7, 'the dragged object is byte-identical after the agent follow-up', canon(elements(before).get(data)) === canon(elements(after).get(data)));
  check(7, 'the recoloured object is byte-identical after the agent follow-up', canon(elements(before).get(billing)) === canon(elements(after).get(billing)));
  check(7, 'the follow-up changed exactly the annotation it meant to', tx.changed.length === 1 && tx.changed[0] === byAlias(after, 'note1').id && byAlias(after, 'note1').text.value === 'Customer records, synced nightly');
  untouched(7, 'follow-up', before, after, tx);
}

// ---- steps 8 and 9: the logo swap
let s9;
{
  const before = read('step09-before.json'); s9 = read('step09-snapshot.json'); const tx = read('step09-transaction.json');
  const logoBefore = byAlias(before, 'esg_logo'), logoAfter = byAlias(s9, 'esg_logo');
  check(9, 'the picture now points at the approved asset', logoBefore.assetId === 'asset:esg-old' && logoAfter.assetId === 'asset:esg-global-primary', `${logoBefore.assetId} -> ${logoAfter.assetId}`);
  check(9, 'only that image object changed', tx.changed.length === 1 && tx.changed[0] === logoAfter.id, tx.changed.join(','));
  check(9, 'its position and size are unchanged', canon(logoBefore.bounds) === canon(logoAfter.bounds));
  untouched(9, 'logo swap', before, s9, tx);
  const assets = s9.document.assets.map(a => a.id);
  check(9, 'both picture descriptors are pinned in the document, with their hashes', assets.includes('asset:esg-old') && assets.includes('asset:esg-global-primary') && s9.document.assets.every(a => /^[0-9a-f]{64}$/.test(a.sha256)), assets.join(','));
}

// ---- step 14: every id resolves, in the app's own export and in the file Visio saved
function roundTrip(label, snapFile, tolerancePt, visioEdits) {
  const back = read(snapFile);
  const a = elements(s9), b = elements(back);
  const missing = [...a.keys()].filter(id => !b.has(id));
  check(14, `${label}: every agent id resolves`, missing.length === 0 && b.size === a.size, `${b.size}/${a.size}${missing.length ? ' missing ' + missing.join(',') : ''}`);
  const same = (x, y) => Math.abs(x - y) <= tolerancePt;
  const drift = [...a.entries()].filter(([id, e]) => {
    const o = b.get(id); if (!o || o.kind !== e.kind) return true;
    if (visioEdits.has(id)) return false;
    // a connector's stored bounds are authored fallback geometry (the live extent is visualBounds), so only its glue is compared
    if (e.kind === 'connector') return o.from.elementId !== e.from.elementId || o.to.elementId !== e.to.elementId;
    return !['x', 'y', 'width', 'height'].every(k => same(e.bounds[k], o.bounds[k]));
  }).map(([id]) => byId(a, id));
  check(14, `${label}: kinds, geometry (within ${tolerancePt} pt) and connector glue are preserved (connectors: glue only)`, drift.length === 0, drift.join(','));
  const textDrift = [...a.entries()].filter(([id, e]) => !visioEdits.has(id) && (e.text?.value ?? '') !== (b.get(id)?.text?.value ?? '')).map(([id]) => byId(a, id));
  check(14, `${label}: labels are preserved`, textDrift.length === 0, textDrift.join(','));
  return back;
}
const byId = (m, id) => m.get(id)?.alias ?? id;
roundTrip('the application\'s own export', file(14, 'own export').snapshot, 0.5, new Set());
if (existsSync(join(dir, 'step14-visio-snapshot.json'))) {
  const crm = byAlias(s9, 'crm').id, billing = byAlias(s9, 'billing').id;
  const back = roundTrip('the file Visio saved', 'step14-visio-snapshot.json', 0.5, new Set([crm, billing]));
  check(14, 'Visio\'s edits arrived (text of CRM, billing moved 0.5 in)', byAlias(back, 'crm').text.value === 'CRM edited in Visio' && Math.abs(byAlias(back, 'billing').bounds.y - byAlias(s9, 'billing').bounds.y - 36) < 1);
  check(14, 'the human\'s fill colour survived a Visio save', byAlias(back, 'billing').style.fill.colour.toLowerCase() === '#fde68a');
}

// ---- what the demonstration itself recorded
const result = read('result.json');
for (const s of result.steps) check(s.step, `recorded step ${s.step}: ${s.title}`, s.status === 'PASS' || s.status === 'NOT RUN', `${s.status}: ${s.detail.split('\n')[0]}`);
const notRun = result.steps.filter(s => s.status === 'NOT RUN').map(s => s.step);

const failed = checks.filter(c => !c.ok);
const report = { verified: new Date().toISOString(), evidenceFrom: result.ran, checks: checks.length, failed: failed.length, notRunSteps: notRun, results: checks };
writeFileSync(join(dir, 'verification.json'), JSON.stringify(report, null, 2));
for (const c of checks) console.log(`${c.ok ? 'PASS' : 'FAIL'}  step ${String(c.step).padStart(2)}  ${c.name}${c.ok ? '' : '  <- ' + c.detail}`);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed${notRun.length ? `; steps not run: ${notRun.join(', ')}` : ''}`);
process.exit(failed.length ? 1 : 0);
