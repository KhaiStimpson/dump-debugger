import type { EntityRef } from '../contracts.gen';
import { rpc } from '../rpc/client';
import { useApp } from './store';

/** Pins and notes, persisted by the host in the dump's workspace sidecar. */
export function usePins() {
  const pins = useApp((s) => s.pins);
  const setPins = useApp((s) => s.setPins);
  return {
    pins,
    isPinned: (uri: string) => pins.some((p) => p.uri === uri),
    noteFor: (uri: string) => pins.find((p) => p.uri === uri)?.note ?? '',
    async pin(entity: EntityRef, note?: string) {
      setPins(
        await rpc.call('pins.upsert', { uri: entity.uri, kind: entity.kind, label: entity.label, note: note ?? null }),
      );
    },
    async setNote(entity: EntityRef, note: string) {
      setPins(await rpc.call('pins.upsert', { uri: entity.uri, kind: entity.kind, label: entity.label, note }));
    },
    async unpin(uri: string) {
      setPins(await rpc.call('pins.remove', { uri }));
    },
  };
}
