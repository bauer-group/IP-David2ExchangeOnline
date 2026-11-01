// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;
using Task = System.Threading.Tasks.Task;

namespace David2Exchange.Output;

/// <summary>
/// Echte Exchange-Online-Ausgabe über EWS app-only (Zertifikat, Impersonation).
/// Unterstützt Mail/Fax/SMS/Voice/Chat als EML, Kalender als Appointment und
/// Kontakte als Contact. Unterordner werden unter dem Zielordner nachgebildet.
/// </summary>
public sealed class EwsOutputTarget : IOutputTarget
{
    private static readonly ExtendedPropertyDefinition PidTagMessageFlags =
        new(0x0E07, MapiPropertyType.Integer);

    private static readonly ExtendedPropertyDefinition PidTagMessageDeliveryTime =
        new(0x0E06, MapiPropertyType.SystemTime);

    private static readonly ExtendedPropertyDefinition PidTagClientSubmitTime =
        new(0x0039, MapiPropertyType.SystemTime);

    private static readonly ExtendedPropertyDefinition PidTagReadReceiptRequested =
        new(0x0029, MapiPropertyType.Boolean);

    private static readonly ExtendedPropertyDefinition PidTagDeliveryReportRequested =
        new(0x0023, MapiPropertyType.Boolean);

    // Stabile Idempotenz für Kalender/Kontakte (keine Message-ID vorhanden).
    private static readonly ExtendedPropertyDefinition ItemKeyProperty = new(
        new Guid("7f9c1a52-3b6e-4f0a-9d2c-5e8b1a4c7d90"),
        "david2exchange.ItemKey",
        MapiPropertyType.String);

    private readonly EwsOutputOptions _options;
    private readonly Dictionary<string, Folder> _folderCache =
        new(StringComparer.OrdinalIgnoreCase);

    private ExchangeService? _service;
    private Folder? _rootFolder;
    private int _written;
    private int _alreadyPresent;

    public EwsOutputTarget(EwsOutputOptions options)
    {
        _options = options;
    }

    public string Name => "ews";

    public async Task OpenAsync(OutputContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var certificate = LoadCertificate(_options.CertificateThumbprint);
        var app = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithCertificate(certificate)
            .WithTenantId(_options.TenantId)
            .Build();

        var token = await app
            .AcquireTokenForClient(new[] { "https://outlook.office365.com/.default" })
            .ExecuteAsync(cancellationToken);

        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
        {
            Url = new Uri(_options.EwsUrl),
            Credentials = new OAuthCredentials(token.AccessToken),
            ImpersonatedUserId = new ImpersonatedUserId(
                ConnectingIdType.SmtpAddress,
                _options.TargetUpn),
            Timeout = 300_000
        };

        service.HttpHeaders["X-AnchorMailbox"] = _options.TargetUpn;
        service.UserAgent = "david2exchange/0.2";
        _service = service;
        _rootFolder = await EnsureFolderAsync(service, _options.FolderName, cancellationToken);

        Console.WriteLine(
            $"  EWS verbunden: {_options.TargetUpn} · Zielordner '{_options.FolderName}'");
    }

