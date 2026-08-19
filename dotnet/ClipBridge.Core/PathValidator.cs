using System.Text.RegularExpressions;

namespace ClipBridge.Core;

public static partial class PathValidator
{
    // Single line, absolute, printable ASCII only. \x21-\x7E excludes space
    // (0x20), C0 control chars (below 0x21), and anything non-ASCII. The
    // path is typed into a prompt unquoted, so anything else isn't safe.
    // \z (absolute end of string), not $: C#'s Regex $ has the same
    // trailing-newline exception as .NET's PowerShell regex - it matches
    // immediately before a single trailing \n too, so a path with one bare
    // trailing newline would slip past a $-anchored check and type as
    // Enter, submitting the prompt early. See CLAUDE.md Gotcha #6.
    [GeneratedRegex(@"^/[\x21-\x7E]+\z")]
    private static partial Regex Pattern();

    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Pattern().IsMatch(path);
    }
}
