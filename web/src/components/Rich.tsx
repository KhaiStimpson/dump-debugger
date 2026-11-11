import { Fragment } from 'react';
import type { EntityRef } from '../contracts.gen';
import { refFor } from '../entities';
import { glossary, markTerms } from '../glossary';
import { useApp } from '../state/store';
import { Chip } from './Chip';

const token = /(`[^`]+`)|(\{\{[^}]+\}\})/g;

/**
 * Renders authored finding text: `code` becomes <code>, {{uri}} becomes the entity's chip
 * (label from the finding's evidence), and the first mention of a glossary term gets a
 * definition on hover. Everything else is plain text, so nothing a dump contains can inject
 * markup.
 */
export function Rich({
  text,
  entities,
  terms = true,
}: {
  text: string;
  entities?: readonly EntityRef[];
  terms?: boolean;
}) {
  const parts: React.ReactNode[] = [];
  const seen = new Set<string>();
  let last = 0;
  let i = 0;
  const plainText = (s: string) => {
    if (!terms) {
      parts.push(s);
      return;
    }
    for (const p of markTerms(s, seen)) {
      parts.push(p.term ? <Term key={i++} term={p.term} text={p.text} /> : p.text);
    }
  };

  for (const m of text.matchAll(token)) {
    if (m.index! > last) plainText(text.slice(last, m.index));
    if (m[1]) parts.push(<code key={i++}>{m[1].slice(1, -1)}</code>);
    else parts.push(<Chip key={i++} entity={refFor(m[2].slice(2, -2), entities)} />);
    last = m.index! + m[0].length;
  }
  if (last < text.length) plainText(text.slice(last));
  return <Fragment>{parts}</Fragment>;
}

/** A glossary term: dotted underline, definition on hover, click for the full glossary. */
export function Term({ term, text }: { term: string; text?: string }) {
  return (
    <span
      className="term"
      title={glossary[term]}
      role="button"
      tabIndex={0}
      aria-label={`${text ?? term}: ${glossary[term]}`}
      onClick={(e) => {
        e.stopPropagation();
        useApp.setState({ helpOpen: true, helpTab: 'glossary', glossaryFocus: term });
      }}
      onKeyDown={(e) =>
        e.key === 'Enter' && useApp.setState({ helpOpen: true, helpTab: 'glossary', glossaryFocus: term })
      }
    >
      {text ?? term}
    </span>
  );
}

/** The same text flattened for clipboard/Markdown: code keeps backticks, chips become labels. */
export function plain(text: string, entities?: readonly EntityRef[]): string {
  return text.replace(/\{\{([^}]+)\}\}/g, (_, u: string) => refFor(u, entities).label);
}
