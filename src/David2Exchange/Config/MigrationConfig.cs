// SPDX-License-Identifier: MIT
using David2Exchange.Cli;
using David2Exchange.Infrastructure;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace David2Exchange.Config;

public sealed class MigrationConfig
{
    public DavidConfig David { get; set; } = new();

    public M365Config M365 { get; set; } = new();

    public StagingConfig Staging { get; set; } = new();

    public LimitsConfig Limits { get; set; } = new();

    public List<MailboxMapping> Mailboxes { get; set; } = new();

    [YamlIgnore]
    public string ConfigPath { get; set; } = "";

    public void Validate(bool requireM365)
    {
        if (string.IsNullOrWhiteSpace(David.ProgId))
        {
            throw new CliException("david.progId fehlt.");
        }

        if (string.IsNullOrWhiteSpace(David.Logon))
        {
            throw new CliException("david.logon fehlt.");
        }

        DavidTimeZone.Resolve(David.Timezone);

        if (Mailboxes.Count == 0)
        {
            throw new CliException("mailboxes ist leer.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mailbox in Mailboxes)
        {
            if (string.IsNullOrWhiteSpace(mailbox.Id) || !ids.Add(mailbox.Id))
            {
                throw new CliException("mailbox.id fehlt oder ist doppelt.");
            }

            if (string.IsNullOrWhiteSpace(mailbox.Source.ArchivePath))
            {
                throw new CliException($"mailbox '{mailbox.Id}': source.archivePath fehlt.");
            }

            if (mailbox.Enabled && string.IsNullOrWhiteSpace(mailbox.Target.Upn))
            {
                throw new CliException($"mailbox '{mailbox.Id}': target.upn fehlt.");
            }

            if (mailbox.Enabled &&
                mailbox.Source.Kind.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(mailbox.Source.Email))
            {
                throw new CliException(
                    $"mailbox '{mailbox.Id}': source.email fehlt. " +
                    "Benutzerpostfächer werden ausschließlich über die E-Mail-Adresse gemappt.");
            }
        }

        if (requireM365)
        {
            if (string.IsNullOrWhiteSpace(M365.TenantId) ||
                string.IsNullOrWhiteSpace(M365.ClientId) ||
                string.IsNullOrWhiteSpace(M365.CertThumbprint))
            {
                throw new CliException(
                    "m365.tenantId/clientId/certThumbprint fehlen. Zuerst 'david2exchange setup' ausführen.");
            }
        }

        if (Limits.Limit < 0)
        {
            throw new CliException("limits.limit muss >= 0 sein.");
        }

        if (Limits.Retries < 0)
        {
            throw new CliException("limits.retries muss >= 0 sein.");
        }

        if (Limits.Workers is < 1 or > 16)
        {
            throw new CliException("limits.workers muss zwischen 1 und 16 liegen.");
        }

        if (Limits.BatchSize is < 1 or > 100)
        {
            throw new CliException("limits.batchSize muss zwischen 1 und 100 liegen.");
        }

        if (Limits.MaxBatchMegabytes < 1)
        {
            throw new CliException("limits.maxBatchMegabytes muss >= 1 sein.");
        }
    }
}

public sealed class DavidConfig
{
    public string Server { get; set; } = "";
    public string ArchiveRoot { get; set; } = "";
    public string ProgId { get; set; } = "DVOBJAPILib.DvISEAPI";
    public string Timezone { get; set; } = "Europe/Berlin";
    public string Logon { get; set; } = "AUTH";
}

public sealed class M365Config
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CertThumbprint { get; set; } = "";
    public string EwsUrl { get; set; } = "https://outlook.office365.com/EWS/Exchange.asmx";
    public string ProvenanceDomain { get; set; } = "david.local";
    public string EwsFolder { get; set; } = "David-Migration";
    public string MigrationAdminUpn { get; set; } = "";
}

public sealed class StagingConfig
{
    public string Root { get; set; } = "staging";
    public string Manifest { get; set; } = "manifest.db";
    public string Logs { get; set; } = "logs";
}

public sealed class LimitsConfig
{
    public int Limit { get; set; } = 10;

    /// <summary>Zusätzliche Durchläufe je Postfach für vorübergehend fehlgeschlagene Items.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Parallele EWS-Worker. 1 mit batchSize 1 ergibt den langsamen Einzelmodus.</summary>
    public int Workers { get; set; } = 8;

    /// <summary>Maximale Nachrichten je CreateItem-Aufruf.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Maximale EML-Größe je Batch; ein einzelnes größeres Item läuft allein.</summary>
    public int MaxBatchMegabytes { get; set; } = 20;

    public string Wave { get; set; } = "default";
}

public sealed class MailboxMapping
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Wave { get; set; } = "default";
    public SourceConfig Source { get; set; } = new();
    public TargetConfig Target { get; set; } = new();
    public List<string> Include { get; set; } = new() { "mail" };
    public List<string> ExcludeArchives { get; set; } = new();
}

public sealed class SourceConfig
{
    public string Kind { get; set; } = "user";
    public string ArchivePath { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class TargetConfig
{
    public string Upn { get; set; } = "";
    public string Type { get; set; } = "shared";
    public string DisplayName { get; set; } = "";
    public string Alias { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public bool CreateIfMissing { get; set; }
}

public static class ConfigLoader
{
    public static MigrationConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Config '{path}' nicht gefunden.");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var yaml = File.ReadAllText(path);
        var config = deserializer.Deserialize<MigrationConfig>(yaml)
                     ?? throw new CliException($"Config '{path}' konnte nicht gelesen werden.");

        config.ConfigPath = Path.GetFullPath(path);
        return config;
    }

    public static string GetConfigDirectory(MigrationConfig config)
    {
        return Path.GetDirectoryName(config.ConfigPath) ?? Directory.GetCurrentDirectory();
    }

    public static string ResolvePath(MigrationConfig config, string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(GetConfigDirectory(config), path));
    }
}