    public async Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken)
    {
        if (_service is null || _rootFolder is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        var folder = await GetFolderAsync(item.FolderPath, cancellationToken);

        switch (item.Kind)
        {
            case "calendar":
                await CreateAppointmentAsync(item, folder, cancellationToken);
                break;
            case "contact":
                await CreateContactAsync(item, folder, cancellationToken);
                break;
            default:
                await ImportMailAsync(item, folder, cancellationToken);
                break;
        }
    }

    private async Task ImportMailAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.StagedEmlPath) || !File.Exists(item.StagedEmlPath))
        {
            throw new InvalidOperationException(
                $"EWS-Ausgabe benötigt eine EML-Datei; Item {item.Id} ({item.Type}) hat keine.");
        }

        var bytes = await File.ReadAllBytesAsync(item.StagedEmlPath, cancellationToken);
        var messageId = TryReadMessageId(bytes);

        if (!string.IsNullOrWhiteSpace(messageId) &&
            await ExistsAsync(messageId, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        if (await ExistsByItemKeyAsync(item, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        var message = new EmailMessage(_service!)
        {
            MimeContent = new MimeContent("UTF-8", bytes),
            IsRead = item.IsRead ?? false
        };

        message.SetExtendedProperty(PidTagMessageFlags, item.IsRead == true ? 1 : 0);
        message.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));

        if (item.ReceivedLocal.HasValue)
        {
            message.SetExtendedProperty(
                PidTagMessageDeliveryTime,
                item.ReceivedLocal.Value.ToUniversalTime());
        }

        if (item.SentLocal.HasValue)
        {
            message.SetExtendedProperty(
                PidTagClientSubmitTime,
                item.SentLocal.Value.ToUniversalTime());
        }

        message.SetExtendedProperty(PidTagReadReceiptRequested, false);
        message.SetExtendedProperty(PidTagDeliveryReportRequested, false);

        var responses = await _service!.CreateItems(
            new[] { message },
            folder.Id,
            MessageDisposition.SaveOnly,
            null);

        if (responses.Count == 0 || responses[0].Result != ServiceResult.Success)
        {
            var error = responses.Count > 0
                ? $"{responses[0].Result}: {responses[0].ErrorMessage}"
                : "Keine ServiceResponse";
            throw new InvalidOperationException($"EWS-Import fehlgeschlagen für {item.Id}: {error}");
        }

        _written++;
    }

    private async Task CreateAppointmentAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (await ExistsByItemKeyAsync(item, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        var start = GetDate(item, "StartTime")
                    ?? item.ReceivedLocal
                    ?? DateTime.Now;
        var end = GetDate(item, "StopTime")
                  ?? start.AddMinutes(30);
        var body = GetText(item, "BodyText") ?? "";
        var attendees = GetText(item, "Attendees");
        var recurring = GetBool(item, "IsRecurring") == true;

        if (!string.IsNullOrWhiteSpace(attendees))
        {
            body += $"{Environment.NewLine}{Environment.NewLine}Teilnehmer: {attendees}";
        }

        if (recurring)
        {
            body = "[David-Serientermin – als Einzeltermin übernommen]" +
                   Environment.NewLine + Environment.NewLine + body;
        }

        var appointment = new Appointment(_service!)
        {
            Subject = item.Subject ?? "(Termin)",
            Body = new MessageBody(BodyType.Text, body),
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            End = DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
            Location = GetText(item, "Location") ?? "",
            IsAllDayEvent = GetBool(item, "AllDayEvent") == true,
            IsReminderSet = false
        };

        appointment.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));
        await appointment.Save(folder.Id);

        _written++;
    }

    private async Task CreateContactAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (await ExistsByItemKeyAsync(item, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        var displayName = GetText(item, "DisplayName")
                          ?? item.Subject
                          ?? "(Kontakt)";

        var contact = new Contact(_service!)
        {
            DisplayName = displayName,
            GivenName = GetText(item, "FirstName") ?? "",
            Surname = GetText(item, "LastName") ?? "",
            CompanyName = GetText(item, "CompanyName") ?? "",
            Department = GetText(item, "Department") ?? "",
            JobTitle = GetText(item, "JobTitle") ?? "",
            FileAs = GetText(item, "FileAs") ?? displayName
        };

        var email = GetText(item, "EMail") ?? GetText(item, "Email");
        if (!string.IsNullOrWhiteSpace(email))
        {
            contact.EmailAddresses[EmailAddressKey.EmailAddress1] = email;
        }

        var phone = GetText(item, "PhoneNumber");
        if (!string.IsNullOrWhiteSpace(phone))
        {
            contact.PhoneNumbers[PhoneNumberKey.BusinessPhone] = phone;
        }

        var fax = GetText(item, "FaxNumber");
        if (!string.IsNullOrWhiteSpace(fax))
        {
            contact.PhoneNumbers[PhoneNumberKey.BusinessFax] = fax;
        }

        var mobile = GetText(item, "SMSNumber");
        if (!string.IsNullOrWhiteSpace(mobile))
        {
            contact.PhoneNumbers[PhoneNumberKey.MobilePhone] = mobile;
        }

        var notes = GetText(item, "BodyText");
        contact.Body = new MessageBody(
            BodyType.Text,
            string.IsNullOrWhiteSpace(notes) ? "Migriert aus David." : notes);

        contact.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));
        await contact.Save(folder.Id);

        _written++;
    }

    private async Task<Folder> GetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        if (_service is null || _rootFolder is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return _rootFolder;
        }

        var normalized = folderPath.Replace('\\', '/').Trim('/');
        if (_folderCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var current = _rootFolder;
        var accumulated = "";

        foreach (var rawSegment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = SanitizeFolderName(rawSegment);
            accumulated = string.IsNullOrEmpty(accumulated)
                ? segment
                : accumulated + "/" + segment;

            if (_folderCache.TryGetValue(accumulated, out var existing))
            {
                current = existing;
                continue;
            }

            var filter = new SearchFilter.IsEqualTo(FolderSchema.DisplayName, segment);
            var view = new FolderView(10) { Traversal = FolderTraversal.Shallow };
            var found = await _service.FindFolders(current.Id, filter, view);

            Folder folder;
            if (found.Folders.Count > 0)
            {
                folder = found.Folders[0];
            }
            else
            {
                folder = new Folder(_service) { DisplayName = segment };
                await folder.Save(current.Id);
            }

            _folderCache[accumulated] = folder;
            current = folder;
        }

        return current;
    }

    private async Task<bool> ExistsByItemKeyAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        return await FindAsync(
            new SearchFilter.IsEqualTo(ItemKeyProperty, BuildItemKey(item)),
            folder,
            cancellationToken);
    }

    private async Task<bool> ExistsAsync(
        string messageId,
        Folder folder,
        CancellationToken cancellationToken)
    {
        return await FindAsync(
            new SearchFilter.IsEqualTo(EmailMessageSchema.InternetMessageId, messageId),
            folder,
            cancellationToken);
    }

    private async Task<bool> FindAsync(
        SearchFilter filter,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            return false;
        }

        try
        {
            var view = new ItemView(1)
            {
                PropertySet = new PropertySet(BasePropertySet.IdOnly)
            };

            var result = await _service.FindItems(folder.Id, filter, view);
            return result.Items.Count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  EWS-Dedupe nicht verfügbar: {ex.Message}");
            return false;
        }
    }

    private static string BuildItemKey(OutputItem item)
    {
        return $"{item.ArchivePath}|{item.Id}";
    }

    private static string? GetText(OutputItem item, string key)
    {
        return item.Properties.TryGetValue(key, out var value) &&
               !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static DateTime? GetDate(OutputItem item, string key)
    {
        var text = GetText(item, key);
        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var date)
            ? date
            : null;
    }

    private static bool? GetBool(OutputItem item, string key)
    {
        var text = GetText(item, key);
        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        return text == "1" ? true : text == "0" ? false : null;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(invalid.Contains(character) || char.IsControl(character)
                ? '_'
                : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..")
        {
            result = "_";
        }

        return result.Length <= 200 ? result : result[..200];
    }

    public Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"  EWS: {_written} Items importiert, {_alreadyPresent} bereits vorhanden (übersprungen).");
        return Task.CompletedTask;
    }

    private static string? TryReadMessageId(byte[] eml)
    {
        var prefix = Encoding.ASCII.GetString(eml, 0, Math.Min(eml.Length, 32_768));
        var match = Regex.Match(
            prefix,
            @"(?im)^Message-ID:\s*(<[^>]+>)",
            RegexOptions.CultureInvariant);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public ValueTask DisposeAsync()
    {
        _folderCache.Clear();
        _rootFolder = null;
        _service = null;
        return ValueTask.CompletedTask;
    }

    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var normalized = thumbprint.Replace(" ", "", StringComparison.Ordinal);
        var certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            normalized,
            validOnly: false);

        if (certificates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Zertifikat mit Thumbprint '{thumbprint}' wurde in CurrentUser\\My nicht gefunden.");
        }

        return certificates[0];
    }

    private static async Task<Folder> EnsureFolderAsync(
        ExchangeService service,
        string folderName,
        CancellationToken cancellationToken)
    {
        var root = await Folder.Bind(service, WellKnownFolderName.MsgFolderRoot);
        var filter = new SearchFilter.IsEqualTo(FolderSchema.DisplayName, folderName);
        var view = new FolderView(10) { Traversal = FolderTraversal.Shallow };

        var found = await service.FindFolders(root.Id, filter, view);
        if (found.Folders.Count > 0)
        {
            return found.Folders[0];
        }

        var folder = new Folder(service) { DisplayName = folderName };
        await folder.Save(root.Id);
        return folder;
    }
}
