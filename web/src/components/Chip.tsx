import type { MouseEvent, ReactNode } from 'react';
import type { EntityRef } from '../contracts.gen';
import { chipClass } from '../entities';
import { useApp } from '../state/store';
import { useContextMenu } from './ContextMenu';

interface ChipProps {
  entity: EntityRef;
  /** Extra text after the label, e.g. the request a thread is serving. */
  suffix?: ReactNode;
}

/**
 * The one link style for every entity (P2: "everything is an entity, every entity is a
 * link"). Click opens the inspector; right-click offers Pin, Copy and Show in graph.
 */
export function Chip({ entity, suffix }: ChipProps) {
  const inspect = useApp((s) => s.inspect);
  const menu = useContextMenu();
  return (
    <button
      type="button"
      className={`chip ${chipClass[entity.kind]}`}
      title={entity.detail ?? entity.label}
      onClick={(e: MouseEvent) => {
        e.stopPropagation();
        inspect(entity.uri);
      }}
      onContextMenu={(e) => menu.open(e, entity)}
    >
      {entity.kind === 'thread' && entity.label.startsWith('#') ? `thread ${entity.label}` : entity.label}
      {suffix && <span className="sfx">{suffix}</span>}
    </button>
  );
}
