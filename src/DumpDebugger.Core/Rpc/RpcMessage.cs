using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDebugger.Core.Rpc;

/// <summary>
/// One JSON-RPC-shaped message. The same shape travels on both hops: web ⇄ host over the
/// WebView2 bridge, and host ⇄ worker over the named pipe. A message with <see cref="Method"/>
/// and <see cref="Id"/> is a request; with only <see cref="Method"/> it is a notification;
/// with <see cref="Id"/> and no method it is a response (<see cref="Result"/> or <see cref="Error"/>).
/// Params and results stay as raw <see cref="JsonElement"/> so the host can forward worker
/// calls without re-serializing them.
/// </summary>
public sealed class RpcMessage
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RpcError? Error { get; set; }

    [JsonIgnore]
    public bool IsRequest => Method is not null && Id is not null;

    [JsonIgnore]
    public bool IsNotification => Method is not null && Id is null;

    [JsonIgnore]
    public bool IsResponse => Method is null && Id is not null;

    public static RpcMessage Request(string id, string method, JsonElement? @params) =>
        new() { Id = id, Method = method, Params = @params };

    public static RpcMessage Notification(string method, JsonElement? @params) =>
        new() { Method = method, Params = @params };

    public static RpcMessage Success(string id, JsonElement result) =>
        new() { Id = id, Result = result };

    public static RpcMessage Failure(string id, RpcError error) =>
        new() { Id = id, Error = error };
}

public sealed record RpcError(int Code, string Message, string? Data = null)
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int Cancelled = -32800;
    public const int NoDumpOpen = -32001;
    public const int WorkerUnavailable = -32002;
}

public sealed class RpcException(RpcError error) : Exception(error.Message)
{
    public RpcError Error { get; } = error;
}
