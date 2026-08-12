using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Ipc;
using DumpDebugger.Llm;
using DumpDebugger_App.Services;
using Microsoft.UI.Xaml.Controls;
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

    [ObservableProperty]
    public partial string LocksSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFindings { get; set; }

    [ObservableProperty]
    public partial bool IsExplaining { get; set; }

    [ObservableProperty]
    public partial string? NarrativeText { get; set; }

    [ObservableProperty]
    public partial bool HasNarrative { get; set; }

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial bool SortStackGroupsByLockCount { get; set; }

    [ObservableProperty]
    public partial DeadlockGraphViewModel? DeadlockGraph { get; set; }

    [ObservableProperty]
    public partial bool HasDeadlockGraph { get; set; }

    [ObservableProperty]
    public partial int ThreadGroupCount { get; set; }

    [ObservableProperty]
    public partial int TypeStatCount { get; set; }

    [ObservableProperty]
    public partial int LargeObjectCount { get; set; }

    private FindingsDocument? _findingsDocument;
    private INarrativeProvider? _narrativeProvider;
    private bool _payloadReviewedThisSession;
    private IReadOnlyList<ThreadInfo> _lastThreads = [];

    public ObservableCollection<ThreadGroupRow> ThreadGroups { get; } = [];
    public ObservableCollection<TypeStatRow> TypeStats { get; } = [];
    public ObservableCollection<LargeObjectRow> LargeObjects { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];

    [RelayCommand]
    private void SelectTab(string index) => SelectedTabIndex = int.Parse(index);

    [RelayCommand]
    private void SetStackGroupSort(string byLockCount)
    {
        SortStackGroupsByLockCount = byLockCount == "true";
        ResortStackGroups();
    }

    private void ResortStackGroups()
    {
        var rows = ThreadGroupRow.FromThreads(_lastThreads);
        var ordered = SortStackGroupsByLockCount
            ? rows.OrderByDescending(r => r.MaxLockCount).ThenByDescending(r => r.Count)
            : rows.OrderByDescending(r => r.Count);

        ThreadGroups.Clear();
        foreach (var row in ordered)
        {
            ThreadGroups.Add(row);
        }

        SelectedThreadGroup = ThreadGroups.FirstOrDefault();
    }

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

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (_findingsDocument is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.FileTypeChoices.Add("Markdown", [".md"]);
        picker.SuggestedFileName = "dump-debugger-report";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var markdown = ReportWriter.ToMarkdown(_findingsDocument);
        await Windows.Storage.FileIO.WriteTextAsync(file, markdown);
        StatusText = $"Report exported to {file.Path}";
    }

    [RelayCommand]
    private async Task ExplainAsync()
    {
        if (_findingsDocument is null)
        {
            return;
        }

        IsExplaining = true;
        HasNarrative = false;
        NarrativeText = null;

        try
        {
            _narrativeProvider ??= await NarrativeProviderFactory.DetectAsync();
            if (!_narrativeProvider.IsAvailable)
            {
                NarrativeText = $"AI narrative is unavailable: {_narrativeProvider.Description}";
                HasNarrative = true;
                return;
            }

            var payloadJson = Redactor.Redact(JsonSerializer.Serialize(
                _findingsDocument,
                new JsonSerializerOptions { WriteIndented = true }));

            // PLAN.md §2.5: show the exact (redacted) payload before the first send this session.
            if (!_payloadReviewedThisSession)
            {
                var confirmed = await ShowPayloadReviewAsync(payloadJson, _narrativeProvider.Description);
                if (!confirmed)
                {
                    return;
                }

                _payloadReviewedThisSession = true;
            }

            var narrative = await _narrativeProvider.SummarizeAsync(_findingsDocument, CancellationToken.None);

            var sb = new StringBuilder();
            sb.AppendLine(narrative.Summary);
            if (narrative.Hypotheses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Hypotheses:");
                foreach (var h in narrative.Hypotheses)
                {
                    sb.AppendLine($"  - {h}");
                }
            }

            if (narrative.NextSteps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Next steps:");
                foreach (var step in narrative.NextSteps)
                {
                    sb.AppendLine($"  - {step}");
                }
            }

            NarrativeText = sb.ToString();
            HasNarrative = true;
        }
        catch (Exception ex)
        {
            NarrativeText = $"Failed to generate narrative: {ex.Message}";
            HasNarrative = true;
        }
        finally
        {
            IsExplaining = false;
        }
    }

    private static async Task<bool> ShowPayloadReviewAsync(string payloadJson, string providerDescription)
    {
        var dialog = new ContentDialog
        {
            Title = $"Send this to {providerDescription}?",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = payloadJson,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                },
                MaxHeight = 400,
            },
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = App.Window.Content.XamlRoot,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task LoadDumpAsync(string path)
    {
        IsBusy = true;
        HasError = false;
        HasThreads = false;
        DumpPath = path;
        MetadataSummary = null;
        LocksSummary = string.Empty;
        ThreadGroups.Clear();
        TypeStats.Clear();
        LargeObjects.Clear();
        Findings.Clear();
        HasFindings = false;
        _findingsDocument = null;
        NarrativeText = null;
        HasNarrative = false;
        DeadlockGraph = null;
        HasDeadlockGraph = false;
        SelectedTabIndex = 0;
        _lastThreads = [];

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        var hadWorkspace = WorkspaceService.TryLoadCachedFindings(path) is not null;
        var progress = new Progress<ProgressNotification>(note =>
            StatusText = $"{note.Stage}... ({note.FractionComplete:P0}) {note.Detail}");

        try
        {
            var (session, metadata) = await WorkerSession.OpenAsync(path, progress);
            _session = session;
            MetadataSummary = Format(metadata);

            StatusText = "Enumerating threads...";
            var threads = await session.GetThreadsAsync();
            _lastThreads = threads;
            foreach (var row in ThreadGroupRow.FromThreads(threads))
            {
                ThreadGroups.Add(row);
            }

            HasThreads = ThreadGroups.Count > 0;
            SelectedThreadGroup = ThreadGroups.FirstOrDefault();

            StatusText = "Enumerating locks...";
            var locks = await session.GetLocksAsync();
            LocksSummary = FormatLocks(locks);
            DeadlockGraph = DeadlockGraphViewModel.From(locks);
            HasDeadlockGraph = DeadlockGraph is not null;

            var deadlockNote = locks.Findings.Count > 0 ? $" {locks.Findings.Count} deadlock finding(s)!" : string.Empty;

            StatusText = "Walking heap...";
            var memory = await session.GetMemoryAsync();
            foreach (var row in memory.TypeStats.Take(50))
            {
                TypeStats.Add(TypeStatRow.From(row));
            }

            foreach (var row in memory.LargeObjects.Take(50))
            {
                LargeObjects.Add(LargeObjectRow.From(row));
            }

            TypeStatCount = memory.TypeStats.Count;
            LargeObjectCount = memory.LargeObjects.Count;
            ThreadGroupCount = ThreadGroups.Count;

            StatusText = "Building triage...";
            _findingsDocument = await session.GetFindingsAsync();
            foreach (var finding in _findingsDocument.Findings)
            {
                Findings.Add(FindingRow.From(finding));
            }

            HasFindings = Findings.Count > 0;

            // PLAN.md §3.1: persist findings to a workspace sidecar folder so a future reopen
            // of this dump has them available without recomputation.
            WorkspaceService.SaveFindings(path, _findingsDocument);

            var workspaceNote = hadWorkspace ? " (workspace reused)" : " (new workspace created)";
            StatusText = $"Dump loaded. {threads.Count} threads in {ThreadGroups.Count} stack group(s), " +
                          $"{memory.TypeStats.Count} heap types, {memory.LargeObjects.Count} large objects." +
                          deadlockNote + workspaceNote;
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

    private static string FormatLocks(GetLocksResponse locks)
    {
        var sb = new StringBuilder();

        if (locks.Findings.Count == 0)
        {
            sb.AppendLine("No deadlocks detected.");
        }
        else
        {
            foreach (var finding in locks.Findings)
            {
                sb.AppendLine($"[{finding.Severity}] {finding.Title}");
                sb.AppendLine(finding.Summary);
                foreach (var evidence in finding.Evidence)
                {
                    sb.AppendLine($"  - {evidence.Kind} {evidence.Ref}: {evidence.Detail}");
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine($"Sync blocks (contended or held): {locks.SyncBlocks.Count}");
        foreach (var syncBlock in locks.SyncBlocks)
        {
            sb.AppendLine($"  0x{syncBlock.ObjectAddress:x} [{syncBlock.ObjectTypeName}] " +
                           $"owner=thread {syncBlock.OwnerOSThreadId?.ToString() ?? "none"} " +
                           $"waiters={syncBlock.WaitingThreadCount} recursion={syncBlock.RecursionCount}");
        }

        return sb.ToString();
    }
}
