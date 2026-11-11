# Dump Debugger — Redesign Roadmap

Companion to [`design.html`](design.html) (vision, IA, design system, interactive mockups).
Status: **draft, not started.** Date: 2026-11-11.

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
- [ ] `DumpDebugger.Host`: WinUI 3 window hosting WebView2, serving bundled `web/dist` via virtual host mapping
- [ ] `web/`: Vite + React + TS, `tokens.css` from the design system, ESLint/Prettier
- [ ] JSON-RPC bridge (request/response + server-push notifications) between web and host
- [ ] TS types generated from Core contracts (e.g. via a source generator or `NJsonSchema`)
- [ ] Delete `DumpDebugger.App` and `DumpDebugger.Llm`, and update the solution and README
- **Done when:** the host opens the fixture dump and the web app lists its threads through the bridge.

### R1 — Engine contract v2
- [ ] `Finding` v2: `plainTitle`, `whatItMeans`, `nextSteps[]`, `symptoms[]`, typed `EntityRef[]` evidence, confidence
- [ ] Entity URIs: `thread/{id}`, `lock/{addr}`, `object/{addr}`, `type/{name}`, `request/{route}`, `finding/{id}`
- [ ] Worker protocol v2: paged queries, streamed findings, named progress stages, cancellation per request
- [ ] First-application-frame classifier (skip runtime helper frames and framework namespaces)
- [ ] Verdict synthesizer: pick and phrase the headline from the top findings (templated, not free-form)
- **Done when:** the deadlock fixture produces an English verdict naming methods, not `.InlinedCallFrame`.

### R2 — Shell, Home, Diagnosis
- [ ] Left rail, titlebar with dump meta, status bar (worker, DAC, timings)
- [ ] Shared inspector panel with back/forward history
- [ ] `Ctrl+K` command palette (requests, threads, types, objects, commands)
- [ ] Home: drag-drop, recents with last verdict badge, sample generator, compare entry
- [ ] Analyzing screen with stages and "see early findings"
- [ ] Diagnosis: verdict, vitals strip, symptom chips (re-rank), expandable findings, mini wait-for graph, pins
- **Done when:** a verdict shows in under 5 s on the fixture, and every evidence chip opens the inspector.

### R3 — Threads & Locks
- [ ] Threads grouped by app frame / request / flat, with state filters, virtualized
- [ ] Thread inspector: folded framework frames, holds/waits, objects on stack
- [ ] Locks: React Flow + ELK layout, cycle highlighting, explanation card, "queued behind" clusters
- **Done when:** a 200-thread dump scrolls at 60 fps, and graph ↔ thread ↔ finding links round-trip.

### R4 — Memory & Object explorer
- [ ] Treemap plus type table (by type / generation / namespace)
- [ ] Large-object identification v2 (owner-aware naming)
- [ ] Worker: `InspectObject(addr, depth)`, `FindReferrers(addr)`, `FindRoots(addr, mode)` (BFS, depth cap, cancellable, lazy reverse-ref index)
- [ ] Object view: field tree with navigable refs, "referenced by", shortest/all root paths
- **Done when:** any object is reachable in 3 clicks from a finding, and a root path on a 2 GB heap takes under 10 s.

### R5 — Analyzer pack
Each analyzer ships with authored explanations, a DumpGen scenario and a golden snapshot test.
- [ ] Thread-pool starvation
- [ ] Sync-over-async (`Task.Result` / `.Wait()`)
- [ ] Async logical stacks (state-machine walking)
- [ ] Exceptions on stacks + last thrown per thread
- [ ] Finalizer queue backlog
- [ ] LOH fragmentation
- [ ] HttpClient / socket exhaustion
- [ ] Timer leaks

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
