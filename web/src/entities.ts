import type { EntityKind, EntityRef } from './contracts.gen';

/**
 * Entity URIs, mirroring DumpDebugger.Core's EntityUri: thread/{id}, lock/0x{addr},
 * object/0x{addr}, type/{escaped}, request/{escaped}, finding/{id}. They key the inspector
 * history, pins and report links.
 */
export const uri = {
  thread: (id: number) => `thread/${id}`,
  lock: (address: number) => `lock/0x${address.toString(16)}`,
  object: (address: number) => `object/0x${address.toString(16)}`,
  type: (name: string) => `type/${encodeURIComponent(name)}`,
  request: (route: string) => `request/${encodeURIComponent(route)}`,
  finding: (id: string) => `finding/${id}`,
};

const kinds: Record<string, EntityKind> = {
  thread: 'thread',
  lock: 'lock',
  object: 'object',
  type: 'type',
  request: 'request',
  finding: 'finding',
};

export function kindOf(u: string): EntityKind | undefined {
  return kinds[u.slice(0, u.indexOf('/'))];
}

export function keyOf(u: string): string {
  const raw = u.slice(u.indexOf('/') + 1);
  const k = kindOf(u);
  return k === 'type' || k === 'request' ? decodeURIComponent(raw) : raw;
}

export function threadIdOf(u: string): number | undefined {
  return kindOf(u) === 'thread' ? Number(keyOf(u)) : undefined;
}

export function addressOf(u: string): number | undefined {
  const k = kindOf(u);
  if (k !== 'object' && k !== 'lock') return undefined;
  return parseInt(keyOf(u).replace(/^0x/, ''), 16);
}

/** A best-effort label for a URI with no EntityRef at hand. */
export function fallbackLabel(u: string): string {
  const k = kindOf(u);
  const key = keyOf(u);
  if (k === 'thread') return `#${key}`;
  if (k === 'type') return shortType(key);
  return key;
}

export function refFor(u: string, known?: readonly EntityRef[]): EntityRef {
  return (
    known?.find((e) => e.uri === u) ?? { uri: u, kind: kindOf(u) ?? 'object', label: fallbackLabel(u), detail: null }
  );
}

/** System.Collections.Generic.Dictionary<System.String,X> → Dictionary<String,X> */
export function shortType(name: string): string {
  return name
    .replace(/(?:[A-Za-z_][\w]*\.)+(?=[A-Za-z_<])/g, '')
    .replace(/\+/g, '.')
    .replace(/\b(Byte|Int32|Int64|String|Object|Boolean|Char|Double|Single|Decimal)\b/g, (t) => aliases[t] ?? t);
}

const aliases: Record<string, string> = {
  Byte: 'byte',
  Int32: 'int',
  Int64: 'long',
  String: 'string',
  Object: 'object',
  Boolean: 'bool',
  Char: 'char',
  Double: 'double',
  Single: 'float',
  Decimal: 'decimal',
};

export const chipClass: Record<EntityKind, string> = {
  thread: 't',
  lock: 'l',
  object: 'o',
  type: 'ty',
  request: 'rq',
  finding: 'f',
};

export const chipKind: Record<EntityKind, string> = {
  thread: 'thr',
  lock: 'lock',
  object: 'obj',
  type: 'type',
  request: 'req',
  finding: 'finding',
};
