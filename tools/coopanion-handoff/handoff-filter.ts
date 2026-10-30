/**
 * The handoff note Coo writes before its context is cleared, filtered.
 *
 * Cormini's renderer keeps every event and every tool call it can fit, folds long receipts to
 * their first lines and leaves the rest. In Coopanion's windows the bulk is `sts2_view` /
 * `sts2_act` receipts of a few KB each, so one note reaches 15 000 characters and once 130 000.
 * This renderer keeps what the next session acts on and drops the rest:
 *
 * - the person's latest `[typed]` / `[voice]` / `[answer]` lines and Coo's own latest bubbles,
 *   verbatim, counted rather than folded;
 * - a resume block: what was last said, what was running, what comes next;
 * - every tool call as one line, name, argument digest and outcome, at most `maxCharsPerReceipt`;
 * - system chatter, previous notes, blob bodies and receipts older than a day that nothing later
 *   referred to are dropped.
 *
 * Pure: no I/O, so the Persona writes the file and a script can render fixtures.
 */
import type { ContextRecord } from 'cortico/protocol/open-responses/context.ts';

/** Workspace directory the notes go in, and the event type a note is delivered as. */
export const HANDOFF_DIR = 'handoffs/';
export const HANDOFF_NOTE_TYPE = 'handoff-note';

/** Core delivers external events as this tool's output; the output is a frame, not a receipt. */
const EVENT_FRAME_TOOL = 'external_event_frame';
/** Cormini's own turn terminator: it says nothing about the work, only that the turn stopped. */
const CONTROL_TOOL = 'end_turn';
/** One frame line starts with the clock the event happened at. */
const CLOCK = /^\[(\d{2}:\d{2})\]\s*/;
/** The person speaking, in the three shapes the desktop-pet World writes. */
const PARTNER = /^\[(typed|voice|answer)\]/;
/** `[typed] 阿梦:…` → who, and what they said. */
const PARTNER_SAID = /^\[(?:typed|voice)\]\s*([^:：]{1,24})[:：]\s*([\s\S]*)$/;
/** Internal lines: deliveries, pressure notices, world lifecycle, the handoff's own wake line. */
const NOISE = /^\[(system|notice)\]/;
/** A frame's header line. */
const FRAME_HEADER = /^\[\d+ new events?\]\s*/;
/** A note delivered by an earlier handoff, kept out of the next one. */
const OLD_NOTE = /^# 交接笔记/;
/** `[blob log:abcd.png image/png cat] its text form` → the marker alone. */
const BLOB = /\[blob\s+(\S+)[^\]]*\][^\n]*/g;

/** Tool names whose receipt is a state reading: only the last one is worth a line. */
const STATUS_READ = /^(sts2_(view|wait|diag|coop_state)|.*_(view|screenshot|shot|capture|windows|list|state|status|diag))$/;
/** Argument keys that hold a whole file or image; the digest names the path instead. */
const BODY_ARGS = new Set(['old_string', 'new_string', 'content', 'images', 'base64']);

/** Scalar fields worth carrying into the one-line conclusion, in the order they are shown. */
const FACTS = ['screen', 'floor', 'act', 'turn', 'hp', 'gold', 'energy', 'block', 'status', 'action', 'waited', 'waiting', 'reason', 'note', 'hint', 'error'];
/** The same fields when the receipt is JSON that got cut off upstream and no longer parses. */
const FACT_PATTERNS: Array<[string, RegExp]> = [
  ['screen', /"screen"\s*:\s*"([A-Za-z_]+)"/],
  ['floor', /"floor"\s*:\s*(\d+)/],
  ['turn', /"turn"\s*:\s*(\d+)/],
  ['hp', /"hp"\s*:\s*(\d+)/],
  ['gold', /"gold"\s*:\s*(\d+)/],
  ['act', /"act"\s*:\s*(\d+)/],
];

