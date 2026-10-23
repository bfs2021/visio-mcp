// Summarises the A05 probe runs (docs/acceptance/evidence/A05/*.json) into one table.
// Usage: node tools/summarize-shim-pause.mjs [evidence-dir]   (also writes summary.json beside the raw files)
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), '..', 'docs', 'acceptance', 'evidence', 'A05');
const runs = readdirSync(dir).filter(f => /^s\d+-.*\.json$/.test(f)).map(f => ({ file: f, ...JSON.parse(readFileSync(join(dir, f), 'utf8')) }));
const byName = new Map();
for (const r of runs) { const k = r.scenario ?? r.file.replace(/\.r\d+\.json$|\.json$/, ''); (byName.get(k) ?? byName.set(k, []).get(k)).push(r); }

const longest = r => Math.max(0, ...(r.poolStarvedWindows ?? []).map(w => w.lengthMs));
const median = xs => { const s = [...xs].sort((a, b) => a - b); return s.length ? s[Math.floor(s.length / 2)] : 0; };
const rows = [...byName.entries()].sort((a, b) => parseInt(a[0].slice(1)) - parseInt(b[0].slice(1))).map(([name, rs]) => {
  const ran = rs.filter(r => r.status !== 'NOT RUN');
  const worst = ran.map(longest);
  return {
    scenario: name, question: ran[0]?.question ?? '', runs: ran.length,
    wholeProcessStops: ran.reduce((n, r) => n + (r.processStops?.length ?? 0), 0),
    gcPauseMsTotal: Math.round(ran.reduce((n, r) => n + (r.processStops ?? []).reduce((m, s) => m + s.gcPauseDeltaMs, 0), 0)),
    worstPoolStarvationMs: Math.max(0, ...worst), medianWorstPerRunMs: median(worst),
    runsWithStarvationOverOneSecond: worst.filter(x => x >= 1000).length,
    runsWithPoolTimerLateOverOneSecond: ran.filter(r => (r.poolTimerLate ?? []).some(e => e.lateMs >= 1000)).length,
    runsWithUiThreadLateOverOneSecond: ran.filter(r => (r.uiThreadLate ?? []).some(e => e.answeredAfterMs >= 1000)).length,
    worstAppPipeAnswerMs: Math.max(0, ...ran.flatMap(r => (r.appPipeSlow ?? []).map(e => e.answeredAfterMs))),
    windowsOver700ms: ran.reduce((n, r) => n + (r.poolStarvedWindows ?? []).filter(w => w.lengthMs >= 700).length, 0),
    windowsOver700msWithin1500msOfChildStart: ran.reduce((n, r) => n + (r.poolStarvedWindows ?? []).filter(w => w.lengthMs >= 700 && w.startMs >= 1900 && w.startMs <= 2000 + 1500).length, 0),
    machineCpuBusyPercent: Math.round(ran.reduce((n, r) => n + (r.machineCpuBusyPercentOverall ?? 0), 0) / Math.max(1, ran.length)),
    notRun: rs.length - ran.length,
  };
});
writeFileSync(join(dir, 'summary.json'), JSON.stringify({ summarised: new Date().toISOString(), rows }, null, 2));
console.log('| Scenario | Runs | Whole-process stops | Worst pool starvation (ms) | Median of per-run worst (ms) | Runs with starvation >= 1 s | Windows >= 0.7 s (of which within 1.5 s of the child start) | Machine CPU busy | App pipe worst (ms) |');
console.log('|---|---|---|---|---|---|---|---|---|');
for (const r of rows) console.log(`| ${r.scenario} | ${r.runs}${r.notRun ? ` (+${r.notRun} not run)` : ''} | ${r.wholeProcessStops} | ${r.worstPoolStarvationMs} | ${r.medianWorstPerRunMs} | ${r.runsWithStarvationOverOneSecond} | ${r.windowsOver700ms} (${r.windowsOver700msWithin1500msOfChildStart}) | ${r.machineCpuBusyPercent}% | ${r.worstAppPipeAnswerMs || ''} |`);
