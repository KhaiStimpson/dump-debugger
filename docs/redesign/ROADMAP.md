# Dump Debugger — Redesign Roadmap

Companion to [`design.html`](design.html) (vision, IA, design system, interactive mockups).
Status: **in progress** (branch `redesign`). Date: 2026-11-11.

## Decisions

| Topic | Decision |
|---|---|
| Audience | On-call developers who are not dump experts |
| Scope | Everything on the table: contracts, analyzers, UI stack |
| UI stack | React + TypeScript (Vite) inside a WinUI 3 + WebView2 host |
| Landing | Diagnosis page (verdict, vitals, ranked findings) + "What were you seeing?" symptom chips |
| New capabilities | Object explorer with GC-root paths, dump compare, analyzer pack |
| AI | Dropped. Remove `DumpDebugger.Llm` and the Explain button |
| Hand-off | Pins & notes in the workspace sidecar, Markdown/HTML incident report |
| Look | Dark pro-tool, teal accent kept, color reserved for meaning |

## Target architecture

```
web/ (React+TS)  ──JSON-RPC/postMessage──  DumpDebugger.Host (WinUI3+WebView2)
                                             ──named pipe──  Worker x86/x64 (kept)
                                                               └─ Analysis + Core (extended)
```

Kept: `Core`, `Analysis`, `Worker`, `DumpGen`. Replaced: `App` → `Host` + `web/`. Removed: `Llm`.

## Phases

Each phase ends shippable.

### R0 — Foundation
- [x] `DumpDebugger.Host`: WinUI 3 window hosting WebView2, serving bundled `web/dist` via virtual host mapping
- [x] `web/`: Vite + React + TS, `tokens.css` from the design system, ESLint/Prettier
- [x] JSON-RPC bridge (request/response + server-push notifications) between web and host
- [x] TS types generated from Core contracts (e.g. via a source generator or `NJsonSchema`)
- [x] Delete `DumpDebugger.App` and `DumpDebugger.Llm`, and update the solution and README
- **Done when:** the host opens the fixture dump and the web app lists its threads through the bridge.

### R1 — Engine contract v2
- [x] `Finding` v2: `plainTitle`, `whatItMeans`, `nextSteps[]`, `symptoms[]`, typed `EntityRef[]` evidence, confidence
- [x] Entity URIs: `thread/{id}`, `lock/{addr}`, `object/{addr}`, `type/{name}`, `request/{route}`, `finding/{id}`
- [x] Worker protocol v2: paged queries, streamed findings, named progress stages, cancellation per request
- [x] First-application-frame classifier (skip runtime helper frames and framework namespaces)
- [x] Verdict synthesizer: pick and phrase the headline from the top findings (templated, not free-form)
- **Done when:** the deadlock fixture produces an English verdict naming methods, not `.InlinedCallFrame`.

### R2 — Shell, Home, Diagnosis
- [x] Left rail, titlebar with dump meta, status bar (worker, DAC, timings)
- [x] Shared inspector panel with back/forward history
- [x] `Ctrl+K` command palette (requests, threads, types, objects, commands)
- [x] Home: drag-drop, recents with last verdict badge, sample generator, compare entry
- [x] Analyzing screen with stages and "see early findings"
- [x] Diagnosis: verdict, vitals strip, symptom chips (re-rank), expandable findings, mini wait-for graph, pins
- **Done when:** a verdict shows in under 5 s on the fixture, and every evidence chip opens the inspector.

### R3 — Threads & Locks
- [x] Threads grouped by app frame / request / flat, with state filters, virtualized
- [x] Thread inspector: folded framework frames, holds/waits, objects on stack
- [x] Locks: React Flow + ELK layout, cycle highlighting, explanation card, "queued behind" clusters
- **Done when:** a 200-thread dump scrolls at 60 fps, and graph ↔ thread ↔ finding links round-trip.

