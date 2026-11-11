namespace DumpDebugger.Core.Rpc;

/// <summary>Doc comment carried into the generated TypeScript interface (XML docs aren't available via reflection).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
public sealed class TsDocAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}
