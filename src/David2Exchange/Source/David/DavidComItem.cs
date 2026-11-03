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
        "Flags", "From", "To", "CC", "Folder", "Archive",
        "BodyText", "TextSource", "Caller",
        "StartTime", "StopTime", "DueTime", "Location", "AllDayEvent", "IsRecurring", "Private", "ShowAs",
        "Attendees", "Recipients", "VCard",
        "TaskStatus", "Percentage", "DateCompleted", "Reminder",
        "DisplayName", "FirstName", "LastName", "EMail", "Email", "FaxNumber", "SMSNumber"
    };

    /// <summary>Am echten David gemessene Mitglieder von AddressItem; nur für Typ 1 gelesen.</summary>
    private static readonly string[] ContactCandidates =
    {
        "Company", "Title", "MiddleInitial", "Salutation", "Birthday",
        "Street", "ZipCode", "City", "State", "Country", "WebSite",
        "PhoneOffice", "PhonePrivate", "PhoneMobile", "AltFaxNumber", "AltEMail", "VoiceBox",
        "Notes", "FormattedNotes"
    };

    private readonly object _item;
    private readonly string _archivePath;
    private IReadOnlyDictionary<string, string?>? _properties;
    private string? _type;

    public DavidComItem(string archivePath, object item)
    {
        _archivePath = archivePath;
        _item = item;
    }

    public string Id =>
        ComHelpers.GetRequiredString(_item, "_ID", "ID", "ItemID", "Id") ?? "";

    public string Type =>
        _type ??= ComHelpers.GetRequiredString(_item, "Type", "ItemType", "DvItemType") ?? "unknown";

    public string? Subject =>
        ComHelpers.GetRequiredString(_item, "Subject", "Betreff", "Title");

    public long? SizeBytes =>
        ComHelpers.GetRequiredLong(_item, "Size", "SizeBytes", "ItemSize", "Bytes");

    public DateTime? ReceivedLocal =>
        ComHelpers.GetRequiredDate(_item, "StatusTime", "ReceivedDate", "Date", "Empfangsdatum", "Time", "Created")
        ?? FieldDate("StatusTime");

    public DateTime? SentLocal =>
        ComHelpers.GetRequiredDate(_item, "SendTime", "SentDate", "SendDate", "Gesendet", "Date")
        ?? FieldDate("SendTime");

    public bool? IsRead =>
        ComHelpers.GetRequiredBool(_item, "Viewed", "Read", "IsRead", "Gelesen")
        ?? (HasFieldsOnly ? ComHelpers.ToNullableBool(ReadField("Viewed")) : null);

    /// <summary>
    /// Wird erst bei Bedarf gelesen: Body und Adressen aufzulösen kostet je Item spürbar
    /// Zeit (gemessen ~1,5 s über VPN), und Mails mit nativer EML brauchen sie nicht.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Properties => _properties ??= ReadProperties();

    public string? SourceFile =>
        ComHelpers.GetRequiredString(_item, "TextSource");

    public bool? IsReceived =>
        ComHelpers.GetRequiredBool(_item, "Received");

    public int? AttachmentCount =>
        ComHelpers.ToNullableLong(ReadField("AttachCount")) is { } count ? (int)count : null;

    public string? FaxImagePath =>
        ReadSourceValue() is string { Length: > 0 } path && !string.IsNullOrWhiteSpace(path) ? path : null;

    public byte[]? FaxImage
    {
        get
        {
            // Bei ungepackten Archiven ist der Wert ein realer Dateipfad.
            // Bei gepackten Archiven existiert die Datei nicht; dann null.
            return ReadSourceValue() switch
            {
                byte[] bytes => bytes,
                string path when File.Exists(path) => File.ReadAllBytes(path),
                _ => null
            };
        }
    }

    public IReadOnlyList<DavidAttachmentInfo> Attachments
    {
        get
        {
            var value = ReadAttachmentCollection();
            if (value is null)
            {
                return Array.Empty<DavidAttachmentInfo>();
            }

            var result = new List<DavidAttachmentInfo>();
            var index = -1;
            foreach (var attachment in ComHelpers.EnumerateRequired(value))
            {
                index++;

                // Ein Anhang ohne Namen wird benannt, nicht verworfen: Der Export läuft über die Position.
                var fileName = ComHelpers.GetRequiredString(
                    attachment,
                    "FileName",
                    "DisplayName",
                    "Name") ?? $"anhang-{index + 1}.bin";

                result.Add(new DavidAttachmentInfo(
                    fileName,
                    ComHelpers.GetRequiredString(attachment, "FileType", "ContentType"),
                    ComHelpers.GetRequiredString(attachment, "_Path", "Path"),
                    ComHelpers.GetRequiredLong(attachment, "FileSize", "Size"),
                    index));
            }

            return result;
        }
    }

    /// <summary>Fax, SMS, Voice und Chat führen Datum und Lesestatus nur in der Fields-Collection.</summary>
    private bool HasFieldsOnly => Type is "3" or "4" or "5" or "6";

    private object? ReadSourceValue()
    {
        // VoiceItem führt laut Typbibliothek Content statt Image.
        var names = Type == "5" ? new[] { "Content", "Image" } : new[] { "Image" };
        foreach (var name in names)
        {
            if (ComHelpers.TryGetRequiredProperty(_item, name, out var value) && value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private object? ReadAttachmentCollection()
    {
        try
        {
            return ComHelpers.TryGetRequiredProperty(_item, "Attachments", out var value) ? value : null;
        }
        catch (Exception ex) when (ex.HResult == ComHelpers.EFail && Type != "2")
        {
            // Fax und Link melden hier E_FAIL statt einer leeren Liste (am echten David gemessen).
            // Führt David laut Fields doch Anhänge, darf das nicht als "keine" durchgehen.
            return AttachmentCount is null or 0
                ? null
                : throw new InvalidOperationException(
                    $"David nennt {AttachmentCount} Anhänge, die sich nicht lesen lassen.",
                    ex);
        }
    }

    private DateTime? FieldDate(string name)
    {
        // Ein nicht gesetztes Datum steht in Fields auf 1970, nicht auf dem OLE-Nullwert.
        return HasFieldsOnly && ComHelpers.ToNullableDate(ReadField(name)) is { Year: > 1970 } date
            ? date
            : null;
    }

    private string? FieldText(string name)
    {
        return ReadField(name) as string is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    private object? ReadField(string name)
    {
        if (!ComHelpers.TryGetRequiredProperty(_item, "Fields", out var fields) || fields is null)
        {
            return null;
        }

        object? field = null;
        try
        {
            field = ComHelpers.Invoke(fields, "Item", name);
            return field is null ? null : ComHelpers.GetProperty(field, "Value");
        }
        catch (Exception ex) when (ex.HResult == ComHelpers.EFail)
        {
            // So meldet David ein Feld, das es für diesen Typ nicht gibt.
            return null;
        }
        finally
        {
            ComHelpers.Release(field);
            ComHelpers.Release(fields);
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

        // Über die Position, weil David mehrere Anhänge gleichen Namens zulässt.
        var raw = ComHelpers.EnumerateRequired(collection).ElementAtOrDefault(attachment.Index);

        if (raw is null)
        {
            error = $"Anhang '{attachment.FileName}' nicht gefunden.";
            return false;
        }

        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(attachment.FileName);
        var target = Path.Combine(directory, fileName);

        // Reste eines früheren Versuchs dürfen nicht als Ergebnis dieses Versuchs gelten.
        File.Delete(target);

        // Die zwei Aufrufformen, weil David je nach Version Verzeichnis oder Dateipfad erwartet.
        foreach (var destination in new[] { directory, target })
        {
            if (File.Exists(target))
            {
                break;
            }

            try
            {
                ComHelpers.Invoke(raw, "CopyOut", destination, attachment.FileName);
            }
            catch (Exception ex)
            {
                error ??= ex.Message;

                // Ein abgebrochenes CopyOut kann eine Teildatei hinterlassen.
                if (File.Exists(target) && new FileInfo(target).Length != attachment.SizeBytes)
                {
                    File.Delete(target);
                }
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
            error ??= $"CopyOut für '{attachment.FileName}' lieferte keine Datei.";
            return false;
        }

        if (attachment.SizeBytes > 0 && new FileInfo(target).Length == 0)
        {
            error = $"CopyOut für '{attachment.FileName}' lieferte eine leere Datei.";
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

        var text = ComHelpers.GetRequiredString(
            value,
            "PlainText",
            "Text",
            "Plain");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var scalar = ComHelpers.ToScalarString(value);
        return scalar == "System.__ComObject" ? null : scalar;
    }

    private static string? ResolveHtmlText(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var html = ComHelpers.GetRequiredString(value, "HTMLText", "HTML", "Html");
        if (!string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        return null;
    }

    private static string? ResolveAddressList(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var addresses = new List<string>();
        foreach (var address in ComHelpers.EnumerateRequired(value))
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

        var text = ComHelpers.GetRequiredString(value, "Address", "EMail", "Email", "Name", "DisplayName");
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

        foreach (var name in Type == "1" ? PropertyCandidates.Concat(ContactCandidates) : PropertyCandidates)
        {
            // VCard meldet am echten David auf Mail, Termin und Aufgabe E_FAIL statt "nicht vorhanden".
            // Bei FormattedNotes kostet ein Fehler nur die Formatierung, Notes hält den Text.
            var isReadable = name is "VCard" or "FormattedNotes"
                ? ComHelpers.TryGetProperty(_item, name, out var value)
                : ComHelpers.TryGetRequiredProperty(_item, name, out value);
            if (!isReadable || value is null)
            {
                continue;
            }

            if (name is "BodyText" or "FormattedNotes")
            {
                var plain = ResolveFormattedText(value);
                if (!string.IsNullOrWhiteSpace(plain) && plain != "System.__ComObject")
                {
                    values["BodyText"] = plain;
                }

                var html = ResolveHtmlText(value);
                if (!string.IsNullOrWhiteSpace(html) && html != "System.__ComObject")
                {
                    values["BodyHtml"] = html;
                }

                continue;
            }

            var text = name switch
            {
                "From" or "To" => ResolveAddress(value),
                "CC" or "Attendees" or "Recipients" => ResolveAddressList(value),
                _ => ComHelpers.ToScalarString(value)
            };

            if (string.IsNullOrWhiteSpace(text) || text == "System.__ComObject")
            {
                continue;
            }

            values[name] = text;
        }

        // FaxItem und VoiceItem haben kein From; der Absender steht in Caller oder Fields.
        if (HasFieldsOnly && !values.ContainsKey("From"))
        {
            var sender = values.GetValueOrDefault("Caller") ?? FieldText("FullnameFrom") ?? FieldText("SRFrom");
            if (sender is not null)
            {
                values["From"] = sender;
            }
        }

        if (!values.ContainsKey("BodyText") && values.TryGetValue("Notes", out var notes))
        {
            values["BodyText"] = notes;
        }

        // Die Priorität einer Aufgabe steht nur in Options; ein Fehler kostet nur dieses Feld.
        if (Type == "12" && ComHelpers.TryGetProperty(_item, "Options", out var options) && options is not null)
        {
            values["Priority"] = ComHelpers.GetFirstString(options, "Priority");
            ComHelpers.Release(options);
        }

        values["ArchivePath"] = ComHelpers.GetFirstString(_item, "ArchivePath", "Archive")
                                 ?? _archivePath;

        // Fields.AttachCount ist auf Termin und Aufgabe auch mit Anhängen 0: Nur die Collection zählt.
        var count = ComHelpers.GetCount(ReadAttachmentCollection());
        if (count.HasValue)
        {
            values["AttachmentCount"] = count.Value.ToString();
        }

        return values;
    }
}
