# Dump Debugger — Design & Implementation Plan

A Windows desktop application (WinUI 3) that turns .NET memory dump analysis from a manual
WinDbg command-grind into a guided, cross-linked investigation that proposes conclusions.

**Status:** planning. No code written yet.
**Date:** 2026-08-12

---

## 1. Problem statement

Today the workflow is: open the dump in WinDbg, load SOS and mex, and manually run and
correlate a sequence of commands — `!threads`, `!clrstack`, `!syncblk`, `!dumpheap -stat`,
`!dumpheap -mt`, `!do`, `!gcroot` — holding the relationships between them in your head.
Four distinct pain points, all confirmed as in scope:

1. **Correlating threads to what they were doing.** What request was this thread serving?
   What is it blocked on? Which threads share a stack?
2. **Lock and deadlock analysis.** Who owns the sync block, who is waiting, is there a cycle?
3. **Finding what's eating memory.** Which types dominate, which instances are large, and
   *what are they* — identified by reading properties off the object.
4. **The navigation itself.** Re-typing the same commands, re-deriving the same context,
   no way to click from a thread to the objects it holds.

Dump sources: Azure App Service crash dumps and manually-captured hang dumps, across
**.NET Framework 4.8, .NET 6, and .NET 8+**.

---

## 2. Key architectural decisions

### 2.1 ClrMD, not WinDbg, as the analysis engine

The original idea was to drive WinDbg + SOS + mex under the hood. **Recommendation: don't.**

| | WinDbg + SOS/mex | ClrMD (`Microsoft.Diagnostics.Runtime`) |
|---|---|---|
| Interface | Shell out to `cdb.exe`, regex-parse text tables | In-process .NET API returning typed objects |
| Object fields | `!do <addr>` then parse, chain manually | `obj.ReadStringField("Path")` directly |
| Robustness | Output format changes break parsers | Compile-time typed API |
| Redistribution | ~700MB Debugging Tools for Windows, licensing questions | NuGet package, ships in the app |
| Speed | Process launch + text round-trip per command | Direct memory reads |

ClrMD is the library SOS itself is built on, so we lose no fidelity for managed analysis —
we gain the ability to read typed field values, which is exactly the "identify the object by
its properties" workflow that is most painful in WinDbg.

**Where WinDbg still wins:** unmanaged/native frames, kernel-mode detail, and exotic
scenarios. Keep an *optional* `dbgeng.dll` path as an escape hatch (§7), not the primary engine.

### 2.2 Out-of-process analyzer workers (this is forced, not a preference)

The preference stated was a single packaged self-contained app. That is not achievable as a
single process, for a hard technical reason:

