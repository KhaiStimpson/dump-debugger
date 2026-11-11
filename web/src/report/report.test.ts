import { describe, expect, it } from 'vitest';
import type { Diagnosis, Pin } from '../contracts.gen';
import { buildReport, defaultSections } from './report';

const diagnosis: Diagnosis = {
  schemaVersion: 2,
  dump: {
    path: 'C:\\dumps\\w3wp.dmp',
    fileName: 'w3wp.dmp',
    fileSizeBytes: 2_400_000_000,
    runtime: '.NET 8.0.11',
    architecture: 'x64',
    threadCount: 64,
    processName: 'Contoso.Orders.Api.exe',
    dacSource: 'local machine',
  },
  verdict: {
    severity: 'critical',
    confidence: 'high',
    headline: 'The app is hung: 2 requests are deadlocked.',
    body: '{{thread/47}} holds {{lock/0x10}} and wants {{lock/0x20}}.',
    entities: [
      { uri: 'thread/47', kind: 'thread', label: '#47', detail: 'POST /api/orders/checkout' },
      { uri: 'lock/0x10', kind: 'lock', label: 'OrderLedger._gate', detail: null },
      { uri: 'lock/0x20', kind: 'lock', label: 'InventoryCache._sync', detail: null },
    ],
    findingId: 'deadlock.cycle.1',
  },
  vitals: [{ key: 'threads', label: 'Threads', value: '64', detail: '33 blocked', fraction: 0.5, tone: 'critical' }],
  findings: [
    {
      id: 'deadlock.cycle.1',
      analyzer: 'deadlock',
      severity: 'critical',
      confidence: 'high',
      title: 'Deadlock between `CheckoutService.Commit` and `InventoryService.Reserve`',
      why: 'Lock-order inversion on 2 locks',
      whatItMeans: 'Two pieces of code take the same two locks in opposite order.',
      nextSteps: ['Restart the process.', 'Take {{lock/0x10}} before {{lock/0x20}}.'],
      symptoms: ['hang'],
      evidence: [
        { uri: 'thread/47', kind: 'thread', label: '#47', detail: 'POST /api/orders/checkout' },
        { uri: 'lock/0x10', kind: 'lock', label: 'OrderLedger._gate', detail: null },
      ],
      score: 110,
      visual: 'waitGraph',
    },
    {
      id: 'heap.x',
      analyzer: 'heap-dominance',
      severity: 'info',
      confidence: 'high',
      title: '`byte[]` is 38% of the heap',
      why: '912 MB',
      whatItMeans: 'Big.',
      nextSteps: ['Look.'],
      symptoms: ['highMemory'],
      evidence: [],
      score: 10,
      visual: 'heap',
    },
  ],
  busiestRequests: [],
  stages: [],
  seconds: 9.2,
  complete: true,
  fromCache: false,
  analyzedAt: '2026-11-11T04:31:00Z',
};

const pins: Pin[] = [
  {
    uri: 'thread/47',
    kind: 'thread',
    label: '#47',
    note: 'Line 118, <script>alert(1)</script> confirm with Sam.',
    createdAt: '',
    updatedAt: '',
  },
];

const at = new Date('2026-11-11T04:31:00Z');

describe('buildReport', () => {
  it('titles the report after the app and the top finding', () => {
    const r = buildReport({ diagnosis, pins, generatedAt: at }, defaultSections);
    expect(r.title).toBe('Contoso.Orders.Api: Deadlock between CheckoutService.Commit and InventoryService.Reserve');
    expect(r.fileName).toBe('incident-contoso-orders-api-2026-11-11');
  });

  it('renders entity chips as labels and keeps code spans in Markdown', () => {
    const { markdown } = buildReport({ diagnosis, pins, generatedAt: at }, defaultSections);
    expect(markdown).toContain('`#47` holds `OrderLedger._gate` and wants `InventoryCache._sync`');
    expect(markdown).toContain('2. Take `OrderLedger._gate` before `InventoryCache._sync`.');
    expect(markdown).toContain('> **Note on `#47`:**');
  });

  it('escapes dump-sourced text in HTML and is self-contained', () => {
    const { html } = buildReport({ diagnosis, pins, generatedAt: at }, defaultSections);
    expect(html).not.toContain('<script>');
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toMatch(/<link|src=|https?:\/\//);
    expect(html).toContain('<code>OrderLedger._gate</code>');
  });

  it('honours section toggles', () => {
    const none = buildReport(
      { diagnosis, pins, generatedAt: at },
      { verdict: false, findings: false, infoFindings: false, pins: false, threads: false, heap: false },
    );
    expect(none.markdown).not.toContain('## Summary');
    expect(none.markdown).not.toContain('## Pins');
    const withInfo = buildReport({ diagnosis, pins, generatedAt: at }, { ...defaultSections, infoFindings: true });
    expect(withInfo.markdown).toContain('is 38% of the heap');
    expect(buildReport({ diagnosis, pins, generatedAt: at }, defaultSections).markdown).not.toContain(
      '38% of the heap',
    );
  });
});
