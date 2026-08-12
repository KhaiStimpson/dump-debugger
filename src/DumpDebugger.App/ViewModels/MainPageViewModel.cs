using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Ipc;
using DumpDebugger_App.Services;
using WinRT.Interop;

namespace DumpDebugger_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private WorkerSession? _session;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "No dump open.";

    [ObservableProperty]
    public partial string? DumpPath { get; set; }

    [ObservableProperty]
    public partial string? MetadataSummary { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool HasThreads { get; set; }

    [ObservableProperty]
    public partial ThreadGroupRow? SelectedThreadGroup { get; set; }

    public ObservableCollection<ThreadGroupRow> ThreadGroups { get; } = [];

    [RelayCommand]
    private async Task OpenDumpAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeFilter.Add(".dmp");
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadDumpAsync(file.Path);
    }

    private async Task LoadDumpAsync(string path)
    {
        IsBusy = true;
        HasError = false;
        HasThreads = false;
        DumpPath = path;
        MetadataSummary = null;
        ThreadGroups.Clear();

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        var progress = new Progress<ProgressNotification>(note =>
            StatusText = $"{note.Stage}... ({note.FractionComplete:P0}) {note.Detail}");

        try
        {
            var (session, metadata) = await WorkerSession.OpenAsync(path, progress);
            _session = session;
            MetadataSummary = Format(metadata);

            StatusText = "Enumerating threads...";
            var threads = await session.GetThreadsAsync();
            foreach (var row in ThreadGroupRow.FromThreads(threads))
            {
                ThreadGroups.Add(row);
            }

            HasThreads = ThreadGroups.Count > 0;
            SelectedThreadGroup = ThreadGroups.FirstOrDefault();
            StatusText = $"Dump loaded. {threads.Count} threads in {ThreadGroups.Count} stack group(s).";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = $"Failed to open dump: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Format(DumpMetadata metadata)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Path: {metadata.DumpPath}");
        sb.AppendLine($"Size: {metadata.FileSizeBytes / 1024.0 / 1024.0:F1} MB");
        sb.AppendLine($"Architecture: {metadata.Architecture}");
        sb.AppendLine($"Full memory dump: {metadata.IsFullMemoryDump}");
        sb.AppendLine($"Process ID: {metadata.ProcessId}");
        sb.AppendLine($"Modules: {metadata.ModuleCount}");
        sb.AppendLine($"Threads: {metadata.ThreadCount}");
        sb.AppendLine();
        sb.AppendLine(metadata.Runtimes.Count == 0
            ? "No managed runtime detected in this dump."
            : "Runtimes:");

        foreach (var runtime in metadata.Runtimes)
        {
            sb.AppendLine($"  - {runtime.Family} {runtime.Version}: " +
                           $"DAC {(runtime.IsManagedAnalysisAvailable ? "resolved" : "NOT resolved")} " +
                           $"(tier: {runtime.DacTier})");
        }

        return sb.ToString();
    }
}
