using System.Windows.Input;
using DumpDebugger.Core.Source;

namespace DumpDebugger_App.ViewModels;

/// <summary>One evidence item on a finding. See FrameRow for why the commands travel with the row.</summary>
public sealed record EvidenceRow(string Kind, string Ref, string Detail, SourceLocation? Location, ICommand OpenCommand, ICommand BlameCommand)
{
    public string Text => $"- {Kind} {Ref}: {Detail}";
}
