// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;
using David2Exchange.Infrastructure;

namespace David2Exchange.Domain;

public sealed class DavidItemSample
{
    public string ArchivePath { get; init; } = "";

    public string Id { get; init; } = "";

    public string Type { get; init; } = "";

    public string? TypeLabel { get; init; }

    public string Kind { get; init; } = "mail";

    public string FolderPath { get; init; } = "";

    public string? Subject { get; init; }

    public long? SizeBytes { get; init; }

    public int? AttachmentCount { get; init; }

    public DateTime? ReceivedLocal { get; init; }

    public DateTime? SentLocal { get; init; }

    public bool? IsRead { get; init; }

    public string? EmlFile { get; init; }

    public Dictionary<string, string?> Properties { get; init; } = new();
}

public sealed class ArchiveProbeResult
{
    public string RequestedPath { get; init; } = "";

    public string? Name { get; set; }

    public string? ResolvedPath { get; set; }

    public bool Opened { get; set; }

    public int? DeclaredItemCount { get; set; }

    public int ScannedItemCount { get; set; }

    public int VisitedArchives { get; set; }

    public int SubArchiveCount { get; set; }

    public int EmlExported { get; set; }

    public long DurationMs { get; set; }

    public Dictionary<string, int> TypeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> SeenItems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<DavidItemSample> Samples { get; } = new();

    public List<string> Errors { get; } = new();
}

public sealed class ProbeReport
{
    public string ToolVersion { get; init; } = ToolInfo.Version;

    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    public DateTime? FinishedUtc { get; set; }

    public string MachineName { get; init; } = Environment.MachineName;

    public string UserName { get; init; } = Environment.UserName;

    public bool Is64BitProcess { get; init; } = Environment.Is64BitProcess;

    public string OsVersion { get; init; } = Environment.OSVersion.ToString();

    public string RuntimeVersion { get; init; } = Environment.Version.ToString();

    public string Server { get; init; } = "";

    public string ProgId { get; init; } = "";

    public string Logon { get; init; } = "";

    public bool Mock { get; init; }

    public bool Recursive { get; init; }

    public int TotalItems { get; set; }

    public int TotalSampled { get; set; }

    public int TotalEmlExported { get; set; }

    public List<ArchiveProbeResult> Archives { get; } = new();

    public List<string> Errors { get; } = new();

    public List<string> Members { get; } = new();

    public Dictionary<string, int> TypeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
}
