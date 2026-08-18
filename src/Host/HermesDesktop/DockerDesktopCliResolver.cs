using System.IO;

namespace HermesDesktop;

internal static class DockerDesktopCliResolver
{
    internal static string Resolve()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localApplicationData, "Programs", "DockerDesktop", "resources", "bin", "docker.exe"),
            Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe"),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved)) return resolved;
        }

        // Preserve the established fail-closed machine-wide path when Docker
        // Desktop is not installed in either trusted location.
        return Path.GetFullPath(candidates[^1]);
    }
}