### R4 — Memory & Object explorer
- [x] Treemap plus type table (by type / generation / namespace)
- [x] Large-object identification v2 (owner-aware naming)
- [x] Worker: `InspectObject(addr, depth)`, `FindReferrers(addr)`, `FindRoots(addr, mode)` (BFS, depth cap, cancellable, lazy reverse-ref index)
- [x] Object view: field tree with navigable refs, "referenced by", shortest/all root paths
- **Done when:** any object is reachable in 3 clicks from a finding, and a root path on a 2 GB heap takes under 10 s.

### R5 — Analyzer pack
Each analyzer ships with authored explanations, a DumpGen scenario and a golden snapshot test.
- [x] Thread-pool starvation
- [x] Sync-over-async (`Task.Result` / `.Wait()`)
- [x] Async logical stacks (state-machine walking)
- [x] Exceptions on stacks + last thrown per thread
- [x] Finalizer queue backlog
- [x] LOH fragmentation
- [x] HttpClient / socket exhaustion
- [x] Timer leaks

### R6 — Pins, notes & Report
- [ ] Pin and annotate any entity URI, persisted in the workspace sidecar
- [ ] Report view: section toggles, live preview
- [ ] Export Markdown and self-contained HTML
- **Done when:** reopening a dump restores its pins, and the exported HTML renders standalone.

### R7 — Compare
- [ ] Heap snapshot persisted per workspace (type → count/size, sampled roots for top growers)
- [ ] Compare picker, type deltas, thread-group deltas, findings diff
- [ ] Leak-suspect heuristic with retention-path drill-down into B
- **Done when:** compare runs from cached snapshots, and the DumpGen `leak` scenario is flagged.

### R8 — Polish & ship
- [ ] Keyboard map + `?` overlay, glossary
- [ ] Accessibility pass (contrast, focus, names), reduced motion
- [ ] MSIX packaging with web assets, updated README and screenshots

## Risks

- **Bridge chattiness.** Never ship raw heaps over WebView2. The worker aggregates and paginates.
- **Root-path cost.** Use a depth cap, cancellation and a lazily built reverse-reference index.
- **Overclaiming in plain English.** Text is templated from evidence and always carries confidence.
- **Line numbers need PDBs.** Fall back to method + IL offset, and make the symbol path a setting.

## Implementation notes & deviations

