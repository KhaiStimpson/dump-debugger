import { Fragment } from 'react';
import type { EntityRef } from '../contracts.gen';
import { refFor } from '../entities';
import { Chip } from './Chip';

const token = /(`[^`]+`)|(\{\{[^}]+\}\})/g;

/**
 * Renders authored finding text: `code` becomes <code>, {{uri}} becomes the entity's chip
 * (label from the finding's evidence). Everything else is plain text, so nothing a dump
 * contains can inject markup.
 */
export function Rich({ text, entities }: { text: string; entities?: readonly EntityRef[] }) {
  const parts: React.ReactNode[] = [];
  let last = 0;
  let i = 0;
  for (const m of text.matchAll(token)) {
    if (m.index! > last) parts.push(text.slice(last, m.index));
    if (m[1]) parts.push(<code key={i++}>{m[1].slice(1, -1)}</code>);
    else parts.push(<Chip key={i++} entity={refFor(m[2].slice(2, -2), entities)} />);
    last = m.index! + m[0].length;
  }
  if (last < text.length) parts.push(text.slice(last));
  return <Fragment>{parts}</Fragment>;
}

/** The same text flattened for clipboard/Markdown: code keeps backticks, chips become labels. */
export function plain(text: string, entities?: readonly EntityRef[]): string {
  return text.replace(/\{\{([^}]+)\}\}/g, (_, u: string) => refFor(u, entities).label);
}
