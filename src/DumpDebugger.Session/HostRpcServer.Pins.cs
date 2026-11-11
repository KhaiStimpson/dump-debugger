using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

public sealed partial class HostRpcServer
{
    private const string PinsFile = "pins.json";

    /// <summary>Pins and notes live in the dump's workspace sidecar, so they come back when the dump is reopened and flow into the report.</summary>
    private IReadOnlyList<Pin> ListPins() => RequireWorkspace().Read<List<Pin>>(PinsFile) ?? [];

    private IReadOnlyList<Pin> UpsertPin(PinUpsert upsert)
    {
        var workspace = RequireWorkspace();
        var pins = workspace.Read<List<Pin>>(PinsFile) ?? [];
        var i = pins.FindIndex(p => p.Uri == upsert.Uri);
        var now = DateTimeOffset.UtcNow;
        var pin = new Pin(upsert.Uri, upsert.Kind, upsert.Label, upsert.Note ?? (i >= 0 ? pins[i].Note : ""), i >= 0 ? pins[i].CreatedAt : now, now);
        if (i >= 0)
        {
            pins[i] = pin;
        }
        else
        {
            pins.Add(pin);
        }

        workspace.Write(PinsFile, pins);
        return pins;
    }

    private IReadOnlyList<Pin> RemovePin(string uri)
    {
        var workspace = RequireWorkspace();
        var pins = workspace.Read<List<Pin>>(PinsFile) ?? [];
        pins.RemoveAll(p => p.Uri == uri);
        workspace.Write(PinsFile, pins);
        return pins;
    }

    private Workspace RequireWorkspace() =>
        _workspace ?? throw new RpcException(new RpcError(RpcError.NoDumpOpen, "No dump is open."));
}
