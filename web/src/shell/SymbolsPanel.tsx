import { useEffect, useRef, useState } from 'react';
import type { SymbolModule, SymbolSettings, SymbolState } from '../contracts.gen';
import { rpc } from '../rpc/client';
import { queryClient } from '../state/queries';
import { useApp } from '../state/store';

/** What the status bar button says: how many of the app's modules have line numbers. */
export const symbolsLabel = (modules: SymbolModule[] | null) => {
  if (!modules || modules.length === 0) return 'Symbols';
  const loaded = modules.filter((m) => m.state === 'loaded').length;
  return `Symbols: ${loaded}/${modules.length}`;
};

export const stateLabel: Record<SymbolState, string> = {
  loaded: 'Loaded',
  notFound: 'Not found',
  mismatched: 'Wrong build',
  unsupported: 'Unsupported',
};

/** One line above the module list: how many of the app's modules are missing a usable PDB. */
export const modulesSummary = (total: number, missing: number) => {
  const noun = total === 1 ? 'application module' : 'application modules';
  if (missing === 0) return total === 1 ? `The ${noun} has symbols.` : `All ${total} ${noun} have symbols.`;
  return total === 1
    ? `The ${noun} has no usable PDB.`
    : `${missing} of ${total} ${noun} ${missing === 1 ? 'has' : 'have'} no usable PDB.`;
};

/** Adds a folder to a list unless it's blank or already there (paths compare case-insensitively on Windows). */
export const addPath = (list: string[], path: string) => {
  const clean = path.trim().replace(/^"|"$/g, '');
  return !clean || list.some((p) => p.toLowerCase() === clean.toLowerCase()) ? list : [...list, clean];
};

/** The status bar button and the panel it opens: where to find the app's PDBs and source files. */
export function SymbolsButton() {
  const dump = useApp((s) => s.dump);
  const [modules, setModules] = useState<SymbolModule[] | null>(null);
  const [open, setOpen] = useState(false);
  const path = dump?.metadata?.dumpPath;
  const analyzable = !!dump && !dump.analysisError;

  useEffect(() => {
    setModules(null);
    if (!rpc.connected || !path || !analyzable) return;
    let stale = false;
    void rpc
      .call('symbols.modules', {})
      .then((m) => !stale && setModules(m))
      .catch(() => {});
    return () => {
      stale = true;
    };
  }, [path, analyzable]);

  return (
    <>
      <button
        className="btn ghost"
        onClick={() => setOpen(true)}
        aria-haspopup="dialog"
        title="Where to find your PDBs and source files, for file and line numbers on stack frames"
      >
        {symbolsLabel(modules)}
      </button>
      {open && (
        <SymbolsPanel modules={analyzable ? modules : null} onModules={setModules} onClose={() => setOpen(false)} />
      )}
    </>
  );
}

