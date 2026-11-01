// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Infrastructure;

namespace David2Exchange.Setup;

public static class MailboxProvisioner
{
    public static async Task<int> RunAsync(
        MailboxProvisionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            options.ConfigPath = ToolPaths.DefaultConfigPath;
        }

        var config = ConfigLoader.Load(options.ConfigPath);
        var adminUpn = string.IsNullOrWhiteSpace(options.AdminUpn)
            ? config.M365.MigrationAdminUpn
            : options.AdminUpn;

        if (string.IsNullOrWhiteSpace(adminUpn))
        {
            throw new CliException(
                "Für Vollzugriff wird ein Admin-UPN benötigt: --admin-upn oder m365.migrationAdminUpn.");
        }

        var mailboxes = config.Mailboxes
            .Where(mailbox => mailbox.Enabled && !string.IsNullOrWhiteSpace(mailbox.Target.Upn))
            .Where(mailbox => string.IsNullOrWhiteSpace(options.Wave) ||
                              mailbox.Wave.Equals(options.Wave, StringComparison.OrdinalIgnoreCase))
            .Select(mailbox => new
            {
                Upn = mailbox.Target.Upn,
                Type = mailbox.Target.Type.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? "user"
                    : "shared",
                DisplayName = string.IsNullOrWhiteSpace(mailbox.Target.DisplayName)
                    ? mailbox.Target.Upn
                    : mailbox.Target.DisplayName,
                Alias = string.IsNullOrWhiteSpace(mailbox.Target.Alias)
                    ? MailboxMatcher.LocalPart(mailbox.Target.Upn)
                    : mailbox.Target.Alias,
                Aliases = mailbox.Target.Aliases,
                CreateIfMissing = mailbox.Target.CreateIfMissing
            })
            .ToList();

        if (mailboxes.Count == 0)
        {
            throw new CliException("Keine Zielpostfächer in der Config.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"david2exchange-mailboxes-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            tempPath,
            JsonSerializer.Serialize(mailboxes, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        try
        {
            var scriptPath = EmbeddedScripts.GetScriptPath("ensure-mailboxes.ps1");
            var startInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-InputJson");
            startInfo.ArgumentList.Add(tempPath);
            startInfo.ArgumentList.Add("-AdminUpn");
            startInfo.ArgumentList.Add(adminUpn);
            if (options.DryRun)
            {
                startInfo.ArgumentList.Add("-WhatIf");
            }

            using var process = Process.Start(startInfo)
                                ?? throw new CliException("pwsh konnte nicht gestartet werden.");

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new CliException($"Postfach-Setup endete mit Exit-Code {process.ExitCode}.");
            }

            return 0;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // Temporäre Datei ist unkritisch.
            }
        }
    }
}
