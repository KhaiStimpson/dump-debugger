import type { Confidence, Severity, ThreadState, Tone } from '../contracts.gen';
import { cap } from '../format';

const sevClass: Record<Severity, string> = { critical: 'crit', warning: 'warn', info: 'info', ok: 'ok' };
const sevLabel: Record<Severity, string> = { critical: 'Critical', warning: 'Warning', info: 'Info', ok: 'Healthy' };

/** Severity pill. Status is never color alone: it always carries its text label. */
export function Sev({ severity, confidence, label }: { severity: Severity; confidence?: Confidence; label?: string }) {
  return (
    <span className={`sev ${sevClass[severity]}`}>
      {label ?? sevLabel[severity]}
      {confidence ? ` · ${confidence} confidence` : ''}
    </span>
  );
}

export function Conf({ confidence }: { confidence: Confidence }) {
  return <span className="conf">{cap(confidence)} confidence</span>;
}

const stateClass: Record<ThreadState, string> = {
  blocked: 'st-blk',
  waiting: 'st-wait',
  running: 'st-run',
  idle: 'st-idle',
};

export function StatePill({ state, label }: { state: ThreadState; label: string }) {
  return <span className={`state ${stateClass[state]}`}>{label}</span>;
}

export const toneColor: Record<Tone, string> = {
  neutral: 'var(--fg-3)',
  accent: 'var(--accent)',
  ok: 'var(--ok)',
  info: 'var(--info)',
  warning: 'var(--warn)',
  critical: 'var(--crit)',
};
