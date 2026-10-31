// SPDX-License-Identifier: MIT
using LiteDB;

namespace David2Exchange.Manifest;

public sealed class LiteDbManifest : IDisposable
{
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<ItemDocument> _items;
    private readonly ILiteCollection<RunDocument> _runs;
    private readonly string _path;

    public LiteDbManifest(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _path = fullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _database = new LiteDatabase($"Filename={fullPath};Upgrade=true");
        _items = _database.GetCollection<ItemDocument>("items");
        _runs = _database.GetCollection<RunDocument>("runs");
        _items.EnsureIndex(x => x.MailboxId);
        _items.EnsureIndex(x => x.State);
        _items.EnsureIndex(x => x.ReceivedLocal);
    }

    public string DatabasePath => _path;

    public RunDocument BeginRun(string command, string toolVersion)
    {
        var run = new RunDocument
        {
            Command = command,
            ToolVersion = toolVersion
        };
        _runs.Insert(run);
        return run;
    }

    public void FinishRun(RunDocument run, string? error = null)
    {
        run.FinishedUtc = DateTime.UtcNow;
        run.Error = error;
        _runs.Update(run);
    }

    public ItemDocument? Get(string key)
    {
        return _items.FindById(key);
    }

    public void UpsertDiscovered(ItemDocument item)
    {
        var existing = _items.FindById(item.Key);
        if (existing is null)
        {
            _items.Insert(item);
            return;
        }

        existing.LastSeenUtc = DateTime.UtcNow;
        existing.Subject = item.Subject;
        existing.SizeBytes = item.SizeBytes;
        existing.ReceivedLocal = item.ReceivedLocal;
        existing.SentLocal = item.SentLocal;
        existing.IsRead = item.IsRead;
        existing.ArchivePath = item.ArchivePath;
        _items.Update(existing);
    }

    public void Save(ItemDocument item)
    {
        _items.Update(item);
    }

    public IReadOnlyList<ItemDocument> GetPending(
        string? mailboxId,
        int limit,
        bool newest)
    {
        var query = _items.Find(x =>
            x.State == ItemState.Discovered ||
            x.State == ItemState.Exported ||
            x.State == ItemState.Importing ||
            x.State == ItemState.Failed);

        if (!string.IsNullOrWhiteSpace(mailboxId))
        {
            query = query.Where(x => x.MailboxId.Equals(mailboxId, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = newest
            ? query.OrderByDescending(x => x.ReceivedLocal).ThenBy(x => x.DavidId)
            : query.OrderBy(x => x.ReceivedLocal).ThenBy(x => x.DavidId);

        return limit > 0 ? ordered.Take(limit).ToList() : ordered.ToList();
    }

    public IReadOnlyList<ItemDocument> GetAll(string? mailboxId = null)
    {
        return string.IsNullOrWhiteSpace(mailboxId)
            ? _items.FindAll().ToList()
            : _items.Find(x => x.MailboxId == mailboxId).ToList();
    }

    public ManifestSummary GetSummary(string? mailboxId = null)
    {
        var all = string.IsNullOrWhiteSpace(mailboxId)
            ? _items.FindAll().ToList()
            : _items.Find(x => x.MailboxId == mailboxId).ToList();

        return new ManifestSummary
        {
            Discovered = all.Count(x => x.State == ItemState.Discovered),
            Exported = all.Count(x => x.State == ItemState.Exported),
            Importing = all.Count(x => x.State == ItemState.Importing),
            Imported = all.Count(x => x.State == ItemState.Imported),
            Failed = all.Count(x => x.State == ItemState.Failed),
            Skipped = all.Count(x => x.State == ItemState.Skipped)
        };
    }

    public void Backup(string backupPath)
    {
        _database.Checkpoint();
        File.Copy(_path, backupPath, overwrite: true);
    }

    public void Dispose()
    {
        _database.Checkpoint();
        _database.Dispose();
    }
}
