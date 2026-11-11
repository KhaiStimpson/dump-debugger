using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Picks the headline from the ranked findings and phrases it from templates, never free-form.
/// The top finding's own verdict phrasing wins when it has one (a deadlock knows how to say
/// "the app is hung"); otherwise its title and meaning are used; with nothing at warning or
/// above, the verdict says so plainly instead of inventing a problem.
/// </summary>
public static class VerdictSynthesizer
{
    public static Verdict Synthesize(IReadOnlyList<AnalyzerResult> ranked, AnalysisContext context, bool complete)
    {
        var top = ranked.FirstOrDefault();
        if (top is null || top.Finding.Severity < Severity.Warning)
        {
            var blocked = context.Threads.Count(t => t.State == ThreadState.Blocked);
            var heap = context.Heap?.Overview.TotalBytes;
            var tail = complete
                ? "Nothing crossed a warning threshold. The views on the left have the detail if you're chasing something specific."
                : "Deeper analyzers are still running; this may change.";
            return new Verdict(
                Severity.Ok,
                complete ? Confidence.Medium : Confidence.Low,
                complete ? "No hang, deadlock or memory problem stood out." : "Nothing alarming so far.",
                $"{Plural(context.Threads.Count, "thread")} ({blocked} blocked)" + (heap is { } h ? $" and a {Bytes(h)} managed heap. " : ". ") + tail,
                [],
                top?.Finding.Id);
        }

        var f = top.Finding;
        var headline = top.VerdictHeadline ?? Sentence(StripMarks(f.Title)) + ".";
        var body = top.VerdictBody ?? f.WhatItMeans;
        return new Verdict(f.Severity, f.Confidence, headline, body, f.Evidence, f.Id);
    }

    /// <summary>Headlines are plain sentences: drop the code marks from a title.</summary>
    private static string StripMarks(string s) => s.Replace("`", "");
}
