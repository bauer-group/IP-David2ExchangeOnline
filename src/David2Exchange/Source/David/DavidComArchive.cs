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

        // Ein einmaliger Lesefehler machte sonst den Anzeigenamen zum Pfad und alle Kinder zu "fremd".
        _ = ComHelpers.GetRequiredString(archive, "ID", "FullPath");
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

            foreach (var raw in ComHelpers.EnumerateRequired(archives, continueAfterGap: true))
            {
                yield return OpenChild(raw);
            }
        }
    }

    /// <summary>Ein Wurf im Iterator beendete ihn und nähme alle folgenden Geschwister mit.</summary>
    private IDavidArchive OpenChild(object raw)
    {
        try
        {
            var name = ComHelpers.GetRequiredString(raw, "DisplayName", "Name", "ArchiveName") ?? "sub";
            return new DavidComArchive(ResolvedPath.TrimEnd('\\') + "\\" + name, raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UnreadableArchive(ResolvedPath.TrimEnd('\\') + "\\(nicht lesbar)", ex);
        }
    }

    /// <summary>Meldet seinen Lesefehler erst beim Öffnen; der Durchlauf zählt ihn dann als Lücke.</summary>
    private sealed class UnreadableArchive : IDavidArchive
    {
        private readonly Exception _failure;

        public UnreadableArchive(string path, Exception failure)
        {
            RequestedPath = path;
            ResolvedPath = path;
            _failure = failure;
        }

        public string RequestedPath { get; }

        public string ResolvedPath { get; }

        public string Name => "(nicht lesbar)";

        public string FolderKey => "(nicht lesbar)";

        public int? ArchiveType => null;

        public string FolderKind => "";

        public int? DeclaredItemCount => null;

        public IEnumerable<IDavidArchive> SubArchives => Array.Empty<IDavidArchive>();

        public IEnumerable<IDavidItem> Items => throw new InvalidOperationException(_failure.Message, _failure);

        public bool TrySetUnpacked(out string? error)
        {
            error = _failure.Message;
            return false;
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

            foreach (var raw in ComHelpers.EnumerateRequired(allItems, continueAfterGap: true))
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
