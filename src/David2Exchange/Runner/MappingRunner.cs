// SPDX-License-Identifier: MIT
using System.Text.Json;
using David2Exchange.Cli;
using David2Exchange.Config;

namespace David2Exchange.Runner;

public static class MappingRunner
{
    public static int Run(
        string configPath,
        string? source,
        string? target,
        string? id,
        string? kind,
        bool list,
        string? sourceEmail = null,
        string? wave = null)
    {
        var config = ConfigLoader.Load(configPath);

        if (list)
        {
            PrintMappings(config);
            return 0;
        }

        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(id))
        {
            throw new CliException(
                "map benötigt --source <archivpfad>, --target <upn> und --id <mailbox-id> " +
                "(oder --list).");
        }

        if (!target.Contains('@', StringComparison.Ordinal))
        {
            throw new CliException($"Ziel-UPN '{target}' ist ungültig.");
        }

        var mapping = config.Mailboxes.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (mapping is null)
        {
            mapping = new MailboxMapping { Id = id };
            config.Mailboxes.Add(mapping);
        }

        mapping.Source.ArchivePath = source;
        mapping.Source.Kind = string.IsNullOrWhiteSpace(kind) ? "user" : kind;
        mapping.Target.Upn = target;
        mapping.Enabled = true;
        if (!string.IsNullOrWhiteSpace(sourceEmail))
        {
            mapping.Source.Email = MailboxMatcher.NormalizeEmail(sourceEmail);
        }

        if (!string.IsNullOrWhiteSpace(wave))
        {
            mapping.Wave = wave;
        }
        if (mapping.Include.Count == 0)
        {
            mapping.Include.AddRange(new[]
            {
                "mail", "fax", "sms", "voice", "chat", "calendar", "contacts", "tasks"
            });
        }

        ConfigSaver.Save(config, configPath);
        Console.WriteLine(
            $"Mapping gespeichert: {mapping.Id} · {mapping.Source.ArchivePath} → {mapping.Target.Upn}");
        return 0;
    }

    private static void PrintMappings(MigrationConfig config)
    {
        if (config.Mailboxes.Count == 0)
        {
            Console.WriteLine("Keine Mailbox-Mappings vorhanden.");
            return;
        }

        Console.WriteLine($"{"ID",-20} {"Quelle",-42} {"Ziel"}");
        Console.WriteLine(new string('-', 96));
        foreach (var mailbox in config.Mailboxes)
        {
            Console.WriteLine(
                $"{mailbox.Id,-20} {mailbox.Source.ArchivePath,-42} {mailbox.Target.Upn}");
        }
    }
}