export interface HandoffFilterOptions {
  /** A tool line, receipt and all, never runs longer than this. */
  maxCharsPerReceipt: number;
  /** Consecutive calls of the same tool with the same arguments become one line with a count. */
  collapseRepeats: boolean;
  /** State readings kept per tool; older ones go. */
  keepLastNStatusReads: number;
  /** The person's lines kept, verbatim and whole. */
  keepLastNPartnerLines: number;
  /** Coo's bubbles kept, text only. */
  keepLastNPetLines: number;
  /**
   * Tool lines kept, newest first. The spec caps each line, not their number; a 128k window can
   * still hold hundreds of calls, so the note needs a ceiling of its own.
   */
  maxToolLines: number;
}

export const DEFAULT_HANDOFF_FILTER: HandoffFilterOptions = {
  maxCharsPerReceipt: 200,
  collapseRepeats: true,
  keepLastNStatusReads: 1,
  keepLastNPartnerLines: 30,
  keepLastNPetLines: 10,
  maxToolLines: 40,
};

/** Reads the spec keys out of whatever the config holds; missing or wrong ones fall back. */
export function normalizeHandoffFilter(raw: Partial<HandoffFilterOptions> | null | undefined): HandoffFilterOptions {
  const out = { ...DEFAULT_HANDOFF_FILTER };
  if (!raw || typeof raw !== 'object') return out;
  const number = (value: unknown, fallback: number, min: number, max: number): number => {
    const n = typeof value === 'number' ? value : Number(value);
    if (!Number.isFinite(n)) return fallback;
    return Math.min(max, Math.max(min, Math.round(n)));
  };
  out.maxCharsPerReceipt = number(raw.maxCharsPerReceipt, out.maxCharsPerReceipt, 80, 2000);
  out.collapseRepeats = raw.collapseRepeats === undefined ? out.collapseRepeats : raw.collapseRepeats !== false;
  out.keepLastNStatusReads = number(raw.keepLastNStatusReads, out.keepLastNStatusReads, 0, 20);
  out.keepLastNPartnerLines = number(raw.keepLastNPartnerLines, out.keepLastNPartnerLines, 0, 500);
  out.keepLastNPetLines = number(raw.keepLastNPetLines, out.keepLastNPetLines, 0, 200);
  out.maxToolLines = number(raw.maxToolLines, out.maxToolLines, 0, 500);
  return out;
}

/** `handoffs/2026-10-30T11-20-00Z.md`: a UTC stamp, colons replaced for Windows. */
export function handoffNoteFileName(now: Date): string {
  return `${HANDOFF_DIR}${now.toISOString().replace(/\.\d{3}Z$/, 'Z').replace(/:/g, '-')}.md`;
}

/** What the renderer needs from the running Persona. */
export interface HandoffFilterHooks {
  /** Tools tagged `speak`: their text is what the person saw, the receipt is not. */
  speakTools: ReadonlySet<string>;
}

export interface FilteredNote {
  text: string;
  /** Lines that made it in. */
  entries: number;
  /** Tool calls and events that did not, for the footer and the log. */
  dropped: number;
}

// --- reading the snapshot -------------------------------------------------

interface ItemLike {
  type: string;
  role?: string;
  content?: unknown;
  output?: unknown;
  arguments?: string;
  name?: string;
  call_id?: string;
}

interface MetaLike {
  ts?: string;
  head?: boolean;
  ephemeral?: boolean;
  frame?: { events?: FrameRef[] };
}

interface FrameRef {
  start: number;
  chars: number;
  ts?: string;
  type?: string;
  tags?: string[];
}

/** A user message's content or a receipt's output: text parts joined, plain strings as they are. */
function partsText(parts: unknown): string {
  if (typeof parts === 'string') return parts;
  if (!Array.isArray(parts)) return '';
  const out: string[] = [];
  for (const part of parts) {
    if (part && typeof part === 'object' && typeof (part as { text?: unknown }).text === 'string') out.push((part as { text: string }).text);
  }
  return out.join('');
}

function textOf(record: ContextRecord): string {
  const item = record.item as unknown as ItemLike;
  return partsText(item.content ?? item.output);
}

