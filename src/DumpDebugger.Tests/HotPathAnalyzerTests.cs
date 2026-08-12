using DumpDebugger.Analysis;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Source;

namespace DumpDebugger.Tests;

public class HotPathAnalyzerTests
{
    private static ThreadInfo MakeThread(int osThreadId, params StackFrameInfo[] frames) => new(
        OSThreadId: osThreadId,
        ManagedThreadId: osThreadId,
        IsAlive: true,
        IsGc: false,
        IsFinalizer: false,
        LockCount: 0,
        CurrentExceptionType: null,
        Frames: frames,
        StackGroupHash: osThreadId.ToString(),
        OperationContext: null);

    [Fact]
    public void Analyze_ReportsApplicationFrameSharedByEnoughThreads()
    {
        var shared = new StackFrameInfo("Checkout", "MyApp.OrderService");
        var threads = Enumerable.Range(1, 5)
            .Select(id => MakeThread(id, shared))
            .ToList();

        var findings = HotPathAnalyzer.Analyze(threads);

        var finding = Assert.Single(findings);
        Assert.Contains("MyApp.OrderService.Checkout", finding.Title);
        Assert.Equal(Severity.Warning, finding.Severity); // 5 of 5 threads = 100% share
    }

    [Fact]
    public void Analyze_ExcludesFrameworkFrames_EvenWhenSharedByAllThreads()
    {
        var frameworkFrame = new StackFrameInfo("Dispatch", "System.Threading.ThreadPoolWorkQueue");
        var threads = Enumerable.Range(1, 10)
            .Select(id => MakeThread(id, frameworkFrame))
            .ToList();

        var findings = HotPathAnalyzer.Analyze(threads);

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_IgnoresFramesBelowThreadCountThreshold()
    {
        var rare = new StackFrameInfo("Rare", "MyApp.SeldomUsed");
        var threads = new[]
        {
            MakeThread(1, rare),
            MakeThread(2, new StackFrameInfo("Other", "MyApp.Elsewhere")),
        };

        var findings = HotPathAnalyzer.Analyze(threads);

        Assert.Empty(findings);
    }

    [Fact]
    public void Analyze_CountsRecursiveFrameOncePerThread()
    {
        var recursive = new StackFrameInfo("Walk", "MyApp.Tree");
        // Same thread appears to recurse through "Walk" three times on its own stack.
        var threads = Enumerable.Range(1, 4)
            .Select(id => MakeThread(id, recursive, recursive, recursive))
            .ToList();

        var findings = HotPathAnalyzer.Analyze(threads);

        var finding = Assert.Single(findings);
        var locationEvidence = Assert.Single(finding.Evidence, e => e.Kind == "location");
        Assert.Contains("4 of 4 threads", locationEvidence.Detail);
    }

    [Fact]
    public void Analyze_AttachesResolvedSourceLocationToEvidence()
    {
        var location = new SourceLocation("https://github.com/org/repo", new string('a', 40), "src/Order.cs", 42, 5);
        var frame = new StackFrameInfo("Checkout", "MyApp.OrderService", location);
        var threads = Enumerable.Range(1, 3).Select(id => MakeThread(id, frame)).ToList();

        var findings = HotPathAnalyzer.Analyze(threads);

        var finding = Assert.Single(findings);
        var locationEvidence = Assert.Single(finding.Evidence, e => e.Kind == "location");
        Assert.Equal(location, locationEvidence.Location);
        Assert.Equal("src/Order.cs:42", locationEvidence.Ref);
    }
}
