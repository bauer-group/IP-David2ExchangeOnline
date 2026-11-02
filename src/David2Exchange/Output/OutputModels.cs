// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;

namespace David2Exchange.Output;

public sealed record OutputContext(
    string OutputDirectory,
    bool DryRun = false);

public sealed record OutputSettings(
    string Kind,
    string TargetUpn = "",
    string EwsUrl = "https://outlook.office365.com/EWS/Exchange.asmx",
    string TenantId = "",
    string ClientId = "",
    string CertificateThumbprint = "",
    string FolderName = "David-Migration");

public sealed record OutputItem(
    string ArchivePath,
    string Id,
    string Type,
    string? TypeLabel,
    string? Subject,
    long? SizeBytes,
    int? AttachmentCount,
    DateTime? ReceivedLocal,
    DateTime? SentLocal,
    bool? IsRead,
    string? EmlFile,
    [property: JsonIgnore] string? StagedEmlPath,
    IReadOnlyDictionary<string, string?> Properties,
    string Kind = "mail",
    string FolderPath = "",
    bool CheckExisting = true);

public sealed record OutputResult(
    OutputItem Item,
    bool Success,
    string? Error,
    bool AlreadyPresent = false);

public sealed record OutputSummary(
    int Items,
    int EmlExported,
    DateTime StartedUtc,
    DateTime FinishedUtc);
