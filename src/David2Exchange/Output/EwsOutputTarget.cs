// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;
using EwsTask = Microsoft.Exchange.WebServices.Data.Task;
using EwsTaskStatus = Microsoft.Exchange.WebServices.Data.TaskStatus;
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

    private const int BatchSize = 10;
    private const int MaxRetries = 5;
    private const int BaseRetryDelayMs = 1_000;
    private const int MaxRetryDelayMs = 30_000;

    private readonly EwsOutputOptions _options;
    private readonly Dictionary<string, Folder> _folderCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WellKnownFolderName, Folder> _wellKnownFolders = new();

    private ExchangeService? _service;
    private IReadOnlyList<ExchangeService> _services = Array.Empty<ExchangeService>();
    private int _serviceIndex;
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

        ExchangeService CreateService()
        {
            var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
            {
                Url = new Uri(_options.EwsUrl),
                Credentials = new OAuthCredentials(token.AccessToken),
                ImpersonatedUserId = new ImpersonatedUserId(
                    ConnectingIdType.SmtpAddress,
                    _options.TargetUpn),
                Timeout = 60_000
            };

            service.HttpHeaders["X-AnchorMailbox"] = _options.TargetUpn;
            service.UserAgent = "david2exchange/0.2";
            return service;
        }

        _services = Enumerable.Range(0, 8).Select(_ => CreateService()).ToList();
        _service = _services[0];

        Console.WriteLine(
            $"  EWS verbunden: {_options.TargetUpn} · Ziel: bestehende Ordner (Inbox, Sent Items, Kalender, Kontakte)");
    }

    public async Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        var folder = await GetTargetFolderAsync(item, cancellationToken);

        switch (item.Kind)
        {
            case "calendar":
                await CreateAppointmentAsync(item, folder, cancellationToken);
                break;
            case "contact":
                await CreateContactAsync(item, folder, cancellationToken);
                break;
            case "task":
                await CreateTaskAsync(item, folder, cancellationToken);
                break;
            default:
                await ImportMailAsync(item, folder, cancellationToken);
                break;
        }
    }

    public async Task<IReadOnlyList<OutputResult>> WriteBatchAsync(
        IReadOnlyList<OutputItem> items,
        CancellationToken cancellationToken)
    {
        if (_service is null || _services.Count == 0)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        var results = new List<OutputResult>(items.Count);
        var mailGroups = new Dictionary<string, (Folder Folder, List<OutputItem> Items)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.Kind != "mail" || item.CheckExisting)
            {
                try
                {
                    await WriteItemAsync(item, cancellationToken);
                    results.Add(new OutputResult(item, true, null));
                }
                catch (Exception ex)
                {
                    results.Add(new OutputResult(item, false, ex.Message));
                }

                continue;
            }

            var folder = await GetTargetFolderAsync(item, cancellationToken);
            var folderKey = $"{folder.Id.UniqueId}|{folder.DisplayName}";
            if (!mailGroups.TryGetValue(folderKey, out var group))
            {
                group = (folder, new List<OutputItem>());
                mailGroups[folderKey] = group;
                Console.WriteLine($"  ZIEL {item.FolderPath,-20} -> {folder.DisplayName}");
            }

            group.Items.Add(item);
        }

        foreach (var group in mailGroups.Values)
        {
            var chunks = group.Items.Chunk(BatchSize).ToList();
            var batchResults = new ConcurrentBag<OutputResult>();

            await Parallel.ForEachAsync(
                chunks,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 8,
                    CancellationToken = cancellationToken
                },
                async (chunk, token) =>
                {
                    var service = _services[
                        (int)((uint)Interlocked.Increment(ref _serviceIndex) % _services.Count)];
                    var chunkResults = await CreateMailBatchAsync(
                        service,
                        group.Folder,
                        chunk,
                        token);

                    foreach (var result in chunkResults)
                    {
                        batchResults.Add(result);
                    }
                });

            results.AddRange(batchResults);
        }

        return results;
    }

    private async Task<IReadOnlyList<OutputResult>> CreateMailBatchAsync(
        ExchangeService service,
        Folder folder,
        IReadOnlyList<OutputItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<(OutputItem Item, EmailMessage Message, string? MessageId)>(items.Count);
        var results = new List<OutputResult>(items.Count);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.StagedEmlPath) ||
                !File.Exists(item.StagedEmlPath))
            {
                results.Add(new OutputResult(
                    item,
                    false,
                    $"EWS-Ausgabe benötigt eine EML-Datei; Item {item.Id} hat keine."));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(item.StagedEmlPath, cancellationToken);
            var messageId = TryReadMessageId(bytes);
            var message = new EmailMessage(service)
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
            pending.Add((item, message, messageId));
        }

        if (pending.Count == 0)
        {
            return results;
        }

        try
        {
            var responses = await CreateItemsWithRetryAsync(
                service,
                pending.Select(entry => entry.Message).ToList(),
                folder.Id,
                cancellationToken);

            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i].Item;
                if (i < responses.Count && responses[i].Result == ServiceResult.Success)
                {
                    var verified = true;
                    if (!string.IsNullOrWhiteSpace(pending[i].MessageId))
                    {
                        try
                        {
                            verified = await ExistsWithRetryAsync(
                                pending[i].MessageId!,
                                folder,
                                cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(
                                $"  WARNUNG Batch-Verifikation {item.Id}: {ex.Message}");
                        }
                    }

                    if (verified)
                    {
                        Interlocked.Increment(ref _written);
                        results.Add(new OutputResult(item, true, null));
                        continue;
                    }
                }

                var batchError = i < responses.Count
                    ? $"{responses[i].Result}: {responses[i].ErrorMessage}"
                    : "Keine ServiceResponse";

                // Ein fehlgeschlagener Batch darf keine Items verschlucken.
                // Deshalb wird das Item einzeln nachgezogen.
                try
                {
                    await WriteItemAsync(item, cancellationToken);
                    results.Add(new OutputResult(item, true, null));
                }
                catch (Exception ex)
                {
                    results.Add(new OutputResult(
                        item,
                        false,
                        $"{batchError}; Einzelimport: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in pending)
            {
                try
                {
                    await WriteItemAsync(entry.Item, cancellationToken);
                    results.Add(new OutputResult(entry.Item, true, null));
                }
                catch (Exception itemEx)
                {
                    results.Add(new OutputResult(
                        entry.Item,
                        false,
                        $"Batch: {ex.Message}; Einzelimport: {itemEx.Message}"));
                }
            }
        }

        return results;
    }

    private async Task SaveWithRetryAsync(
        Func<Task> save,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await save();
                return;
            }
            catch (Exception ex) when (
                attempt < MaxRetries &&
                IsTransientEwsError(ex) &&
                !cancellationToken.IsCancellationRequested)
            {
                var delay = GetRetryDelay(ex, attempt);
                Console.Error.WriteLine(
                    $"  EWS-Throttling: {ex.Message} · Retry {attempt + 1}/{MaxRetries} in {delay} ms");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<ServiceResponseCollection<ServiceResponse>> CreateItemsWithRetryAsync(
        ExchangeService service,
        IReadOnlyList<EmailMessage> messages,
        FolderId folderId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await service.CreateItems(
                    messages,
                    folderId,
                    MessageDisposition.SaveOnly,
                    null);
            }
            catch (Exception ex) when (
                attempt < MaxRetries &&
                IsTransientEwsError(ex) &&
                !cancellationToken.IsCancellationRequested)
            {
                var delay = GetRetryDelay(ex, attempt);
                Console.Error.WriteLine(
                    $"  EWS-Throttling: {ex.Message} · Retry {attempt + 1}/{MaxRetries} in {delay} ms");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransientEwsError(Exception exception)
    {
        if (exception is ServiceResponseException responseException)
        {
            return responseException.ErrorCode is
                ServiceError.ErrorServerBusy or
                ServiceError.ErrorTimeoutExpired or
                ServiceError.ErrorBatchProcessingStopped or
                ServiceError.ErrorInternalServerTransientError or
                ServiceError.ErrorInternalServerError;
        }

        return exception is ServiceRequestException or
            HttpRequestException or
            TimeoutException;
    }

    private static int GetRetryDelay(Exception exception, int attempt)
    {
        if (exception is ServiceResponseException responseException &&
            responseException.Response?.ErrorDetails is { } details &&
            details.TryGetValue("BackOffMilliseconds", out var backOffText) &&
            int.TryParse(backOffText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var backOff))
        {
            return Math.Clamp(backOff, BaseRetryDelayMs, MaxRetryDelayMs);
        }

        var exponential = BaseRetryDelayMs * (1 << Math.Min(attempt, 5));
        var jitter = Random.Shared.Next(0, 500);
        return Math.Min(MaxRetryDelayMs, exponential + jitter);
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

        if (item.CheckExisting &&
            !string.IsNullOrWhiteSpace(messageId) &&
            await ExistsAsync(messageId, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        if (item.CheckExisting &&
            await ExistsByItemKeyAsync(item, folder, cancellationToken))
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

        var responses = await CreateItemsWithRetryAsync(
            _service!,
            new[] { message },
            folder.Id,
            cancellationToken);

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
        if (item.CheckExisting &&
            await ExistsByItemKeyAsync(item, folder, cancellationToken))
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
        var html = GetText(item, "BodyHtml");

        var appointment = new Appointment(_service!)
        {
            Subject = item.Subject ?? "(Termin)",
            Body = string.IsNullOrWhiteSpace(html)
                ? new MessageBody(BodyType.Text, body)
                : new MessageBody(BodyType.HTML, html),
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            End = DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
            Location = GetText(item, "Location") ?? "",
            IsAllDayEvent = GetBool(item, "AllDayEvent") == true,
            IsReminderSet = false
        };

        appointment.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));
        try
        {
            await SaveWithRetryAsync(
                () => appointment.Save(folder.Id),
                cancellationToken);
        }
        catch (ServiceResponseException)
        {
            var calendarRoot = await BindWellKnownAsync(
                WellKnownFolderName.Calendar,
                cancellationToken);
            if (folder.Id.UniqueId == calendarRoot.Id.UniqueId)
            {
                throw;
            }

            await SaveWithRetryAsync(
                () => appointment.Save(calendarRoot.Id),
                cancellationToken);
        }

        _written++;
    }

    private async Task CreateContactAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (item.CheckExisting &&
            await ExistsByItemKeyAsync(item, folder, cancellationToken))
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
        var html = GetText(item, "BodyHtml");
        contact.Body = string.IsNullOrWhiteSpace(html)
            ? new MessageBody(BodyType.Text, notes ?? "")
            : new MessageBody(BodyType.HTML, html);

        contact.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));
        try
        {
            await SaveWithRetryAsync(
                () => contact.Save(folder.Id),
                cancellationToken);
        }
        catch (ServiceResponseException)
        {
            var contactsRoot = await BindWellKnownAsync(
                WellKnownFolderName.Contacts,
                cancellationToken);
            if (folder.Id.UniqueId == contactsRoot.Id.UniqueId)
            {
                throw;
            }

            await SaveWithRetryAsync(
                () => contact.Save(contactsRoot.Id),
                cancellationToken);
        }

        _written++;
    }

    private async Task CreateTaskAsync(
        OutputItem item,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (item.CheckExisting &&
            await ExistsByItemKeyAsync(item, folder, cancellationToken))
        {
            _alreadyPresent++;
            return;
        }

        var body = GetText(item, "BodyText") ?? "";
        var html = GetText(item, "BodyHtml");
        var start = GetDate(item, "StartTime");
        var due = GetDate(item, "DueDate") ?? GetDate(item, "StopTime");
        var status = GetText(item, "Status");
        var priority = GetText(item, "Priority");
        var percent = GetInt(item, "PercentComplete");
        var completed = GetBool(item, "Completed") == true ||
                        GetBool(item, "IsComplete") == true;

        var task = new EwsTask(_service!)
        {
            Subject = item.Subject ?? "(Aufgabe)",
            Body = string.IsNullOrWhiteSpace(html)
                ? new MessageBody(BodyType.Text, body)
                : new MessageBody(BodyType.HTML, html),
            StartDate = start,
            DueDate = due,
            Status = completed ? EwsTaskStatus.Completed : MapTaskStatus(status),
            Importance = MapImportance(priority),
            PercentComplete = percent ?? (completed ? 100 : 0),
            IsReminderSet = false
        };

        task.SetExtendedProperty(ItemKeyProperty, BuildItemKey(item));
        try
        {
            await SaveWithRetryAsync(
                () => task.Save(folder.Id),
                cancellationToken);
        }
        catch (ServiceResponseException)
        {
            var tasksRoot = await BindWellKnownAsync(
                WellKnownFolderName.Tasks,
                cancellationToken);
            if (folder.Id.UniqueId == tasksRoot.Id.UniqueId)
            {
                throw;
            }

            await SaveWithRetryAsync(
                () => task.Save(tasksRoot.Id),
                cancellationToken);
        }

        _written++;
    }

    private static EwsTaskStatus MapTaskStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "completed" or "erledigt" or "abgeschlossen" => EwsTaskStatus.Completed,
            "inprogress" or "in progress" or "in bearbeitung" => EwsTaskStatus.InProgress,
            "waiting" or "waitingonothers" or "wartet" => EwsTaskStatus.WaitingOnOthers,
            "deferred" or "zurückgestellt" => EwsTaskStatus.Deferred,
            _ => EwsTaskStatus.NotStarted
        };
    }

    private static Importance MapImportance(string? priority)
    {
        return priority?.Trim().ToLowerInvariant() switch
        {
            "high" or "hoch" or "1" => Importance.High,
            "low" or "niedrig" or "3" => Importance.Low,
            _ => Importance.Normal
        };
    }

    private static int? GetInt(OutputItem item, string key)
    {
        var text = GetText(item, key);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private async Task<Folder> GetTargetFolderAsync(
        OutputItem item,
        CancellationToken cancellationToken)
    {
        var segments = SanitizeSegments(item.FolderPath);

        if (item.Kind == "calendar")
        {
            var calendar = await BindWellKnownAsync(
                WellKnownFolderName.Calendar,
                cancellationToken);
            return await EnsureSubfoldersAsync(
                calendar,
                RemoveRoot(segments, "cal", "calendar", "kalender"),
                "calendar",
                cancellationToken,
                "IPF.Appointment");
        }

        if (item.Kind == "contact")
        {
            var contacts = await BindWellKnownAsync(
                WellKnownFolderName.Contacts,
                cancellationToken);
            return await EnsureSubfoldersAsync(
                contacts,
                RemoveRoot(segments, "address", "addresses", "contacts", "adressen", "kontakte"),
                "contacts",
                cancellationToken,
                "IPF.Contact");
        }

        if (item.Kind == "task")
        {
            var tasks = await BindWellKnownAsync(
                WellKnownFolderName.Tasks,
                cancellationToken);
            return await EnsureSubfoldersAsync(
                tasks,
                RemoveRoot(segments, "todo", "tasks", "aufgaben"),
                "tasks",
                cancellationToken,
                "IPF.Task");
        }

        var inbox = await BindWellKnownAsync(
            WellKnownFolderName.Inbox,
            cancellationToken);

        // Die Wurzel zusätzlich aus dem echten David-Archivpfad bestimmen.
        // Das schützt gegen falsche/leere FolderPath-Werte aus alten Manifesten.
        var archiveRoot = FindArchiveRoot(item.ArchivePath);
        if (archiveRoot is not null)
        {
            var root = await BindWellKnownAsync(archiveRoot.Value, cancellationToken);
            var subSegments = segments.ToList();
            while (subSegments.Count > 0 &&
                   TryMapMailRoot(subSegments[0], out _))
            {
                subSegments.RemoveAt(0);
            }

            return await EnsureSubfoldersAsync(
                root,
                subSegments,
                archiveRoot.Value.ToString(),
                cancellationToken);
        }

        if (segments.Count == 0)
        {
            return inbox;
        }

        if (TryMapMailRoot(segments[0], out var mapped))
        {
            var root = await BindWellKnownAsync(mapped, cancellationToken);
            return await EnsureSubfoldersAsync(
                root,
                segments.Skip(1).ToList(),
                mapped.ToString(),
                cancellationToken);
        }

        // Unbekannte David-Wurzel (z. B. Unverteilt): unter Inbox einsortieren.
        return await EnsureSubfoldersAsync(
            inbox,
            segments,
            "Inbox",
            cancellationToken);
    }

    private async Task<Folder> EnsureSubfoldersAsync(
        Folder root,
        IReadOnlyList<string> segments,
        string cachePrefix,
        CancellationToken cancellationToken,
        string? folderClass = null)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        var current = root;
        var accumulated = cachePrefix;

        foreach (var segment in segments)
        {
            accumulated += "/" + segment;
            if (_folderCache.TryGetValue(accumulated, out var cached))
            {
                current = cached;
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
                if (!string.IsNullOrWhiteSpace(folderClass))
                {
                    folder.FolderClass = folderClass;
                }

                await folder.Save(current.Id);
            }

            _folderCache[accumulated] = folder;
            current = folder;
        }

        return current;
    }

    private async Task<Folder> BindWellKnownAsync(
        WellKnownFolderName name,
        CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        if (_wellKnownFolders.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var folder = await Folder.Bind(_service, name);
        _wellKnownFolders[name] = folder;
        return folder;
    }

    private static List<string> SanitizeSegments(string folderPath)
    {
        return folderPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeFolderName)
            .ToList();
    }

    private static List<string> RemoveRoot(List<string> segments, params string[] roots)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        return roots.Any(root => segments[0].Equals(root, StringComparison.OrdinalIgnoreCase))
            ? segments.Skip(1).ToList()
            : segments;
    }

    private static WellKnownFolderName? FindArchiveRoot(string archivePath)
    {
        foreach (var segment in archivePath
                     .Replace('/', '\\')
                     .Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryMapMailRoot(segment, out var folder))
            {
                return folder;
            }
        }

        return null;
    }

    private static bool TryMapMailRoot(string segment, out WellKnownFolderName folder)
    {
        switch (segment.ToLowerInvariant())
        {
            case "in":
            case "inbox":
            case "eingang":
                folder = WellKnownFolderName.Inbox;
                return true;
            case "out":
            case "sent":
            case "sentitems":
            case "ausgang":
            case "gesendet":
                folder = WellKnownFolderName.SentItems;
                return true;
            case "draft":
            case "drafts":
            case "entwurf":
            case "entwürfe":
                folder = WellKnownFolderName.Drafts;
                return true;
            case "trash":
            case "deleted":
            case "deleteditems":
            case "gelöscht":
            case "papierkorb":
                folder = WellKnownFolderName.DeletedItems;
                return true;
            case "spam":
            case "junk":
            case "junkemail":
                folder = WellKnownFolderName.JunkEmail;
                return true;
            case "cal":
            case "calendar":
            case "kalender":
                folder = WellKnownFolderName.Calendar;
                return true;
            case "address":
            case "contacts":
            case "adressen":
            case "kontakte":
                folder = WellKnownFolderName.Contacts;
                return true;
            case "todo":
            case "tasks":
            case "aufgaben":
                folder = WellKnownFolderName.Tasks;
                return true;
            default:
                folder = WellKnownFolderName.Inbox;
                return false;
        }
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

    private async Task<bool> ExistsWithRetryAsync(
        string messageId,
        Folder folder,
        CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var filter = new SearchFilter.IsEqualTo(
                    EmailMessageSchema.InternetMessageId,
                    messageId);
                var view = new ItemView(1)
                {
                    PropertySet = new PropertySet(BasePropertySet.IdOnly)
                };

                var result = await _service.FindItems(folder.Id, filter, view);
                return result.Items.Count > 0;
            }
            catch (Exception ex) when (
                attempt < MaxRetries &&
                IsTransientEwsError(ex) &&
                !cancellationToken.IsCancellationRequested)
            {
                var delay = GetRetryDelay(ex, attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }
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
        _wellKnownFolders.Clear();
        _services = Array.Empty<ExchangeService>();
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


}