/** The events a frame carries, sliced by the sidecar Core wrote next to it. */
function frameEvents(record: ContextRecord): Array<{ text: string; ts?: string; type?: string }> {
  const text = textOf(record);
  const refs = (record.context as MetaLike).frame?.events;
  if (!refs || refs.length === 0) {
    const body = text.replace(FRAME_HEADER, '');
    return body.trim() ? [{ text: body, ts: (record.context as MetaLike).ts }] : [];
  }
  const out: Array<{ text: string; ts?: string; type?: string }> = [];
  for (const ref of refs) out.push({ text: text.slice(ref.start, ref.start + ref.chars), ts: ref.ts, type: ref.type });
  return out;
}

/** `[19:09] [typed] 阿梦:…` → the clock and the rest. The frame's clock is local; the text's may be UTC. */
function splitClock(text: string): { clock?: string; body: string } {
  const match = CLOCK.exec(text);
  if (!match) return { body: text };
  return { clock: match[1], body: text.slice(match[0].length) };
}

/** Local `HH:MM`. Frame clocks arrive with a zone of their own, sometimes UTC. */
function clockOf(ts: string | undefined): string | undefined {
  if (!ts) return undefined;
  const at = new Date(ts);
  if (Number.isNaN(at.getTime())) {
    const match = /T(\d{2}:\d{2})/.exec(ts);
    return match ? match[1] : undefined;
  }
  return `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
}

function oneLine(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

function clip(text: string, max: number): string {
  const clean = oneLine(text);
  if (clean.length <= max) return clean;
  if (max <= 1) return clean.slice(0, Math.max(0, max));
  return `${clean.slice(0, max - 1)}…`;
}

/** Clipped, and how long it was: a cut event says so instead of pretending to end there. */
function clipWithSize(text: string, max: number): string {
  const clean = oneLine(text);
  if (clean.length <= max) return clean;
  return `${clean.slice(0, Math.max(0, max - 18))}…(原文 ${clean.length} 字符)`;
}

// --- one tool call --------------------------------------------------------

interface Call {
  name: string;
  args: string;
  digest: string;
  ok: boolean;
  outcome: string;
  clock?: string;
  ts?: string;
  callId: string;
  /** Calls folded into this line, itself included. */
  count: number;
  /** Calls folded in that failed; a line that ends ok may still carry them. */
  failures: number;
  status: boolean;
  pending: boolean;
}

/** Top-level arguments, values shortened: `path=notes/x.md offset=-30`. */
function digestArgs(raw: string, max: number): string {
  const trimmed = (raw ?? '').trim();
  if (!trimmed || trimmed === '{}') return '';
  let data: unknown;
  try {
    data = JSON.parse(trimmed);
  } catch {
    return clip(trimmed, max);
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return clip(trimmed, max);
  const bits: string[] = [];
  for (const [key, value] of Object.entries(data as Record<string, unknown>)) {
    if (BODY_ARGS.has(key)) continue;
    bits.push(`${key}=${shortValue(value)}`);
    if (bits.join(' ').length >= max) break;
  }
  return clip(bits.join(' '), max);
}

function shortValue(value: unknown): string {
  if (Array.isArray(value)) return `[${value.length}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).slice(0, 3).join(',')}}`;
  return clip(String(value ?? ''), 40);
}

/** Scalars worth naming, nearest the top first: screen, floor, hp, gold, turn… */
function collectFacts(value: unknown, into: Map<string, string>, depth: number): void {
  if (depth > 4 || !value || typeof value !== 'object' || into.size >= FACTS.length) return;
  for (const [key, item] of Object.entries(value as Record<string, unknown>)) {
    if (into.size >= FACTS.length) return;
    if (Array.isArray(item)) continue;
    if (item && typeof item === 'object') {
      collectFacts(item, into, depth + 1);
      continue;
    }
    if (typeof item !== 'number' && typeof item !== 'string') continue;
    if (!FACTS.includes(key) || into.has(key)) continue;
    into.set(key, clip(String(item), 40));
  }
}

function findScalar(value: unknown, key: string, depth: number): string | undefined {
  if (depth > 4 || !value || typeof value !== 'object') return undefined;
  for (const [name, item] of Object.entries(value as Record<string, unknown>)) {
    if (name === key && (typeof item === 'number' || typeof item === 'string')) return String(item);
    if (item && typeof item === 'object' && !Array.isArray(item)) {
      const hit = findScalar(item, key, depth + 1);
      if (hit !== undefined) return hit;
    }
  }
  return undefined;
}

/** A receipt the upstream cut off is no longer JSON; the fields still read out of the text. */
function factsFromText(text: string): { ok: boolean | undefined; facts: Map<string, string> } {
  const facts = new Map<string, string>();
  for (const [key, pattern] of FACT_PATTERNS) {
    const match = pattern.exec(text);
    if (match) facts.set(key, match[1]);
  }
  const max = /"max_hp"\s*:\s*(\d+)/.exec(text);
  if (max && facts.has('hp')) facts.set('hp', `${facts.get('hp')}/${max[1]}`);
  const ok = /"ok"\s*:\s*(true|false)/.exec(text);
  return { ok: ok ? ok[1] === 'true' : undefined, facts };
}

function factLine(facts: Map<string, string>): string {
  const shown = [...facts.entries()]
    .sort((a, b) => FACTS.indexOf(a[0]) - FACTS.indexOf(b[0]))
    .slice(0, 5)
    .map(([key, value]) => `${key} ${value}`);
  return shown.length ? `ok · ${shown.join(' · ')}` : 'ok';
}

/** Blob and image bodies leave a marker; their text form is what filled the note. */
function withoutBlobBodies(text: string): string {
  return text.replace(BLOB, (_all, handle: string) => `[blob ${handle}]`);
}

function outcomeOf(receipt: string | undefined): { ok: boolean; text: string } {
  if (receipt === undefined) return { ok: false, text: '(还没回来)' };
  const trimmed = receipt.trim();
  if (!trimmed) return { ok: true, text: '(空回执)' };
  let data: unknown;
  try {
    data = JSON.parse(trimmed);
  } catch {
    data = undefined;
  }
  if (data === undefined || data === null || typeof data !== 'object') {
    const failed = trimmed.startsWith('[tool failed]');
    const text = withoutBlobBodies(trimmed.replace(/^\[tool failed\]\s*/, ''));
    const cut = factsFromText(text);
    if (cut.facts.size) return { ok: cut.ok !== false && !failed, text: factLine(cut.facts) };
    return { ok: !failed, text: clip(text, 110) };
  }
  const obj = data as Record<string, unknown>;
  const ok = obj.ok === false ? false : obj.ok === true ? true : !('error' in obj);
  if (!ok) {
    const reason = [obj.note, obj.hint, obj.message, obj.reason, obj.error, obj.status].find((v) => typeof v === 'string' && v.trim() !== '') as string | undefined;
    return { ok: false, text: reason ? `fail: ${clip(reason, 90)}` : 'fail' };
  }
  const facts = new Map<string, string>();
  collectFacts(obj, facts, 0);
  // hp and max_hp read as one fact; the pair is what tells whether the run is in danger.
  const hp = facts.get('hp');
  if (hp !== undefined) {
    const max = findScalar(obj, 'max_hp', 0);
    if (max !== undefined) facts.set('hp', `${hp}/${max}`);
  }
  return { ok: true, text: factLine(facts) };
}

/** The text Coo said in a speaking call: markers are stage directions, not words. */
function speakText(args: string): string {
  try {
    const value = JSON.parse(args ?? '{}') as { script?: unknown; question?: unknown };
    const text = typeof value.script === 'string' ? value.script : typeof value.question === 'string' ? value.question : '';
    return oneLine(text.replace(/【[^】]*】/g, '').replace(/<[^>]*>/g, ''));
  } catch {
    return '';
  }
}

// --- the note -------------------------------------------------------------

interface Line {
  clock?: string;
  ts?: string;
  text: string;
  count: number;
}

function collectLines(snapshot: readonly ContextRecord[], hooks: HandoffFilterHooks): {
  partner: Line[];
  events: Line[];
  bubbles: string[];
  calls: Call[];
  ended: boolean;
} {
  const outputs = new Map<string, ContextRecord>();
  for (const record of snapshot) {
    const item = record.item as unknown as ItemLike;
    if (item.type === 'function_call_output' && item.call_id) outputs.set(item.call_id, record);
  }

  const partner: Line[] = [];
  const events: Line[] = [];
  const bubbles: string[] = [];
  const calls: Call[] = [];
  let ended = false;

  const pushEvent = (text: string, clock: string | undefined, ts: string | undefined): void => {
    const body = text.trim();
    if (!body) return;
    if (NOISE.test(body) || OLD_NOTE.test(body) || FRAME_HEADER.test(body)) return;
    const clean = body.replace(BLOB, (_all, handle: string) => `[blob ${handle}]`);
    const list = PARTNER.test(clean) ? partner : events;
    const last = list[list.length - 1];
    if (last && last.text === clean) {
      last.count++;
      return;
    }
    list.push({ clock, ts, text: clean, count: 1 });
  };

  for (const record of snapshot) {
    const item = record.item as unknown as ItemLike;
    const meta = record.context as MetaLike;
    if (meta.head || meta.ephemeral) continue;

    if (item.type === 'message') {
      if (item.role === 'assistant' || item.role === 'system' || item.role === 'developer') continue;
      for (const event of frameEvents(record)) {
        const { clock, body } = splitClock(event.text);
        // The frame's own clock is local; the one inside the text comes from whoever wrote it.
        pushEvent(body, clockOf(event.ts ?? meta.ts) ?? clock, event.ts ?? meta.ts);
      }
      continue;
    }

    if (item.type !== 'function_call') continue;
    const name = item.name ?? '';
    const receipt = item.call_id ? outputs.get(item.call_id) : undefined;

    if (name === EVENT_FRAME_TOOL) {
      if (!receipt) continue;
      for (const event of frameEvents(receipt)) {
        const { clock, body } = splitClock(event.text);
        pushEvent(body, clockOf(event.ts) ?? clock, event.ts);
      }
      continue;
    }

    if (hooks.speakTools.has(name)) {
      const said = speakText(item.arguments ?? '');
      if (said) bubbles.push(said);
      continue;
    }

    if (name === CONTROL_TOOL) {
      ended = true;
      continue;
    }
    ended = false;

    const outcome = outcomeOf(receipt ? textOf(receipt) : undefined);
    calls.push({
      name,
      args: item.arguments ?? '',
      digest: digestArgs(item.arguments ?? '', 60),
      ok: outcome.ok,
      outcome: outcome.text,
      clock: clockOf(meta.ts),
      ts: meta.ts,
      callId: item.call_id ?? '',
      count: 1,
      failures: outcome.ok ? 0 : 1,
      status: STATUS_READ.test(name),
      pending: receipt === undefined,
    });
  }

  return { partner, events, bubbles, calls, ended };
}

/** Folds consecutive repeats, drops day-old calls nothing referred to, keeps N state readings. */
function foldCalls(calls: readonly Call[], opts: HandoffFilterOptions, now: Date): { kept: Call[]; dropped: number } {
  let dropped = 0;
  const merged: Call[] = [];
  for (const call of calls) {
    const last = merged[merged.length - 1];
    if (opts.collapseRepeats && last && last.name === call.name && last.args === call.args) {
      last.count += call.count;
      last.failures += call.failures;
      last.ok = call.ok;
      last.outcome = call.outcome;
      last.clock = call.clock;
      last.ts = call.ts;
      last.pending = call.pending;
      dropped += 1;
      continue;
    }
    merged.push({ ...call });
  }

  // A failed try followed by a success of the same tool before any other tool ran is a retry:
  // the success keeps the line, the failures become a count on it.
  const retried = new Set<Call>();
  for (let i = 0; i < merged.length; i++) {
    const failed = merged[i];
    if (failed.ok) continue;
    let j = i + 1;
    while (j < merged.length && merged[j].name === failed.name && !merged[j].ok) j++;
    const success = merged[j];
    if (!success || success.name !== failed.name || !success.ok) continue;
    retried.add(failed);
    success.count += failed.count;
    success.failures += failed.failures;
    dropped += failed.count;
  }
  const attempts = merged.filter((call) => !retried.has(call));

  // A day-old call nothing later referred to is history the workspace files already hold.
  const cutoff = now.getTime() - 24 * 60 * 60 * 1000;
  const referenced = (call: Call, index: number): boolean => {
    const key = primaryArg(call.args);
    for (let i = index + 1; i < attempts.length; i++) {
      const later = attempts[i];
      if (call.callId && (later.args.includes(call.callId) || later.outcome.includes(call.callId))) return true;
      if (key && (later.args.includes(key) || later.outcome.includes(key))) return true;
    }
    return false;
  };
  const fresh: Call[] = [];
  attempts.forEach((call, index) => {
    const time = call.ts ? Date.parse(call.ts) : NaN;
    const old = Number.isFinite(time) && time < cutoff;
    const tail = index >= attempts.length - 3;
    if (old && !tail && !referenced(call, index)) {
      dropped += call.count;
      return;
    }
    fresh.push(call);
  });

  // State readings: the last N per tool, with the count of the ones that went.
  const totals = new Map<string, number>();
  for (const call of fresh) if (call.status) totals.set(call.name, (totals.get(call.name) ?? 0) + call.count);
  const seen = new Map<string, number>();
  const statusKept = new Set<Call>();
  for (let i = fresh.length - 1; i >= 0; i--) {
    const call = fresh[i];
    if (!call.status) continue;
    const seenSoFar = seen.get(call.name) ?? 0;
    seen.set(call.name, seenSoFar + 1);
    if (seenSoFar < opts.keepLastNStatusReads) statusKept.add(call);
    else dropped += call.count;
  }
  const withStatus = fresh.filter((call) => !call.status || statusKept.has(call));
  for (const call of withStatus) if (statusKept.has(call)) call.count = totals.get(call.name) ?? call.count;

  const over = Math.max(0, withStatus.length - opts.maxToolLines);
  const kept = withStatus.slice(over);
  dropped += withStatus.slice(0, over).reduce((sum, call) => sum + call.count, 0);
  return { kept, dropped };
}

/** The first string argument, used to see whether a later call referred to this one. */
function primaryArg(args: string): string | undefined {
  try {
    const value = JSON.parse(args ?? '{}') as Record<string, unknown>;
    for (const [key, item] of Object.entries(value)) {
      if (BODY_ARGS.has(key)) continue;
      if (typeof item === 'string' && item.length >= 4) return item.slice(0, 80);
    }
  } catch {
    return undefined;
  }
  return undefined;
}

function callLine(call: Call, opts: HandoffFilterOptions): string {
  const clock = call.clock ? `${call.clock} ` : '';
  const args = call.digest ? `(${call.digest})` : '';
  const suffix = call.count > 1
    ? call.ok && call.failures > 0
      ? ` (共 ${call.count} 次,前 ${call.failures} 次失败)`
      : call.ok ? ` (共 ${call.count} 次)` : ` (共 ${call.count} 次失败)`
    : '';
  const head = `${clock}${call.name}${args}`;
  const room = Math.max(24, opts.maxCharsPerReceipt - head.length - suffix.length - 4);
  return `${head} → ${clip(call.outcome, room)}${suffix}`;
}

function clockLine(line: Line): string {
  return line.clock ? `${line.clock} ${line.text}` : line.text;
}

/** What the person said, without the `[typed] 阿梦:` delivery prefix. */
function partnerText(text: string): string {
  const match = PARTNER_SAID.exec(text);
  return match ? match[2] : text;
}

/** Where the conversation was and what comes next, in one to three lines. */
function resumeBlock(partner: readonly Line[], bubbles: readonly string[], calls: readonly Call[], ended: boolean): string[] {
  const lines: string[] = [];
  const lastPartner = partner[partner.length - 1];
  const lastBubble = bubbles[bubbles.length - 1];
  if (lastPartner || lastBubble) {
    const said = lastPartner ? `阿梦「${clip(partnerText(lastPartner.text), 100)}」` : '';
    const answered = lastBubble ? `汐「${clip(lastBubble, 100)}」` : '';
    lines.push(`- 上次说到:${[said, answered].filter(Boolean).join(';')}`);
  }
  const last = calls[calls.length - 1];
  if (last) lines.push(`- 正在做:${clip(`${last.name}${last.digest ? `(${last.digest})` : ''} → ${last.outcome}`, 160)}`);
  const pending = [...calls].reverse().find((call) => call.pending);
  const waitingOnPerson = lastPartner?.ts && (!last?.ts || Date.parse(lastPartner.ts) > Date.parse(last.ts));
  const next = pending
    ? `等 ${pending.name} 的回执`
    : waitingOnPerson
      ? '回阿梦最后那句'
      : ended
        ? '等阿梦说话或下一条事件'
        : last && !last.ok
          ? `重试 ${last.name}`
          : last ? `接着 ${last.name} 之后继续` : '';
  if (next) lines.push(`- 下一步:${next}`);
  return lines;
}

export function renderFilteredNote(
  snapshot: readonly ContextRecord[],
  options: Partial<HandoffFilterOptions> | undefined,
  now: Date,
  hooks: HandoffFilterHooks,
): FilteredNote {
  const opts = normalizeHandoffFilter(options);
  const { partner, events, bubbles, calls, ended } = collectLines(snapshot, hooks);
  const { kept, dropped: droppedCalls } = foldCalls(calls, opts, now);

  const partnerKept = partner.slice(Math.max(0, partner.length - opts.keepLastNPartnerLines));
  const bubblesKept = bubbles.slice(Math.max(0, bubbles.length - opts.keepLastNPetLines));
  const eventsKept = events.slice(Math.max(0, events.length - 20));

  const times = [...partnerKept, ...eventsKept, ...kept]
    .map((entry) => ({ time: entry.ts ? Date.parse(entry.ts) : NaN, ts: entry.ts }))
    .filter((entry) => Number.isFinite(entry.time))
    .sort((a, b) => a.time - b.time);
  const first = times.length ? clockOf(times[0].ts) : undefined;
  const last = times.length ? clockOf(times[times.length - 1].ts) : undefined;
  const range = first && last ? (first === last ? first : `${first}–${last}`) : '';
  const stamp = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;

  const out: string[] = [`# 交接笔记 · ${range || stamp}`, ''];

  const resume = resumeBlock(partnerKept, bubblesKept, kept, ended);
  if (resume.length) out.push('## 接着上次', ...resume, '');

  if (partnerKept.length) {
    out.push(
      `## 阿梦最近说的话(${partnerKept.length} 条)`,
      ...partnerKept.map((line) => `- ${clockLine(line)}${line.count > 1 ? `(共 ${line.count} 次)` : ''}`),
    );
    if (partner.length > partnerKept.length) out.push(`- 更早的 ${partner.length - partnerKept.length} 条没进这份笔记`);
    out.push('');
  }

  if (bubblesKept.length) {
    out.push(
      `## 汐最近说的话(${bubblesKept.length} 条)`,
      ...bubblesKept.map((line) => `- ${clip(line, opts.maxCharsPerReceipt)}`),
    );
    if (bubbles.length > bubblesKept.length) out.push(`- 更早的 ${bubbles.length - bubblesKept.length} 条没进这份笔记`);
    out.push('');
  }

  if (kept.length || droppedCalls) {
    out.push('## 做过的事', ...kept.map((call) => `- ${callLine(call, opts)}`));
    if (droppedCalls) out.push(`- 更早的 ${droppedCalls} 条工具调用没进这份笔记`);
    out.push('');
  }

  if (eventsKept.length) {
    out.push(
      '## 其他事件',
      ...eventsKept.map((line) => {
        const head = clockLine({ ...line, text: clipWithSize(line.text, opts.maxCharsPerReceipt) });
        return `- ${head}${line.count > 1 ? `(共 ${line.count} 次)` : ''}`;
      }),
    );
    if (events.length > eventsKept.length) out.push(`- 更早的 ${events.length - eventsKept.length} 条没进这份笔记`);
    out.push('');
  }

  const text = `${out.join('\n').trimEnd()}\n`;
  return {
    text,
    entries: partnerKept.length + bubblesKept.length + kept.length + eventsKept.length,
    dropped: (partner.length - partnerKept.length) + (bubbles.length - bubblesKept.length) + droppedCalls + (events.length - eventsKept.length),
  };
}
