/**
 * DrawScript lexer: one physical line at a time. There is no evaluation of any kind here, only classification of characters.
 * Strings are double quoted with JSON escapes; `#` starts a comment only outside a quoted string.
 */

export interface Span { line: number; column: number; length: number }

export class ScriptError extends Error {
  constructor(message: string, readonly span: Span, readonly hint?: string) { super(message); }
}

export type Token =
  | (Span & { type: 'word'; text: string })
  | (Span & { type: 'string'; value: string; raw: string })
  | (Span & { type: 'number'; value: number; unit: string; raw: string })
  /** `name=`: the value token that follows must start immediately after the equals sign. */
  | (Span & { type: 'key'; text: string });

export interface LexedToken { token: Token; /** The token began directly after an equals sign. */ attached: boolean }

const WORD = /^[A-Za-z_][A-Za-z0-9_.\-]*$/;
const KEY = /^[A-Za-z_][A-Za-z0-9_]*$/;
const NUMBER = /^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)([A-Za-z%]*)$/;

export function lexLine(text: string, line: number): LexedToken[] {
  const out: LexedToken[] = [];
  let i = 0;
  let afterEquals = false;
  const push = (token: Token) => { out.push({ token, attached: afterEquals }); afterEquals = false; };
  while (i < text.length) {
    const c = text[i]!;
    if (c === ' ' || c === '\t' || c === '\r') { afterEquals = false; i++; continue; }
    if (c === '#') break;
    if (c === '"') {
      let j = i + 1;
      for (; j < text.length; j++) {
        if (text[j] === '\\') { j++; continue; }
        if (text[j] === '"') break;
      }
      if (j >= text.length) throw new ScriptError('this string is not closed', { line, column: i + 1, length: text.length - i });
      const raw = text.slice(i, j + 1);
      let value: string;
      try { value = JSON.parse(raw) as string; } catch { throw new ScriptError('this string has an invalid escape or a raw control character', { line, column: i + 1, length: raw.length }, 'use JSON escapes such as \\" \\\\ \\n \\u00e9'); }
      push({ type: 'string', value, raw, line, column: i + 1, length: raw.length });
      i = j + 1;
      continue;
    }
    if (c === '=') throw new ScriptError('an equals sign must follow a field name directly, as in x=10mm', { line, column: i + 1, length: 1 });
    // a run of ordinary characters
    let j = i;
    while (j < text.length && !/[\s"#=]/.test(text[j]!)) j++;
    const run = text.slice(i, j);
    if (text[j] === '=') {
      if (!KEY.test(run)) throw new ScriptError(`"${run}" is not a valid field name`, { line, column: i + 1, length: run.length + 1 });
      push({ type: 'key', text: run, line, column: i + 1, length: run.length + 1 });
      i = j + 1;
      afterEquals = true;
      continue;
    }
    const num = NUMBER.exec(run);
    if (num) push({ type: 'number', value: Number(num[1]), unit: num[2]!, raw: run, line, column: i + 1, length: run.length });
    else if (WORD.test(run)) push({ type: 'word', text: run, line, column: i + 1, length: run.length });
    else throw new ScriptError(`unexpected characters "${run}"`, { line, column: i + 1, length: run.length }, 'quote text that contains spaces or symbols');
    i = j;
  }
  if (afterEquals) {
    const last = out[out.length - 1]!.token;
    throw new ScriptError(`${last.type === 'key' ? last.text : 'field'}= has no value`, { line, column: last.column, length: last.length });
  }
  return out;
}
