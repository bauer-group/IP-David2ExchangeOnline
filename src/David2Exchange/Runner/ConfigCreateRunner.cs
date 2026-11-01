// SPDX-License-Identifier: MIT
using System.Text.Json;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Source;
using David2Exchange.Source.David;

namespace David2Exchange.Runner;

public sealed record MappingRow(
    string SourcePath,
    string SourceDisplayName,
    string SourceEmail,
    string SourceKind,
    string TargetUpn,
    string Method);

public static class ConfigCreateRunner
{
    public static async Task<int> RunAsync(
        ConfigCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            options.ConfigPath = Infrastructure.ToolPaths.DefaultConfigPath;
        }

        var targets = await LoadTargetsAsync(options, cancellationToken);
        if (targets.Count == 0)
        {
            throw new CliException(
                "Keine Zielpostfächer geladen. 'targets --json > targets.json' ausführen " +
                "und mit --targets-file übergeben.");
        }

        var sources = (await LoadSourcesAsync(options, cancellationToken))
            .Where(source =>
                (source.Depth == 1 && source.Kind is "USER" or "COMMON") ||
                (source.Depth == 0 && source.Kind == "COMMON"))
            .ToList();

        if (sources.Count == 0)
        {
            throw new CliException("Keine Quellarchive gefunden.");
        }

        var domain = ResolveDomain(options, targets);
        var usedUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<MappingRow>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var config = new MigrationConfig
        {
            ConfigPath = Path.GetFullPath(options.ConfigPath),
            David = new DavidConfig
            {
                Server = options.DavidServer,
                ArchiveRoot = options.ArchiveRoot,
                ProgId = options.ProgId,
                Logon = options.Logon
            },
            M365 = new M365Config
            {
                MigrationAdminUpn = options.AdminUpn
            },
            Limits = new LimitsConfig { Limit = 10 },
            Staging = new StagingConfig
            {
                Root = @".\staging",
                Manifest = @".\staging\manifest.db",
                Logs = @".\staging\logs"
            }
        };

