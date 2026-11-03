// SPDX-License-Identifier: MIT
using System.Text.Json.Serialization;

namespace David2Exchange.Output;

/// <param name="AppendItems">Fortsetzbare Läufe behalten frühere Zeilen; eine Probe beginnt neu.</param>
public sealed record OutputContext(
    string OutputDirectory,
    bool DryRun = false,
    bool AppendItems = false);

public sealed record OutputSettings(
    string Kind,
    string TargetUpn = "",
    string EwsUrl = "https://outlook.office365.com/EWS/Exchange.asmx",
    string TenantId = "",
    string ClientId = "",
    string CertificateThumbprint = "",
    string FolderName = "David-Migration",
    string TimeZone = "Europe/Berlin",
    int Workers = 8);

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
    bool CheckExisting = true,
    string ItemKey = "");

/// <param name="Retryable">false bei dauerhaften Fehlern, die ein neuer Versuch nicht behebt.</param>
/// <param name="TargetId">Kennung des Objekts im Ziel, sofern bekannt.</param>
public sealed record OutputResult(
    OutputItem Item,
    bool Success,
    string? Error,
    bool AlreadyPresent = false,
    bool Retryable = true,
    string? TargetId = null);

public sealed record OutputSummary(
    int Items,
    int EmlExported,
    DateTime StartedUtc,
    DateTime FinishedUtc);
