// SPDX-License-Identifier: MIT
using LiteDB;

namespace David2Exchange.Manifest;

public sealed class ItemDocument
{
    [BsonId]
    public string Key { get; set; } = "";

    public string MailboxId { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string DavidId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string? Subject { get; set; }
    public string? MessageId { get; set; }
    public long? SizeBytes { get; set; }
    public int? AttachmentCount { get; set; }
    public Dictionary<string, string?> Properties { get; set; } = new();
    public DateTime? ReceivedLocal { get; set; }
    public DateTime? SentLocal { get; set; }
    public bool? IsRead { get; set; }
    public ItemState State { get; set; } = ItemState.Discovered;
    public string? StagingPath { get; set; }
    public string? DstId { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExportedUtc { get; set; }
    public DateTime? ImportedUtc { get; set; }
}

public sealed class RunDocument
{
    [BsonId]
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public string Command { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public int Discovered { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? Error { get; set; }
}

public sealed class ManifestSummary
{
    public int Discovered { get; set; }
    public int Exported { get; set; }
    public int Importing { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Total => Discovered + Exported + Importing + Imported + Failed + Skipped;
}
