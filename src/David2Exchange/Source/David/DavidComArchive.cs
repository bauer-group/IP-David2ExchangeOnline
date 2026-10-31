// SPDX-License-Identifier: MIT
using David2Exchange.Infrastructure;

namespace David2Exchange.Source.David;

internal sealed class DavidComArchive : IDavidArchive
{
    private readonly object _archive;

    public DavidComArchive(string requestedPath, object archive)
    {
        RequestedPath = requestedPath;
        _archive = archive;
        ResolvedPath = DavidArchivePaths.GetRelativePath(archive) ?? requestedPath;
        Name = ComHelpers.GetFirstString(archive, "DisplayName", "Name", "ArchiveName")
               ?? DavidArchivePaths.LastSegment(ResolvedPath);
    }

    public string RequestedPath { get; }

    public string ResolvedPath { get; }

    public string Name { get; }

    public int? DeclaredItemCount
    {
        get
        {
            if (ComHelpers.TryGetProperty(_archive, "AllItems", out var allItems))
            {
                var count = ComHelpers.GetCount(allItems);
                if (count.HasValue)
                {
                    return count;
                }
            }

            return ComHelpers.GetFirstLong(_archive, "ItemCount", "Count") is { } itemCount
                ? (int)itemCount
                : null;
        }
    }

    public IEnumerable<IDavidArchive> SubArchives
    {
        get
        {
            if (!ComHelpers.TryGetProperty(_archive, "Archives", out var archives) &&
                !ComHelpers.TryGetProperty(_archive, "SubArchives", out archives))
            {
                yield break;
            }

            foreach (var raw in ComHelpers.Enumerate(archives))
            {
                var name = ComHelpers.GetFirstString(raw, "DisplayName", "Name", "ArchiveName") ?? "sub";
                var path = ResolvedPath.TrimEnd('\\') + "\\" + name;
                yield return new DavidComArchive(path, raw);
            }
        }
    }

    public IEnumerable<IDavidItem> Items
    {
        get
        {
            if (!ComHelpers.TryGetProperty(_archive, "AllItems", out var allItems) &&
                !ComHelpers.TryGetProperty(_archive, "Items", out allItems))
            {
                yield break;
            }

            foreach (var raw in ComHelpers.Enumerate(allItems))
            {
                yield return new DavidComItem(RequestedPath, raw);
            }
        }
    }

}
