import { createContext, useContext, useEffect, useState, type MouseEvent, type ReactNode } from 'react';
import type { EntityRef } from '../contracts.gen';
import { navigate } from '../router';
import { usePins } from '../state/pins';
import { useApp } from '../state/store';

interface MenuState {
  x: number;
  y: number;
  entity: EntityRef;
}

const Ctx = createContext<{ open(e: MouseEvent, entity: EntityRef): void }>({ open: () => {} });

export const useContextMenu = () => useContext(Ctx);

/** Right-click on any chip: Open, Pin, Copy, Show in graph. */
export function ContextMenuProvider({ children }: { children: ReactNode }) {
  const [menu, setMenu] = useState<MenuState | null>(null);
  const inspect = useApp((s) => s.inspect);
  const pins = usePins();

  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    window.addEventListener('click', close);
    window.addEventListener('keydown', close);
    return () => {
      window.removeEventListener('click', close);
      window.removeEventListener('keydown', close);
    };
  }, [menu]);

  const open = (e: MouseEvent, entity: EntityRef) => {
    e.preventDefault();
    e.stopPropagation();
    setMenu({ x: e.clientX, y: e.clientY, entity });
  };

  const pinned = menu ? pins.isPinned(menu.entity.uri) : false;
  return (
    <Ctx.Provider value={{ open }}>
      {children}
      {menu && (
        <div className="ctxmenu" style={{ left: menu.x, top: menu.y }} role="menu">
          <button role="menuitem" onClick={() => inspect(menu.entity.uri)}>
            Open in inspector
          </button>
          <button role="menuitem" onClick={() => (pinned ? pins.unpin(menu.entity.uri) : pins.pin(menu.entity))}>
            {pinned ? 'Unpin' : 'Pin'} <span className="kbd">P</span>
          </button>
          <button role="menuitem" onClick={() => navigator.clipboard?.writeText(menu.entity.label)}>
            Copy name
          </button>
          {(menu.entity.kind === 'thread' || menu.entity.kind === 'lock') && (
            <button role="menuitem" onClick={() => navigate('locks')}>
              Show in graph
            </button>
          )}
        </div>
      )}
    </Ctx.Provider>
  );
}