        foreach (var source in sources.OrderBy(source => source.Kind)
                     .ThenBy(source => source.DisplayName))
        {
            var isCommon = source.Kind.Equals("COMMON", StringComparison.OrdinalIgnoreCase);
            var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? source.Path
                : source.DisplayName;

            var sourceEmail = MailboxMatcher.NormalizeEmail(source.Email ?? "");
            var match = MailboxMatcher.MatchByEmail(sourceEmail, targets, usedUpns);
            var targetUpn = match.Target?.PrimarySmtpAddress ?? "";
            var method = match.Method;

            if (isCommon &&
                (source.Path.Contains("unverteilt", StringComparison.OrdinalIgnoreCase) ||
                 source.DisplayName.Contains("unverteilt", StringComparison.OrdinalIgnoreCase)))
            {
                var unverteiltTarget = ResolveUnverteiltTarget(options, targets, usedUpns);
                if (!string.IsNullOrWhiteSpace(unverteiltTarget))
                {
                    targetUpn = unverteiltTarget;
                    method = options.UnverteiltUpn is not null
                        ? "option-unverteilt"
                        : "unverteilt-target";
                }
            }

            if (!string.IsNullOrWhiteSpace(targetUpn))
            {
                usedUpns.Add(targetUpn);
            }

            var targetInfo = targets.FirstOrDefault(candidate =>
                candidate.PrimarySmtpAddress.Equals(targetUpn, StringComparison.OrdinalIgnoreCase));

            var primaryAddress = targetInfo?.PrimarySmtpAddress ?? targetUpn;
            var id = UniqueId(displayName, source.Path, usedIds);
            config.Mailboxes.Add(new MailboxMapping
            {
                Id = id,
                Enabled = !string.IsNullOrWhiteSpace(targetUpn),
                Wave = string.IsNullOrWhiteSpace(options.Wave) ? "default" : options.Wave,
                Source = new SourceConfig
                {
                    Kind = isCommon ? "common" : "user",
                    ArchivePath = source.Path,
                    Email = sourceEmail
                },
                Target = new TargetConfig
                {
                    Upn = targetUpn,
                    Type = targetInfo?.RecipientTypeDetails
                        .Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase) == true
                        ? "shared"
                        : "user",
                    DisplayName = string.IsNullOrWhiteSpace(targetInfo?.DisplayName)
                        ? displayName
                        : targetInfo!.DisplayName,
                    Alias = MailboxMatcher.LocalPart(targetUpn),
                    Aliases = (targetInfo?.EmailAddresses ?? new List<string>())
                        .Select(MailboxMatcher.NormalizeEmail)
                        .Where(address => !string.IsNullOrWhiteSpace(address))
                        .Where(address => !address.Equals(
                            MailboxMatcher.NormalizeEmail(primaryAddress),
                            StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>(),
                    CreateIfMissing = options.CreateIfMissing
                },
                Include = new List<string>
                {
                    "mail", "fax", "sms", "voice", "chat", "calendar", "contacts"
                },
                ExcludeArchives = isCommon
                    ? new List<string> { "ADDRESS", "#001#Adressen" }
                    : new List<string>()
            });

            rows.Add(new MappingRow(
                source.Path,
                displayName,
                sourceEmail,
                source.Kind,
                targetUpn,
                method));
        }

        PrintRows(rows, domain);

        var unmatched = rows.Count(row => string.IsNullOrWhiteSpace(row.TargetUpn));
        Console.WriteLine();
        Console.WriteLine(
            $"Config: {options.ConfigPath} · Mappings: {rows.Count} · offen: {unmatched}");

        if (options.DryRun)
        {
            Console.WriteLine("Dry-Run: keine Datei geschrieben.");
            return 0;
        }

        var fullPath = Path.GetFullPath(options.ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        ConfigSaver.Save(config, fullPath);

        var reportPath = Path.ChangeExtension(fullPath, ".mapping.json");
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Mapping-Report: {reportPath}");
        return unmatched == 0 ? 0 : 1;
    }

    private static async Task<IReadOnlyList<DavidArchiveDescriptor>> LoadSourcesAsync(
        ConfigCreateOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.SourcesFile))
        {
            var path = Path.GetFullPath(options.SourcesFile);
            if (!File.Exists(path))
            {
                throw new CliException($"Sources-Datei '{path}' nicht gefunden.");
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<List<DavidArchiveDescriptor>>(
                       json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<DavidArchiveDescriptor>();
        }

        using var session = new DavidComSession(options.ProgId);
        session.Logon(options.Logon);
        return session.ListArchives(maxDepth: 2);
    }

    private static async Task<IReadOnlyList<MailboxTarget>> LoadTargetsAsync(
        ConfigCreateOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.TargetsFile))
        {
            var path = Path.GetFullPath(options.TargetsFile);
            if (!File.Exists(path))
            {
                throw new CliException($"Targets-Datei '{path}' nicht gefunden.");
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<MailboxTarget>>(
                       json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<MailboxTarget>();
        }

        Console.WriteLine("Lade Zielpostfächer aus Exchange Online (interaktiv) ...");
        return await TargetLister.LoadAsync(cancellationToken);
    }

    private static string ResolveUnverteiltTarget(
        ConfigCreateOptions options,
        IReadOnlyList<MailboxTarget> targets,
        ISet<string> usedUpns)
    {
        if (!string.IsNullOrWhiteSpace(options.UnverteiltUpn))
        {
            return options.UnverteiltUpn;
        }

        var existing = targets.FirstOrDefault(target =>
            !usedUpns.Contains(target.PrimarySmtpAddress) &&
            (target.PrimarySmtpAddress.Contains("unverteilt", StringComparison.OrdinalIgnoreCase) ||
             target.DisplayName.Contains("unverteilt", StringComparison.OrdinalIgnoreCase)));

        return existing?.PrimarySmtpAddress ?? "";
    }

    private static string ResolveDomain(
        ConfigCreateOptions options,
        IReadOnlyList<MailboxTarget> targets)
    {
        if (!string.IsNullOrWhiteSpace(options.Domain))
        {
            return options.Domain;
        }

        return targets
                   .Select(target => MailboxMatcher.DomainOf(target.PrimarySmtpAddress))
                   .Where(domain => !string.IsNullOrWhiteSpace(domain))
                   .GroupBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                   .OrderByDescending(group => group.Count())
                   .Select(group => group.Key)
                   .FirstOrDefault()
               ?? "example.com";
    }

    private static string UniqueId(string displayName, string path, ISet<string> usedIds)
    {
        var baseId = MailboxMatcher.Slug(displayName);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = MailboxMatcher.Slug(path);
        }

        var id = baseId;
        var suffix = 2;
        while (!usedIds.Add(id))
        {
            id = $"{baseId}-{suffix++}";
        }

        return id;
    }

    private static void PrintRows(IReadOnlyList<MappingRow> rows, string domain)
    {
        Console.WriteLine($"{"Quelle",-38} {"Quell-E-Mail",-38} {"Ziel-UPN",-42} Methode");
        Console.WriteLine(new string('-', 130));
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"{row.SourcePath,-38} {Truncate(row.SourceEmail, 38),-38} " +
                $"{(string.IsNullOrWhiteSpace(row.TargetUpn) ? "(offen)" : row.TargetUpn),-42} {row.Method}");
        }

        Console.WriteLine();
        Console.WriteLine($"Domain für Vorschläge: {domain}");
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }
}
