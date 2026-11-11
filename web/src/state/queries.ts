import { QueryClient, useQuery } from '@tanstack/react-query';
import type { RpcMethod, RpcMethods } from '../contracts.gen';
import { rpc } from '../rpc/client';

export const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: Infinity, retry: false, refetchOnWindowFocus: false } },
});

/**
 * A worker/host query cached by method + params. Results are per open dump; the store clears
 * the cache whenever a dump is opened or closed. Each query gets an AbortSignal, so leaving a
 * view cancels its in-flight worker request.
 */
export function useRpc<M extends RpcMethod>(method: M, params: RpcMethods[M]['params'], enabled = true) {
  return useQuery({
    queryKey: [method, params],
    queryFn: ({ signal }) => rpc.call(method, params, { signal }),
    enabled,
  });
}
