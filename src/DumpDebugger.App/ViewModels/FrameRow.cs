using System.Windows.Input;
using DumpDebugger.Core.Source;

namespace DumpDebugger_App.ViewModels;

/// <summary>One frame in a stack-group's representative stack. Carries the page's
/// open/blame commands directly (rather than reaching for the page's ViewModel via
/// ElementName binding) so the item template can stay plain x:Bind, like the rest of this
/// codebase's XAML.</summary>
public sealed record FrameRow(string Text, SourceLocation? Location, ICommand OpenCommand, ICommand BlameCommand);
