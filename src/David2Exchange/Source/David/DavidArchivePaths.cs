// SPDX-License-Identifier: MIT
using David2Exchange.Infrastructure;

namespace David2Exchange.Source.David;

internal static class DavidArchivePaths
{
    private const string ArchiveMarker = "\\ARCHIVE\\";

    public static string? GetRelativePath(object archive)
    {
        var id = ComHelpers.GetFirstString(archive, "ID", "FullPath");
        if (!string.IsNullOrWhiteSpace(id))
        {
            var normalized = id.Replace('/', '\\').Trim();
            var markerIndex = normalized.IndexOf(ArchiveMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var relative = normalized[(markerIndex + ArchiveMarker.Length)..].Trim('\\');
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    return relative;
                }
            }
        }

        var name = ComHelpers.GetFirstString(archive, "DisplayName", "Name");
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public static string LastSegment(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var index = normalized.LastIndexOf('\\');
        return index >= 0 ? normalized[(index + 1)..] : normalized;
    }

    public static bool Matches(string candidate, string requested)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedRequested = Normalize(requested);

        return normalizedCandidate.Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.EndsWith("\\" + normalizedRequested, StringComparison.OrdinalIgnoreCase) ||
               normalizedRequested.EndsWith("\\" + normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        return path.Replace('/', '\\').Trim().Trim('\\');
    }
}
