using System.Collections.Concurrent;
using System.Text.Json;

namespace DumpDebugger.Core.Rpc;

/// <summary>Context handed to a request handler: the request's id, its cancellation, and a way
/// to push notifications (progress, streamed findings) while it runs.</summary>
public sealed class RpcCallContext(string id, CancellationToken cancellation, RpcConnection connection)
{
    public string Id { get; } = id;

    public CancellationToken Cancellation { get; } = cancellation;

    public Task NotifyAsync<T>(string method, T payload) => connection.NotifyAsync(method, payload);
}

public delegate Task<JsonElement?> RpcRequestHandler(string method, JsonElement? @params, RpcCallContext context);

/// <summary>
/// Symmetric JSON-RPC endpoint over a length-prefixed stream (see <see cref="RpcFrame"/>). Both
/// sides can call, notify and handle. Incoming requests run concurrently, each with its own
/// cancellation source, so a <c>$/cancel</c> notification aborts one request without touching
/// the others. Writes are serialized because notifications and responses interleave.
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    public const string CancelMethod = "$/cancel";

    private readonly Stream _stream;
    private readonly RpcRequestHandler? _handler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextId;
    private Task? _readLoop;

    public RpcConnection(Stream stream, RpcRequestHandler? handler = null)
    {
        _stream = stream;
        _handler = handler;
    }

    public event Action<string, JsonElement?>? NotificationReceived;

    public event Action<Exception?>? Closed;

    public Task Completion => _readLoop ?? Task.CompletedTask;

    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<JsonElement?> CallRawAsync(string method, JsonElement? @params, CancellationToken ct = default)
    {
        var id = $"c{Interlocked.Increment(ref _nextId)}";
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
            {
                removed.TrySetCanceled(ct);
                _ = NotifyAsync(CancelMethod, new CancelParams(id));
            }
        });

        await SendAsync(RpcMessage.Request(id, method, @params)).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<TResult?> CallAsync<TResult>(string method, object? @params = null, CancellationToken ct = default)
    {
        var element = @params is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(@params, @params.GetType(), ContractJson.Options);
        var result = await CallRawAsync(method, element, ct).ConfigureAwait(false);
        return ContractJson.FromElement<TResult>(result);
    }

    public Task NotifyAsync<T>(string method, T payload) =>
        SendAsync(RpcMessage.Notification(method, ContractJson.ToElement(payload)));

    public async Task SendAsync(RpcMessage message)
    {
        var frame = RpcFrame.Encode(message);
        await _writeLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await RpcFrame.ReadAsync(_stream, _lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException("RPC connection closed.", failure));
            }

            _pending.Clear();
            Closed?.Invoke(failure);
        }
    }

    private void Dispatch(RpcMessage message)
    {
        if (message.IsResponse)
        {
            if (_pending.TryRemove(message.Id!, out var tcs))
            {
                if (message.Error is not null)
                {
                    tcs.TrySetException(new RpcException(message.Error));
                }
                else
                {
                    tcs.TrySetResult(message.Result);
                }
            }

            return;
        }

        if (message.IsNotification)
        {
            if (message.Method == CancelMethod)
            {
                var cancel = ContractJson.FromElement<CancelParams>(message.Params);
                if (cancel is not null && _inflight.TryGetValue(cancel.Id, out var cts))
                {
                    cts.Cancel();
                }

                return;
            }

            NotificationReceived?.Invoke(message.Method!, message.Params);
            return;
        }

        if (message.IsRequest)
        {
            _ = Task.Run(() => HandleRequestAsync(message));
        }
    }

    private async Task HandleRequestAsync(RpcMessage request)
    {
        var id = request.Id!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inflight[id] = cts;
        RpcMessage response;
        try
        {
            if (_handler is null)
            {
                throw new RpcException(new RpcError(RpcError.MethodNotFound, $"No handler for '{request.Method}'."));
            }

            var result = await _handler(request.Method!, request.Params, new RpcCallContext(id, cts.Token, this)).ConfigureAwait(false);
            response = RpcMessage.Success(id, result ?? NullElement);
        }
        catch (RpcException ex)
        {
            response = RpcMessage.Failure(id, ex.Error);
        }
        catch (OperationCanceledException)
        {
            response = RpcMessage.Failure(id, new RpcError(RpcError.Cancelled, "Request cancelled."));
        }
        catch (Exception ex)
        {
            response = RpcMessage.Failure(id, new RpcError(RpcError.InternalError, ex.Message, ex.ToString()));
        }
        finally
        {
            _inflight.TryRemove(id, out _);
        }

        try
        {
            await SendAsync(response).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    public static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}

public sealed record CancelParams(string Id);
