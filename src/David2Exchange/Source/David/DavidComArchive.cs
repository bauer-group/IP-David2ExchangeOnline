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

        // Schlüssel und Name bestimmen den Zielordner; ein Lesefehler darf nicht falsch einsortieren.
        Name = ComHelpers.GetRequiredString(archive, "DisplayName", "Name", "ArchiveName")
               ?? DavidArchivePaths.LastSegment(ResolvedPath);
        FolderKey = DavidArchivePaths.LastSegment(
            ComHelpers.GetRequiredString(archive, "ID", "ArchivePath") ?? ResolvedPath);
        ArchiveType = (int?)ComHelpers.GetRequiredLong(archive, "Type", "ArchiveType");
        FolderKind = DavidArchiveTypes.ToFolderKind(ArchiveType);
    }

    public string RequestedPath { get; }

    public string ResolvedPath { get; }

    public string Name { get; }

    public string FolderKey { get; }

    public int? ArchiveType { get; }

    public string FolderKind { get; }

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
            if (!ComHelpers.TryGetRequiredProperty(_archive, "Archives", out var archives) &&
                !ComHelpers.TryGetRequiredProperty(_archive, "SubArchives", out archives))
            {
                yield break;
            }

            foreach (var raw in ComHelpers.EnumerateRequired(archives))
            {
                var name = ComHelpers.GetRequiredString(raw, "DisplayName", "Name", "ArchiveName") ?? "sub";
                var path = ResolvedPath.TrimEnd('\\') + "\\" + name;
                yield return new DavidComArchive(path, raw);
            }
        }
    }

    public bool TrySetUnpacked(out string? error)
    {
        error = null;
        try
        {
            if (!ComHelpers.TryGetProperty(_archive, "Options", out var options) ||
                options is null)
            {
                error = "Archive.Options nicht verfügbar.";
                return false;
            }

            ComHelpers.SetProperty(options, "BundleFiles", 0);
            ComHelpers.Invoke(_archive, "Save");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public IEnumerable<IDavidItem> Items
    {
        get
        {
            if (!ComHelpers.TryGetRequiredProperty(_archive, "AllItems", out var allItems) &&
                !ComHelpers.TryGetRequiredProperty(_archive, "Items", out allItems))
            {
                yield break;
            }

            foreach (var raw in ComHelpers.EnumerateRequired(allItems))
            {
                try
                {
                    yield return new DavidComItem(RequestedPath, raw);
                }
                finally
                {
                    // Sofort freigeben: Sonst sammeln sich je Item RCWs an, deren Finalizer auf
                    // diesen STA warten müssten. Items gelten nur bis zum nächsten Schleifenschritt.
                    ComHelpers.Release(raw);
                }
            }
        }
    }

}
