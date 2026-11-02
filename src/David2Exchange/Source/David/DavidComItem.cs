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
        "Flags", "Attachments", "From", "To", "CC", "Folder", "Archive",
        "BodyText", "TextSource",
        "StartTime", "StopTime", "Location", "AllDayEvent", "IsRecurring",
        "Attendees", "Recipients", "VCard",
        "DisplayName", "FirstName", "LastName", "CompanyName", "Department",
        "JobTitle", "FileAs", "EMail", "Email", "PhoneNumber", "FaxNumber", "SMSNumber"
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

    public byte[]? FaxImage
    {
        get
        {
            if (!ComHelpers.TryGetProperty(_item, "Image", out var value) || value is null)
            {
                return null;
            }

            if (value is byte[] bytes)
            {
                return bytes;
            }

            if (value is string path && File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }

            return null;
        }
    }

    public IReadOnlyList<DavidAttachmentInfo> Attachments
    {
        get
        {
            if (!ComHelpers.TryGetProperty(_item, "Attachments", out var value) ||
                value is null)
            {
                return Array.Empty<DavidAttachmentInfo>();
            }

            var result = new List<DavidAttachmentInfo>();
            foreach (var attachment in ComHelpers.Enumerate(value))
            {
                var fileName = ComHelpers.GetFirstString(
                    attachment,
                    "FileName",
                    "DisplayName",
                    "Name");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                result.Add(new DavidAttachmentInfo(
                    fileName,
                    ComHelpers.GetFirstString(attachment, "FileType", "ContentType"),
                    ComHelpers.GetFirstString(attachment, "_Path", "Path"),
                    ComHelpers.GetFirstLong(attachment, "FileSize", "Size")));
            }

            return result;
        }
    }

    public bool TryCopyAttachment(
        DavidAttachmentInfo attachment,
        string directory,
        out string? copiedPath,
        out string? error)
    {
        copiedPath = null;
        error = null;

        if (!ComHelpers.TryGetProperty(_item, "Attachments", out var collection) ||
            collection is null)
        {
            error = "David-Item hat keine Attachments-Collection.";
            return false;
        }

        var raw = ComHelpers.Enumerate(collection)
            .FirstOrDefault(candidate =>
            {
                var name = ComHelpers.GetFirstString(
                    candidate,
                    "FileName",
                    "DisplayName",
                    "Name");
                return string.Equals(name, attachment.FileName, StringComparison.OrdinalIgnoreCase);
            });

        if (raw is null)
        {
            error = $"Anhang '{attachment.FileName}' nicht gefunden.";
            return false;
        }

        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(attachment.FileName);
        var target = Path.Combine(directory, fileName);

        try
        {
            ComHelpers.Invoke(raw, "CopyOut", directory, attachment.FileName);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!File.Exists(target))
        {
            try
            {
                ComHelpers.Invoke(raw, "CopyOut", target, attachment.FileName);
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }
        }

        if (!File.Exists(target) &&
            !string.IsNullOrWhiteSpace(attachment.SourcePath) &&
            File.Exists(attachment.SourcePath))
        {
            File.Copy(attachment.SourcePath, target, overwrite: true);
        }

        if (!File.Exists(target))
        {
            var found = Directory.GetFiles(directory)
                .FirstOrDefault(file =>
                    string.Equals(
                        Path.GetFileName(file),
                        fileName,
                        StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                target = found;
            }
        }

        if (!File.Exists(target))
        {
            error ??= $"CopyOut für '{attachment.FileName}' lieferte keine Datei.";
            return false;
        }

        copiedPath = target;
        error = null;
        return true;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "attachment.bin" : result;
    }

    public bool TryExportEml(string path, out string? error)
    {
        return DvApiNative.TryExport(_item, path, out error);
    }

    private static string? ResolveFormattedText(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = ComHelpers.GetFirstString(
            value,
            "PlainText",
            "Text",
            "HTMLText",
            "HTML",
            "Html",
            "Plain");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var scalar = ComHelpers.ToScalarString(value);
        return scalar == "System.__ComObject" ? null : scalar;
    }

    private static string? ResolveAddressList(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var addresses = new List<string>();
        foreach (var address in ComHelpers.Enumerate(value))
        {
            var resolved = ResolveAddress(address);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                addresses.Add(resolved);
            }
        }

        return addresses.Count == 0 ? null : string.Join("; ", addresses);
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

            var text = name switch
            {
                "From" or "To" or "CC" => ResolveAddress(value),
                "Attendees" or "Recipients" => ResolveAddressList(value),
                "BodyText" => ResolveFormattedText(value),
                _ => ComHelpers.ToScalarString(value)
            };

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
