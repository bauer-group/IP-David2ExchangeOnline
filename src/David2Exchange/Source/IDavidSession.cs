// SPDX-License-Identifier: MIT
namespace David2Exchange.Source;

public interface IDavidSession : IDisposable
{
    string ProgId { get; }

    string? ServerName { get; }

    string? DavidRoot { get; }

    void Logon(string logon);

    IDavidArchive OpenArchive(string path);

    IReadOnlyList<DavidArchiveDescriptor> ListArchives(int maxDepth = 2);

    IReadOnlyList<string> GetMemberInventory();
}

public sealed record DavidArchiveDescriptor(
    string Path,
    string DisplayName,
    string Kind,
    int Depth,
    string? Email = null);

public interface IDavidArchive
{
    string RequestedPath { get; }

    string ResolvedPath { get; }

    string Name { get; }

    /// <summary>Technischer David-Ordnername, z. B. in, out, cal, address.</summary>
    string FolderKey { get; }

    /// <summary>Sprachunabhängiger David-Archivtyp (DvArchiveTypes), z. B. 101 = Inbox.</summary>
    int? ArchiveType { get; }

    /// <summary>Sprachunabhängiger Zielordner-Schlüssel, z. B. inbox, sentitems, calendar.</summary>
    string FolderKind { get; }

    int? DeclaredItemCount { get; }

    IEnumerable<IDavidArchive> SubArchives { get; }

    IEnumerable<IDavidItem> Items { get; }
}

public interface IDavidItem
{
    string Id { get; }

    string Type { get; }

    string? Subject { get; }

    long? SizeBytes { get; }

    DateTime? ReceivedLocal { get; }

    DateTime? SentLocal { get; }

    bool? IsRead { get; }

    IReadOnlyDictionary<string, string?> Properties { get; }

    IReadOnlyList<DavidAttachmentInfo> Attachments { get; }

    byte[]? FaxImage { get; }

    string? FaxImagePath { get; }

    bool TryExportEml(string path, out string? error);

    bool TryCopyAttachment(
        DavidAttachmentInfo attachment,
        string directory,
        out string? copiedPath,
        out string? error);
}

public sealed record DavidAttachmentInfo(
    string FileName,
    string? FileType,
    string? SourcePath,
    long? SizeBytes);
