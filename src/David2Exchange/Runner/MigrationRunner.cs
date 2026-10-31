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
    public async Task<int> DiscoverAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        config.Validate(requireM365: false);

        using var manifest = CreateManifest(config);
        var run = manifest.BeginRun("discover", ToolInfo.Version);
        var summary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using IDavidSession session = new DavidComSession(config.David.ProgId);
            session.Logon(config.David.Logon);

            foreach (var mailbox in SelectMailboxes(config, options.MailboxId, options.Wave))
            {
                var archive = session.OpenArchive(mailbox.Source.ArchivePath);
                var count = 0;
                foreach (var (item, archivePath) in Walk(archive, mailbox.ExcludeArchives))
                {
                    if (options.Limit > 0 && count >= options.Limit)
                    {
                        break;
                    }

                    if (!options.DryRun)
                    {
                        var document = ToDocument(mailbox, item, archivePath);
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
    }

    public async Task<int> RunAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);
        var outputKind = options.OutputKind ??
                         (string.IsNullOrWhiteSpace(config.M365.TenantId) ? "local" : "ews");
        config.Validate(requireM365: outputKind.Equals("ews", StringComparison.OrdinalIgnoreCase));

        var discover = new RunOptions
        {
            ConfigPath = options.ConfigPath,
            MailboxId = options.MailboxId,
            Wave = options.Wave,
            Limit = options.Limit,
            DiscoverOnly = true,
            DryRun = options.DryRun
        };

        var discoverExit = await DiscoverAsync(discover, cancellationToken);
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
        var run = manifest.BeginRun("run", ToolInfo.Version);

        try
        {
            var selectedIds = SelectMailboxes(config, options.MailboxId, options.Wave)
                .Select(mailbox => mailbox.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pendingQuery = manifest
                .GetPending(options.MailboxId, 0, options.Newest)
                .Where(item => selectedIds.Contains(item.MailboxId));

            var pending = options.Limit > 0
                ? pendingQuery.Take(options.Limit).ToList()
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

            using IDavidSession session = new DavidComSession(config.David.ProgId);
            session.Logon(config.David.Logon);

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
                        var archive = session.OpenArchive(mailbox.Source.ArchivePath);
                        foreach (var (item, archivePath) in Walk(archive, mailbox.ExcludeArchives))
                        {
                            var key = BuildKey(mailbox.Id, archivePath, item.Id);
                            if (!pendingKeys.Contains(key))
                            {
                                continue;
                            }

                            await ProcessItemAsync(
                                manifest,
                                mailbox,
                                item,
                                archivePath,
                                key,
                                mailboxOutput,
                                Path.Combine(emlStageRoot, mailbox.Id),
                                cancellationToken);
                            pendingKeys.Remove(key);
                            if (pendingKeys.Count == 0)
                            {
                                break;
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

    private static async Task ProcessItemAsync(
        LiteDbManifest manifest,
        MailboxMapping mailbox,
        IDavidItem item,
        string archivePath,
        string key,
        IOutputTarget output,
        string emlStageDirectory,
        CancellationToken cancellationToken)
    {
        var document = manifest.Get(key)
                       ?? throw new InvalidOperationException($"Manifest-Item {key} fehlt.");

        if (document.State == ItemState.Imported)
        {
            return;
        }

        Directory.CreateDirectory(emlStageDirectory);
        var emlPath = Path.Combine(emlStageDirectory, $"{Sanitize(item.Id)}.eml");

        var needsExport = document.State != ItemState.Exported &&
                          document.State != ItemState.Importing;

        if (needsExport || !File.Exists(emlPath))
        {
            if (!MayExportEml(document.ItemType))
            {
                document.State = ItemState.Skipped;
                document.LastError = $"Typ {document.ItemType} wird in diesem Lauf nicht exportiert.";
                manifest.Save(document);
                return;
            }

            if (!item.TryExportEml(emlPath, out var error))
            {
                document.State = ItemState.Failed;
                document.Attempts++;
                document.LastError = error;
                manifest.Save(document);
                Console.Error.WriteLine($"  FEHLER {item.Id}: {error}");
                return;
            }

            document.State = ItemState.Exported;
            document.StagingPath = emlPath;
            document.ExportedUtc = DateTime.UtcNow;
            document.LastError = null;
            manifest.Save(document);
        }

        try
        {
            document.State = ItemState.Importing;
            manifest.Save(document);

            var outputItem = new OutputItem(
                archivePath,
                item.Id,
                document.ItemType,
                document.ItemType,
                item.Subject,
                item.SizeBytes,
                item.Properties.TryGetValue("AttachmentCount", out var count) && int.TryParse(count, out var parsed)
                    ? parsed
                    : null,
                item.ReceivedLocal,
                item.SentLocal,
                item.IsRead,
                Path.Combine("eml", Path.GetFileName(emlPath)),
                emlPath,
                item.Properties);

            await output.WriteItemAsync(outputItem, cancellationToken);

            document.State = ItemState.Imported;
            document.ImportedUtc = DateTime.UtcNow;
            document.LastError = null;
            manifest.Save(document);
            Console.WriteLine($"  OK {item.Id} · {item.Subject}");
        }
        catch (Exception ex)
        {
            document.State = ItemState.Failed;
            document.Attempts++;
            document.LastError = ex.Message;
            manifest.Save(document);
            Console.Error.WriteLine($"  FEHLER {item.Id}: {ex.Message}");
        }
    }

    private static IEnumerable<(IDavidItem Item, string ArchivePath)> Walk(
        IDavidArchive archive,
        IReadOnlyCollection<string> excludeArchives)
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
            yield return (item, itemPath);
        }

        foreach (var subArchive in archive.SubArchives)
        {
            foreach (var entry in Walk(subArchive, excludeArchives))
            {
                yield return entry;
            }
        }
    }

    private static ItemDocument ToDocument(
        MailboxMapping mailbox,
        IDavidItem item,
        string archivePath)
    {
        var type = string.IsNullOrWhiteSpace(item.Type) ? "unknown" : item.Type.Trim();
        return new ItemDocument
        {
            Key = BuildKey(mailbox.Id, archivePath, item.Id),
            MailboxId = mailbox.Id,
            ArchivePath = archivePath,
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

    private static string BuildKey(string mailboxId, string archivePath, string davidId)
    {
        return $"{mailboxId}|{archivePath}|{davidId}";
    }

    private static bool MayExportEml(string type)
    {
        return type == "2" ||
               type.Contains("mail", StringComparison.OrdinalIgnoreCase);
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
