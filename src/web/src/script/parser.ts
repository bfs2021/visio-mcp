import { lexLine, ScriptError, type Span, type Token } from './lexer';

export type ValueToken = Exclude<Token, { type: 'key' }>;

export interface Named { key: string; keyToken: Token; value: ValueToken }

export interface Command {
  /** The first word of the line. */
  verb: string;
  verbToken: Token;
  positionals: ValueToken[];
  named: Named[];
  span: Span;
}

/** Splits source into command lines. The grammar of each command lives in the compiler; this only knows words, strings, numbers and key=value. */
export function parse(source: string): Command[] {
  const commands: Command[] = [];
  const lines = source.split('\n');
  for (let n = 0; n < lines.length; n++) {
    const lexed = lexLine(lines[n]!, n + 1);
    if (!lexed.length) continue;
    const first = lexed[0]!.token;
    if (first.type !== 'word') throw new ScriptError('a command must start with a word such as add, set or move', first, 'one command per line');
    const cmd: Command = { verb: first.text, verbToken: first, positionals: [], named: [], span: { line: n + 1, column: first.column, length: lines[n]!.trimEnd().length - first.column + 1 } };
    for (let i = 1; i < lexed.length; i++) {
      const t = lexed[i]!.token;
      if (t.type === 'key') {
        const v = lexed[i + 1];
        if (!v || !v.attached) throw new ScriptError(`${t.text}= has no value`, t);
        if (v.token.type === 'key') throw new ScriptError(`${t.text}= has no value`, t);
        if (cmd.named.some(x => x.key === t.text)) throw new ScriptError(`${t.text}= is given twice`, t);
        cmd.named.push({ key: t.text, keyToken: t, value: v.token });
        i++;
      } else {
        cmd.positionals.push(t);
      }
    }
    commands.push(cmd);
  }
  return commands;
}
