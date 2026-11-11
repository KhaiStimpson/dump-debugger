/**
 * In-app glossary ("teach in place"). Terms are matched in finding text and get a dotted
 * underline with this definition on hover; the full list is in the "?" overlay.
 */
export const glossary: Record<string, string> = {
  deadlock:
    'Two or more threads each waiting for something another one holds, in a closed loop. None of them can ever proceed.',
  'sync block': "The CLR's bookkeeping for a lock(obj) statement: which thread owns the lock and how many are waiting.",
  'lock-order inversion':
    'Two code paths take the same locks in opposite order. Under load they eventually interleave and deadlock.',
  'wait-for graph':
    "Who's waiting on whom: each blocked thread points at the lock it wants, and each lock at the thread holding it.",
  'GC root':
    "Something the garbage collector treats as always alive: a static field, a local variable on a thread's stack, or a GC handle. Anything reachable from a root can't be collected.",
  'static field':
    'A field that belongs to a type rather than an object. It lives for the whole life of the process, and so does everything it references.',
  'retention path': "The chain of references from a GC root to an object: the reason it's still in memory.",
  'thread pool':
    'The shared set of worker threads .NET uses for ASP.NET requests, async continuations and timers. It grows slowly, so blocking its threads makes everything wait.',
  'work item': 'A piece of work queued to the thread pool, waiting for a free thread to run it.',
  'sync-over-async':
    'Blocking a thread on an async operation (.Result, .Wait(), GetAwaiter().GetResult()) instead of awaiting it.',
  'state machine':
    'How the compiler implements an async method: an object holding its locals that is resumed when the awaited work completes.',
  'Large Object Heap':
    'Where objects of 85,000 bytes or more live. It is collected only in full (Gen2) collections and is not compacted by default.',
  LOH: 'Large Object Heap: where objects of 85,000 bytes or more live. Collected only in full GCs and not compacted by default.',
  Gen2: 'The oldest generation of the managed heap. Objects that survive two collections end up here and are collected least often.',
  finalizer:
    'A ~Type() method the runtime calls on its single finalizer thread before freeing the object. Usually used to release native resources.',
  SafeHandle: 'A .NET wrapper for a native handle that releases it reliably without you writing a finalizer.',
  'GC handle':
    'An explicit reference the runtime keeps to an object (for timers, pinned buffers, interop). The object stays alive until the handle is freed.',
  pinned: 'Fixed in memory so native code can use its address; the GC cannot move it, which can fragment the heap.',
  DAC: "The runtime's data-access library (mscordaccore / mscordacwks). Reading a dump needs the one that exactly matches the dump's runtime version.",
  HttpClient: ".NET's HTTP client. Meant to be created once and reused: each instance has its own connection pool.",
};

const terms = Object.keys(glossary).sort((a, b) => b.length - a.length);
const pattern = new RegExp(`\\b(${terms.map((t) => t.replace(/[.*+?^${}()|[\]\\-]/g, '\\$&')).join('|')})\\b`, 'gi');

export interface TextPart {
  text: string;
  term?: string;
}

/** Splits plain text, marking the first occurrence of each glossary term (by its canonical key). */
export function markTerms(text: string, seen: Set<string>): TextPart[] {
  const parts: TextPart[] = [];
  let last = 0;
  for (const m of text.matchAll(pattern)) {
    const key = terms.find((t) => t.toLowerCase() === m[0].toLowerCase())!;
    if (seen.has(key)) continue;
    seen.add(key);
    if (m.index! > last) parts.push({ text: text.slice(last, m.index) });
    parts.push({ text: m[0], term: key });
    last = m.index! + m[0].length;
  }
  if (last < text.length) parts.push({ text: text.slice(last) });
  return parts;
}