- **R0.** Host-side logic lives in a new UI-free library, `DumpDebugger.Session` (worker lifecycle, bridge dispatcher, workspace), so the bridge is tested headlessly; `DumpDebugger.Host` is only the window. The same JSON-RPC message shape is used on both hops (web ⇄ host over `postMessage`, host ⇄ worker over the named pipe); worker methods are forwarded with params untouched. TS types come from a small reflection generator (`tools/DumpDebugger.TsGen`) over Core's `RpcCatalog` rather than NJsonSchema: it also emits the typed method/notification maps, which a schema generator wouldn't. The host is unpackaged + self-contained by default; MSIX is opt-in (`-p:Packaged=true`). Vite 6 rather than 7 because Vite 7 needs Node ≥ 20.19. Screenshots are taken from the real app through WebView2's CDP port (`web/scripts/shot.mjs`).
- **R1.** Wait-for edges turned out to be readable exactly, so deadlocks are now reported at *high* confidence (v1 said medium): a blocked thread's `Monitor.Enter` frame keeps the wanted lock object as a stack root, and sync-block owners give the held side. Sync blocks with "waiters" but no owner are `Monitor.Wait` condition variables and are ignored. Locks are named after the field that holds them (`OrderLedger._gate`) from objects on the involved stacks, static fields, or, as a fallback, a referrer scan during the heap walk. Requests come from Kestrel's `Http1Connection` (or `DefaultHttpContext`/`HttpResponse` leading to it) among stack roots. Source lines are read from portable PDBs next to the module when present. Instead of streaming findings one at a time, the worker pushes a whole `Diagnosis` snapshot (`analysis.updated`) after each stage: it's small, and the UI just replaces state. The DumpGen `deadlock` scenario is now a real Kestrel app whose checkout and inventory endpoints deadlock under HTTP load. Paging lands with the first unbounded list (heap types, R4); thread lists are aggregated into groups in the worker.
- **R2.** The inspector docks as a column on Diagnosis (replacing the side panel while open) and slides over the right edge elsewhere. Pins & notes storage landed here rather than R6 because the Diagnosis page shows them; R6 adds the report. Drag-drop uses WebView2's `postMessageWithAdditionalObjects`, so the host gets the dropped file's real path. "Generate a sample" runs the bundled DumpGen (`Tools\DumpGen` next to the host). The open-to-verdict time on the deadlock fixture is asserted in tests (< 5 s; ~0.5 s in practice). Types join the palette with the heap queries in R4.
- **R3.** React Flow + ELK (layered, top-down) worked as planned; "queued behind" edges are reversed for layout only, so clusters sit under their lock as in the mockup. The inspector now docks as a column in every view rather than sliding over (an overlay covered view toolbars). Measured on the DumpGen `syncoverasync` fixture (229 threads, Flat grouping, scrolled continuously in the real WebView2 through CDP): 60.4 fps with 44 of 229 rows in the DOM. Round-trips: graph node → inspector; inspector "Show in graph" → `#/locks/thread/{id}` with the node highlighted; explanation card "Back to finding" → Diagnosis with the finding in the inspector.
- **R4.** The reverse-reference index is CSR int arrays over an address-ordered object table that the heap walk fills (no second walk); the reference pass runs across cores when ClrMD reports the runtime thread-safe. It's built as the analysis stage "Large-object identification" (which also warms the root and static-field maps), so root paths are usually instant by the time someone asks. Measured on the DumpGen `bigheap` fixture (1.99 GB, 12.0M objects, 12.0M references, Release): heap walk 5.9 s, index 2.9 s, root path 0.04 s; cold (nothing built yet) under 10 s, asserted by an opt-in perf test (`DUMPDEBUGGER_PERF=1`). A naive version took 12 s to index and 2.6 s per root path: labeling an edge out of a 4M-element array enumerated every element, now replaced by a raw memory scan. Root labels prefer static fields (from a static-field map over loaded types) over the handles that store them. Large objects are named by content sniffing (PDF, PNG, JSON…) + an identifying field of the nearest app-type owner + the static/app type that holds it ("PDF document "Q3-statements" in ReportCache"). Heap-dominance findings now cite those objects. New DumpGen scenarios: `leak`, `leak-baseline`, `bigheap`.
- **R5.** Scenario ↔ analyzer: `threadpool` (starvation, with the queue length read from `ThreadPoolWorkQueue`'s ConcurrentQueue segments and the per-thread work-stealing queues), `syncoverasync` (sync-over-async + async logical stacks, which show the 200 blocked `.Result`s are waiting on a downstream stuck in `Task.Delay`), `exceptions` (in flight on threads + exception storm with its holder), `crash` (unhandled exception), `finalizer`, `loh`, `httpclient`, `timers`. Goldens snapshot the verdict and the scenario's own findings; numbers that depend on background-I/O timing are kept out (or normalized) so goldens stay stable across fresh captures (verified by regenerating every fixture twice). Deviation: dumps written by the runtime's own crash dumper (createdump, `DOTNET_DbgEnableMiniDump`) carry no minidump exception stream, so the crashing thread is identified as the one caught mid-dispatch (`System.Runtime.EH` frames on top) and the crash is reported at *medium* confidence; a dump with an exception stream (e.g. WER/procdump `-e`) gives high confidence. A lone socket count is no longer a finding (a busy server has plenty); many HttpClient instances is. Wait labels now name the primitive the app called (`SemaphoreSlim.Wait`, not the `Monitor.Wait` inside it).
