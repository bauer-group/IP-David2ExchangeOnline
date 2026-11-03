// SPDX-License-Identifier: MIT
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Source;

namespace David2Exchange.Runner;

public sealed record TransferOptions(int Workers, int BatchSize, long MaxBatchBytes);

public sealed record TransferResult(int Imported, int AlreadyPresent, int Failed, IReadOnlyList<string> RetryableKeys);

/// <summary>
/// Begrenzte Warteschlange zwischen dem David-Thread und den Ziel-Workern.
///
/// <para>
/// Der Aufrufer von <see cref="Enqueue"/> ist der einzige Thread mit COM-Zugriff. Er bildet die
/// Batches und blockiert, wenn die Queue voll ist. Die Worker sehen nur Manifest-Dokumente und
/// EML-Dateien, nie ein David-Objekt. Jedes Dokument gehört ab <see cref="Enqueue"/> der Queue.
/// </para>
/// <para>
/// Zustandsfolge je Batch: Importing wird vor dem Senden gespeichert. Nach einem Absturz ist
/// damit bekannt, welche Items den Server erreicht haben könnten; nur diese werden vor dem
/// nächsten Senden im Ziel gesucht.
/// </para>
/// </summary>
public sealed class TransferQueue : IDisposable
{
    private readonly LiteDbManifest _manifest;
    private readonly IOutputTarget _output;
    private readonly TransferOptions _options;
    private readonly string _progressLabel;
    private readonly Channel<List<ItemDocument>> _batches;
    private readonly CancellationTokenSource _cancellation;
    private readonly List<string> _retryableKeys = new();
    private readonly object _resultGate = new();

    private List<ItemDocument> _pending = new();
    private string? _pendingGroup;
    private long _pendingBytes;
    private Task[] _workers = Array.Empty<Task>();
    private Exception? _abort;
    private int _imported;
    private int _alreadyPresent;
    private int _failed;
    private long _lastProgressTicks = Environment.TickCount64;

