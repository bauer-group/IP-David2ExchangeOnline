// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using David2Exchange.Infrastructure;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;
using EwsTask = Microsoft.Exchange.WebServices.Data.Task;
using EwsTaskStatus = Microsoft.Exchange.WebServices.Data.TaskStatus;
using Task = System.Threading.Tasks.Task;

namespace David2Exchange.Output;

/// <summary>
/// Exchange-Online-Ausgabe über EWS app-only (Zertifikat, Impersonation). Mail/Fax/SMS/Voice/Chat
/// werden als MIME per CreateItems im Batch gespeichert, Termine, Kontakte und Aufgaben einzeln.
///
/// <para>
/// Sichere Übertragung: Jeder Worker mietet eine eigene <see cref="ExchangeService"/>-Instanz.
/// Nach einem Fehler mit unbekanntem Ausgang wird vor dem erneuten Senden über die eigene
/// Kennung (<see cref="ItemKeyProperty"/>) im Zielordner gesucht. Jedes gespeicherte Item wird
/// über seine ItemId samt Elternordner gegengeprüft, statt dem Server-Erfolg allein zu trauen.
/// </para>
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

    // Stabile Kennung jedes migrierten Objekts im Ziel.
    private static readonly ExtendedPropertyDefinition ItemKeyProperty = new(
        new Guid("7f9c1a52-3b6e-4f0a-9d2c-5e8b1a4c7d90"),
        "david2exchange.ItemKey",
        MapiPropertyType.String);

    private static readonly PropertySet LookupProperties = new(BasePropertySet.IdOnly, ItemKeyProperty);

    private static readonly PropertySet VerifyProperties =
        new(BasePropertySet.IdOnly, ItemSchema.ParentFolderId, ItemKeyProperty);

    // Importe früherer Versionen können fremde Inhalte tragen und zählen nicht als vorhanden.
    private const string ItemKeyVersion = "v2|";

    private const string PolicyReasonHeader = "X-EWS-Policy-Reason";
    private const int MaxAttempts = 8;
    private const int MinimumTimeoutMs = 120_000;
    private const int MaximumTimeoutMs = 900_000;
    private const int BytesPerSecondAssumed = 128 * 1024;

    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumServerBackOff = TimeSpan.FromMinutes(10);

    // Exchange kann einen Batch nach einem Verbindungsabbruch noch bis zum Ablauf des
    // dokumentierten Limits von einer Minute verarbeiten.
    private static readonly TimeSpan ServerSettleTime = TimeSpan.FromSeconds(65);

    private readonly EwsOutputOptions _options;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, FolderId> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WellKnownFolderName, FolderId> _wellKnownFolders = new();
    private readonly SemaphoreSlim _folderLock = new(1, 1);
    private readonly Channel<ServiceLease> _workers = Channel.CreateUnbounded<ServiceLease>();

    private Func<bool, CancellationToken, Task<string>>? _tokenProvider;
    private ServiceLease? _folderLease;
    private long _resumeAtMilliseconds;
    private int _written;
    private int _alreadyPresent;

    public EwsOutputTarget(EwsOutputOptions options)
        : this(options, null, null)
    {
    }

    /// <param name="tokenProvider">Liefert das Zugriffstoken; true erzwingt ein neues.</param>
    /// <param name="delay">Wartefunktion für Wiederholungen.</param>
    internal EwsOutputTarget(
        EwsOutputOptions options,
        Func<bool, CancellationToken, Task<string>>? tokenProvider,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _options = options;
        _tokenProvider = tokenProvider;
        _delay = delay ?? Task.Delay;
        _timeZone = DavidTimeZone.Resolve(options.TimeZone);
    }

    public string Name => OutputTargetFactory.TargetName("ews", _options.TargetUpn);

    public async Task OpenAsync(OutputContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _tokenProvider ??= CreateTokenProvider();

        _folderLease = new ServiceLease(CreateService());
        await PrepareAsync(_folderLease, forceRefresh: false, cancellationToken);

        // Früh scheitern: Anmeldung, Sperren und Impersonation noch vor dem ersten David-Export prüfen.
        await BindWellKnownAsync(WellKnownFolderName.Inbox, cancellationToken);

        for (var i = 0; i < _options.Workers; i++)
        {
            _workers.Writer.TryWrite(new ServiceLease(CreateService()));
        }

        Console.WriteLine($"  EWS verbunden: {_options.TargetUpn}");
    }

    public async Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken)
    {
        var results = await WriteBatchAsync(new[] { item }, cancellationToken);
        var result = results[0];
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    public async Task<IReadOnlyList<OutputResult>> WriteBatchAsync(
        IReadOnlyList<OutputItem> items,
        CancellationToken cancellationToken)
    {
        if (_folderLease is null)
        {
            throw new InvalidOperationException("EwsOutputTarget wurde nicht geöffnet.");
        }

        var results = new List<OutputResult>(items.Count);
        var mimeGroups = new Dictionary<string, (FolderId Folder, List<OutputItem> Items)>(StringComparer.Ordinal);
        var lease = await _workers.Reader.ReadAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                FolderId folder;
                try
                {
                    folder = await GetTargetFolderAsync(item, cancellationToken);
                }
                catch (Exception ex) when (IsItemLevelFailure(ex))
                {
                    results.Add(Failed(item, $"Zielordner nicht verfügbar: {Describe(ex)}", retryable: true));
                    continue;
                }

                if (IsMime(item))
                {
                    if (!mimeGroups.TryGetValue(folder.UniqueId, out var group))
                    {
                        group = (folder, new List<OutputItem>());
                        mimeGroups[folder.UniqueId] = group;
                    }

                    group.Items.Add(item);
                }
                else
                {
                    results.Add(await WriteSingleAsync(lease, item, folder, cancellationToken));
                }
            }

            foreach (var group in mimeGroups.Values)
            {
                var pending = group.Items.Select(item => new PendingItem(item)).ToList();
                await ImportMimeAsync(lease, group.Folder, pending, results, cancellationToken);
            }

            return results;
        }
        finally
        {
            _workers.Writer.TryWrite(lease);
        }
    }

    public Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"  EWS: {Volatile.Read(ref _written)} Items importiert, " +
            $"{Volatile.Read(ref _alreadyPresent)} bereits vorhanden.");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _workers.Writer.TryComplete();
        _folderLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsMime(OutputItem item)
    {
        return item.Kind is not ("calendar" or "contact" or "task");
    }

    /// <summary>
    /// Speichert MIME-Items in einen Ordner: nachsehen falls nötig, senden, Antworten einzeln
    /// auswerten, Gespeichertes prüfen, Rest nach Wartezeit wiederholen.
    /// </summary>
    private async Task ImportMimeAsync(
        ServiceLease lease,
        FolderId folder,
        List<PendingItem> pending,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        var tokenRefreshed = false;

        for (var attempt = 0; pending.Count > 0 && attempt < MaxAttempts; attempt++)
        {
            try
            {
                await ResolveMaybeExistingAsync(lease, folder, pending, results, cancellationToken);
            }
            catch (ServiceXmlSerializationException ex)
            {
                // Eine unzulässige Kennung: Die Suche verließ den Rechner nie und scheitert immer gleich.
                // Sie steckt im Filter aller Items, also einzeln nachsehen, damit die übrigen durchkommen.
                if (pending.Count > 1)
                {
                    await IsolateAsync(lease, folder, pending, results, cancellationToken);
                    return;
                }

                pending.ForEach(entry => results.Add(Failed(entry.Item, Describe(ex), retryable: false)));
                pending.Clear();
                return;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                // Nachsehen war nicht möglich: Der Ausgang bleibt offen, also nicht senden.
                pending.ForEach(entry => entry.LastError = $"Prüfung im Ziel fehlgeschlagen: {Describe(ex)}");
                await BackOffAsync(ex, attempt, cancellationToken);
                continue;
            }

            if (pending.Count == 0)
            {
                break;
            }

            var messages = await BuildMessagesAsync(lease.Service, pending, results, cancellationToken);
            if (messages.Count == 0)
            {
                break;
            }

            await PrepareAsync(lease, forceRefresh: false, cancellationToken);
            lease.Service.Timeout = TimeoutFor(messages.Sum(entry => entry.Bytes));

            ServiceResponseCollection<ServiceResponse>? responses = null;
            Exception? failure = null;
            try
            {
                await WaitForGateAsync(cancellationToken);
                responses = await lease.Service.CreateItems(
                    messages.Select(entry => (Item)entry.Message),
                    folder,
                    MessageDisposition.SaveOnly,
                    null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var saved = new List<SendEntry>();
            TimeSpan? serverBackOff = null;

            for (var i = 0; i < messages.Count; i++)
            {
                var entry = messages[i];
                if (!entry.Message.IsNew)
                {
                    // Auch bei einer teilweise gelesenen Antwort: Dieses Item hat eine Id.
                    saved.Add(entry);
                    continue;
                }

                if (responses is null || i >= responses.Count)
                {
                    continue;
                }

                var response = responses[i];
                if (response.Result == ServiceResult.Success)
                {
                    // Erfolg ohne ItemId ist nicht belegt: vor dem nächsten Senden nachsehen.
                    entry.Pending.MayExist = true;
                    entry.Pending.LastError = "Server meldete Erfolg ohne ItemId.";
                    continue;
                }

                var kind = EwsFailure.Classify(response.ErrorCode);
                entry.Pending.LastError = $"{response.ErrorCode}: {response.ErrorMessage}";
                serverBackOff = EwsFailure.ServerBackOff(response) ?? serverBackOff;

                switch (kind)
                {
                    case EwsFailureKind.Permanent:
                        pending.Remove(entry.Pending);
                        results.Add(Failed(entry.Pending.Item, entry.Pending.LastError, retryable: false));
                        break;
                    case EwsFailureKind.Ambiguous:
                        entry.Pending.MayExist = true;
                        break;
                    case EwsFailureKind.AbortMailbox:
                    case EwsFailureKind.AbortRun:
                        throw new OutputAbortedException(
                            $"{_options.TargetUpn}: {entry.Pending.LastError}",
                            kind == EwsFailureKind.AbortRun);
                }
            }

            foreach (var entry in saved)
            {
                pending.Remove(entry.Pending);
            }

            await VerifyAsync(lease, folder, saved, results, cancellationToken);

            if (failure is not null)
            {
                var kind = EwsFailure.Classify(failure);
                var status = EwsFailure.HttpStatus(failure);
                if (status == EwsFailure.Unauthorized && !tokenRefreshed)
                {
                    // Das Token kann kurz vor Ablauf bereits ungültig geworden sein.
                    tokenRefreshed = true;
                    await PrepareAsync(lease, forceRefresh: true, cancellationToken);
                    continue;
                }

                switch (kind)
                {
                    case EwsFailureKind.AbortMailbox:
                    case EwsFailureKind.AbortRun:
                        throw new OutputAbortedException(AbortMessage(lease, failure), kind == EwsFailureKind.AbortRun, failure);

                    case EwsFailureKind.Permanent when pending.Count > 1:
                        // Ein einzelnes Item kann die ganze Anfrage ablehnen lassen. Die Antwort sagt
                        // aber nicht, was davor gespeichert wurde: erst nachsehen, dann einzeln senden.
                        pending.ForEach(entry => entry.MayExist = true);
                        await IsolateAsync(lease, folder, pending, results, cancellationToken);
                        return;

                    case EwsFailureKind.Permanent:
                        pending.ForEach(entry =>
                            results.Add(Failed(entry.Item, Describe(failure), retryable: false)));
                        pending.Clear();
                        return;

                    case EwsFailureKind.Ambiguous:
                        pending.ForEach(entry => entry.MayExist = true);
                        break;
                }

                pending.ForEach(entry => entry.LastError = Describe(failure));
                await WaitAfterFailureAsync(failure, kind, attempt, cancellationToken);
            }
            else if (pending.Count > 0)
            {
                await BackOffAsync(serverBackOff, attempt, cancellationToken);
            }
        }

        foreach (var entry in pending)
        {
            results.Add(Failed(entry.Item, entry.LastError ?? "Nicht übertragen.", retryable: true));
        }
    }

    private async Task IsolateAsync(
        ServiceLease lease,
        FolderId folder,
        List<PendingItem> pending,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        foreach (var entry in pending)
        {
            await ImportMimeAsync(lease, folder, new List<PendingItem> { entry }, results, cancellationToken);
        }
    }

    /// <summary>Items mit möglichem früherem Versuch werden im Zielordner gesucht.</summary>
    private async Task ResolveMaybeExistingAsync(
        ServiceLease lease,
        FolderId folder,
        List<PendingItem> pending,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        var lookups = pending.Where(entry => entry.MayExist).ToList();
        if (lookups.Count == 0)
        {
            return;
        }

        var found = await FindExistingAsync(
            lease,
            folder,
            lookups.Select(entry => TargetKey(entry.Item)).ToList(),
            cancellationToken);

        foreach (var entry in lookups)
        {
            if (found.TryGetValue(TargetKey(entry.Item), out var existingId))
            {
                pending.Remove(entry);
                Interlocked.Increment(ref _alreadyPresent);
                results.Add(new OutputResult(entry.Item, true, null, AlreadyPresent: true, TargetId: existingId));
            }
            else
            {
                entry.MayExist = false;
            }
        }
    }

    private async Task<Dictionary<string, string>> FindExistingAsync(
        ServiceLease lease,
        FolderId folder,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        SearchFilter filter = keys.Count == 1
            ? new SearchFilter.IsEqualTo(ItemKeyProperty, keys[0])
            : new SearchFilter.SearchFilterCollection(
                LogicalOperator.Or,
                keys.Select(key => (SearchFilter)new SearchFilter.IsEqualTo(ItemKeyProperty, key)));
        var view = new ItemView(keys.Count * 2) { PropertySet = LookupProperties };
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        while (true)
        {
            var result = await CallAsync(
                lease,
                service => service.FindItems(folder, filter, view),
                cancellationToken);

            foreach (var item in result.Items)
            {
                if (item.TryGetProperty(ItemKeyProperty, out string? key) && key is not null)
                {
                    found[key] = item.Id.UniqueId;
                }
            }

            // EWS darf unter Last Teilergebnisse liefern; was fehlt, gilt erst danach als nicht vorhanden.
            if (!result.MoreAvailable || found.Count == keys.Count)
            {
                return found;
            }

            if (result.Items.Count == 0)
            {
                throw new TimeoutException("EWS meldet weitere Treffer, liefert aber keine; die Suche im Ziel ist nicht schlüssig.");
            }

            view.Offset += result.Items.Count;
        }
    }

    private async Task<List<SendEntry>> BuildMessagesAsync(
        ExchangeService service,
        List<PendingItem> pending,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        var messages = new List<SendEntry>(pending.Count);
        foreach (var entry in pending.ToList())
        {
            var path = entry.Item.StagedEmlPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                pending.Remove(entry);
                results.Add(Failed(
                    entry.Item,
                    $"EWS-Ausgabe benötigt eine EML-Datei; Item {entry.Item.Id} hat keine.",
                    retryable: false));
                continue;
            }

            // Je Versuch neu aufbauen: Eine einmal gespeicherte Instanz ist nicht mehr "new".
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Eine gesperrte Datei darf die übrigen Items des Batches nicht aufhalten.
                pending.Remove(entry);
                results.Add(Failed(entry.Item, $"EML-Datei nicht lesbar: {Describe(ex)}", retryable: true));
                continue;
            }

            messages.Add(new SendEntry(entry, CreateMessage(service, entry.Item, bytes), bytes.Length));
        }

        return messages;
    }

    private EmailMessage CreateMessage(ExchangeService service, OutputItem item, byte[] bytes)
    {
        var message = new EmailMessage(service)
        {
            MimeContent = new MimeContent("UTF-8", bytes),
            IsRead = item.IsRead ?? false
        };

        // Ohne PidTagMessageFlags legt Exchange MIME-Importe als Entwurf an.
        message.SetExtendedProperty(PidTagMessageFlags, item.IsRead == true ? 1 : 0);
        message.SetExtendedProperty(ItemKeyProperty, TargetKey(item));

        var delivered = item.ReceivedLocal ?? item.SentLocal;
        if (delivered.HasValue)
        {
            message.SetExtendedProperty(PidTagMessageDeliveryTime, ToUtc(delivered.Value));
        }

        var submitted = item.SentLocal ?? item.ReceivedLocal;
        if (submitted.HasValue)
        {
            message.SetExtendedProperty(PidTagClientSubmitTime, ToUtc(submitted.Value));
        }

        message.SetExtendedProperty(PidTagReadReceiptRequested, false);
        message.SetExtendedProperty(PidTagDeliveryReportRequested, false);
        return message;
    }

    /// <summary>Bestätigt Elternordner und Kennung jedes gespeicherten Items beim Server.</summary>
    private async Task VerifyAsync(
        ServiceLease lease,
        FolderId folder,
        List<SendEntry> saved,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        var unconfirmed = saved;
        for (var attempt = 0; unconfirmed.Count > 0; attempt++)
        {
            ServiceResponseCollection<GetItemResponse> responses;
            try
            {
                var ids = unconfirmed.Select(entry => entry.Message.Id).ToList();
                responses = await CallAsync(
                    lease,
                    service => service.BindToItems(ids, VerifyProperties),
                    cancellationToken);
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                // Gespeichert, aber nicht bestätigt: Der nächste Versuch sucht zuerst im Ziel.
                foreach (var entry in unconfirmed)
                {
                    results.Add(Failed(entry.Pending.Item, $"Prüfung nach dem Speichern fehlgeschlagen: {Describe(ex)}", retryable: true));
                }

                return;
            }

            // Einzelne Items können gedrosselt sein: Diese erneut lesen, nie erneut senden.
            var retry = new List<SendEntry>();
            TimeSpan? serverBackOff = null;
            for (var i = 0; i < unconfirmed.Count; i++)
            {
                var entry = unconfirmed[i];
                var response = i < responses.Count ? responses[i] : null;
                if (response is { Result: ServiceResult.Success, Item: not null })
                {
                    await ConfirmSavedAsync(lease, folder, entry, response.Item, results, cancellationToken);
                    continue;
                }

                var kind = response is null ? EwsFailureKind.Ambiguous : EwsFailure.Classify(response.ErrorCode);
                if (kind is EwsFailureKind.AbortMailbox or EwsFailureKind.AbortRun)
                {
                    throw new OutputAbortedException(
                        $"{_options.TargetUpn}: {response!.ErrorCode}: {response.ErrorMessage}",
                        kind == EwsFailureKind.AbortRun);
                }

                if (kind == EwsFailureKind.Permanent)
                {
                    results.Add(Failed(entry.Pending.Item, $"Gespeichertes Item nicht auffindbar: {response!.ErrorCode}", retryable: true));
                }
                else
                {
                    serverBackOff = (response is null ? null : EwsFailure.ServerBackOff(response)) ?? serverBackOff;
                    if (attempt + 1 < MaxAttempts)
                    {
                        retry.Add(entry);
                    }
                    else
                    {
                        results.Add(Failed(entry.Pending.Item, $"Gespeichert, Prüfung noch nicht bestätigt: {response?.ErrorCode}", retryable: true));
                    }
                }
            }

            // Auch ohne weiteres Lesen: Die Vorgabe des Servers gilt für den nächsten Batch.
            if (retry.Count > 0 || serverBackOff is not null)
            {
                await BackOffAsync(serverBackOff, attempt, cancellationToken);
            }

            unconfirmed = retry;
        }
    }

    private async Task ConfirmSavedAsync(
        ServiceLease lease,
        FolderId folder,
        SendEntry entry,
        Item stored,
        List<OutputResult> results,
        CancellationToken cancellationToken)
    {
        var item = entry.Pending.Item;
        var expected = TargetKey(item);
        var isExpectedItem = stored.TryGetProperty(ItemKeyProperty, out string? storedKey) &&
                             string.Equals(storedKey, expected, StringComparison.Ordinal);
        var isExpectedFolder = string.Equals(
            stored.ParentFolderId.UniqueId,
            folder.UniqueId,
            StringComparison.Ordinal);

        if (isExpectedItem && !isExpectedFolder)
        {
            // Folder-IDs können sich in der Darstellung unterscheiden: Die Suche im Zielordner entscheidet.
            var present = await FindExistingAsync(lease, folder, new[] { expected }, cancellationToken);
            isExpectedFolder = present.ContainsKey(expected);
        }

        if (isExpectedItem && isExpectedFolder)
        {
            Interlocked.Increment(ref _written);
            results.Add(new OutputResult(item, true, null, TargetId: entry.Message.Id.UniqueId));
        }
        else
        {
            results.Add(Failed(
                item,
                isExpectedItem
                    ? "Item liegt nicht im vorgesehenen Zielordner."
                    : "Server meldet ein anderes Item als das gesendete.",
                retryable: false));
        }
    }

    /// <summary>Termin, Kontakt oder Aufgabe einzeln anlegen; dieselben Regeln wie bei MIME.</summary>
    private async Task<OutputResult> WriteSingleAsync(
        ServiceLease lease,
        OutputItem item,
        FolderId folder,
        CancellationToken cancellationToken)
    {
        var mayExist = item.CheckExisting;
        var tokenRefreshed = false;
        string? lastError = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                if (mayExist)
                {
                    var found = await FindExistingAsync(lease, folder, new[] { TargetKey(item) }, cancellationToken);
                    if (found.TryGetValue(TargetKey(item), out var existingId))
                    {
                        Interlocked.Increment(ref _alreadyPresent);
                        return new OutputResult(item, true, null, AlreadyPresent: true, TargetId: existingId);
                    }

                    mayExist = false;
                }

                await PrepareAsync(lease, forceRefresh: false, cancellationToken);
                lease.Service.Timeout = MinimumTimeoutMs;
                await WaitForGateAsync(cancellationToken);

                var created = await SaveNonMimeAsync(lease.Service, item, folder);
                Interlocked.Increment(ref _written);
                return new OutputResult(item, true, null, TargetId: created.Id.UniqueId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutputAbortedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = Describe(ex);
                var kind = EwsFailure.Classify(ex);

                if (EwsFailure.HttpStatus(ex) == EwsFailure.Unauthorized && !tokenRefreshed)
                {
                    tokenRefreshed = true;
                    await PrepareAsync(lease, forceRefresh: true, cancellationToken);
                    continue;
                }

                switch (kind)
                {
                    case EwsFailureKind.AbortMailbox:
                    case EwsFailureKind.AbortRun:
                        throw new OutputAbortedException(AbortMessage(lease, ex), kind == EwsFailureKind.AbortRun, ex);
                    case EwsFailureKind.Permanent:
                        return Failed(item, lastError, retryable: false);
                    case EwsFailureKind.Ambiguous:
                        mayExist = true;
                        break;
                }

                await WaitAfterFailureAsync(ex, kind, attempt, cancellationToken);
            }
        }

        return Failed(item, lastError ?? "Nicht übertragen.", retryable: true);
    }

    private async Task<Item> SaveNonMimeAsync(ExchangeService service, OutputItem item, FolderId folder)
    {
        switch (item.Kind)
        {
            case "calendar":
                var appointment = CreateAppointment(service, item);
                // Migrierte Termine dürfen nie Einladungen an echte Personen auslösen.
                await appointment.Save(folder, SendInvitationsMode.SendToNone);
                return appointment;
            case "contact":
                var contact = CreateContact(service, item);
                await contact.Save(folder);
                return contact;
            default:
                var task = CreateTask(service, item);
                await task.Save(folder);
                return task;
        }
    }

    private Appointment CreateAppointment(ExchangeService service, OutputItem item)
    {
        var rawStart = GetDate(item, "StartTime") ?? item.ReceivedLocal ?? DateTime.Now;
        var rawEnd = GetDate(item, "StopTime") ?? rawStart.AddMinutes(30);
        var start = ToValidWallClock(rawStart);
        var end = ToValidWallClock(rawEnd);
        if (end < start)
        {
            // Die Verschiebung aus der Zeitumstellungslücke darf das Ende nicht vor den Beginn legen.
            end = start + (rawEnd > rawStart ? rawEnd - rawStart : TimeSpan.Zero);
        }

        var html = GetText(item, "BodyHtml");

        var appointment = new Appointment(service)
        {
            Subject = XmlSafe(item.Subject) ?? "(Termin)",
            Body = string.IsNullOrWhiteSpace(html)
                ? new MessageBody(BodyType.Text, GetText(item, "BodyText") ?? "")
                : new MessageBody(BodyType.HTML, html),
            Start = start,
            End = end,
            Location = GetText(item, "Location") ?? "",
            IsAllDayEvent = GetBool(item, "AllDayEvent") == true,
            IsReminderSet = false
        };

        appointment.SetExtendedProperty(ItemKeyProperty, TargetKey(item));
        return appointment;
    }

    private Contact CreateContact(ExchangeService service, OutputItem item)
    {
        var displayName = GetText(item, "DisplayName") ?? XmlSafe(item.Subject) ?? "(Kontakt)";

        var contact = new Contact(service)
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

        var html = GetText(item, "BodyHtml");
        contact.Body = string.IsNullOrWhiteSpace(html)
            ? new MessageBody(BodyType.Text, GetText(item, "BodyText") ?? "")
            : new MessageBody(BodyType.HTML, html);

        contact.SetExtendedProperty(ItemKeyProperty, TargetKey(item));
        return contact;
    }

    private EwsTask CreateTask(ExchangeService service, OutputItem item)
    {
        var html = GetText(item, "BodyHtml");
        var completed = GetBool(item, "Completed") == true || GetBool(item, "IsComplete") == true;

        var task = new EwsTask(service)
        {
            Subject = XmlSafe(item.Subject) ?? "(Aufgabe)",
            Body = string.IsNullOrWhiteSpace(html)
                ? new MessageBody(BodyType.Text, GetText(item, "BodyText") ?? "")
                : new MessageBody(BodyType.HTML, html),
            StartDate = GetDate(item, "StartTime") is { } startDate ? ToValidWallClock(startDate) : null,
            DueDate = (GetDate(item, "DueDate") ?? GetDate(item, "StopTime")) is { } dueDate ? ToValidWallClock(dueDate) : null,
            Status = completed ? EwsTaskStatus.Completed : MapTaskStatus(GetText(item, "Status")),
            Importance = MapImportance(GetText(item, "Priority")),
            PercentComplete = GetInt(item, "PercentComplete") ?? (completed ? 100 : 0),
            IsReminderSet = false
        };

        task.SetExtendedProperty(ItemKeyProperty, TargetKey(item));
        return task;
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

    private async Task<FolderId> GetTargetFolderAsync(OutputItem item, CancellationToken cancellationToken)
    {
        await _folderLock.WaitAsync(cancellationToken);
        try
        {
            var (root, subfolders, folderClass) = ResolveTarget(item);
            var current = await BindWellKnownAsync(root, cancellationToken);
            var accumulated = root.ToString();

            foreach (var segment in subfolders)
            {
                accumulated += "/" + segment;
                if (!_folderCache.TryGetValue(accumulated, out var next))
                {
                    next = await EnsureSubfolderAsync(current, segment, folderClass, cancellationToken);
                    _folderCache[accumulated] = next;
                }

                current = next;
            }

            return current;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    /// <summary>
    /// Das erste Pfadsegment ist der technische Wurzelschlüssel aus dem David-Baum, alle
    /// weiteren sind Anzeigenamen. Damit kann ein Benutzerordner namens "Ausgang" unter dem
    /// Eingang nie zu Gesendete Elemente werden.
    /// </summary>
    internal static (WellKnownFolderName Root, IReadOnlyList<string> Subfolders, string? FolderClass) ResolveTarget(OutputItem item)
    {
        var segments = item.FolderPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var hasRoot = segments.Count > 0 && FolderMapping.TryMapRoot(segments[0], out _);
        var mapped = WellKnownFolderName.Inbox;
        if (hasRoot)
        {
            FolderMapping.TryMapRoot(segments[0], out mapped);
        }

        var named = segments.Skip(hasRoot ? 1 : 0).Select(SanitizeFolderName).ToList();

        // Gelöschte Termine, Kontakte und Aufgaben dürfen nicht im Live-Ordner auftauchen.
        // Direkt nach DeletedItems oder JunkEmail lassen sie sich per CreateItem nicht anlegen.
        var holdingFolder = mapped switch
        {
            WellKnownFolderName.DeletedItems => "David-Papierkorb",
            WellKnownFolderName.JunkEmail => "David-Spam",
            WellKnownFolderName.Drafts => "David-Entwürfe",
            _ => null
        };
        if (holdingFolder is not null && item.Kind is "calendar" or "contact" or "task")
        {
            named.Insert(0, holdingFolder);
        }

        switch (item.Kind)
        {
            case "calendar":
                return (WellKnownFolderName.Calendar, named, "IPF.Appointment");
            case "contact":
                return (WellKnownFolderName.Contacts, named, "IPF.Contact");
            case "task":
                return (WellKnownFolderName.Tasks, named, "IPF.Task");
        }

        return mapped is WellKnownFolderName.Calendar or WellKnownFolderName.Contacts or WellKnownFolderName.Tasks
            ? (WellKnownFolderName.Inbox, segments.Select(SanitizeFolderName).ToList(), null)
            : (mapped, named, null);
    }

    private async Task<FolderId> EnsureSubfolderAsync(
        FolderId parent,
        string name,
        string? folderClass,
        CancellationToken cancellationToken)
    {
        var lease = _folderLease!;
        var found = await FindSubfolderAsync(lease, parent, name, cancellationToken);
        if (found is not null)
        {
            return found;
        }

        return await CallAsync(
            lease,
            async service =>
            {
                var folder = new Folder(service) { DisplayName = name };
                if (!string.IsNullOrWhiteSpace(folderClass))
                {
                    folder.FolderClass = folderClass;
                }

                try
                {
                    await folder.Save(parent);
                    return folder.Id;
                }
                catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorFolderExists)
                {
                    // Nach einem Timeout kann der erste Versuch den Ordner bereits angelegt haben.
                    var existing = await FindSubfolderAsync(lease, parent, name, cancellationToken);
                    if (existing is null)
                    {
                        throw;
                    }

                    return existing;
                }
            },
            cancellationToken);
    }

    private async Task<FolderId?> FindSubfolderAsync(
        ServiceLease lease,
        FolderId parent,
        string name,
        CancellationToken cancellationToken)
    {
        var view = new FolderView(2) { Traversal = FolderTraversal.Shallow };
        var result = await CallAsync(
            lease,
            service => service.FindFolders(parent, new SearchFilter.IsEqualTo(FolderSchema.DisplayName, name), view),
            cancellationToken);
        return result.Folders.Count > 0 ? result.Folders[0].Id : null;
    }

    private async Task<FolderId> BindWellKnownAsync(WellKnownFolderName name, CancellationToken cancellationToken)
    {
        if (_wellKnownFolders.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var folder = await CallAsync(_folderLease!, service => Folder.Bind(service, name), cancellationToken);
        _wellKnownFolders[name] = folder.Id;
        return folder.Id;
    }

    /// <summary>Lesender oder idempotenter Aufruf mit Wiederholung bei vorübergehenden Fehlern.</summary>
    private async Task<T> CallAsync<T>(
        ServiceLease lease,
        Func<ExchangeService, Task<T>> call,
        CancellationToken cancellationToken)
    {
        var tokenRefreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            await PrepareAsync(lease, forceRefresh: false, cancellationToken);
            lease.Service.Timeout = MinimumTimeoutMs;
            await WaitForGateAsync(cancellationToken);

            try
            {
                return await call(lease.Service);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutputAbortedException)
            {
                // Aus einem verschachtelten Aufruf: Die Entscheidung ist bereits gefallen.
                throw;
            }
            catch (Exception ex)
            {
                if (EwsFailure.HttpStatus(ex) == EwsFailure.Unauthorized && !tokenRefreshed)
                {
                    tokenRefreshed = true;
                    await PrepareAsync(lease, forceRefresh: true, cancellationToken);
                    continue;
                }

                var kind = EwsFailure.Classify(ex);
                if (kind is EwsFailureKind.AbortMailbox or EwsFailureKind.AbortRun)
                {
                    throw new OutputAbortedException(AbortMessage(lease, ex), kind == EwsFailureKind.AbortRun, ex);
                }

                if (kind == EwsFailureKind.Permanent || attempt + 1 >= MaxAttempts)
                {
                    // Ohne weiteren Versuch wartet niemand, die Vorgabe des Servers bleibt aber gültig.
                    await BackOffAsync(ex, MaxAttempts - 1, cancellationToken);
                    throw;
                }

                await BackOffAsync(ex, attempt, cancellationToken);
            }
        }
    }

    private async Task PrepareAsync(ServiceLease lease, bool forceRefresh, CancellationToken cancellationToken)
    {
        var token = await AcquireTokenAsync(forceRefresh, cancellationToken);

        if (!string.Equals(lease.Token, token, StringComparison.Ordinal))
        {
            lease.Service.Credentials = new OAuthCredentials(token);
            lease.Token = token;
        }
    }

    private async Task<string> AcquireTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // MSAL liefert aus dem Cache und erneuert kurz vor Ablauf selbst.
                return await _tokenProvider!(forceRefresh, cancellationToken);
            }
            catch (Exception ex) when (EwsFailure.IsTransientTokenFailure(ex) && attempt + 1 < MaxAttempts)
            {
                await BackOffAsync(ex, attempt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OutputAbortedException($"Anmeldung an Exchange Online fehlgeschlagen: {ex.Message}", true, ex);
            }
        }
    }

    private string AbortMessage(ServiceLease lease, Exception failure)
    {
        var reason = lease.Service.HttpResponseHeaders.TryGetValue(PolicyReasonHeader, out var value)
            ? $" {value}. EwsEnabled und EwsAllowedAppIDs des Tenants prüfen ('david2exchange setup --check')."
            : "";
        return $"{_options.TargetUpn}: {Describe(failure)}{reason}";
    }

    private async Task WaitForGateAsync(CancellationToken cancellationToken)
    {
        // Ein anderer Worker kann die Pause verlängern, während dieser noch wartet.
        long honoured = 0;
        while (true)
        {
            var resumeAt = Interlocked.Read(ref _resumeAtMilliseconds);
            var wait = resumeAt - Environment.TickCount64;
            if (wait <= 0 || resumeAt == honoured)
            {
                return;
            }

            honoured = resumeAt;
            await _delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
        }
    }

    /// <summary>
    /// Nach einem Verbindungsfehler kann der Server den Batch noch verarbeiten. Eine sofortige Suche
    /// fände dann nichts, und das erneute Senden erzeugte Duplikate. Nur dieser Worker wartet, auch
    /// nach dem letzten Versuch: Die nächste Wiederholung kommt früher, als der Server fertig ist.
    /// </summary>
    private async Task WaitAfterFailureAsync(Exception failure, EwsFailureKind kind, int attempt, CancellationToken cancellationToken)
    {
        await BackOffAsync(failure, attempt, cancellationToken);

        if (kind == EwsFailureKind.Ambiguous && EwsFailure.IsTransportFailure(failure))
        {
            await _delay(ServerSettleTime, cancellationToken);
        }
    }

    private Task BackOffAsync(Exception failure, int attempt, CancellationToken cancellationToken)
    {
        return BackOffAsync(EwsFailure.ServerBackOff(failure), attempt, cancellationToken);
    }

    /// <summary>
    /// Eine Server-Vorgabe gilt für alle Worker: Drosselung trifft das ganze Postfach, und
    /// Weitersenden würde sie nur verlängern. Eine eigene Wartezeit gilt nur für diesen Versuch.
    /// </summary>
    private async Task BackOffAsync(TimeSpan? serverBackOff, int attempt, CancellationToken cancellationToken)
    {
        if (serverBackOff is { } hint)
        {
            var capped = hint > MaximumServerBackOff ? MaximumServerBackOff : hint;
            var resumeAt = Environment.TickCount64 + (long)capped.TotalMilliseconds;
            long seen;
            while (resumeAt > (seen = Interlocked.Read(ref _resumeAtMilliseconds)))
            {
                if (Interlocked.CompareExchange(ref _resumeAtMilliseconds, resumeAt, seen) == seen)
                {
                    break;
                }
            }

            Console.Error.WriteLine($"  EWS-Drosselung: Server verlangt {capped.TotalSeconds:F0} s Pause.");

            // Die Pause gilt über das Tor für alle; nach dem letzten Versuch wartet niemand mehr.
            if (attempt + 1 < MaxAttempts)
            {
                await _delay(capped, cancellationToken);
            }

            return;
        }

        if (attempt + 1 >= MaxAttempts)
        {
            return;
        }

        var exponential = TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, Math.Pow(2, attempt)));
        await _delay(exponential + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), cancellationToken);
    }

    private static int TimeoutFor(long bytes)
    {
        var milliseconds = 60_000 + (bytes / BytesPerSecondAssumed * 1000);
        return (int)Math.Clamp(milliseconds, MinimumTimeoutMs, MaximumTimeoutMs);
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is not OutputAbortedException and not OperationCanceledException;
    }

    private static bool IsItemLevelFailure(Exception exception)
    {
        return exception is not OutputAbortedException and not OperationCanceledException;
    }

    private static OutputResult Failed(OutputItem item, string error, bool retryable)
    {
        return new OutputResult(item, false, error, Retryable: retryable);
    }

    private static string Describe(Exception exception)
    {
        return exception switch
        {
            ServiceResponseException response => $"{response.ErrorCode}: {response.Message}",

            // Die Meldung enthielte den abgelehnten Wert im Klartext. Texte und Ordnernamen gehen durch
            // XmlSafe, die Kennung darf sich nicht ändern: Nur sie erreicht den XML-Schreiber ungeprüft.
            ServiceXmlSerializationException => $"{exception.GetType().Name}: Ungültiges Zeichen in der Item-Kennung.",
            _ => $"{exception.GetType().Name}: {exception.Message}"
        };
    }

    private DateTime ToUtc(DateTime local)
    {
        return TimeZoneInfo.ConvertTimeToUtc(ToValidWallClock(local), _timeZone);
    }

    /// <summary>
    /// Das Manifest liefert Local, gemeint ist die Wanduhrzeit des David-Servers. Eine Zeit in der
    /// Lücke der Zeitumstellung gibt es nicht, und die EWS Managed API lehnt sie ab.
    /// </summary>
    private DateTime ToValidWallClock(DateTime local)
    {
        var wallClock = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return _timeZone.IsInvalidTime(wallClock) ? wallClock.AddHours(1) : wallClock;
    }

    private static string TargetKey(OutputItem item)
    {
        return ItemKeyVersion + (string.IsNullOrWhiteSpace(item.ItemKey) ? $"{item.ArchivePath}|{item.Id}" : item.ItemKey);
    }

    private static string? GetText(OutputItem item, string key)
    {
        return item.Properties.TryGetValue(key, out var value) && XmlSafe(value) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    /// <summary>
    /// Der XML-Schreiber der EWS Managed API lehnt in XML 1.0 unzulässige Zeichen ab, bevor etwas
    /// gesendet wird. MIME ist nicht betroffen (Base64). Leerzeichen statt Löschen, damit Wörter
    /// getrennt bleiben; das Manifest behält den Originaltext.
    /// </summary>
    internal static string? XmlSafe(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
            {
                builder.Append(text[i]);
            }
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                builder.Append(text[i]).Append(text[++i]);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static int? GetInt(OutputItem item, string key)
    {
        return int.TryParse(GetText(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static DateTime? GetDate(OutputItem item, string key)
    {
        // Das OLE-Nulldatum 30.12.1899 bedeutet in David "nicht gesetzt".
        return DateTime.TryParse(GetText(item, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) &&
               date.Year >= 1900
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
            builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        // Steuerzeichen sind schon ersetzt; das hält die Namen bereits angelegter Ordner stabil.
        var result = XmlSafe(builder.ToString())!.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "_";
        }

        // Der Schnitt darf kein Surrogatpaar trennen, die Hälfte wäre unzulässig.
        return result.Length <= 200 ? result : result[..(char.IsHighSurrogate(result[199]) ? 199 : 200)];
    }

    private ExchangeService CreateService()
    {
        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1, _timeZone)
        {
            Url = new Uri(_options.EwsUrl),
            ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, _options.TargetUpn),
            Timeout = MinimumTimeoutMs,
            UserAgent = "david2exchange/" + ToolInfo.Version
        };

        service.HttpHeaders["X-AnchorMailbox"] = _options.TargetUpn;
        return service;
    }

    private Func<bool, CancellationToken, Task<string>> CreateTokenProvider()
    {
        var application = ConfidentialClientApplicationBuilder
            .Create(_options.ClientId)
            .WithCertificate(LoadCertificate(_options.CertificateThumbprint))
            .WithTenantId(_options.TenantId)

            // Jedes Postfach hat eine eigene Instanz; ohne geteilten Cache holte jede ein neues Token.
            .WithCacheOptions(CacheOptions.EnableSharedCacheOptions)
            .Build();

        return async (forceRefresh, cancellationToken) =>
        {
            var result = await application
                .AcquireTokenForClient(new[] { "https://outlook.office365.com/.default" })
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        };
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

    private sealed class ServiceLease
    {
        public ServiceLease(ExchangeService service)
        {
            Service = service;
        }

        public ExchangeService Service { get; }

        public string? Token { get; set; }
    }

    private sealed class PendingItem
    {
        public PendingItem(OutputItem item)
        {
            Item = item;
            MayExist = item.CheckExisting;
        }

        public OutputItem Item { get; }

        /// <summary>Ein früherer Versuch kann das Item bereits gespeichert haben.</summary>
        public bool MayExist { get; set; }

        public string? LastError { get; set; }
    }

    private sealed record SendEntry(PendingItem Pending, EmailMessage Message, long Bytes);
}
