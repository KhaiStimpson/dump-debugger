using System.Text.RegularExpressions;

namespace DumpDebugger.Llm;

/// <summary>
/// PLAN.md §2.5 privacy requirement: "Redact by pattern (connection strings, JWTs,
/// `Authorization` headers, email addresses) in any string that does make it into findings."
/// Applied to the findings JSON right before it would leave the process, as a defense-in-depth
/// layer on top of "never send raw memory, only the structured findings document".
/// </summary>
public static partial class Redactor
{
    public static string Redact(string text)
    {
        text = JwtPattern().Replace(text, "[REDACTED-JWT]");
        text = ConnectionStringSecretPattern().Replace(text, "$1=[REDACTED]");
        text = AuthorizationHeaderPattern().Replace(text, "Authorization: [REDACTED]");
        text = EmailPattern().Replace(text, "[REDACTED-EMAIL]");
        return text;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)(password|pwd|user id|uid)\s*=\s*[^;""']+")]
    private static partial Regex ConnectionStringSecretPattern();

    [GeneratedRegex(@"(?i)Authorization:\s*\S+(\s+\S+)?")]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();
}