> **ClrMD loads the target runtime's native DAC (`mscordacwks.dll` / `mscordaccore.dll`).
> Because the DAC is native, the analyzing process must match the dump's architecture.**
> An x86 dump can only be read by an x86 process.
> — [ClrMD Getting Started](https://github.com/microsoft/clrmd/blob/main/doc/GettingStarted.md)

Azure App Service .NET Framework 4.8 sites very commonly run **32-bit**, so x86 dumps are a
routine input, not an edge case. A single x64 WinUI 3 process would silently fail on a large
share of your real dumps.

**Therefore:** WinUI 3 shell (x64/ARM64) + two small analyzer worker executables (`x86`, `x64`)
that the shell launches based on the dump's detected architecture and talks to over IPC.

This costs some plumbing and buys three things beyond correctness:
- UI stays responsive while a 30GB dump indexes.
- A worker crash on a corrupt dump is recoverable, not fatal.
- Analysis can be cancelled by killing the worker.

Distribution is still **one app** from the user's point of view: the workers ship inside the
package. "No dependencies" is preserved — nothing to install.

### 2.3 The DAC problem and offline operation

The DAC (Data Access Component) is the native DLL that knows the exact in-memory layout of one
specific CLR build — `mscordacwks.dll` for .NET Framework, `mscordaccore.dll` for .NET Core+.
Without it a dump is undifferentiated bytes. It is **version-locked to the exact servicing
build** that produced the dump, not merely to the runtime major version.

**Decision: never bundle DACs with the app.** Resolve them at runtime, in this order:

1. **Embedded in the dump.** Full-memory dumps — which Azure crash dumps typically are —
   contain the loaded module images including the DAC. *Reliability across our dump types
   must be measured in Phase 0; treat as an optimisation, not a guarantee.*
2. **Local machine runtime.** For .NET Framework, `C:\Windows\Microsoft.NET\Framework[64]\v4.0.30319\`.
   Free to try, but only matches when the local patch level happens to equal the dump's —
   frequently false for Azure dumps.
3. **App-managed DAC cache.** Keyed by runtime version + architecture + index. Populated on
   first successful resolution and reused forever. This is what makes the app work offline
   for any runtime it has seen before.
4. **Microsoft symbol server.** One opt-in toggle. Downloads the DAC and populates the cache.

If all four fail, degrade gracefully: still show native threads, module list, and dump
metadata, with a clear banner explaining that managed analysis needs the DAC and offering the
symbol-server download.

**Why not bundle.** Two independent reasons, either sufficient on its own:

- *Licensing.* .NET 6/8+ is MIT and its runtime packs are on NuGet, so redistribution is
  likely fine — but .NET Framework 4.8 is closed-source, ships as a Windows OS component, and
  has no NuGet package, so redistributing `mscordacwks.dll` is almost certainly not permitted.
  4.8 is the runtime we need most (x86 Azure dumps), so bundling solves the easy half and
  misses the hard half.
- *Versioning.* Even with unrestricted rights, we cannot ship every servicing build of every
  runtime. Any bundle would be partial, so the cache and symbol-server tiers must exist anyway.

Fetching from Microsoft's symbol server at runtime is Microsoft delivering their own binary to
the end user — no redistribution by us, no licensing question. This is exactly what WinDbg,
`dotnet-dump`, and Visual Studio all do. The only cost is one network round trip the first
time an unseen runtime version is encountered.

### 2.4 Analysis is rules-first, LLM-optional

Per decision: deterministic analyzers produce a **structured findings document**; an optional
LLM layer turns that document into prose and speculates about root cause. The app is fully
functional with the LLM layer disabled, and never requires network access for its core value.

This ordering matters for a debugging tool. A deadlock cycle is a graph property — proving it
deterministically and showing the cycle is strictly better than asking a model to notice it.
The model's job is synthesis and narrative, not detection.

### 2.5 LLM access without an API key

You have a Claude subscription but no API key. The integration therefore targets, in order:

1. **Claude Code CLI in headless mode** (primary). If `claude` is on `PATH`, invoke:
   `claude -p "<prompt>" --output-format json`, feeding the findings JSON on stdin.
   This reuses your existing subscription login — no key to manage, no key stored by this app.
   The app detects the CLI at startup and only enables the narrative feature if present.
2. **`ANTHROPIC_API_KEY` environment variable** (alternate). If set, call the Messages API
   directly via the official `Anthropic` NuGet package. Never prompt for or store a key in
   app settings — read it from the environment only.
3. **Disabled** (default when neither is available). Every panel still works; the "Explain
   this" affordances are hidden rather than shown broken.

Model: `claude-opus-5` with adaptive thinking. The findings JSON is small (a few KB of
aggregates, never raw heap contents), so cost and latency are modest.

**Privacy is the reason this layer is opt-in and off by default.** Dumps contain connection
strings, tokens, PII, and customer data in memory. The app must:
- Send only the **structured findings document**, never raw memory or object contents.
- Show the exact payload in a review pane before the first send, with a "don't ask again" per-workspace.
- Redact by pattern (connection strings, JWTs, `Authorization` headers, email addresses) in
  any string that does make it into findings.

---

## 3. Product shape

### 3.1 Workspace concept

Opening a dump creates a **workspace** — a folder holding the analysis index, findings,
user annotations, and bookmarks, sidecar to the `.dmp`. Reopening is instant; investigations
survive restarts and can be zipped and shared with a colleague (minus the dump itself).

### 3.2 Screens

**Triage (landing).** What the app thinks is wrong, ranked. Each finding is a card:
severity, one-line claim, the evidence that supports it, and a button that jumps to the
panel showing that evidence. This is the answer to "just tell me what's going on."

**Threads.** Sortable, filterable grid of all threads. Columns: ID, OS ID, kind (worker /
IO / GC / finalizer / main), state, lock count, exception, top managed frame, wait reason,
and — the important one — **stack group**, so 200 threads with identical stacks collapse to
one row with a count of 200. Selecting a thread shows the full interleaved managed/native
stack, its locals, and the "operation context" (§4.1).

**Locks & Deadlocks.** A graph view of the wait-for relation: threads as nodes, "waiting on
object owned by" as edges. Cycles are highlighted in red and promoted to Triage. Also a flat
sync-block table for people who want the `!syncblk` view.

**Memory.** Three linked views:
- *Type statistics* — `!dumpheap -stat` equivalent, sortable by count and total size, with
  generation breakdown and delta-vs-baseline when two dumps are loaded.
- *Large objects* — everything on the LOH plus anything over a threshold, with the
  **object identity** column (§4.3).
- *Retention* — for a selected object, the shortest path from a GC root, rendered as a chain
  you can click through.

**Object Explorer.** A tree/inspector for any object address: fields with typed values,
expandable references, string previews, collection contents. This is `!do` that you can
actually navigate.

**Exceptions.** All exception objects on the heap and in thread contexts, grouped by type
and message, with inner-exception chains and the stack that threw.

**Report.** Export the whole investigation — findings, selected evidence, annotations — to
Markdown or HTML for a ticket or a post-mortem.

### 3.3 Cross-linking is the feature

Every address in the UI is a link. Click a thread's blocked-on object to open it in Object
Explorer. Click a type in the statistics view to list its instances. Click an instance to see
its retention path and the threads that reference it. This is the "just navigating and
repeating commands" pain point, and it's addressed by design rather than by a feature.

---

## 4. Analyzer catalogue

Each analyzer is a pure function from the dump index to zero or more findings. Each finding
carries: id, severity, title, one-paragraph explanation, structured evidence, and confidence.

### 4.1 Thread analyzers

| Analyzer | Detects | Approach |
|---|---|---|
| **Stack grouping** | N threads doing the same thing | Hash normalized managed frame lists; cluster |
| **Thread pool starvation** | Pool exhausted, work queued behind blocked threads | Count pool threads vs. blocked pool threads vs. queued work items; read `ThreadPool` internals per runtime |
| **Sync-over-async** | `.Result` / `.Wait()` / `GetAwaiter().GetResult()` on a pool thread | Frame pattern match against known blocking-on-task methods |
| **Blocked-thread clustering** | Many threads waiting on the same resource | Group by the object address they're waiting on |
| **Long-running thread** | A thread far into an operation | Requires operation start context; heuristic on stack depth + known entry points |
| **Finalizer blocked** | Finalizer thread not idle → finalization queue backing up | Check finalizer thread stack + queue length |
| **Exception on stack** | Thread is unwinding or faulted | Thread's current exception object |

**Operation context** — the highest-value piece. For each thread, walk the stack looking for
a frame whose `this` or arguments identify the unit of work, then read fields off it:

- ASP.NET Framework 4.8: `HttpContext` → `Request.RawUrl`, `Request.HttpMethod`, timestamps.
- ASP.NET Core: `HttpContext` / `DefaultHttpContext` → `Request.Path`, `Method`, `TraceIdentifier`.
- Generic fallback: the deepest application-code frame (namespace not in a framework prefix list).

This turns "thread 47 is blocked in `Monitor.Enter`" into "thread 47 is serving
`POST /api/orders/checkout` and is blocked in `Monitor.Enter`", which is the difference
between a data point and a lead.

### 4.2 Lock analyzers

| Analyzer | Detects | Approach |
|---|---|---|
| **Monitor deadlock** | Cycle in the wait-for graph | Build directed graph from sync-block owner/waiter pairs; find strongly connected components |
| **Lock convoy** | One owner, many waiters, no cycle | Fan-in count on a single sync block above a threshold |
| **ReaderWriterLock contention** | Writers starved / readers blocked | Read `ReaderWriterLockSlim` internal state fields |
| **Async deadlock** | Task blocked on a task completed by a blocked thread | Walk task continuation chains; cross-reference with blocked threads |
| **Cross-lock ordering** | Two threads acquiring the same two locks in opposite order | Reconstruct held-lock sets per thread from stacks |

Note that the classic ASP.NET Framework deadlock (sync-over-async on the request context)
needs both the thread analyzer and the lock analyzer to fire; the Triage layer correlates
them into one finding rather than reporting two.

### 4.3 Memory analyzers

| Analyzer | Detects | Approach |
|---|---|---|
| **Type dominance** | A few types account for most of the heap | Rank type statistics by total size |
| **Large object identification** | *What* the big object actually is | See below — this is the key one |
| **Duplicate strings** | Wasted memory from unshared identical strings | Hash string contents; report total waste |
| **LOH fragmentation** | Free space between LOH objects | Walk the LOH segment, measure free blocks |
| **Growing collections** | Oversized `List<T>`/`Dictionary<K,V>` backing arrays | Read `_size` vs `_items.Length` |
| **Event handler leak** | Delegate invocation lists with many targets | Walk `MulticastDelegate._invocationList` |
| **Static roots** | Large graphs held by statics | Enumerate static fields as roots, measure retained size |
| **Pinned objects** | Pinning causing fragmentation | Enumerate pinned handles |
| **Cache growth** | `MemoryCache` / `IMemoryCache` entry counts and sizes | Known-type field reads |

**Object identification** deserves detail, because it's the stated pain point. For a large
object, don't just report `byte[] — 84 MB`. Instead:

1. Read the object's own fields and render the ones that look identifying (any `string`
   field, anything named `Id`/`Name`/`Key`/`Path`/`Url`, timestamps).
2. Walk *up* to the object's referrers and identify those. A big `byte[]` is meaningless;
   a big `byte[]` held by a `MemoryStream` held by a `HttpResponseMessage` for
   `GET /api/reports/export` is a diagnosis.
3. Compute retained size, not just object size, so a small object holding a huge graph ranks
   correctly.
4. Apply a **type identity rule set** — a data-driven, user-extensible mapping from type name
   to "which fields to show" — so common framework types render usefully out of the box and
   you can add your own application types.

The type identity rule set should be a plain JSON/YAML file in the app that users can extend.
That makes the tool improve without code changes, which matters for domain-specific types.

### 4.4 Cross-cutting correlation

Triage runs a final pass over all findings to merge related ones into a narrative. E.g.
"thread pool starvation" + "200 threads blocked on the same lock" + "the lock owner is doing
synchronous I/O" become a single ranked finding with three pieces of evidence, not three
separate cards competing for attention.

---

## 5. Runtime coverage matrix

| Concern | .NET Framework 4.8 | .NET 6 | .NET 8+ |
|---|---|---|---|
| ClrMD support | Yes (`mscordacwks.dll`) | Yes (`mscordaccore.dll`) | Yes |
| Common architecture | **x86 and x64** | x64 | x64 / ARM64 |
| Thread pool internals | Legacy native pool — limited managed visibility | Managed `PortableThreadPool` | Managed `PortableThreadPool` |
| Async state machines | Present but harder to walk | Good | Good |
| `HttpContext` shape | `System.Web.HttpContext` | `DefaultHttpContext` | `DefaultHttpContext` |
| Server GC detection | Yes | Yes | Yes |

Runtime differences are isolated behind a `IRuntimeAdapter` abstraction — one implementation
per runtime family — so analyzers are written once against a normalized model. Where a signal
is unavailable on a runtime (e.g. detailed thread-pool queue depth on 4.8), the analyzer
reports reduced confidence rather than silently omitting the finding.

**Azure ingestion is explicitly deferred to v2** (not selected in scoping). v1 opens local
`.dmp` files. v2 can add Kudu/blob-storage fetch and App Service naming conventions.

---

## 6. Technical architecture

```
┌──────────────────────────────────────────────────────────┐
│  DumpDebugger.App          (WinUI 3, x64/ARM64, MSIX)    │
│  Views · ViewModels · Triage · Cross-link navigation      │
└───────────────┬──────────────────────────────────────────┘
                │  IPC (named pipe, length-prefixed JSON or MessagePack)
                │  request/response + progress + cancellation
┌───────────────▼──────────────────────────────────────────┐
│  DumpDebugger.Worker.x64  │  DumpDebugger.Worker.x86      │
│  ClrMD · DAC resolution · Index build · Analyzers         │
└──────────────────────────────────────────────────────────┘
                │
┌───────────────▼──────────────────────────────────────────┐
│  DumpDebugger.Core   (netstandard/net8.0, AnyCPU)         │
│  Domain model · Analyzer framework · Findings schema      │
│  Type identity rules · Redaction                          │
└──────────────────────────────────────────────────────────┘
```

**Projects**

| Project | Target | Purpose |
|---|---|---|
| `DumpDebugger.Core` | net10.0 | Domain model, analyzer contracts, findings schema, redaction, type-identity rules. No ClrMD dependency in the contracts. |
| `DumpDebugger.Analysis` | net10.0 | ClrMD-backed implementation: dump loading, indexing, runtime adapters, all analyzers. |
| `DumpDebugger.Worker` | net10.0, published x86 + x64 | Thin host: IPC server around `DumpDebugger.Analysis`. |
| `DumpDebugger.App` | net10.0-windows, WinUI 3 | UI shell, worker lifecycle, navigation. |
| `DumpDebugger.Llm` | net10.0 | CLI/API narrative provider behind an interface. |
| `DumpDebugger.Tests` | net10.0 | Unit + analyzer tests against fixture dumps. |
| `DumpDebugger.DumpGen` | net10.0 / net48 | Test-fixture generator (§9). |

**Indexing.** Large dumps must not be re-walked per query. On open, the worker builds a
persistent index into the workspace:
- Heap object table: address → (type id, size, generation, segment).
- Type table with aggregate statistics.
- Reference edges, stored compactly, sufficient for retention queries.
- Thread table with stacks and roots.

Use a memory-mapped columnar store or SQLite depending on measured performance. Target:
a 4GB dump indexes in under 60 seconds and answers subsequent queries in under a second.
Streaming progress reporting throughout; the UI is usable for thread analysis before the
heap index finishes.

**Findings schema.** Stable, versioned JSON. It is simultaneously the UI's data source, the
LLM's input, the report generator's input, and the test assertion target. Designing it well
is the highest-leverage early decision.

```jsonc
{
  "schemaVersion": 1,
  "dump": { "path": "...", "runtime": "net8.0", "arch": "x64", "capturedAt": "...", "isCrash": false },
  "findings": [
    {
      "id": "deadlock.monitor-cycle.1",
      "analyzer": "MonitorDeadlockAnalyzer",
      "severity": "critical",
      "confidence": "high",
      "title": "Deadlock between 2 threads on OrderCache and InventoryLock",
      "summary": "Thread 12 holds OrderCache and waits on InventoryLock; thread 31 holds InventoryLock and waits on OrderCache.",
      "evidence": [
        { "kind": "thread", "ref": "12", "detail": "blocked in Monitor.Enter" },
        { "kind": "object", "ref": "0x1f2a3b40", "detail": "InventoryLock, owned by thread 31" }
      ],
      "links": [ { "label": "View lock graph", "target": "locks#cycle-1" } ]
    }
  ]
}
```

---

## 7. The WinDbg escape hatch

Not the primary engine, but worth keeping for the cases ClrMD can't reach — deep native
stacks, kernel structures, or a command you already know by heart.

Design it as an **optional, detected** integration: if the Debugging Tools for Windows are
installed locally, expose a command console panel that runs `dbgeng` commands against the
same dump and renders raw output. Never required, never bundled, never on the critical path.
`mex` likewise stays user-supplied if wanted.

This keeps the "single application, no dependencies" promise intact while not throwing away
your existing muscle memory.

---

## 8. LLM layer detail

**Input:** the findings JSON plus a compact context block (runtime, thread counts, top types).
Never raw memory. Total payload budget: 32KB, truncating lowest-severity findings first.

**Output:** three things, requested in one structured call —
1. A narrative summary of what is likely happening, in plain prose.
2. A ranked list of hypotheses, each with the evidence that supports and contradicts it.
3. Suggested next investigative steps, phrased as things to look at *in this app*.

**Prompt discipline.** The system prompt states that findings are authoritative and the model
must not invent evidence, must attribute every claim to a finding id, and must say "the dump
does not show this" rather than speculating beyond the data. Hallucinated debugging advice is
worse than no advice.

**Provider interface** (`DumpDebugger.Llm`):

```csharp
public interface INarrativeProvider {
    bool IsAvailable { get; }
    string Description { get; }          // "Claude Code CLI (subscription)" | "Anthropic API key"
    Task<Narrative> SummarizeAsync(FindingsDocument findings, CancellationToken ct);
}
```

Implementations: `ClaudeCliNarrativeProvider` (primary, subscription), `AnthropicApiNarrativeProvider`
(if `ANTHROPIC_API_KEY` is set), `NullNarrativeProvider` (default). Detection at startup,
surfaced in Settings so it's obvious which is active and why.

Also worth offering: a **per-panel "explain this"** action (explain this thread's stack,
explain this object graph) rather than only a whole-dump summary. Smaller, faster, more
useful, and easier to keep grounded.

---

## 9. Testing strategy

Analyzer correctness is the whole product, so test fixtures matter more than usual.

**`DumpDebugger.DumpGen`** is a companion tool that deliberately produces pathological
processes and captures dumps of them:

| Fixture | Produces |
|---|---|
| `deadlock-monitor` | Two threads, two locks, opposite order |
| `deadlock-async` | Classic sync-over-async on a captured context |
| `starvation-threadpool` | Pool saturated by blocking calls |
| `leak-static-list` | Static `List<T>` growing without bound |
| `leak-event-handler` | Event with thousands of subscribers |
| `loh-fragmentation` | Alternating large allocations and frees |
| `duplicate-strings` | Millions of unshared identical strings |
| `crash-unhandled` | Unhandled exception crash dump |

Each fixture is built for **net48 x86, net48 x64, net6.0 x64, and net8.0 x64**, giving a
32-dump matrix that exercises every runtime adapter. Dumps are large, so they're generated
on demand by a script rather than committed; a small set of trimmed minidumps can be
committed for CI.

**Assertions** run analyzers over a fixture dump and assert on the findings JSON: the expected
finding id is present, at the expected severity, citing the expected threads. Because findings
are structured, these tests are precise rather than string-matching.

---

## 10. Delivery phases

Each phase ends with something usable, and each builds on the last.

### Phase 0 — Foundations (walking skeleton)
Solution structure, Core domain model, findings schema, worker IPC contract, WinUI shell
that can launch the correct-architecture worker, open a dump, and display dump metadata.

Also in this phase: implement the full four-tier DAC resolution chain (§2.3) and **measure
how often tier 1 succeeds** across a representative sample of your real Azure dumps for 4.8
x86, 4.8 x64, .NET 6, and .NET 8+. If DAC-from-dump proves reliable, offline operation is
effectively free; if it doesn't, the symbol-server tier carries more weight and its UX
deserves more attention. Either way this is cheap to answer now and expensive to discover in
Phase 3.

Deliverable: *the app opens a dump of any supported runtime and tells you what it is.*

### Phase 1 — Threads
Thread enumeration, managed + native stack walking, stack grouping, the threads grid, thread
detail view. Runtime adapters for 4.8 / 6 / 8+.
Deliverable: *replaces `!threads` and `!clrstack` with something better.*

### Phase 2 — Locks & deadlocks
Sync-block enumeration, wait-for graph construction, cycle detection, lock graph view,
the first real Triage findings.
Deliverable: *finds deadlocks automatically — the highest-value single feature.*

### Phase 3 — Memory
Heap indexing, type statistics, large object view, object explorer, retention paths,
type identity rules, the memory analyzers.
Deliverable: *replaces `!dumpheap`, `!do`, and `!gcroot`, with object identification.*

### Phase 4 — Triage & correlation
Cross-analyzer correlation, severity ranking, the Triage landing screen, report export.
Deliverable: *the app leads with conclusions rather than data.*

### Phase 5 — Operation context
`HttpContext` extraction for 4.8 and ASP.NET Core, request correlation on threads,
long-running-operation detection.
Deliverable: *"thread 47 is serving POST /api/orders/checkout" — the correlation payoff.*

### Phase 6 — LLM narrative
Provider detection, CLI integration, redaction and payload review, narrative and per-panel
explanations.
Deliverable: *plain-English root-cause hypotheses on top of hard evidence.*

### Phase 7 — Polish
Workspace persistence, bookmarks and annotations, dump comparison (two dumps, delta view),
performance work on very large dumps, WinDbg escape hatch.

Phases 1–3 are the core value; 4–5 are the differentiators; 6–7 are amplifiers. If time runs
short, shipping through Phase 4 already beats the current workflow substantially.

---

## 11. Open questions

1. **Index storage.** SQLite vs. a custom memory-mapped columnar format. Decide with a
   benchmark on a real ~8GB dump during Phase 3; don't guess.
2. **ARM64.** Are ARM64 dumps in scope? If so a third worker flavour is needed. Assumed
   **out of scope for v1** unless you say otherwise.
3. **Framework prefix list.** The "deepest application frame" heuristic needs a maintained
   list of framework namespaces to skip. Ship a default, make it user-editable.
4. **MSIX vs. unpackaged.** MSIX gives clean install/update; unpackaged self-contained is
   easier to xcopy to a locked-down machine. Both are buildable from the same project —
   decide based on where you actually run this.
5. **Dump comparison scope.** Two-dump delta is listed in Phase 7, but for memory-leak hunts
   it's arguably a Phase 3 feature. Revisit after Phase 3.

---

## 12. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| DAC unavailable for a dump | Cannot do managed analysis at all | Three-tier resolution (§2.3); graceful degradation with a clear explanation |
| Very large dumps (30GB+) exhaust memory | App unusable on the worst cases | Streaming indexing, memory-mapped access, never load the whole heap into managed memory |
| .NET Framework 4.8 signal gaps | Weaker findings on the runtime you use most | Explicit per-runtime confidence in findings; invest in the 4.8 adapter early rather than last |
| Analyzer false positives | Erodes trust in Triage, worse than no analysis | Confidence levels; always show the evidence; never hide the raw data behind a conclusion |
| LLM hallucination | Confidently wrong debugging advice | Findings-only input, attribution requirement, off by default, prose clearly marked as generated |
| Scope creep across 4 pain points | Nothing finished well | Phased delivery; each phase independently useful |

---

## References

- [microsoft/clrmd — Getting Started](https://github.com/microsoft/clrmd/blob/main/doc/GettingStarted.md) — DAC and architecture-matching requirements
- [Microsoft.Diagnostics.Runtime on NuGet](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime/)
- [.NET Crash Dump and Live Process Inspection — .NET Blog](https://devblogs.microsoft.com/dotnet/net-crash-dump-and-live-process-inspection/)
