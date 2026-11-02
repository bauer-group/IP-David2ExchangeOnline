// SPDX-License-Identifier: MIT
using System.Text;
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Manifest;
using David2Exchange.Output;
using David2Exchange.Source;
using David2Exchange.Source.David;

namespace David2Exchange.Runner;

public sealed class MigrationRunner
{
    public async Task<int> DiscoverAsync(
        RunOptions options,
        IDavidSession? existingSession = null,
        CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        config.Validate(requireM365: false);
        WarnIfFax(config);

        using var manifest = CreateManifest(config);
        var run = manifest.BeginRun("discover", ToolInfo.Version);
        var summary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IDavidSession? ownedSession = null;

        try
        {
            var session = existingSession;
            if (session is null)
            {
                ownedSession = new DavidComSession(config.David.ProgId);
                session = ownedSession;
                session.Logon(config.David.Logon);
            }

            var limit = options.Limit >= 0 ? options.Limit : config.Limits.Limit;
            if (limit > 0)
            {
                Console.WriteLine(
                    $"HINWEIS: limits.limit={limit} aktiv; nur {limit} Items. " +
                    "Für alle Items --all verwenden.");
            }

            foreach (var mailbox in SelectMailboxes(config, options.MailboxId, options.Wave))
            {
                var archive = session.OpenArchive(mailbox.Source.ArchivePath);
                var count = 0;
                foreach (var (item, archivePath, folderPath) in Walk(
                             archive,
                             mailbox.ExcludeArchives,
                             archive.FolderKind))
                {
                    if (limit > 0 && count >= limit)
                    {
                        break;
                    }

                    if (!options.DryRun)
                    {
                        var document = ToDocument(mailbox, item, archivePath, folderPath);
                        manifest.UpsertDiscovered(document);
                    }

                    count++;
                    summary[mailbox.Id] = count;
                }

                Console.WriteLine($"  {mailbox.Id}: {count} Items erfasst ({mailbox.Source.ArchivePath})");
            }

            manifest.FinishRun(run);
            PrintSummary(manifest.GetSummary(), "discover");
            return 0;
        }
        catch (Exception ex)
        {
            manifest.FinishRun(run, ex.Message);
            Console.Error.WriteLine($"DISCOVER FEHLER: {ex.Message}");
            return 2;
        }
        finally
        {
            ownedSession?.Dispose();
        }
    }

    public async Task<int> RunAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        var outputKind = options.OutputKind ??
                         (string.IsNullOrWhiteSpace(config.M365.TenantId) ? "local" : "ews");
        config.Validate(requireM365: outputKind.Equals("ews", StringComparison.OrdinalIgnoreCase));

        var effectiveLimit = options.Limit >= 0 ? options.Limit : config.Limits.Limit;

        var discover = new RunOptions
        {
            ConfigPath = options.ConfigPath,
            MailboxId = options.MailboxId,
            Wave = options.Wave,
            Limit = effectiveLimit,
            DiscoverOnly = true,
            DryRun = options.DryRun
        };

        using IDavidSession session = new DavidComSession(config.David.ProgId);
        session.Logon(config.David.Logon);

        var discoverExit = await DiscoverAsync(discover, session, cancellationToken);
        if (discoverExit != 0)
        {
            return discoverExit;
        }

        if (options.DryRun)
        {
            Console.WriteLine("Dry-Run: Es wurden keine Manifest-Items und keine Zielobjekte geschrieben.");
            return 0;
        }

        using var manifest = CreateManifest(config);
        CleanupImportedStaging(manifest);
        var run = manifest.BeginRun("run", ToolInfo.Version);

