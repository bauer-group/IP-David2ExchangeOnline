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

        var allSources = (await LoadSourcesAsync(options, cancellationToken)).ToList();
        var sources = allSources
            .Where(source => source.Depth == 1 && source.Kind == "USER")
            .ToList();
        var commonSources = allSources
            .Where(source =>
                source.Kind == "COMMON" &&
                (source.Depth == 0 || source.Depth == 1))
            .ToList();

        if (sources.Count == 0 && commonSources.Count == 0)
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
                MigrationAdminUpn = options.AdminUpn,
                ProvenanceDomain = domain
            },
            Limits = new LimitsConfig { Limit = 10 },
            Staging = new StagingConfig
            {
                Root = @".\data",
                Manifest = @".\data\manifest.db",
                Logs = @".\logs"
            }
        };

        foreach (var source in sources.OrderBy(source => source.Kind)
                     .ThenBy(source => source.DisplayName))
        {
            var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? source.Path
                : source.DisplayName;
            var isSystem = IsSystemUser(source.Path);

            var sourceEmail = MailboxMatcher.NormalizeEmail(source.Email ?? "");
            var targetUpn = "";
            var method = "";
            var enabled = false;
            var createIfMissing = options.CreateIfMissing;
            var targetType = "user";
            MailboxTarget? targetInfo = null;

            if (isSystem)
            {
                method = "system-user-disabled";
            }
            else
            {
                var match = MailboxMatcher.MatchByEmail(sourceEmail, targets, usedUpns);
                if (match.Target is not null)
                {
                    targetUpn = match.Target.PrimarySmtpAddress;
                    method = match.Method;
                    enabled = true;
                    targetInfo = match.Target;
                }
                else
                {
                    var archiveId = LastPathSegment(source.Path);
                    var fallbackEmail = BuildFallbackEmail(sourceEmail, archiveId, domain);
                    var fallbackMatch = MailboxMatcher.MatchByEmail(
                        fallbackEmail,
                        targets,
                        usedUpns);

                    if (fallbackMatch.Target is not null)
                    {
                        targetUpn = fallbackMatch.Target.PrimarySmtpAddress;
                        targetInfo = fallbackMatch.Target;
                        method = $"fallback-{fallbackMatch.Method}";
                    }
                    else
                    {
                        targetUpn = fallbackEmail;
                        targetType = "shared";
                        createIfMissing = true;
                        method = string.IsNullOrWhiteSpace(sourceEmail)
                            ? "user-id-domain"
                            : "unmatched-created-shared";
                    }

                    sourceEmail = fallbackEmail;
                    enabled = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(targetUpn))
            {
                usedUpns.Add(targetUpn);
                targetInfo ??= targets.FirstOrDefault(candidate =>
                    candidate.PrimarySmtpAddress.Equals(
                        targetUpn,
                        StringComparison.OrdinalIgnoreCase));
            }

            var primaryAddress = targetInfo?.PrimarySmtpAddress ?? targetUpn;
            var id = UniqueId(displayName, source.Path, usedIds);
            config.Mailboxes.Add(new MailboxMapping
            {
                Id = id,
                Enabled = enabled,
                Wave = string.IsNullOrWhiteSpace(options.Wave) ? "default" : options.Wave,
                Source = new SourceConfig
                {
                    Kind = "user",
                    ArchivePath = source.Path,
                    Email = sourceEmail
                },
                Target = new TargetConfig
                {
                    Upn = targetUpn,
                    Type = targetInfo?.RecipientTypeDetails
                        .Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase) == true
                        ? "shared"
                        : targetType,
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
                        .ToList(),
                    CreateIfMissing = createIfMissing
                },
                Include = new List<string>
                {
                    "mail", "fax", "sms", "voice", "chat", "calendar", "contacts"
                },
                ExcludeArchives = new List<string>()
            });

            rows.Add(new MappingRow(
                source.Path,
                displayName,
                sourceEmail,
                "USER",
                targetUpn,
                method));
        }

        if (commonSources.Count > 0)
        {
            var commonTarget = string.IsNullOrWhiteSpace(options.UnverteiltUpn)
                ? $"info@{domain}"
                : options.UnverteiltUpn;
            var commonMatch = MailboxMatcher.MatchByEmail(commonTarget, targets, usedUpns);
            var commonTargetInfo = commonMatch.Target;
            var commonUpn = commonTargetInfo?.PrimarySmtpAddress ?? commonTarget;
            usedUpns.Add(commonUpn);

            config.Mailboxes.Add(new MailboxMapping
            {
                Id = UniqueId("unverteilt", "COMMON", usedIds),
                Enabled = true,
                Wave = string.IsNullOrWhiteSpace(options.Wave) ? "default" : options.Wave,
                Source = new SourceConfig
                {
                    Kind = "common",
                    ArchivePath = "COMMON",
                    Email = ""
                },
                Target = new TargetConfig
                {
                    Upn = commonUpn,
                    Type = commonTargetInfo?.RecipientTypeDetails
                        .Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase) == true
                        ? "shared"
                        : "shared",
                    DisplayName = string.IsNullOrWhiteSpace(commonTargetInfo?.DisplayName)
                        ? "BAUER GROUP Info"
                        : commonTargetInfo!.DisplayName,
                    Alias = MailboxMatcher.LocalPart(commonUpn),
                    Aliases = (commonTargetInfo?.EmailAddresses ?? new List<string>())
                        .Select(MailboxMatcher.NormalizeEmail)
                        .Where(address => !string.IsNullOrWhiteSpace(address))
                        .Where(address => !address.Equals(
                            MailboxMatcher.NormalizeEmail(commonUpn),
                            StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    CreateIfMissing = true
                },
                Include = new List<string>
                {
                    "mail", "fax", "sms", "voice", "chat", "calendar", "contacts"
                },
                ExcludeArchives = new List<string> { "ADDRESS", "#001#Adressen" }
            });

            rows.Add(new MappingRow(
                "COMMON",
                "COMMON",
                "",
                "COMMON",
                commonUpn,
                commonTargetInfo is null ? "common-info-shared" : "common-info-target"));
        }

        PrintRows(rows, domain);

        var unmatched = rows.Count(row =>
            string.IsNullOrWhiteSpace(row.TargetUpn) &&
            !row.Method.Equals("system-user-disabled", StringComparison.OrdinalIgnoreCase));
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
        if (!string.IsNullOrWhiteSpace(session.ServerName))
        {
            options.DavidServer = session.ServerName;
        }

        if (!string.IsNullOrWhiteSpace(session.DavidRoot))
        {
            options.ArchiveRoot = session.DavidRoot;
        }

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

    private static bool IsSystemUser(string path)
    {
        return LastPathSegment(path).Equals("00000000", StringComparison.OrdinalIgnoreCase);
    }

    private static string LastPathSegment(string path)
    {
        return path.TrimEnd('\\', '/')
            .Split('\\', '/')
            .LastOrDefault()
            ?? "";
    }

    private static string BuildFallbackEmail(string sourceEmail, string archiveId, string domain)
    {
        if (!string.IsNullOrWhiteSpace(sourceEmail) &&
            !sourceEmail.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return sourceEmail;
        }

        return $"{archiveId}@{domain}";
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
            var target = string.IsNullOrWhiteSpace(row.TargetUpn)
                ? row.Method.Equals("system-user-disabled", StringComparison.OrdinalIgnoreCase)
                    ? "(deaktiviert)"
                    : "(offen)"
                : row.TargetUpn;

            Console.WriteLine(
                $"{row.SourcePath,-38} {Truncate(row.SourceEmail, 38),-38} " +
                $"{target,-42} {row.Method}");
        }

        Console.WriteLine();
        Console.WriteLine($"Domain für Vorschläge: {domain}");
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }
}