function SymbolsPanel({
  modules,
  onModules,
  onClose,
}: {
  modules: SymbolModule[] | null;
  onModules: (m: SymbolModule[] | null) => void;
  onClose: () => void;
}) {
  const hasDump = useApp((s) => !!s.dump && !s.dump.analysisError);
  const [settings, setSettings] = useState<SymbolSettings | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const dialog = useRef<HTMLDivElement>(null);

  useEffect(() => {
    dialog.current?.focus();
    void rpc
      .call('symbols.get', {})
      .then(setSettings)
      .catch((e) => setFailure(e instanceof Error ? e.message : String(e)));
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  /** Saves, lets the worker look the PDBs up again, and refreshes everything that shows a frame. */
  const save = async (next: SymbolSettings) => {
    setBusy(true);
    setFailure(null);
    try {
      setSettings(await rpc.call('symbols.set', next));
      if (hasDump) {
        onModules(await rpc.call('symbols.modules', {}));
        await queryClient.invalidateQueries({ queryKey: ['threads.get'] });
        await queryClient.invalidateQueries({ queryKey: ['source.snippet'] });
      }
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const missing = modules?.filter((m) => m.state !== 'loaded').length ?? 0;

  return (
    <div className="palette-scrim" onClick={onClose}>
      <div
        ref={dialog}
        className="palette on mcp sym"
        role="dialog"
        aria-modal="true"
        aria-labelledby="sym-title"
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="pane-h">
          <h3 id="sym-title">Symbols &amp; source</h3>
          <div className="r">
            <button className="btn ghost" onClick={onClose} aria-label="Close symbol settings">
              ✕
            </button>
          </div>
        </div>

        <div className="mcp-body">
          <p className="mcp-lede">
            Stack frames in your own code show a file and line when the module’s portable PDB can be found. PDBs next to
            the module, or at the path the build recorded, are found on their own; add folders here for anything else.
            Nothing is downloaded.
          </p>
          {failure && (
            <p className="mcp-state bad" role="alert" style={{ marginLeft: 0 }}>
              {failure}
            </p>
          )}

          {settings && (
            <>
              <FolderList
                id="sym-pdb"
                title="Symbol folders"
                hint="Build output, a symbol store, or a folder of PDBs. Subfolders are searched too."
                paths={settings.symbolPaths}
                busy={busy}
                onChange={(symbolPaths) => void save({ ...settings, symbolPaths })}
              />
              <FolderList
                id="sym-src"
                title="Source folders"
                hint="A checkout of the code. Used to show the source lines when the build’s own paths don’t exist here."
                paths={settings.sourcePaths}
                busy={busy}
                onChange={(sourcePaths) => void save({ ...settings, sourcePaths })}
              />
            </>
          )}

          <h4>Modules in this dump</h4>
          {!hasDump ? (
            <p className="mcp-hint">Open a dump to see which of its modules have symbols.</p>
          ) : !modules ? (
            <p className="mcp-hint" role="status">
              Looking for PDBs…
            </p>
          ) : modules.length === 0 ? (
            <p className="mcp-hint">This dump has no application modules of its own.</p>
          ) : (
            <>
              <p className="mcp-hint" role="status" aria-live="polite">
                {modulesSummary(modules.length, missing)}
              </p>
              <ul className="sym-mods">
                {modules.map((m) => (
                  <li key={m.modulePath ?? m.module}>
                    <span className="mono" title={m.modulePath ?? undefined}>
                      {m.module}
                    </span>
                    <span className={`sym-state ${m.state === 'loaded' ? 'good' : 'bad'}`}>{stateLabel[m.state]}</span>
                    <span className="mcp-hint sym-detail" title={m.pdbPath ?? undefined}>
                      {m.state === 'loaded' ? (m.pdbPath ?? m.detail) : m.detail}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function FolderList({
  id,
  title,
  hint,
  paths,
  busy,
  onChange,
}: {
  id: string;
  title: string;
  hint: string;
  paths: string[];
  busy: boolean;
  onChange: (paths: string[]) => void;
}) {
  const [typed, setTyped] = useState('');
  const add = (path: string) => {
    const next = addPath(paths, path);
    if (next !== paths) onChange(next);
    setTyped('');
  };
  const browse = async () => {
    const picked = await rpc.call('symbols.pickFolder', {});
    if (picked.path) add(picked.path);
  };

  return (
    <>
      <h4 id={`${id}-h`}>{title}</h4>
      <p className="mcp-hint">{hint}</p>
      {paths.length > 0 && (
        <ul className="sym-paths" aria-labelledby={`${id}-h`}>
          {paths.map((p) => (
            <li key={p}>
              <span className="mono">{p}</span>
              <button
                className="btn ghost"
                disabled={busy}
                onClick={() => onChange(paths.filter((x) => x !== p))}
                aria-label={`Remove ${p}`}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      <div className="mcp-row">
        <button className="btn" disabled={busy} onClick={() => void browse()}>
          Add folder…
        </button>
        <input
          className="sym-input"
          value={typed}
          placeholder="or paste a path"
          aria-label={`${title}: path to add`}
          onChange={(e) => setTyped(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && typed.trim()) add(typed);
          }}
        />
        <button className="btn" disabled={busy || !typed.trim()} onClick={() => add(typed)}>
          Add
        </button>
      </div>
    </>
  );
}