    public TransferQueue(
        LiteDbManifest manifest,
        IOutputTarget output,
        TransferOptions options,
        string progressLabel,
        CancellationToken cancellationToken)
    {
        _manifest = manifest;
        _output = output;
        _options = options;
        _progressLabel = progressLabel;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _batches = Channel.CreateBounded<List<ItemDocument>>(new BoundedChannelOptions(options.Workers)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });
    }

    /// <summary>Wird ausgelöst, wenn der Aufrufer abbricht oder der Transfer gestoppt wurde.</summary>
    public CancellationToken Token => _cancellation.Token;

    public void Start()
    {
        // Task.Run löst die Worker vom STA-Kontext des Aufrufers.
        _workers = Enumerable.Range(0, _options.Workers)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    /// <summary>
    /// Nimmt ein bereitgestelltes Item an. Blockiert bei voller Queue und wirft, sobald der
    /// Transfer abgebrochen wurde, damit der David-Thread nicht weiter exportiert.
    /// </summary>
    public void Enqueue(ItemDocument document)
    {
        _cancellation.Token.ThrowIfCancellationRequested();

        var group = BatchGroup(document);
        var bytes = StagedBytes(document);

        var exceedsBytes = _pending.Count > 0 && _pendingBytes + bytes > _options.MaxBatchBytes;
        if (group != _pendingGroup || _pending.Count >= _options.BatchSize || exceedsBytes)
        {
            FlushPending();
        }

        _pendingGroup = group;
        _pendingBytes += bytes;
        _pending.Add(document);
    }

    /// <summary>Sendet den Rest, wartet auf alle Worker und liefert das Ergebnis.</summary>
    public async Task<TransferResult> CompleteAsync()
    {
        try
        {
            FlushPending();
        }
        catch (OperationCanceledException)
        {
            // Abbruch läuft bereits; der Grund wird unten gemeldet.
        }

        _batches.Writer.TryComplete();
        await Task.WhenAll(_workers);

        var abort = Volatile.Read(ref _abort);
        if (abort is not null)
        {
            ExceptionDispatchInfo.Capture(abort).Throw();
        }

        _cancellation.Token.ThrowIfCancellationRequested();

        return new TransferResult(_imported, _alreadyPresent, _failed, _retryableKeys.ToList());
    }

    public void Dispose()
    {
        _cancellation.Dispose();
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var batch = _pending;
        _pending = new List<ItemDocument>(_options.BatchSize);
        _pendingBytes = 0;

        // Bewusst synchron: der David-Thread bleibt so ausnahmslos auf seinem STA.
        _batches.Writer.WriteAsync(batch, _cancellation.Token).AsTask().GetAwaiter().GetResult();
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var batch in _batches.Reader
                               .ReadAllAsync(_cancellation.Token)
                               .ConfigureAwait(false))
            {
                await ProcessBatchAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Unterbrochene Items bleiben Importing und werden beim nächsten Lauf abgeglichen.
        }
        catch (Exception ex)
        {
            // Zustand nicht speicherbar: weiterzusenden würde Items ohne Nachweis übertragen.
            await AbortAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task AbortAsync(Exception reason)
    {
        Interlocked.CompareExchange(ref _abort, reason, null);
        await _cancellation.CancelAsync().ConfigureAwait(false);
    }

    private async Task ProcessBatchAsync(List<ItemDocument> batch)
    {
        var items = batch.Select(ToOutputItem).ToList();

        foreach (var document in batch)
        {
            document.State = ItemState.Importing;
            document.Attempts++;
        }

        _manifest.SaveMany(batch);

        IReadOnlyList<OutputResult> results;
        try
        {
            results = await _output.WriteBatchAsync(items, _cancellation.Token).ConfigureAwait(false);
        }
        catch (OutputAbortedException ex)
        {
            await AbortAsync(ex).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            results = items.Select(item => new OutputResult(item, false, ex.Message)).ToList();
        }

        ApplyResults(batch, items, results);
    }

    private void ApplyResults(
        List<ItemDocument> batch,
        List<OutputItem> items,
        IReadOnlyList<OutputResult> results)
    {
        // Ohne Rückmeldung gilt ein Item nie als übertragen.
        var resultByItem = new Dictionary<OutputItem, OutputResult>(ReferenceEqualityComparer.Instance);
        foreach (var result in results)
        {
            resultByItem[result.Item] = result;
        }

        var stagedToDelete = new List<string>();
        for (var i = 0; i < batch.Count; i++)
        {
            var document = batch[i];
            if (resultByItem.TryGetValue(items[i], out var result) && result.Success)
            {
                if (!string.IsNullOrWhiteSpace(document.StagingPath))
                {
                    stagedToDelete.Add(document.StagingPath);
                }

                document.State = ItemState.Imported;
                document.ImportedUtc = DateTime.UtcNow;
                document.ImportedTo = _output.Name;
                document.StagingPath = null;
                document.DstId = result.TargetId;
                document.LastError = null;
                if (result.AlreadyPresent)
                {
                    Interlocked.Increment(ref _alreadyPresent);
                }
                else
                {
                    Interlocked.Increment(ref _imported);
                }

                continue;
            }

            document.State = ItemState.Failed;
            document.LastError = result?.Error ?? "Das Ziel hat für dieses Item keine Rückmeldung geliefert.";
            Interlocked.Increment(ref _failed);
            Console.Error.WriteLine($"  FEHLER {document.ArchivePath}\\{document.DavidId}: {document.LastError}");

            if (result?.Retryable ?? true)
            {
                lock (_resultGate)
                {
                    _retryableKeys.Add(document.Key);
                }
            }
        }

        _manifest.SaveMany(batch);

        foreach (var path in stagedToDelete)
        {
            TryDelete(path);
        }

        ReportProgress();
    }

    private void ReportProgress()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastProgressTicks);
        if (now - last < 15_000 || Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last)
        {
            return;
        }

        Console.WriteLine(
            $"  {_progressLabel}: übertragen={Volatile.Read(ref _imported)} " +
            $"vorhanden={Volatile.Read(ref _alreadyPresent)} fehler={Volatile.Read(ref _failed)}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  WARNUNG Staging-Datei nicht gelöscht: {ex.Message}");
        }
    }

    /// <summary>
    /// Batches enthalten nur Items desselben Quellarchivs und damit desselben Zielordners.
    /// Termine, Kontakte und Aufgaben laufen nicht über CreateItems mit MimeContent.
    /// </summary>
    private static string BatchGroup(ItemDocument document)
    {
        var kind = DavidItemTypes.IsMailLike(document.ItemType) ? "mime" : document.ItemType;
        return kind + "|" + document.FolderPath + "|" + document.ArchivePath;
    }

    private static long StagedBytes(ItemDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.StagingPath))
        {
            return 0;
        }

        var file = new FileInfo(document.StagingPath);
        return file.Exists ? file.Length : 0;
    }

    private static OutputItem ToOutputItem(ItemDocument document)
    {
        var kind = DavidItemTypes.Kind(document.ItemType);
        return new OutputItem(
            document.ArchivePath,
            document.DavidId,
            document.ItemType,
            kind,
            document.Subject,
            document.SizeBytes,
            document.AttachmentCount,
            document.ReceivedLocal,
            document.SentLocal,
            document.IsRead,
            document.StagingPath is null
                ? null
                : Path.Combine("eml", Path.GetFileName(document.StagingPath)),
            document.StagingPath,
            document.Properties,
            kind,
            document.FolderPath,
            document.Attempts > 0,
            document.Key);
    }
}
