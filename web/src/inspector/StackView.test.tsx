import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { SourceSnippet, StackFrame } from '../contracts.gen';
import { frameLocation, SourceLines } from './StackView';

const frame = (over: Partial<StackFrame>): StackFrame => ({
  display: 'OrderLedger.Post(Order)',
  fullName: 'Contoso.Orders.OrderLedger.Post',
  kind: 'app',
  module: 'Contoso.Orders',
  ilOffset: 0x1c,
  sourceFile: null,
  line: null,
  isWait: false,
  sourcePath: null,
  ...over,
});

const snippet: SourceSnippet = {
  path: '/_/src/Orders/OrderLedger.cs',
  resolvedPath: 'C:\\src\\shop\\src\\Orders\\OrderLedger.cs',
  origin: 'sourceFolder',
  line: 42,
  lines: [
    { number: 41, text: '    lock (_gate)' },
    { number: 42, text: '    {' },
    { number: 43, text: '' },
  ],
  modified: false,
  unavailable: null,
};

describe('Frame location', () => {
  it('shows file and line when the PDB was found', () => {
    expect(frameLocation(frame({ sourceFile: 'OrderLedger.cs', line: 42 }))).toBe('OrderLedger.cs:42');
  });

  it('falls back to the IL offset for app frames, and to nothing for framework frames', () => {
    expect(frameLocation(frame({}))).toBe('IL +0x1c');
    expect(frameLocation(frame({ kind: 'framework' }))).toBe('');
  });
});

describe('Source lines', () => {
  it('marks the frame’s line and says where the file came from', () => {
    const html = renderToStaticMarkup(<SourceLines snippet={snippet} />);
    expect(html).toContain('<span class="cur"><i>42</i>');
    expect(html).toContain('from a source folder');
    expect(html).not.toContain('changed since this build');
  });

  it('warns when the file no longer matches the build', () => {
    expect(renderToStaticMarkup(<SourceLines snippet={{ ...snippet, modified: true }} />)).toContain(
      'changed since this build',
    );
  });

  it('explains why there is no source instead of showing an empty box', () => {
    const html = renderToStaticMarkup(
      <SourceLines snippet={{ ...snippet, lines: [], origin: 'none', unavailable: 'Add the folder that holds it.' }} />,
    );
    expect(html).toContain('Add the folder that holds it.');
    expect(html).not.toContain('<pre>');
  });
});
