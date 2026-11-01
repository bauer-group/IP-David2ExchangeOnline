// SPDX-License-Identifier: MIT
using System.Diagnostics;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Domain;
using David2Exchange.Output;
using David2Exchange.Source;
using David2Exchange.Source.David;
using David2Exchange.Source.Mock;
using David2Exchange.Writer;

namespace David2Exchange.Probe;

public sealed class ProbeRunner
{
    public async Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken = default)
    {
        var report = new ProbeReport
        {
            Server = options.Server,
            ProgId = options.Mock ? "mock" : options.ProgId,
            Logon = options.Logon,
            Mock = options.Mock,
            Recursive = options.Recursive
        };

        var reportWriter = new LocalProbeWriter(options.OutputDirectory);
        var stagingDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), ".staging");

        try
        {
            var output = OutputTargetFactory.Create(options);
            await using (output)
            {
                await output.OpenAsync(
                    new OutputContext(options.OutputDirectory),
                    cancellationToken);

                using IDavidSession session = options.Mock
                    ? new MockDavidSession()
                    : new DavidComSession(options.ProgId);

                Console.WriteLine(
                    $"David-Probe startet · Prozess {(Environment.Is64BitProcess ? "x64" : "x86")} · " +
                    $"ProgID {session.ProgId} · Ausgabe {output.Name}");

                if (options.ListMembers)
                {
                    AddMembers(report, session.GetMemberInventory());
                    Console.WriteLine($"COM-Member vor Logon: {report.Members.Count} Einträge");
                }

                session.Logon(options.Logon);

                if (options.ListMembers)
                {
                    AddMembers(report, session.GetMemberInventory());
                    Console.WriteLine($"COM-Member nach Logon: {report.Members.Count} Einträge");
                }

                Directory.CreateDirectory(stagingDirectory);

                foreach (var requestedPath in options.Archives)
                {
                    var result = new ArchiveProbeResult { RequestedPath = requestedPath };
                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
                        Console.WriteLine($"  öffne {requestedPath} …");
                        var archive = session.OpenArchive(requestedPath);
                        result.Opened = true;
                        result.Name = archive.Name;
                        result.ResolvedPath = archive.ResolvedPath;
                        Console.WriteLine($"  geöffnet: {archive.ResolvedPath} ({archive.Name})");

                        if (options.Counts)
                        {
                            Console.WriteLine("  lese AllItems.Count …");
                            result.DeclaredItemCount = archive.DeclaredItemCount;
                            Console.WriteLine($"  AllItems.Count = {result.DeclaredItemCount?.ToString() ?? "?"}");
                        }

                        await WalkAsync(
                            archive,
                            result,
                            report,
                            options,
                            output,
                            stagingDirectory,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(Unwrap(ex).Message);
                        report.Errors.Add($"{requestedPath}: {Unwrap(ex).Message}");
                    }
                    finally
                    {
                        stopwatch.Stop();
                        result.DurationMs = stopwatch.ElapsedMilliseconds;
                        report.Archives.Add(result);
                        MergeTypeCounts(report.TypeCounts, result.TypeCounts);
                    }
                }

                report.FinishedUtc = DateTime.UtcNow;

                await output.CompleteAsync(
                    new OutputSummary(
                        report.TotalItems,
                        report.TotalEmlExported,
                        report.StartedUtc,
                        report.FinishedUtc.Value),
                    cancellationToken);

                reportWriter.Write(report);
                PrintSummary(report, options);

                if (!report.Archives.Any(archive => archive.Opened))
                {
                    return 2;
                }

                var hasWarnings = report.Errors.Count > 0 ||
                                  report.Archives.Any(archive => archive.Errors.Count > 0 || !archive.Opened);
                return hasWarnings ? 1 : 0;
            }
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            var actual = Unwrap(ex);
            report.Errors.Add($"{actual.GetType().Name}: {actual.Message}");
            report.FinishedUtc = DateTime.UtcNow;

            try
            {
                reportWriter.Write(report);
            }
            catch (Exception writeException)
            {
                Console.Error.WriteLine($"Report konnte nicht geschrieben werden: {writeException.Message}");
            }

            Console.Error.WriteLine($"FEHLER: {actual.Message}");
            Console.Error.WriteLine(
                "Diagnose: david2exchange probe --list-members --mock ausführen bzw. auf dem David-Rechner wiederholen.");
            return 2;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Aufräumen ist nicht kritisch.
            }
        }
    }

    private static async Task WalkAsync(
        IDavidArchive archive,
        ArchiveProbeResult result,
        ProbeReport report,
        ProbeOptions options,
        IOutputTarget output,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result.VisitedArchives > 0 &&
            !options.FullScan &&
            result.ScannedItemCount >= options.MaxItemsPerArchive)
        {
            return;
        }

        result.VisitedArchives++;

        try
        {
            foreach (var item in archive.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!options.FullScan && result.ScannedItemCount >= options.MaxItemsPerArchive)
                {
                    break;
                }

                await ReadItemAsync(item, result, report, options, output, stagingDirectory, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Items in '{archive.RequestedPath}': {Unwrap(ex).Message}");
        }

        if (!options.Recursive)
        {
            return;
        }

        if (!options.FullScan && result.ScannedItemCount >= options.MaxItemsPerArchive)
        {
            return;
        }

        try
        {
            foreach (var subArchive in archive.SubArchives)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!options.FullScan && result.ScannedItemCount >= options.MaxItemsPerArchive)
                {
                    break;
                }

                result.SubArchiveCount++;
                await WalkAsync(
                    subArchive,
                    result,
                    report,
                    options,
                    output,
                    stagingDirectory,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Unterarchive in '{archive.RequestedPath}': {Unwrap(ex).Message}");
        }
    }

    private static async Task ReadItemAsync(
        IDavidItem item,
        ArchiveProbeResult result,
        ProbeReport report,
        ProbeOptions options,
        IOutputTarget output,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var actualArchivePath = item.Properties.TryGetValue("ArchivePath", out var archivePath) &&
                                !string.IsNullOrWhiteSpace(archivePath)
            ? archivePath
            : result.RequestedPath;

        if (!result.SeenItems.Add($"{actualArchivePath}|{item.Id}"))
        {
            return;
        }

        if (IsExcluded(item, options))
        {
            return;
        }

        var type = NormalizeType(item.Type);

        if (options.Types.Count > 0 &&
            !options.Types.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (options.ExcludeTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        result.ScannedItemCount++;
        report.TotalItems++;
        result.TypeCounts[type] = result.TypeCounts.GetValueOrDefault(type) + 1;

        if (result.Samples.Count >= options.MaxItemsPerArchive)
        {
            return;
        }

        string? emlFile = null;
        string? stagedEmlPath = null;
        long? sizeBytes = item.SizeBytes;

        if (options.ExportEmlPerArchive > 0 &&
            result.EmlExported < options.ExportEmlPerArchive &&
            MayExportEml(type))
        {
            var fileName =
                $"{Sanitize(result.Name ?? "archiv")}_{Sanitize(item.Id)}_{result.EmlExported + 1:D3}.eml";
            var fullPath = Path.Combine(stagingDirectory, fileName);

            if (item.TryExportEml(fullPath, out var error))
            {
                emlFile = Path.Combine("eml", fileName);
                stagedEmlPath = fullPath;
                sizeBytes ??= new FileInfo(fullPath).Length;
                result.EmlExported++;
                report.TotalEmlExported++;
            }
            else
            {
                result.Errors.Add($"EML {item.Id}: {error}");
            }
        }

        var sample = new DavidItemSample
        {
            ArchivePath = actualArchivePath,
            Id = item.Id,
            Type = type,
            TypeLabel = TypeLabel(type),
            Kind = TypeLabel(type),
            Subject = item.Subject,
            SizeBytes = sizeBytes,
            AttachmentCount = item.Properties.TryGetValue("AttachmentCount", out var attachmentCount) &&
                              int.TryParse(attachmentCount, out var parsedCount)
                ? parsedCount
                : null,
            ReceivedLocal = item.ReceivedLocal,
            SentLocal = item.SentLocal,
            IsRead = item.IsRead,
            EmlFile = emlFile,
            Properties = item.Properties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };

        result.Samples.Add(sample);
        report.TotalSampled++;

        await output.WriteItemAsync(
            new OutputItem(
                sample.ArchivePath,
                sample.Id,
                sample.Type,
                sample.TypeLabel,
                sample.Subject,
                sample.SizeBytes,
                sample.AttachmentCount,
                sample.ReceivedLocal,
                sample.SentLocal,
                sample.IsRead,
                sample.EmlFile,
                stagedEmlPath,
                sample.Properties,
                sample.Kind,
                sample.FolderPath),
            cancellationToken);
    }

    private static void AddMembers(ProbeReport report, IEnumerable<string> members)
    {
        foreach (var member in members)
        {
            if (!report.Members.Contains(member))
            {
                report.Members.Add(member);
            }
        }
    }

    private static void MergeTypeCounts(
        Dictionary<string, int> target,
        IReadOnlyDictionary<string, int> source)
    {
        foreach (var (type, count) in source)
        {
            target[type] = target.GetValueOrDefault(type) + count;
        }
    }

    private static string TypeLabel(string type)
    {
        if (!int.TryParse(type, out _))
        {
            return type;
        }

        return DavidItemTypes.Kind(type);
    }

    private static string NormalizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "unknown";
        }

        return type.Trim();
    }

    private static bool MayExportEml(string type)
    {
        return type is "2" or "3" or "4" or "5" or "6" ||
               type.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("email", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("fax", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("sms", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("chat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(IDavidItem item, ProbeOptions options)
    {
        if (options.ExcludePaths.Count == 0)
        {
            return false;
        }

        var path = item.Properties.TryGetValue("ArchivePath", out var value) ? value ?? "" : "";
        return options.ExcludePaths.Any(exclude =>
            path.Contains(exclude, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "item";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (result.Length == 0)
        {
            result = "item";
        }

        return result.Length <= 80 ? result : result[..80];
    }

    private static void PrintSummary(ProbeReport report, ProbeOptions options)
    {
        Console.WriteLine();
        Console.WriteLine("=== David-Probe ===");
        Console.WriteLine($"Server:  {report.Server}");
        Console.WriteLine($"ProgID:  {report.ProgId}");
        Console.WriteLine($"Logon:   {report.Logon}");
        Console.WriteLine($"Mock:    {(report.Mock ? "ja" : "nein")}");
        Console.WriteLine($"Ausgabe: {options.OutputKind}");
        Console.WriteLine();

        foreach (var archive in report.Archives)
        {
            var status = archive.Opened ? "OK" : "FEHLER";
            Console.WriteLine(
                $"{status,-7} {archive.RequestedPath,-32} " +
                $"Items={archive.ScannedItemCount,6}  " +
                $"Archive={archive.VisitedArchives,4}  " +
                $"{archive.DurationMs,6} ms");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Items gesamt: {report.TotalItems} · Beispiele: {report.TotalSampled} · EML: {report.TotalEmlExported}");

        if (report.TypeCounts.Count > 0)
        {
            Console.WriteLine(
                "Typen: " + string.Join(
                    ", ",
                    report.TypeCounts.OrderBy(pair => pair.Key)
                        .Select(pair => $"{pair.Key}={pair.Value}")));
        }

        if (report.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Fehler:");
            foreach (var error in report.Errors)
            {
                Console.WriteLine($"  - {error}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Ausgabe: {Path.GetFullPath(options.OutputDirectory)}");
    }

    private static Exception Unwrap(Exception exception)
    {
        if (exception is System.Reflection.TargetInvocationException { InnerException: not null } invocation)
        {
            return invocation.InnerException!;
        }

        return exception;
    }
}
