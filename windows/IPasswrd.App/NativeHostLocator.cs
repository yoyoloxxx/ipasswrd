namespace IPasswrd.App;

/// <summary>Resolves IPasswrd.Host.exe next to the app (and common sibling build outputs).
/// Absolute developer-machine paths are intentionally not searched (audit L8).</summary>
internal static class NativeHostLocator
{
    public static string[] SearchPaths(string baseDirectory)
    {
        string b = baseDirectory;
        return new[]
        {
            Path.Combine(b, "IPasswrd.Host.exe"),
            Path.GetFullPath(Path.Combine(b, "..", "dist-host", "IPasswrd.Host.exe")),
            Path.GetFullPath(Path.Combine(b, "..", "IPasswrd.Host", "IPasswrd.Host.exe")),
        };
    }

    public static string? Resolve(string baseDirectory, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (string path in SearchPaths(baseDirectory))
            if (exists(path)) return path;
        return null;
    }
}
