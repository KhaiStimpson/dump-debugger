import { describe, expect, it, vi } from 'vitest';
import type { SymbolModule } from '../contracts.gen';
import { addPath, modulesSummary, symbolsLabel } from './SymbolsPanel';

vi.mock('../state/store', () => ({ useApp: () => null }));
vi.mock('../state/queries', () => ({ queryClient: { invalidateQueries: vi.fn() } }));

const module = (state: SymbolModule['state']): SymbolModule => ({
  module: 'Orders.dll',
  modulePath: 'C:/app/Orders.dll',
  state,
  pdbPath: null,
  detail: '',
});

describe('Symbols status bar label', () => {
  it('stays plain until a dump with application modules is open', () => {
    expect(symbolsLabel(null)).toBe('Symbols');
    expect(symbolsLabel([])).toBe('Symbols');
  });

  it('counts the modules that have line numbers', () => {
    expect(symbolsLabel([module('loaded'), module('notFound'), module('mismatched')])).toBe('Symbols: 1/3');
  });
});

describe('Module summary', () => {
  it('reads naturally for one module and for several', () => {
    expect(modulesSummary(1, 0)).toBe('The application module has symbols.');
    expect(modulesSummary(1, 1)).toBe('The application module has no usable PDB.');
    expect(modulesSummary(4, 0)).toBe('All 4 application modules have symbols.');
    expect(modulesSummary(4, 1)).toBe('1 of 4 application modules has no usable PDB.');
    expect(modulesSummary(4, 3)).toBe('3 of 4 application modules have no usable PDB.');
  });
});

describe('Adding a symbol folder', () => {
  it('trims the path and the quotes Explorer puts around a copied one', () => {
    expect(addPath([], '  "C:\\src\\app\\bin"  ')).toEqual(['C:\\src\\app\\bin']);
  });

  it('ignores blanks and folders already listed, whatever their case', () => {
    const list = ['C:\\Symbols'];
    expect(addPath(list, '   ')).toBe(list);
    expect(addPath(list, 'c:\\symbols')).toBe(list);
  });
});
