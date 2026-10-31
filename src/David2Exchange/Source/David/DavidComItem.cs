// SPDX-License-Identifier: MIT
using David2Exchange.Infrastructure;

namespace David2Exchange.Source.David;

internal sealed class DavidComItem : IDavidItem
{
    private static readonly string[] PropertyCandidates =
    {
        "_ID", "ID", "ItemID",
        "Type", "ItemType",
        "Subject", "Betreff", "Title",
        "Size", "SizeBytes", "ItemSize",
        "StatusTime", "ReceivedDate", "Received", "Date", "Empfangsdatum", "Time", "Created",
        "SendTime", "SentDate", "SendDate", "Sent", "Gesendet",
        "Viewed", "Read", "IsRead", "Gelesen",
        "Flags", "Attachments", "From", "To", "CC", "Folder", "Archive"
    };

    private readonly object _item;
    private readonly string _archivePath;

    public DavidComItem(string archivePath, object item)
    {
        _archivePath = archivePath;
        _item = item;
        Properties = ReadProperties();
    }

    public string Id =>
        ComHelpers.GetFirstString(_item, "_ID", "ID", "ItemID", "Id") ?? "";

    public string Type =>
        ComHelpers.GetFirstString(_item, "Type", "ItemType", "DvItemType") ?? "unknown";

    public string? Subject =>
        ComHelpers.GetFirstString(_item, "Subject", "Betreff", "Title");

    public long? SizeBytes =>
        ComHelpers.GetFirstLong(_item, "Size", "SizeBytes", "ItemSize", "Bytes");

    public DateTime? ReceivedLocal =>
        ComHelpers.GetFirstDate(_item, "StatusTime", "ReceivedDate", "Date", "Received", "Empfangsdatum", "Time", "Created");

    public DateTime? SentLocal =>
        ComHelpers.GetFirstDate(_item, "SendTime", "SentDate", "SendDate", "Gesendet", "Date");

    public bool? IsRead =>
        ComHelpers.GetFirstBool(_item, "Viewed", "Read", "IsRead", "Gelesen");

    public IReadOnlyDictionary<string, string?> Properties { get; }

    public bool TryExportEml(string path, out string? error)
    {
        return DvApiNative.TryExport(_item, path, out error);
    }

    private static string? ResolveAddress(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = ComHelpers.GetFirstString(value, "Address", "EMail", "Email", "Name", "DisplayName");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var scalar = ComHelpers.ToScalarString(value);
        return scalar == "System.__ComObject" ? null : scalar;
    }

    private IReadOnlyDictionary<string, string?> ReadProperties()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in PropertyCandidates)
        {
            if (!ComHelpers.TryGetProperty(_item, name, out var value) || value is null)
            {
                continue;
            }

            var text = name is "From" or "To" or "CC"
                ? ResolveAddress(value)
                : ComHelpers.ToScalarString(value);

            if (string.IsNullOrWhiteSpace(text) || text == "System.__ComObject")
            {
                continue;
            }

            values[name] = text;
        }

        values["ArchivePath"] = ComHelpers.GetFirstString(_item, "ArchivePath", "Archive")
                                 ?? _archivePath;

        if (ComHelpers.TryGetProperty(_item, "Attachments", out var attachments))
        {
            var count = ComHelpers.GetCount(attachments);
            if (count.HasValue)
            {
                values["AttachmentCount"] = count.Value.ToString();
            }
        }

        return values;
    }
}