        try
        {
            var selectedIds = SelectMailboxes(config, options.MailboxId, options.Wave)
                .Select(mailbox => mailbox.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pendingQuery = manifest
                .GetPending(options.MailboxId, 0, options.Newest)
                .Where(item => selectedIds.Contains(item.MailboxId));

            var pending = effectiveLimit > 0
                ? pendingQuery.Take(effectiveLimit).ToList()
                : pendingQuery.ToList();

            if (pending.Count == 0)
            {
                Console.WriteLine("Keine offenen Items.");
                manifest.FinishRun(run);
                PrintSummary(manifest.GetSummary(options.MailboxId), "run");
                return 0;
            }

            Console.WriteLine($"Offene Items: {pending.Count} (Ausgabe: {outputKind})");

            var stagingRoot = ConfigLoader.ResolvePath(config, config.Staging.Root);
            var outputRoot = Path.Combine(stagingRoot, "output");
            var emlStageRoot = Path.Combine(stagingRoot, "eml-stage");
            Directory.CreateDirectory(emlStageRoot);

            IOutputTarget? sharedLocalOutput = null;
            if (outputKind.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                sharedLocalOutput = new LocalFileOutputTarget();
                await sharedLocalOutput.OpenAsync(new OutputContext(outputRoot), cancellationToken);
            }

            try
            {
                foreach (var mailbox in SelectMailboxes(config, options.MailboxId, options.Wave))
                {
                    var mailboxPending = pending
                        .Where(x => x.MailboxId.Equals(mailbox.Id, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (mailboxPending.Count == 0)
                    {
                        continue;
                    }

                    var pendingKeys = mailboxPending
                        .Select(x => x.Key)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    IOutputTarget mailboxOutput = sharedLocalOutput ??
                                                  OutputTargetFactory.Create(new OutputSettings(
                                                      outputKind,
                                                      mailbox.Target.Upn,
                                                      config.M365.EwsUrl,
                                                      config.M365.TenantId,
                                                      config.M365.ClientId,
                                                      config.M365.CertThumbprint,
                                                      config.M365.EwsFolder));

                    if (sharedLocalOutput is null)
                    {
                        await mailboxOutput.OpenAsync(new OutputContext(outputRoot), cancellationToken);
                    }

                    try
                    {
                        var exported = new List<OutputItem>();
                        var archive = session.OpenArchive(mailbox.Source.ArchivePath);
                        foreach (var (item, archivePath, folderPath) in Walk(
                                     archive,
                                     mailbox.ExcludeArchives,
                                     archive.FolderKind))
                        {
                            var key = BuildKey(mailbox.Id, archivePath, item.Id);
                            if (!pendingKeys.Contains(key))
                            {
                                continue;
                            }

                            var outputItem = ExportItem(
                                manifest,
                                mailbox,
                                config.David.ArchiveRoot,
                                item,
                                archivePath,
                                folderPath,
                                key,
                                Path.Combine(emlStageRoot, mailbox.Id));

                            if (outputItem is not null)
                            {
                                exported.Add(outputItem);
                            }

                            pendingKeys.Remove(key);
                            if (pendingKeys.Count == 0)
                            {
                                break;
                            }
                        }

                        foreach (var batch in exported.Chunk(100))
                        {
                            var results = await mailboxOutput.WriteBatchAsync(
                                batch,
                                cancellationToken);

                            foreach (var result in results)
                            {
                                var key = BuildKey(
                                    mailbox.Id,
                                    result.Item.ArchivePath,
                                    result.Item.Id);
                                var document = manifest.Get(key);
                                if (document is null)
                                {
                                    continue;
                                }

                                if (result.Success)
                                {
                                    document.State = ItemState.Imported;
                                    document.ImportedUtc = DateTime.UtcNow;
                                    document.LastError = null;
                                    manifest.Save(document);
                                    CleanupStagedItem(result.Item);
                                    Console.WriteLine(
                                        $"  OK {result.Item.Id} · {result.Item.Subject}");
                                }
                                else
                                {
                                    document.State = ItemState.Failed;
                                    document.Attempts++;
                                    document.LastError = result.Error;
                                    manifest.Save(document);
                                    Console.Error.WriteLine(
                                        $"  FEHLER {result.Item.Id}: {result.Error}");
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (sharedLocalOutput is null)
                        {
                            await mailboxOutput.CompleteAsync(
                                new OutputSummary(mailboxPending.Count, 0, run.StartedUtc, DateTime.UtcNow),
                                cancellationToken);
                            await mailboxOutput.DisposeAsync();
                        }
                    }
                }
            }
            finally
            {
                if (sharedLocalOutput is not null)
                {
                    await sharedLocalOutput.CompleteAsync(
                        new OutputSummary(pending.Count, 0, run.StartedUtc, DateTime.UtcNow),
                        cancellationToken);
                    await sharedLocalOutput.DisposeAsync();
                }
            }

            manifest.FinishRun(run);
            PrintSummary(manifest.GetSummary(options.MailboxId), "run");
            return 0;
        }
        catch (Exception ex)
        {
            manifest.FinishRun(run, ex.Message);
            Console.Error.WriteLine($"RUN FEHLER: {ex.Message}");
            return 2;
        }
    }

    public int Verify(RunOptions options)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        using var manifest = CreateManifest(config);

        var boxes = SelectMailboxes(config, options.MailboxId, options.Wave).ToList();
        var totalImported = 0;
        var totalFailed = 0;
        var totalOpen = 0;
        var totalItems = 0;

        Console.WriteLine($"{"Postfach",-28} {"importiert",10} {"failed",7} {"offen",7} {"skipped",8}");
        Console.WriteLine(new string('-', 70));

        foreach (var mailbox in boxes)
        {
            var summary = manifest.GetSummary(mailbox.Id);
            var open = summary.Discovered + summary.Exported + summary.Importing;
            totalImported += summary.Imported;
            totalFailed += summary.Failed;
            totalOpen += open;
            totalItems += summary.Total;

            Console.WriteLine(
                $"{mailbox.Id,-28} {summary.Imported,10} {summary.Failed,7} {open,7} {summary.Skipped,8}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Wave: {options.Wave ?? "alle"} · importiert={totalImported} " +
            $"failed={totalFailed} offen={totalOpen}");

        if (totalItems == 0)
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

        Console.WriteLine("VERIFY OK: Alle Manifest-Items der Auswahl sind importiert oder bewusst skipped.");
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
        html.AppendLine($"<tr><td>Imported</td><td>{summary.Imported}</td></tr>");
        html.AppendLine($"<tr><td>Failed</td><td>{summary.Failed}</td></tr>");
        html.AppendLine($"<tr><td>Skipped</td><td>{summary.Skipped}</td></tr>");
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

    private static OutputItem? ExportItem(
        LiteDbManifest manifest,
        MailboxMapping mailbox,
        string archiveRoot,
        IDavidItem item,
        string archivePath,
        string folderPath,
        string key,
        string emlStageDirectory)
    {
        var document = manifest.Get(key)
                       ?? throw new InvalidOperationException($"Manifest-Item {key} fehlt.");

        if (document.State == ItemState.Imported)
        {
            return null;
        }

        var kind = DavidItemTypes.Kind(document.ItemType);
        if (!DavidItemTypes.IsSupported(document.ItemType))
        {
            document.State = ItemState.Skipped;
            document.LastError = $"Typ {document.ItemType} ({kind}) wird nicht unterstützt.";
            manifest.Save(document);
            return null;
        }

        if (!IsIncluded(mailbox, kind))
        {
            document.State = ItemState.Skipped;
            document.LastError = $"Typ '{kind}' ist nicht in include enthalten.";
            manifest.Save(document);
            return null;
        }

        var checkExisting = document.State is ItemState.Exported or ItemState.Importing;
        var needsEml = DavidItemTypes.IsMailLike(document.ItemType);
        string? emlPath = null;

        if (needsEml)
        {
            Directory.CreateDirectory(emlStageDirectory);
            emlPath = Path.Combine(emlStageDirectory, $"{Sanitize(item.Id)}.eml");

            var needsExport = document.State != ItemState.Exported &&
                              document.State != ItemState.Importing;

            if (needsExport || !File.Exists(emlPath))
            {
                // DVEmlFromMailItem ist nur für echte MailItem-Objekte sicher.
                // Bei Fax/SMS/Voice/Chat kann der native Aufruf mit einer
                // Access Violation den gesamten Prozess beenden.
                if (document.ItemType == "2")
                {
                    if (!item.TryExportEml(emlPath, out var error))
                    {
                        Console.WriteLine(
                            $"  HINWEIS {item.Id}: David-EML nicht verfügbar; " +
                            "generiere EML-Fallback für mail.");

                        if (!WriteSyntheticEml(
                                emlPath,
                                item,
                                mailbox,
                                archiveRoot,
                                document.ItemType,
                                emlStageDirectory,
                                out var syntheticError))
                        {
                            document.State = ItemState.Failed;
                            document.Attempts++;
                            document.LastError = syntheticError;
                            manifest.Save(document);
                            Console.Error.WriteLine($"  FEHLER {item.Id}: {syntheticError}");
                            return null;
                        }
                    }
                }
                else
                {
                    // Fax/SMS/Voice/Chat: kein nativer MailItem-Export.
                    if (!WriteSyntheticEml(
                            emlPath,
                            item,
                            mailbox,
                            archiveRoot,
                            document.ItemType,
                            emlStageDirectory,
                            out var syntheticError))
                    {
                        document.State = ItemState.Failed;
                        document.Attempts++;
                        document.LastError = syntheticError;
                        manifest.Save(document);
                        Console.Error.WriteLine($"  FEHLER {item.Id}: {syntheticError}");
                        return null;
                    }
                }

                document.State = ItemState.Exported;
                document.StagingPath = emlPath;
                document.ExportedUtc = DateTime.UtcNow;
                document.LastError = null;
                manifest.Save(document);
            }
        }
        else if (document.State == ItemState.Discovered)
        {
            document.State = ItemState.Exported;
            document.ExportedUtc = DateTime.UtcNow;
            document.StagingPath = null;
            manifest.Save(document);
        }

        return new OutputItem(
            archivePath,
            item.Id,
            document.ItemType,
            kind,
            item.Subject,
            item.SizeBytes,
            item.Properties.TryGetValue("AttachmentCount", out var count) && int.TryParse(count, out var parsed)
                ? parsed
                : null,
            item.ReceivedLocal,
            item.SentLocal,
            item.IsRead,
            emlPath is null ? null : Path.Combine("eml", Path.GetFileName(emlPath)),
            emlPath,
            item.Properties,
            kind,
            document.FolderPath ?? string.Empty,
            checkExisting);
    }

    private static IEnumerable<(IDavidItem Item, string ArchivePath, string FolderPath)> Walk(
        IDavidArchive archive,
        IReadOnlyCollection<string> excludeArchives,
        string folderPath = "")
    {
        var path = archive.ResolvedPath;

        if (excludeArchives.Any(exclude =>
                path.Contains(exclude, StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        foreach (var item in archive.Items)
        {
            var itemPath = item.Properties.TryGetValue("ArchivePath", out var value) &&
                           !string.IsNullOrWhiteSpace(value)
                ? value
                : path;
            yield return (item, itemPath, folderPath);
        }

        foreach (var subArchive in archive.SubArchives)
        {
            var segment = string.IsNullOrWhiteSpace(folderPath)
                ? (!string.IsNullOrWhiteSpace(subArchive.FolderKind)
                    ? subArchive.FolderKind
                    : subArchive.FolderKey)
                : subArchive.Name;
            if (string.IsNullOrWhiteSpace(segment))
            {
                segment = subArchive.Name;
            }

            var subFolder = string.IsNullOrWhiteSpace(folderPath)
                ? segment
                : folderPath.TrimEnd('/') + "/" + segment;

            foreach (var entry in Walk(subArchive, excludeArchives, subFolder))
            {
                yield return entry;
            }
        }
    }

    private static ItemDocument ToDocument(
        MailboxMapping mailbox,
        IDavidItem item,
        string archivePath,
        string folderPath)
    {
        var type = string.IsNullOrWhiteSpace(item.Type) ? "unknown" : item.Type.Trim();
        return new ItemDocument
        {
            Key = BuildKey(mailbox.Id, archivePath, item.Id),
            MailboxId = mailbox.Id,
            ArchivePath = archivePath,
            FolderPath = folderPath,
            DavidId = item.Id,
            ItemType = type,
            Subject = item.Subject,
            SizeBytes = item.SizeBytes,
            ReceivedLocal = item.ReceivedLocal,
            SentLocal = item.SentLocal,
            IsRead = item.IsRead,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
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

    private static string BuildKey(string mailboxId, string archivePath, string davidId)
    {
        return $"{mailboxId}|{archivePath}|{davidId}";
    }

    private static bool WriteSyntheticEml(
        string path,
        IDavidItem item,
        MailboxMapping mailbox,
        string archiveRoot,
        string type,
        string stageDirectory,
        out string? error)
    {
        error = null;

        string Get(string key) =>
            item.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "";

        var kind = DavidItemTypes.Kind(type);
        var from = Get("From");
        if (string.IsNullOrWhiteSpace(from))
        {
            from = "david2exchange@david.local";
        }

        var to = mailbox.Target.Upn;
        var faxNumber = type == "3" ? Get("To") : "";
        var subject = !string.IsNullOrWhiteSpace(item.Subject)
            ? item.Subject
            : type == "3" && !string.IsNullOrWhiteSpace(faxNumber)
                ? $"Fax {faxNumber}"
                : $"[David {kind}] {item.Id}";
        var body = Get("BodyText");
        var html = Get("BodyHtml");
        var date = item.ReceivedLocal ?? DateTime.Now;

        var attachments = new List<(string Name, string Path)>();
        var faxImage = item.FaxImage;
        if (faxImage is null && !string.IsNullOrWhiteSpace(item.FaxImagePath))
        {
            var resolvedFaxPath = ResolveArchivePath(item.FaxImagePath, archiveRoot);
            if (File.Exists(resolvedFaxPath))
            {
                faxImage = File.ReadAllBytes(resolvedFaxPath);
            }
        }

        if (type == "3" && item.Attachments.Count == 0 && faxImage is { Length: > 0 })
        {
            var faxDirectory = Path.Combine(
                stageDirectory,
                "attachments",
                Sanitize(item.Id));
            Directory.CreateDirectory(faxDirectory);

            var faxLabel = string.IsNullOrWhiteSpace(faxNumber)
                ? Sanitize(item.Id)
                : Sanitize(faxNumber);
            var pdfPath = Path.Combine(
                faxDirectory,
                $"fax-{faxLabel}-{date:yyyyMMdd-HHmm}.pdf");

            var pdfMetadata = new FaxPdfMetadata(
                subject,
                from,
                string.IsNullOrWhiteSpace(faxNumber) ? subject : $"Fax {faxNumber}",
                string.IsNullOrWhiteSpace(faxNumber)
                    ? "David;Fax"
                    : $"David;Fax;{faxNumber}",
                $"david2exchange {ToolInfo.Version}",
                $"david2exchange {ToolInfo.Version}",
                date);

            if (FaxPdfConverter.TryConvertTiffToPdf(faxImage, pdfPath, pdfMetadata, out _))
            {
                attachments.Add((Path.GetFileName(pdfPath), pdfPath));
            }
            else
            {
                var tiffPath = Path.Combine(faxDirectory, $"fax-{faxLabel}.tif");
                File.WriteAllBytes(tiffPath, faxImage);
                attachments.Add((Path.GetFileName(tiffPath), tiffPath));
            }
        }

        if (item.Attachments.Count > 0)
        {
            var attachmentDirectory = Path.Combine(
                stageDirectory,
                "attachments",
                Sanitize(item.Id));
            Directory.CreateDirectory(attachmentDirectory);

            foreach (var attachment in item.Attachments)
            {
                if (!item.TryCopyAttachment(
                        attachment,
                        attachmentDirectory,
                        out var copiedPath,
                        out var copyError))
                {
                    error = $"Anhang '{attachment.FileName}' konnte nicht exportiert werden: {copyError}";
                    return false;
                }

                if (copiedPath is not null)
                {
                    attachments.Add((attachment.FileName, copiedPath));
                }
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine($"From: {SanitizeHeader(from)}");
        builder.AppendLine($"To: {SanitizeHeader(to)}");
        var cc = Get("CC");
        if (!string.IsNullOrWhiteSpace(cc))
        {
            builder.AppendLine($"Cc: {SanitizeHeader(cc)}");
        }

        builder.AppendLine($"Subject: {SanitizeHeader(subject)}");
        builder.AppendLine($"Date: {date:r}");
        builder.AppendLine($"Message-ID: <david-{Sanitize(item.Id)}@{mailbox.Id}.david.local>");
        builder.AppendLine("MIME-Version: 1.0");
        builder.AppendLine($"X-David-Type: {kind}");
        builder.AppendLine($"X-David-Item-ID: {SanitizeHeader(item.Id)}");

        if (type == "3" && attachments.Count == 0)
        {
            error = "Fax enthält kein exportierbares Faxbild (Image/Attachment). " +
                    $"Image-Pfad: {item.FaxImagePath ?? "(keiner)"}. " +
                    "Es wird keine reine Text-EML erzeugt.";
            return false;
        }

        var hasHtml = !string.IsNullOrWhiteSpace(html);

        if (attachments.Count == 0)
        {
            if (hasHtml)
            {
                var alternative = "----david2exchange-alt-" + Guid.NewGuid().ToString("N");
                builder.AppendLine($"Content-Type: multipart/alternative; boundary=\"{alternative}\"");
                builder.AppendLine();
                AppendTextPart(builder, alternative, "text/plain", body);
                AppendTextPart(builder, alternative, "text/html", html);
                builder.AppendLine($"--{alternative}--");
            }
            else
            {
                builder.AppendLine("Content-Type: text/plain; charset=utf-8");
                builder.AppendLine();
                builder.AppendLine(body);
            }
        }
        else
        {
            var boundary = "----david2exchange-" + Guid.NewGuid().ToString("N");
            builder.AppendLine($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
            builder.AppendLine();
            builder.AppendLine($"--{boundary}");

            if (hasHtml)
            {
                var alternative = "----david2exchange-alt-" + Guid.NewGuid().ToString("N");
                builder.AppendLine($"Content-Type: multipart/alternative; boundary=\"{alternative}\"");
                builder.AppendLine();
                AppendTextPart(builder, alternative, "text/plain", body);
                AppendTextPart(builder, alternative, "text/html", html);
                builder.AppendLine($"--{alternative}--");
            }
            else
            {
                builder.AppendLine("Content-Type: text/plain; charset=utf-8");
                builder.AppendLine();
                builder.AppendLine(body);
            }

            foreach (var attachment in attachments)
            {
                var name = SanitizeHeader(attachment.Name);
                var mime = MimeTypeFor(name);
                builder.AppendLine($"--{boundary}");
                builder.AppendLine($"Content-Type: {mime}; name=\"{name}\"");
                builder.AppendLine("Content-Transfer-Encoding: base64");
                builder.AppendLine($"Content-Disposition: attachment; filename=\"{name}\"");
                builder.AppendLine();
                builder.AppendLine(WrapBase64(File.ReadAllBytes(attachment.Path)));
            }

            builder.AppendLine($"--{boundary}--");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return true;
    }

    private static void AppendTextPart(
        StringBuilder builder,
        string boundary,
        string contentType,
        string content)
    {
        builder.AppendLine($"--{boundary}");
        builder.AppendLine($"Content-Type: {contentType}; charset=utf-8");
        builder.AppendLine("Content-Transfer-Encoding: 8bit");
        builder.AppendLine();
        builder.AppendLine(content);
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
        if (File.Exists(withArchive))
        {
            return withArchive;
        }

        return direct;
    }

    private static string MimeTypeFor(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".tif" or ".tiff" => "image/tiff",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
    }

    private static string WrapBase64(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var builder = new StringBuilder(base64.Length + base64.Length / 76 * 2);
        for (var offset = 0; offset < base64.Length; offset += 76)
        {
            builder.AppendLine(base64.Substring(offset, Math.Min(76, base64.Length - offset)));
        }

        return builder.ToString();
    }

    private static string SanitizeHeader(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static void CleanupImportedStaging(LiteDbManifest manifest)
    {
        foreach (var document in manifest.GetAll()
                     .Where(item =>
                         item.State == ItemState.Imported &&
                         !string.IsNullOrWhiteSpace(item.StagingPath)))
        {
            CleanupStagedPath(document.StagingPath, document.DavidId);
        }
    }

    private static void CleanupStagedItem(OutputItem item)
    {
        CleanupStagedPath(item.StagedEmlPath, item.Id);
    }

    private static void CleanupStagedPath(string? path, string id)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var attachmentDirectory = Path.Combine(
                    directory,
                    "attachments",
                    Sanitize(id));
                if (Directory.Exists(attachmentDirectory))
                {
                    Directory.Delete(attachmentDirectory, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"  WARNUNG Aufräumen {path}: {ex.Message}");
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

        return string.IsNullOrWhiteSpace(mailboxId)
            ? enabled
            : enabled.Where(x =>
                x.Id.Equals(mailboxId, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintSummary(ManifestSummary summary, string phase)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {phase} ===");
        Console.WriteLine(
            $"discovered={summary.Discovered} exported={summary.Exported} " +
            $"imported={summary.Imported} failed={summary.Failed} skipped={summary.Skipped} " +
            $"total={summary.Total}");
    }
}
