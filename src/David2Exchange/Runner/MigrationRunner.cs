// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Text;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Source;
using David2Exchange.Source.David;

namespace David2Exchange.Runner;

/// <summary>
/// Migrationsablauf je Postfach. Der Aufrufer-Thread ist der einzige mit Zugriff auf die
/// David-API (STA, single-threaded): Er liest, exportiert und füllt die Queue. Die Worker der
/// <see cref="TransferQueue"/> übertragen parallel und kennen nur Manifest und EML-Dateien.
/// </summary>
public sealed class MigrationRunner
{
    private const long BytesPerMegabyte = 1024 * 1024;
    private const string SyntheticAddressDomain = "david.local";

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(30);

    public int Discover(RunOptions options)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        config.Validate(requireM365: false);
        WarnIfFax(config);

        using IDavidSession session = new DavidComSession(config.David.ProgId);
        session.Logon(config.David.Logon);
        return DiscoverCore(config, options, session);
    }

    internal static int DiscoverCore(
        MigrationConfig config,
        RunOptions options,
        IDavidSession session,
        CancellationToken cancellationToken = default)
    {
        using var manifest = options.DryRun ? null : CreateManifest(config);
        manifest?.EnsureCurrentSchema();
        var run = manifest?.BeginRun("discover", ToolInfo.Version);
        var failedMailboxes = new List<string>();

        try
        {
            var limit = EffectiveLimit(config, options);
            var mailboxes = SelectMailboxes(config, options.MailboxId, options.Wave);
            foreach (var mailbox in mailboxes)
            {
                try
                {
                    DiscoverMailbox(manifest, session, mailbox, limit, cancellationToken);
                }
                catch (Exception ex) when (ex is not CliException and not LiteDB.LiteException and not OperationCanceledException)
                {
                    // Ein defektes Quellarchiv darf die übrigen Postfächer nicht aufhalten.
                    failedMailboxes.Add(mailbox.Id);
                    Console.Error.WriteLine($"POSTFACH FEHLER {mailbox.Id}: {ex.Message}");
                }
            }

            if (manifest is not null && run is not null)
            {
                manifest.TryFinishRun(
                    run,
                    failedMailboxes.Count == 0 ? null : "Fehlgeschlagen: " + string.Join(", ", failedMailboxes));
                PrintSummary(Summarize(manifest, mailboxes), "discover");
            }

            return failedMailboxes.Count == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Discover abgebrochen.");
            if (manifest is not null && run is not null)
            {
                manifest.TryFinishRun(run, "Abgebrochen");
            }

            return 2;
        }
        catch (Exception ex) when (ex is not CliException)
        {
            Console.Error.WriteLine($"DISCOVER FEHLER: {ex}");
            if (manifest is not null && run is not null)
            {
                manifest.TryFinishRun(run, ex.Message);
            }

            return 2;
        }
    }

    private static void DiscoverMailbox(
        LiteDbManifest? manifest,
        IDavidSession session,
        MailboxMapping mailbox,
        int limit,
        CancellationToken cancellationToken)
    {
        var archive = OpenMailboxArchive(session, mailbox);
        var keys = new ItemKeys(mailbox.Source.ArchivePath);
        var count = 0;
        var errors = 0;

        foreach (var entry in Walk(archive, mailbox.ExcludeArchives, (path, ex) => errors += ReportArchiveError(path, ex)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (limit > 0 && count >= limit)
            {
                break;
            }

            try
            {
                var (key, isRepeatedFile) = keys.Next(entry);
                var document = ToDocument(mailbox, entry, key);
                if (manifest is not null)
                {
                    document = manifest.UpsertDiscovered(document);
                    if (isRepeatedFile)
                    {
                        SkipRepeatedFile(manifest, document);
                    }
                }

                if (!isRepeatedFile)
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is not LiteDB.LiteException and not OperationCanceledException)
            {
                errors++;
                Console.Error.WriteLine($"  FEHLER {entry.ArchivePath}: Item nicht lesbar: {ex.Message}");
            }
        }

        Console.WriteLine($"  {mailbox.Id}: {count} Items erfasst ({mailbox.Source.ArchivePath})");

        if (errors > 0)
        {
            throw new InvalidOperationException($"{errors} Items oder Archive waren nicht lesbar.");
        }
    }

    public async Task<int> RunAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        var outputKind = options.OutputKind ??
                         (string.IsNullOrWhiteSpace(config.M365.TenantId) ? "local" : "ews");
        config.Validate(requireM365: outputKind.Equals("ews", StringComparison.OrdinalIgnoreCase));

        // Vor der David-Anmeldung: Im Postfachlauf wäre ein Tippfehler nur noch ein Postfachfehler.
        _ = OutputTargetFactory.TargetName(outputKind, "");

        if (options.OutputKind is null && outputKind == "local")
        {
            Console.WriteLine(
                "WARNUNG: m365.tenantId ist leer, deshalb lokale Ausgabe. Es wird nichts nach Exchange übertragen.");
        }

        using IDavidSession session = new DavidComSession(config.David.ProgId);
        session.Logon(config.David.Logon);

        if (options.DryRun)
        {
            var exitCode = DiscoverCore(config, options, session, cancellationToken);
            Console.WriteLine("Dry-Run: Es wurden keine Manifest-Items und keine Zielobjekte geschrieben.");
            return exitCode;
        }

        return await RunCoreAsync(
            config,
            options,
            session,
            mailbox => OutputTargetFactory.Create(new OutputSettings(
                outputKind,
                mailbox.Target.Upn,
                config.M365.EwsUrl,
                config.M365.TenantId,
                config.M365.ClientId,
                config.M365.CertThumbprint,
                config.M365.EwsFolder,
                config.David.Timezone,
                config.Limits.Workers)),
            DefaultRetryDelay,
            cancellationToken);
    }

    /// <summary>
    /// Exit-Codes: 0 = alles übertragen, 1 = fertig, aber Items oder Postfächer sind
    /// fehlgeschlagen, 2 = Lauf abgebrochen.
    /// </summary>
    internal static async Task<int> RunCoreAsync(
        MigrationConfig config,
        RunOptions options,
        IDavidSession session,
        Func<MailboxMapping, IOutputTarget> outputFactory,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        WarnIfFax(config);

        if (!options.Newest)
        {
            Console.WriteLine(
                "HINWEIS: --oldest wird nicht unterstützt. Items laufen in der David-Reihenfolge (neueste zuerst), " +
                "damit das Postfach nicht komplett in den Speicher muss.");
        }

        var mailboxes = SelectMailboxes(config, options.MailboxId, options.Wave);

        using var manifest = CreateManifest(config);
        manifest.EnsureCurrentSchema();
        var run = manifest.BeginRun("run", ToolInfo.Version);
        var failedMailboxes = new List<string>();
        var limit = EffectiveLimit(config, options);

        try
        {
            foreach (var mailbox in mailboxes)
            {
                try
                {
                    await MigrateMailboxAsync(
                        config,
                        manifest,
                        session,
                        mailbox,
                        outputFactory,
                        limit,
                        retryDelay,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OutputAbortedException { AffectsAllMailboxes: true } and not OperationCanceledException)
                {
                    // Ein defektes Quellarchiv darf die übrigen Postfächer nicht aufhalten.
                    failedMailboxes.Add(mailbox.Id);
                    Console.Error.WriteLine($"POSTFACH FEHLER {mailbox.Id}: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RUN ABGEBROCHEN: {ex}");
            manifest.TryFinishRun(run, ex.Message);
            PrintSummary(Summarize(manifest, mailboxes), "run (abgebrochen)");
            return 2;
        }

        manifest.TryFinishRun(
            run,
            failedMailboxes.Count == 0 ? null : "Fehlgeschlagen: " + string.Join(", ", failedMailboxes));

        var summary = Summarize(manifest, mailboxes);
        PrintSummary(summary, "run");
        PrintSkippedReasons(manifest, mailboxes);
        return failedMailboxes.Count > 0 || summary.Failed > 0 ? 1 : 0;
    }

    public int Verify(RunOptions options)
    {
        return VerifyCore(ConfigLoader.Load(options.ConfigPath), options);
    }

    /// <summary>
    /// Ohne --output zählt nur Exchange: Ein lokaler Testlauf ist keine abgeschlossene Migration.
    /// </summary>
    internal static int VerifyCore(MigrationConfig config, RunOptions options)
    {
        using var manifest = CreateManifest(config);

        var kind = OutputTargetFactory.CanonicalKind(options.OutputKind ?? "ews");
        var boxes = SelectMailboxes(config, options.MailboxId, options.Wave).ToList();
        var totalImported = 0;
        var totalElsewhere = 0;
        var totalFailed = 0;
        var totalOpen = 0;
        var totalItems = 0;
        var incompleteWalks = new List<string>();

        Console.WriteLine($"Ziel: {kind}");
        Console.WriteLine($"{"Postfach",-28} {"importiert",10} {"anderes Ziel",13} {"failed",7} {"offen",7} {"skipped",8} {"entfallen",10}");
        Console.WriteLine(new string('-', 95));

        foreach (var mailbox in boxes)
        {
            var summary = manifest.GetSummary(mailbox.Id);
            var elsewhere = manifest.CountImportedElsewhere(
                mailbox.Id,
                OutputTargetFactory.TargetName(kind, mailbox.Target.Upn));
            var imported = summary.Imported - elsewhere;
            var open = summary.Discovered + summary.Exported + summary.Importing;
            totalImported += imported;
            totalElsewhere += elsewhere;
            totalFailed += summary.Failed;
            totalOpen += open;
            totalItems += summary.Total;
            if (!manifest.HasCompleteWalk(mailbox.Id, mailbox.Source.ArchivePath))
            {
                incompleteWalks.Add(mailbox.Id);
            }

            Console.WriteLine(
                $"{mailbox.Id,-28} {imported,10} {elsewhere,13} {summary.Failed,7} {open,7} {summary.Skipped,8} {summary.NotInSource,10}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Wave: {options.Wave ?? "alle"} · importiert={totalImported} " +
            $"anderes Ziel={totalElsewhere} failed={totalFailed} offen={totalOpen}");

        // Null Zeilen heißen nur dann "noch nicht gelaufen", wenn der Lesenachweis fehlt.
        if (totalItems == 0 && incompleteWalks.Count > 0)
        {
            Console.Error.WriteLine(
                "VERIFY FAILED: Keine Manifest-Items für diese Auswahl. Zuerst discover/run ausführen.");
            return 1;
        }

        if (totalFailed > 0 || totalOpen > 0)
        {
            Console.Error.WriteLine(
                "VERIFY FAILED: Es sind noch Items offen oder fehlgeschlagen. " +
                "Report und Failed-Liste prüfen.");
            return 1;
        }

        if (totalElsewhere > 0)
        {
            Console.Error.WriteLine(
                $"VERIFY FAILED: {totalElsewhere} Items wurden in ein anderes Ziel übertragen als '{kind}' " +
                $"(lokaler Testlauf oder geändertes Zielpostfach). 'run --output {kind}' ausführen.");
            return 1;
        }

        if (incompleteWalks.Count > 0)
        {
            Console.Error.WriteLine(
                "VERIFY FAILED: Der letzte Lauf hat diese Postfächer nicht vollständig gelesen " +
                "(Limit, Lesefehler, Abbruch) oder die Quelle wurde geändert: " +
                string.Join(", ", incompleteWalks) + $". 'run --all --output {kind}' ausführen.");
            return 1;
        }

        PrintSkippedReasons(manifest, boxes);
        Console.WriteLine($"VERIFY OK: Alle Manifest-Items der Auswahl sind nach '{kind}' importiert oder bewusst skipped.");
        if (totalItems == 0)
        {
            Console.WriteLine("HINWEIS: Die Quelle war beim letzten vollständigen Lauf leer; übertragen wurde nichts.");
        }

        if (kind == "local")
        {
            Console.WriteLine("HINWEIS: Geprüft wurde die lokale Ausgabe. Nach Exchange wurde damit nichts übertragen.");
        }

        return 0;
    }

    public int Report(RunOptions options)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        using var manifest = CreateManifest(config);
        var summary = manifest.GetSummary(options.MailboxId);
        var items = manifest.GetAll(options.MailboxId);

        var outputPath = options.ReportPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var stagingRoot = ConfigLoader.ResolvePath(config, config.Staging.Root);
            outputPath = Path.Combine(stagingRoot, "report.html");
        }

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var jsonPath = Path.ChangeExtension(outputPath, ".json");
        File.WriteAllText(
            jsonPath,
            System.Text.Json.JsonSerializer.Serialize(
                new { summary, items },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<title>david2exchange Report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:.4rem;text-align:left}th{background:#f3f3f3}.ok{color:#1b5e20}.bad{color:#b71c1c}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>david2exchange – Migrationsreport</h1>");
        html.AppendLine($"<p>Erstellt: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · Gesamt: {summary.Total}</p>");
        html.AppendLine("<table><tr><th>Status</th><th>Anzahl</th></tr>");
        html.AppendLine($"<tr><td>Discovered</td><td>{summary.Discovered}</td></tr>");
        html.AppendLine($"<tr><td>Exported</td><td>{summary.Exported}</td></tr>");
        html.AppendLine($"<tr><td>Importing</td><td>{summary.Importing}</td></tr>");
        html.AppendLine($"<tr><td>Imported</td><td>{summary.Imported}</td></tr>");
        html.AppendLine($"<tr><td>Failed</td><td>{summary.Failed}</td></tr>");
        html.AppendLine($"<tr><td>Skipped</td><td>{summary.Skipped}</td></tr>");
        html.AppendLine($"<tr><td>Nicht mehr in der Quelle</td><td>{summary.NotInSource}</td></tr>");
        html.AppendLine("</table><h2>Items</h2>");
        html.AppendLine("<table><tr><th>Mailbox</th><th>Archiv</th><th>ID</th><th>Typ</th><th>Betreff</th><th>Status</th><th>Fehler</th></tr>");

        foreach (var item in items.OrderBy(x => x.MailboxId).ThenBy(x => x.ArchivePath).ThenBy(x => x.DavidId))
        {
            var css = item.State == ItemState.Imported ? "ok" : item.State == ItemState.Failed ? "bad" : "";
            html.AppendLine(
                $"<tr><td>{System.Net.WebUtility.HtmlEncode(item.MailboxId)}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(item.ArchivePath)}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(item.DavidId)}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(item.ItemType)}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(item.Subject ?? "")}</td>" +
                $"<td class=\"{css}\">{item.State}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(item.LastError ?? "")}</td></tr>");
        }

        html.AppendLine("</table></body></html>");
        File.WriteAllText(outputPath, html.ToString(), new UTF8Encoding(false));

        PrintSummary(summary, "report");
        Console.WriteLine($"Report: {outputPath}");
        Console.WriteLine($"JSON:   {jsonPath}");
        return 0;
    }

    private static async Task MigrateMailboxAsync(
        MigrationConfig config,
        LiteDbManifest manifest,
        IDavidSession session,
        MailboxMapping mailbox,
        Func<MailboxMapping, IOutputTarget> outputFactory,
        int limit,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var stagingRoot = ConfigLoader.ResolvePath(config, config.Staging.Root);
        var stageDirectory = Path.Combine(stagingRoot, "eml-stage", Sanitize(mailbox.Id));
        Directory.CreateDirectory(stageDirectory);

        // Der Nachweis gilt nur für den letzten Lauf; Scheitert dieser, bleibt er entzogen.
        manifest.ClearWalkComplete(mailbox.Id);

        var archive = OpenMailboxArchive(session, mailbox);
        var transfer = new TransferOptions(
            config.Limits.Workers,
            config.Limits.BatchSize,
            config.Limits.MaxBatchMegabytes * BytesPerMegabyte);

        await using var output = outputFactory(mailbox);
        await output.OpenAsync(
            new OutputContext(Path.Combine(stagingRoot, "output", Sanitize(mailbox.Id)), AppendItems: true),
            cancellationToken);

        var startedUtc = DateTime.UtcNow;
        var exported = 0;
        Exception? exportFailure = null;
        TransferResult result;

        using (var queue = new TransferQueue(manifest, output, transfer, mailbox.Id, cancellationToken))
        {
            queue.Start();
            try
            {
                // queue.Token enthält den Aufrufer-Token und stoppt auch bei einem Transferabbruch.
                ExportMailbox(archive, mailbox, config, manifest, stageDirectory, limit, output.Name, document =>
                {
                    queue.Enqueue(document);
                    exported++;
                }, queue.Token);
            }
            catch (OperationCanceledException)
            {
                // Der Transfer wurde gestoppt; den Grund meldet CompleteAsync.
            }
            catch (Exception ex)
            {
                // Bereits Bereitgestelltes wird trotzdem noch übertragen.
                exportFailure = ex;
            }

            result = await queue.CompleteAsync();
        }

        var transferred = result.Imported + result.AlreadyPresent;
        var retryable = result.RetryableKeys;
        for (var pass = 1; pass <= config.Limits.Retries && retryable.Count > 0; pass++)
        {
            Console.WriteLine(
                $"  {mailbox.Id}: Wiederholung {pass}/{config.Limits.Retries} für {retryable.Count} Items");
            await Task.Delay(retryDelay * pass, cancellationToken);

            using var retryQueue = new TransferQueue(manifest, output, transfer, mailbox.Id, cancellationToken);
            retryQueue.Start();
            Exception? retryFailure = null;
            try
            {
                foreach (var key in retryable)
                {
                    var document = manifest.Get(key);
                    if (document is { State: ItemState.Failed })
                    {
                        retryQueue.Enqueue(document);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Die Worker müssen enden, bevor Queue und Ausgabe freigegeben werden.
                retryFailure = ex;
            }

            // Stoppt der Transfer, wirft Enqueue; den Grund meldet CompleteAsync.
            var retried = await retryQueue.CompleteAsync();
            transferred += retried.Imported + retried.AlreadyPresent;
            retryable = retried.RetryableKeys;

            if (retryFailure is not null)
            {
                throw new InvalidOperationException(
                    $"Wiederholung für '{mailbox.Id}' abgebrochen: {retryFailure.Message}",
                    retryFailure);
            }
        }

        await output.CompleteAsync(
            new OutputSummary(transferred, exported, startedUtc, DateTime.UtcNow),
            cancellationToken);

        Console.WriteLine($"  {mailbox.Id}: {exported} bereitgestellt, {transferred} übertragen");
        RemoveStageDirectoryIfDone(manifest, mailbox, stageDirectory);

        if (exportFailure is not null)
        {
            throw new InvalidOperationException(
                $"David-Export für '{mailbox.Id}' unvollständig: {exportFailure.Message}",
                exportFailure);
        }
    }

    /// <summary>
    /// Läuft vollständig synchron auf dem aufrufenden STA-Thread; ein await würde hier
    /// den Thread und damit die COM-Apartment-Zuordnung verlieren können.
    /// </summary>
    private static void ExportMailbox(
        IDavidArchive archive,
        MailboxMapping mailbox,
        MigrationConfig config,
        LiteDbManifest manifest,
        string stageDirectory,
        int limit,
        string targetName,
        Action<ItemDocument> enqueue,
        CancellationToken cancellationToken)
    {
        var keys = new ItemKeys(mailbox.Source.ArchivePath);
        var walkStartedUtc = DateTime.UtcNow;
        var walked = 0;
        var handled = 0;
        var retried = 0;
        var queued = 0;
        var itemErrors = 0;
        var archiveErrors = 0;
        var isComplete = true;

        foreach (var entry in Walk(archive, mailbox.ExcludeArchives, (path, ex) => archiveErrors += ReportArchiveError(path, ex)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (limit > 0 && handled >= limit)
            {
                isComplete = false;
                break;
            }

            ItemDocument document;
            var isRetry = false;
            try
            {
                var (key, isRepeatedFile) = keys.Next(entry);
                walked++;
                document = manifest.UpsertDiscovered(ToDocument(mailbox, entry, key));
                if (isRepeatedFile)
                {
                    SkipRepeatedFile(manifest, document);
                    continue;
                }

                // ImportedTo nennt den letzten bestätigten Import; ein späterer Fehlversuch
                // für ein anderes Ziel ändert nichts daran, dass das Item in diesem Ziel liegt.
                var isInTarget = string.Equals(document.ImportedTo, targetName, StringComparison.OrdinalIgnoreCase);
                if (isInTarget && document.State != ItemState.Imported)
                {
                    document.State = ItemState.Imported;
                    document.LastError = null;
                    manifest.Save(document);
                }

                if (isInTarget || (document.State == ItemState.Imported && document.ImportedTo is null))
                {
                    continue;
                }

                if (document.Attempts == 0 && manifest.FindImportedCopy(document, targetName) is { } earlier)
                {
                    Skip(
                        manifest,
                        document,
                        $"Dieselbe David-Datei wurde bereits aus {earlier.ArchivePath} übertragen (in David verschoben); " +
                        "die Kopie bleibt im bisherigen Zielordner.");
                    continue;
                }

                // Bekannte Fehler dürfen das Limit nicht füllen, sonst kommt ein Pilot nie weiter.
                isRetry = document.State == ItemState.Failed;
                if (isRetry && limit > 0 && retried >= limit)
                {
                    isComplete = false;
                    continue;
                }

                if (!ExportItem(manifest, document, mailbox, config, entry, stageDirectory))
                {
                    if (document.State == ItemState.Failed)
                    {
                        _ = isRetry ? retried++ : handled++;
                    }

                    continue;
                }
            }
            catch (Exception ex) when (ex is not LiteDB.LiteException and not OperationCanceledException)
            {
                // Ein nicht lesbares Item darf den Rest des Archivs nicht aufhalten, zählt aber
                // als Lücke: Ohne vollständigen Durchlauf meldet verify kein OK.
                itemErrors++;
                Console.Error.WriteLine($"  FEHLER {entry.ArchivePath}: Item nicht lesbar: {ex.Message}");
                continue;
            }

            enqueue(document);
            queued++;
            _ = isRetry ? retried++ : handled++;
        }

        Console.WriteLine($"  {mailbox.Id}: {walked} Items gelesen, {queued} zur Übertragung ({mailbox.Source.ArchivePath})");

        if (itemErrors > 0 || archiveErrors > 0)
        {
            throw new InvalidOperationException($"{itemErrors} Items und {archiveErrors} Archive waren nicht lesbar.");
        }

        if (isComplete)
        {
            CloseVanishedRows(manifest, mailbox, keys, targetName, enqueue);

            var notSeen = manifest.CountImportedNotSeenSince(mailbox.Id, walkStartedUtc);
            if (notSeen > 0)
            {
                Console.WriteLine(
                    $"  HINWEIS {mailbox.Id}: {notSeen} übertragene Items sind in David nicht mehr an ihrem Ort " +
                    "(gelöscht oder verschoben). In Exchange bleiben sie unverändert.");
            }

            manifest.MarkWalkComplete(mailbox.Id, mailbox.Source.ArchivePath);
        }
    }

    /// <summary>
    /// Ein vollständiges Lesen beweist, dass diese Zeilen keine Quelle mehr haben. Offen blockierten
    /// sie verify für immer, und der einzige Ausweg wäre ein neues Manifest mit lauter Duplikaten.
    /// </summary>
    private static void CloseVanishedRows(
        LiteDbManifest manifest,
        MailboxMapping mailbox,
        ItemKeys keys,
        string targetName,
        Action<ItemDocument> enqueue)
    {
        var closed = 0;
        foreach (var row in manifest.GetOpenOrElsewhere(mailbox.Id, targetName).Where(row => !keys.WasIssued(row.Key)))
        {
            // Kann schon im Ziel liegen: Die Worker suchen zuerst und brauchen David nicht mehr.
            var canFinish = row.StagingPath is { } staged ? File.Exists(staged) : !DavidItemTypes.IsMailLike(row.ItemType);
            if (row.State == ItemState.Importing && canFinish)
            {
                enqueue(row);
                continue;
            }

            if (row.StagingPath is not null)
            {
                TryDeleteFile(row.StagingPath);
            }

            var history = row.State == ItemState.Imported
                ? $" Zuletzt übertragen nach '{row.ImportedTo}'."
                : row.State == ItemState.Importing ? " Kann im Ziel vorhanden sein." : "";
            row.State = ItemState.NotInSource;
            row.StagingPath = null;
            row.ExportedUtc = null;
            row.LastError = "Nicht mehr in der Quelle (in David gelöscht, verschoben oder ausgeschlossen)." + history;
            manifest.Save(row);
            closed++;
        }

        if (closed > 0)
        {
            Console.WriteLine($"  HINWEIS {mailbox.Id}: {closed} offene Zeilen haben keine Quelle mehr und wurden geschlossen.");
        }
    }

    private static int ReportArchiveError(string path, Exception exception)
    {
        Console.Error.WriteLine($"  FEHLER Archiv {path}: nicht vollständig lesbar: {exception.Message}");
        return 1;
    }

    private static void SkipRepeatedFile(LiteDbManifest manifest, ItemDocument document)
    {
        const string Reason = "David listet dieselbe Datei mehrfach im Archiv; nur der erste Eintrag wird übertragen.";
        Console.Error.WriteLine($"  WARNUNG {document.ArchivePath}\\{document.DavidId}: {Reason}");
        Skip(manifest, document, Reason);
    }

    /// <summary>Liefert true, wenn das Item für die Übertragung bereitsteht.</summary>
    private static bool ExportItem(
        LiteDbManifest manifest,
        ItemDocument document,
        MailboxMapping mailbox,
        MigrationConfig config,
        WalkEntry entry,
        string stageDirectory)
    {
        var kind = DavidItemTypes.Kind(document.ItemType);
        if (!DavidItemTypes.IsSupported(document.ItemType))
        {
            Skip(manifest, document, $"Typ {document.ItemType} ({kind}) wird nicht unterstützt.");
            return false;
        }

        if (!IsIncluded(mailbox, kind))
        {
            Skip(manifest, document, $"Typ '{kind}' ist nicht in include enthalten.");
            return false;
        }

        if (!DavidItemTypes.IsMailLike(document.ItemType))
        {
            if (document.Properties.GetValueOrDefault("IsRecurring")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.WriteLine(
                    $"  HINWEIS {document.ArchivePath}\\{document.DavidId}: Serientermin wird als Einzeltermin übertragen.");
            }

            // Termine, Kontakte und Aufgaben brauchen nur die Properties im Manifest; ihre Anhänge
            // liegen im Staging, weil nur dieser Thread David lesen darf.
            if (document.AttachmentCount is null or 0)
            {
                MarkExported(manifest, document, stagingPath: null);
                return true;
            }

            return StageAttachmentBundle(manifest, document, entry, stageDirectory);
        }

        document.AttachmentCount = entry.Item.AttachmentCount ?? document.AttachmentCount;

        var emlPath = StagingLayout.EmlPath(stageDirectory, document.Key);
        if (document.ExportedUtc is not null &&
            string.Equals(document.StagingPath, emlPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(document.StagedForUpn, mailbox.Target.Upn, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(emlPath))
        {
            // Aus einem früheren Lauf bereitgestellt; EML-Dateien ändern sich nicht mehr.
            return true;
        }

        var workDirectory = StagingLayout.WorkDirectory(stageDirectory, document.Key);
        var temporaryPath = emlPath + ".tmp";
        string? error;
        try
        {
            TryDeleteFile(temporaryPath);
            error = WriteEml(temporaryPath, document, mailbox, config, entry, workDirectory);
            if (error is null)
            {
                // Erst die vollständige Datei bekommt ihren Namen: Ein Absturz beim
                // Schreiben kann so nie eine abgeschnittene Nachricht übertragen.
                File.Move(temporaryPath, emlPath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not LiteDB.LiteException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            TryDeleteDirectory(workDirectory);
        }

        if (error is not null)
        {
            MarkFailed(manifest, document, error);
            return false;
        }

        document.StagedForUpn = mailbox.Target.Upn;
        MarkExported(manifest, document, emlPath);
        return true;
    }

    private static bool StageAttachmentBundle(
        LiteDbManifest manifest,
        ItemDocument document,
        WalkEntry entry,
        string stageDirectory)
    {
        var bundlePath = StagingLayout.EmlPath(stageDirectory, document.Key);
        if (document.ExportedUtc is not null &&
            string.Equals(document.StagingPath, bundlePath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(bundlePath))
        {
            return true;
        }

        var workDirectory = StagingLayout.WorkDirectory(stageDirectory, document.Key);
        var temporaryPath = bundlePath + ".tmp";
        string? error = null;
        try
        {
            var attachments = new List<SyntheticAttachment>();
            var position = 0;
            foreach (var attachment in entry.Item.Attachments)
            {
                // Eigener Ordner je Anhang: David erlaubt mehrere Anhänge gleichen Namens.
                var attachmentDirectory = Path.Combine(workDirectory, (position++).ToString());
                Directory.CreateDirectory(attachmentDirectory);
                if (!entry.Item.TryCopyAttachment(attachment, attachmentDirectory, out var copiedPath, out var copyError))
                {
                    error = $"Anhang '{attachment.FileName}' konnte nicht exportiert werden: {copyError}";
                    break;
                }

                if (copiedPath is not null)
                {
                    attachments.Add(new SyntheticAttachment(attachment.FileName, copiedPath));
                }
            }

            // Lieber sichtbar fehlschlagen, als das Objekt ohne seine Dateien als übertragen zu melden.
            if (error is null && attachments.Count != document.AttachmentCount)
            {
                error = $"David meldet {document.AttachmentCount} Anhänge, exportiert wurden {attachments.Count}.";
            }

            if (error is null)
            {
                AttachmentBundle.Write(temporaryPath, attachments);
                File.Move(temporaryPath, bundlePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is not LiteDB.LiteException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            TryDeleteDirectory(workDirectory);
        }

        if (error is not null)
        {
            MarkFailed(manifest, document, error);
            return false;
        }

        MarkExported(manifest, document, bundlePath);
        return true;
    }

    private static void MarkFailed(LiteDbManifest manifest, ItemDocument document, string error)
    {
        document.State = ItemState.Failed;
        document.StagingPath = null;
        document.ExportedUtc = null;
        document.LastError = ManifestText(error);
        manifest.Save(document);
        Console.Error.WriteLine($"  FEHLER {document.ArchivePath}\\{document.DavidId}: {error}");
    }

    private static void Skip(LiteDbManifest manifest, ItemDocument document, string reason)
    {
        document.State = ItemState.Skipped;
        document.LastError = reason;
        manifest.Save(document);
    }

    private static void MarkExported(LiteDbManifest manifest, ItemDocument document, string? stagingPath)
    {
        document.State = ItemState.Exported;
        document.StagingPath = stagingPath;
        document.ExportedUtc = DateTime.UtcNow;
        document.LastError = null;
        manifest.Save(document);
    }

    /// <summary>Schreibt die Nachricht und liefert im Fehlerfall den Grund.</summary>
    private static string? WriteEml(
        string path,
        ItemDocument document,
        MailboxMapping mailbox,
        MigrationConfig config,
        WalkEntry entry,
        string workDirectory)
    {
        string? nativeFailure = null;

        // DVEmlFromMailItem ist nur für echte MailItem-Objekte sicher. Bei Fax/SMS/Voice/Chat
        // kann der native Aufruf mit einer Access Violation den gesamten Prozess beenden.
        if (document.ItemType == "2")
        {
            if (entry.Item.TryExportEml(path, out var nativeError) && new FileInfo(path).Length > 0)
            {
                return null;
            }

            // Fehlt die David-API auf diesem Rechner, träfe das jede Mail. Die generierte
            // Nachricht ist nur Ersatz für Einzelfälle und darf nicht still die Regel werden.
            if (nativeError?.Contains("DvApi32.dll", StringComparison.OrdinalIgnoreCase) == true)
            {
                return nativeError;
            }

            nativeFailure = nativeError ?? "leere Datei";
            Console.WriteLine(
                $"  HINWEIS {document.ArchivePath}\\{document.DavidId}: David-EML nicht verfügbar " +
                $"({nativeFailure}); generiere EML-Fallback.");
        }

        return WriteSyntheticEml(path, document, mailbox, config, entry, workDirectory, nativeFailure);
    }

    private static string? WriteSyntheticEml(
        string path,
        ItemDocument document,
        MailboxMapping mailbox,
        MigrationConfig config,
        WalkEntry entry,
        string workDirectory,
        string? nativeFailure)
    {
        var item = entry.Item;
        var type = document.ItemType;
        var kind = DavidItemTypes.Kind(type);
        var isOutgoing = IsOutgoing(item, entry.FolderPath);

        string Get(string key) =>
            item.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "";

        // SMS und Chat sind MailItems: Ausgehend ist To leer, die Nummer steht in Recipients.
        var counterpart = isOutgoing
            ? type == "2" || Get("To").Length > 0 ? Get("To") : Get("Recipients")
            : Get("From");
        var subject = !string.IsNullOrWhiteSpace(item.Subject)
            ? item.Subject
            : string.IsNullOrWhiteSpace(counterpart)
                ? FallbackSubject(type, kind)
                : $"{FallbackSubject(type, kind)} {counterpart}";

        var knownDate = item.ReceivedLocal ?? item.SentLocal;
        if (knownDate is null)
        {
            Console.WriteLine(
                $"  HINWEIS {document.ArchivePath}\\{document.DavidId}: David nennt kein lesbares Datum; die Nachricht trägt die aktuelle Zeit.");
        }

        var localDate = knownDate ?? DateTime.Now;
        var attachments = new List<SyntheticAttachment>();

        // Auch bei vorhandenen Anhängen: Das Faxbild selbst darf nie verloren gehen.
        var image = type is "3" or "5"
            ? ReadSourceImage(item, config.David.ArchiveRoot)
            : null;

        // Fax und Voice führen am echten David keine lesbaren Anhänge: Ohne die genannte Datei fehlt der Inhalt selbst.
        if (type is "3" or "5" && image is null && !string.IsNullOrWhiteSpace(item.FaxImagePath))
        {
            return $"{(type == "3" ? "Fax" : "Voice")}: Die genannte Datei ist nicht lesbar oder leer: {item.FaxImagePath}. " +
                   "Archiv entpacken ('david2exchange unpack' plus David-Bereinigung) oder david.archiveRoot prüfen.";
        }

        if (type == "3" && image is not null)
        {
            Directory.CreateDirectory(workDirectory);
            // Eine Rundsendung nennt hier Dutzende Nummern; der Dateiname darf daran nicht scheitern.
            var labelText = string.IsNullOrWhiteSpace(counterpart) ? item.Id : counterpart;
            var label = Sanitize(labelText.Length > 60 ? labelText[..60] : labelText);
            var pdfPath = Path.Combine(workDirectory, $"fax-{label}-{localDate:yyyyMMdd-HHmm}.pdf");
            var metadata = new FaxPdfMetadata(
                subject,
                string.IsNullOrWhiteSpace(Get("From")) ? mailbox.Target.Upn : Get("From"),
                string.IsNullOrWhiteSpace(counterpart) ? subject : $"Fax {counterpart}",
                string.IsNullOrWhiteSpace(counterpart) ? "David;Fax" : $"David;Fax;{counterpart}",
                $"david2exchange {ToolInfo.Version}",
                $"david2exchange {ToolInfo.Version}",
                localDate);

            if (FaxPdfConverter.TryConvertTiffToPdf(image, pdfPath, metadata, out var pdfError))
            {
                attachments.Add(new SyntheticAttachment(Path.GetFileName(pdfPath), pdfPath));
            }
            else
            {
                Console.WriteLine(
                    $"  HINWEIS {document.ArchivePath}\\{document.DavidId}: PDF nicht erzeugt ({pdfError}); hänge TIFF an.");
                var tiffPath = Path.Combine(workDirectory, $"fax-{label}.tif");
                File.WriteAllBytes(tiffPath, image);
                attachments.Add(new SyntheticAttachment(Path.GetFileName(tiffPath), tiffPath));
            }
        }
        else if (type == "5" && image is not null)
        {
            Directory.CreateDirectory(workDirectory);
            var audioPath = Path.Combine(workDirectory, $"voice-{Sanitize(item.Id)}{AudioExtension(image)}");
            File.WriteAllBytes(audioPath, image);
            attachments.Add(new SyntheticAttachment(Path.GetFileName(audioPath), audioPath));
        }

        var position = 0;
        foreach (var attachment in item.Attachments)
        {
            // Eigener Ordner je Anhang: David erlaubt mehrere Anhänge gleichen Namens.
            var attachmentDirectory = Path.Combine(workDirectory, (position++).ToString());
            Directory.CreateDirectory(attachmentDirectory);
            if (!item.TryCopyAttachment(attachment, attachmentDirectory, out var copiedPath, out var copyError))
            {
                return $"Anhang '{attachment.FileName}' konnte nicht exportiert werden: {copyError}";
            }

            if (copiedPath is not null)
            {
                attachments.Add(new SyntheticAttachment(attachment.FileName, copiedPath));
            }
        }

        if (attachments.Count == 0 && type is "3" or "5")
        {
            return $"{(type == "3" ? "Fax" : "Voice")} enthält keine exportierbare Datei (Bild/Audio/Anhang). " +
                   $"Quellpfad: {item.FaxImagePath ?? "(keiner)"}. Es wird keine reine Text-Nachricht erzeugt.";
        }

        if (attachments.Count == 0 &&
            type is "2" or "4" or "6" &&
            string.IsNullOrWhiteSpace(Get("BodyText")) &&
            string.IsNullOrWhiteSpace(Get("BodyHtml")))
        {
            // Lieber sichtbar fehlschlagen als eine leere Nachricht als übertragen melden.
            return type switch
            {
                "2" => $"Mail enthält keinen lesbaren Text, David-EML nicht verfügbar: {nativeFailure}",
                "4" => "SMS enthält keinen lesbaren Text.",
                _ => "Chat enthält keinen lesbaren Text."
            };
        }

        var timeZone = DavidTimeZone.Resolve(config.David.Timezone);
        var date = new DateTimeOffset(
            DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified),
            timeZone.GetUtcOffset(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified)));

        var targetAddress = mailbox.Target.Upn;
        var davidFrom = string.IsNullOrWhiteSpace(Get("From")) ? "david2exchange" : Get("From");

        // Bei Mails ist To nur ein Anzeigetext; die echten Adressen stehen in Recipients.
        var recipients = type is "2" or "4" or "6" ? Get("Recipients") : "";
        var davidTo = recipients.Length > 0
            ? recipients
            : string.IsNullOrWhiteSpace(Get("To")) ? targetAddress : Get("To");

        SyntheticEml.Write(
            new SyntheticEmlContent(
                // Eine Mail behält ihre echten Absender und Empfänger, auch wenn das Zielpostfach ein anderes ist.
                type == "2" && Get("From").Length > 0 ? Get("From") : isOutgoing ? targetAddress : davidFrom,
                type == "2" || isOutgoing ? davidTo : targetAddress,
                string.IsNullOrWhiteSpace(Get("CC")) ? null : Get("CC"),
                subject,
                date,
                $"david-{StagingLayout.Token(document.Key)}@{SyntheticAddressDomain}",
                kind,
                item.Id,
                Get("BodyText"),
                Get("BodyHtml"),
                attachments,
                SyntheticAddressDomain),
            path);

        return null;
    }

    private static string FallbackSubject(string type, string kind)
    {
        return type switch
        {
            "3" => "[FAX]",
            "4" => "[SMS]",
            "5" => "[Voice]",
            "6" => "[Chat]",
            _ => $"[David {kind}]"
        };
    }

    private static byte[]? ReadSourceImage(IDavidItem item, string archiveRoot)
    {
        if (item.FaxImage is { Length: > 0 } bytes)
        {
            return bytes;
        }

        if (string.IsNullOrWhiteSpace(item.FaxImagePath))
        {
            return null;
        }

        var resolved = ResolveArchivePath(item.FaxImagePath, archiveRoot);
        if (!File.Exists(resolved))
        {
            return null;
        }

        // Eine leere Datei (z. B. abgebrochener Empfang) ist kein Inhalt.
        var content = File.ReadAllBytes(resolved);
        return content.Length > 0 ? content : null;
    }

    private static string AudioExtension(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            return ".wav";
        }

        return data.Length >= 3 && data[0] == 'I' && data[1] == 'D' && data[2] == '3' ? ".mp3" : ".bin";
    }

    private static bool IsOutgoing(IDavidItem item, string folderPath)
    {
        // David führt die Richtung je Item; der Ordner ist nur die Rückfallebene. FaxItem hat kein
        // Received, gemessen ist To dort nur bei gesendeten Faxen gefüllt, auch in eigenen Ordnern.
        return item.IsReceived is { } isReceived
            ? !isReceived
            : FolderMapping.IsSentRoot(folderPath) ||
              (item.Type.Trim() == "3" && !string.IsNullOrWhiteSpace(item.Properties.GetValueOrDefault("To")));
    }

    internal sealed record WalkEntry(IDavidItem Item, string ArchivePath, string FolderPath);

    /// <summary>Vergibt je Lauf eindeutige Manifest-Schlüssel für ein Postfach.</summary>
    private sealed class ItemKeys
    {
        private readonly string _sourcePath;
        private readonly Dictionary<string, int> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _issued = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Die Quelle statt der Postfach-ID: Die ID ist nur ein Name in der Config, und ihre Änderung
        /// sendete das ganze Postfach erneut. Der Pfad bleibt Teil der Kennung, weil ArchivePath auf
        /// einen Anzeigenamen zurückfallen kann, den sich zwei Benutzer teilen.
        /// </summary>
        public ItemKeys(string sourcePath)
        {
            _sourcePath = DavidArchivePaths.Normalize(sourcePath);
        }

        public bool WasIssued(string key) => _issued.Contains(key);

        /// <summary>
        /// Inhaltsbasierte Kennungen dürfen sich wiederholen (z. B. identische Kontakte) und werden
        /// durchnummeriert. Dieselbe David-Datei wird nicht doppelt gesendet, bekommt aber eine eigene
        /// Zeile im Manifest, damit die Wiederholung nachvollziehbar bleibt.
        /// </summary>
        public (string Key, bool IsRepeatedFile) Next(WalkEntry entry)
        {
            var token = DavidItemIdentity.Of(entry.Item);
            var key = ManifestText($"{_sourcePath}|{entry.ArchivePath}|{token}");
            _seen.TryGetValue(key, out var occurrences);
            _seen[key] = occurrences + 1;

            var result = occurrences == 0
                ? (key, false)
                : token.StartsWith(DavidItemIdentity.ContentPrefix, StringComparison.Ordinal)
                    ? ($"{key}#{occurrences + 1}", false)
                    : ($"{key}#dup{occurrences + 1}", true);
            _issued.Add(result.Item1);
            return result;
        }
    }

    private static IEnumerable<WalkEntry> Walk(
        IDavidArchive root,
        IReadOnlyCollection<string> excludeArchives,
        Action<string, Exception> onArchiveError)
    {
        // Das erste Pfadsegment ist immer ein technischer Schlüssel (in, out, cal, ...).
        var rootSegment = RootSegment(root);
        return WalkCore(root, root.ResolvedPath, excludeArchives, rootSegment ?? "", onArchiveError);
    }

    /// <summary>
    /// Ein nicht lesbares Archiv darf seine Geschwister nicht aufhalten, zählt aber als Lücke.
    /// Von Hand iteriert, weil yield nicht in einem try mit catch stehen darf.
    /// </summary>
    private static IEnumerable<T> Guarded<T>(
        Func<IEnumerable<T>> open,
        string path,
        Action<string, Exception> onError)
    {
        IEnumerator<T>? enumerator = null;
        try
        {
            enumerator = open().GetEnumerator();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onError(path, ex);
        }

        if (enumerator is null)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    onError(path, ex);
                    hasNext = false;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private static string? RootSegment(IDavidArchive archive)
    {
        if (!string.IsNullOrWhiteSpace(archive.FolderKind))
        {
            return archive.FolderKind;
        }

        return FolderMapping.IsKnownRoot(archive.FolderKey) ? archive.FolderKey : null;
    }

    private static IEnumerable<WalkEntry> WalkCore(
        IDavidArchive archive,
        string rootPath,
        IReadOnlyCollection<string> excludeArchives,
        string folderPath,
        Action<string, Exception> onArchiveError)
    {
        var path = archive.ResolvedPath;

        if (IsExcluded(archive, excludeArchives))
        {
            Console.WriteLine($"  HINWEIS: Archiv '{path}' per excludeArchives ausgeschlossen.");
            yield break;
        }

        foreach (var item in Guarded(() => archive.Items, path, onArchiveError))
        {
            yield return new WalkEntry(item, path, folderPath);
        }

        foreach (var subArchive in Guarded(() => archive.SubArchives, path, onArchiveError))
        {
            // Verknüpfte Archive gehören nicht in dieses Zielpostfach.
            if (DavidArchivePaths.IsForeign(subArchive.ResolvedPath, rootPath))
            {
                Console.WriteLine($"  HINWEIS: Verknüpftes Archiv '{subArchive.ResolvedPath}' übersprungen.");
                continue;
            }

            foreach (var entry in WalkCore(subArchive, rootPath, excludeArchives, SubFolderPath(folderPath, subArchive), onArchiveError))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Vergleicht ganze Pfadsegmente und den Anzeigenamen. Ein Teilstring würde mit
    /// "ADDRESS" auch ein Mailarchiv wie COMMON\IP-Addresses still ausschließen.
    /// </summary>
    private static bool IsExcluded(IDavidArchive archive, IReadOnlyCollection<string> excludeArchives)
    {
        var path = "\\" + archive.ResolvedPath.Trim('\\') + "\\";
        return excludeArchives.Any(exclude =>
            path.Contains("\\" + exclude.Trim().Trim('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
            archive.Name.Equals(exclude.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string SubFolderPath(string parent, IDavidArchive subArchive)
    {
        // Ein "/" im Anzeigenamen ist Teil des Namens und keine weitere Ordnerebene.
        var name = (string.IsNullOrWhiteSpace(subArchive.Name) ? subArchive.FolderKey : subArchive.Name)
            .Replace('/', '_')
            .Replace('\\', '_');
        if (!string.IsNullOrWhiteSpace(parent))
        {
            return parent.TrimEnd('/') + "/" + name;
        }

        // "system" ist nur ein Behälter: Papierkorb, Spam und Entwürfe liegen darunter.
        if (subArchive.FolderKey.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        // Eigene Ordner der obersten Ebene kommen unter den Eingang; ihr Anzeigename
        // darf nie mit einem technischen Schlüssel verwechselt werden.
        return RootSegment(subArchive) ?? "in/" + name;
    }

    private static IDavidArchive OpenMailboxArchive(IDavidSession session, MailboxMapping mailbox)
    {
        var archive = session.OpenArchive(mailbox.Source.ArchivePath);
        if (archive.FolderKey.Equals("archive", StringComparison.OrdinalIgnoreCase))
        {
            // Sonst würden alle Benutzerarchive in dieses eine Zielpostfach wandern.
            throw new CliException(
                $"mailbox '{mailbox.Id}': '{mailbox.Source.ArchivePath}' löst auf die gesamte " +
                "David-Archivwurzel auf. Bitte den konkreten Archivpfad angeben, z. B. COMMON.");
        }

        return archive;
    }

    private static ItemDocument ToDocument(MailboxMapping mailbox, WalkEntry entry, string key)
    {
        var item = entry.Item;
        var type = string.IsNullOrWhiteSpace(item.Type) ? "unknown" : item.Type.Trim();

        // Nur Termine, Kontakte und Aufgaben brauchen die teuer zu lesenden Properties.
        var isMailLike = DavidItemTypes.IsMailLike(type);
        var properties = isMailLike
            ? new Dictionary<string, string?>()
            : item.Properties.ToDictionary(pair => pair.Key, pair => ManifestText(pair.Value));

        return new ItemDocument
        {
            Key = key,
            MailboxId = mailbox.Id,
            ArchivePath = ManifestText(entry.ArchivePath),
            FolderPath = ManifestText(entry.FolderPath),
            DavidId = item.Id,
            FileToken = FileTokenOf(key),
            ItemType = type,
            Subject = ManifestText(item.Subject),
            SizeBytes = item.SizeBytes,
            AttachmentCount = properties.TryGetValue("AttachmentCount", out var count) &&
                              int.TryParse(count, out var parsed)
                ? parsed
                : null,
            Properties = properties,
            ReceivedLocal = item.ReceivedLocal,
            SentLocal = item.SentLocal,
            IsRead = IsOutgoing(item, entry.FolderPath) ? true : item.IsRead,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
    }

    private static string? FileTokenOf(string key)
    {
        var token = key[(key.LastIndexOf('|') + 1)..];
        return token.StartsWith(DavidItemIdentity.ContentPrefix, StringComparison.Ordinal) || token.Contains('#')
            ? null
            : token;
    }

    /// <summary>
    /// LiteDB schreibt strikt UTF-8 und lehnt ein einzelnes Surrogat ab; das Item käme nie ins
    /// Manifest. Nur dieses wird zu U+FFFD, jeder gültige Text bleibt unverändert.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    private static string? ManifestText(string? text)
    {
        return text is null ? null : Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
    }

    private static int EffectiveLimit(MigrationConfig config, RunOptions options)
    {
        var limit = options.Limit >= 0 ? options.Limit : config.Limits.Limit;
        if (limit > 0)
        {
            Console.WriteLine(
                $"HINWEIS: limits.limit={limit} aktiv; je Postfach nur {limit} Items. " +
                "Für alle Items --all verwenden.");
        }

        return limit;
    }

    private static void WarnIfFax(MigrationConfig config)
    {
        var faxEnabled = config.Mailboxes.Any(mailbox =>
            mailbox.Enabled &&
            (mailbox.Include.Count == 0 ||
             mailbox.Include.Any(value =>
                 value.Equals("fax", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("all", StringComparison.OrdinalIgnoreCase))));

        if (faxEnabled)
        {
            Console.WriteLine(
                "WARNUNG: Faxe werden nur migriert, wenn die David-Archive entpackt sind. " +
                "Vorher 'david2exchange unpack --config migration.yaml' ausführen und " +
                "die David-Datenbereinigung/Archiv-Optimierung durch den David-Administrator starten.");
        }
    }

    private static bool IsIncluded(MailboxMapping mailbox, string kind)
    {
        if (mailbox.Include.Count == 0 ||
            mailbox.Include.Any(value => value.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return mailbox.Include.Any(value =>
            value.Equals(kind, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(kind + "s", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveArchivePath(string path, string archiveRoot)
    {
        if (File.Exists(path) || string.IsNullOrWhiteSpace(archiveRoot))
        {
            return path;
        }

        var normalized = path.Replace('/', '\\');
        var marker = "\\archive\\";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return path;
        }

        var relative = normalized[(index + marker.Length)..].TrimStart('\\');

        // archiveRoot kann sowohl \\server\david als auch
        // \\server\david\archive sein.
        var direct = Path.Combine(archiveRoot, relative);
        if (File.Exists(direct))
        {
            return direct;
        }

        var withArchive = Path.Combine(archiveRoot, "archive", relative);
        return File.Exists(withArchive) ? withArchive : direct;
    }

    /// <summary>Entfernt verwaiste Staging-Dateien, sobald für das Postfach nichts mehr offen ist.</summary>
    private static void RemoveStageDirectoryIfDone(
        LiteDbManifest manifest,
        MailboxMapping mailbox,
        string stageDirectory)
    {
        var summary = manifest.GetSummary(mailbox.Id);
        if (summary.Discovered + summary.Exported + summary.Importing + summary.Failed == 0)
        {
            TryDeleteDirectory(stageDirectory);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  WARNUNG Aufräumen {path}: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  WARNUNG Aufräumen {path}: {ex.Message}");
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "item" : builder.ToString();
    }

    private static LiteDbManifest CreateManifest(MigrationConfig config)
    {
        var path = ConfigLoader.ResolvePath(config, config.Staging.Manifest);
        return new LiteDbManifest(path);
    }

    private static IEnumerable<MailboxMapping> SelectMailboxes(
        MigrationConfig config,
        string? mailboxId,
        string? wave)
    {
        var enabled = config.Mailboxes.Where(mailbox => mailbox.Enabled);

        if (!string.IsNullOrWhiteSpace(wave))
        {
            enabled = enabled.Where(mailbox =>
                mailbox.Wave.Equals(wave, StringComparison.OrdinalIgnoreCase));
        }

        var selected = (string.IsNullOrWhiteSpace(mailboxId)
            ? enabled
            : enabled.Where(x => x.Id.Equals(mailboxId, StringComparison.OrdinalIgnoreCase))).ToList();

        // Ein Tippfehler darf nicht wie ein erfolgreicher Lauf ohne Arbeit aussehen.
        return selected.Count > 0
            ? selected
            : throw new CliException(
                $"Kein aktives Postfach passt zur Auswahl (mailbox={mailboxId ?? "alle"}, wave={wave ?? "alle"}).");
    }

    /// <summary>Nur die Auswahl: Ein Fehler in einer anderen Wave darf diesen Lauf nicht rot machen.</summary>
    private static ManifestSummary Summarize(LiteDbManifest manifest, IEnumerable<MailboxMapping> mailboxes)
    {
        var total = new ManifestSummary();
        foreach (var mailbox in mailboxes)
        {
            var part = manifest.GetSummary(mailbox.Id);
            total.Discovered += part.Discovered;
            total.Exported += part.Exported;
            total.Importing += part.Importing;
            total.Imported += part.Imported;
            total.Failed += part.Failed;
            total.Skipped += part.Skipped;
            total.NotInSource += part.NotInSource;
        }

        return total;
    }

    private static void PrintSkippedReasons(LiteDbManifest manifest, IEnumerable<MailboxMapping> mailboxes)
    {
        var groups = mailboxes
            .SelectMany(mailbox => manifest.GetByState(ItemState.Skipped, mailbox.Id))
            .GroupBy(item => string.IsNullOrWhiteSpace(item.LastError)
                ? "ohne Grund"
                : item.LastError)
            .OrderByDescending(group => group.Count())
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }

        Console.WriteLine("Skipped-Gründe:");
        foreach (var group in groups)
        {
            Console.WriteLine($"  {group.Count(),5} · {group.Key}");
        }
    }

    private static void PrintSummary(ManifestSummary summary, string phase)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {phase} ===");
        Console.WriteLine(
            $"discovered={summary.Discovered} exported={summary.Exported} " +
            $"importing={summary.Importing} imported={summary.Imported} " +
            $"failed={summary.Failed} skipped={summary.Skipped} entfallen={summary.NotInSource} total={summary.Total}");
    }
}
