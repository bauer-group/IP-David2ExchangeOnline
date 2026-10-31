// SPDX-License-Identifier: MIT
namespace David2Exchange.Source;

public interface IDavidSession : IDisposable
{
    string ProgId { get; }

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

    bool TryExportEml(string path, out string? error);
}
