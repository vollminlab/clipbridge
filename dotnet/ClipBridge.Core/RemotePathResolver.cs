namespace ClipBridge.Core;

public static class RemotePathResolver
{
    // C# has no equivalent of the PowerShell bug this replaces (a
    // single-element array unrolling to the bare string on function
    // return, so .Count reported 1 and [0] indexed the first CHARACTER).
    // IReadOnlyList<string> here never silently unrolls, so that entire
    // class of bug does not reproduce in C#.
    public static IReadOnlyList<string> NonBlankLines(string text) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    public static ResolvedPath Resolve(string stdOut)
    {
        var lines = NonBlankLines(stdOut);
        if (lines.Count != 1)
        {
            return ResolvedPath.Failure(
                $"receiver returned {lines.Count} non-blank line(s), expected exactly 1: '{stdOut}'");
        }
        if (!PathValidator.IsValid(lines[0]))
        {
            return ResolvedPath.Failure($"unusable path from receiver: '{stdOut}'");
        }
        return ResolvedPath.Ok(lines[0]);
    }
}

public sealed record ResolvedPath(string? Path, string? Reason)
{
    public static ResolvedPath Ok(string path) => new(path, null);
    public static ResolvedPath Failure(string reason) => new(null, reason);
}
