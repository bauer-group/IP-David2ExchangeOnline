// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using David2Exchange.Cli;

namespace David2Exchange.Runner;

public sealed class MailboxTarget
{
    public string DisplayName { get; init; } = "";
    public string PrimarySmtpAddress { get; init; } = "";
    public string RecipientTypeDetails { get; init; } = "";
    public List<string> EmailAddresses { get; init; } = new();
}

public static class TargetLister
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(bool json, CancellationToken cancellationToken = default)
    {
        var targets = await LoadAsync(cancellationToken);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(targets, JsonOptions));
            return 0;
        }

        Console.WriteLine($"{"Typ",-16} {"UPN",-48} Anzeigename");
        Console.WriteLine(new string('-', 100));
        foreach (var mailbox in targets)
        {
            Console.WriteLine(
                $"{mailbox.RecipientTypeDetails,-16} {mailbox.PrimarySmtpAddress,-48} {mailbox.DisplayName}");
        }

        Console.WriteLine();
        Console.WriteLine($"Gesamt: {targets.Count} Postfächer (User/Shared)");
        return 0;
    }

    public static async Task<IReadOnlyList<MailboxTarget>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var scriptPath = Infrastructure.EmbeddedScripts.GetScriptPath("list-targets.ps1");
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"david2exchange-targets-{Guid.NewGuid():N}.json");

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
        startInfo.ArgumentList.Add("-OutputJson");
        startInfo.ArgumentList.Add(resultPath);

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new CliException("pwsh konnte nicht gestartet werden.");

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new CliException($"Postfach-Liste endete mit Exit-Code {process.ExitCode}.");
            }

            if (!File.Exists(resultPath))
            {
                throw new CliException("Postfach-Liste lieferte kein JSON-Ergebnis.");
            }

            var jsonText = await File.ReadAllTextAsync(resultPath, cancellationToken);
            return JsonSerializer.Deserialize<List<MailboxTarget>>(jsonText, JsonOptions)
                   ?? new List<MailboxTarget>();
        }
        finally
        {
            try
            {
                File.Delete(resultPath);
            }
            catch (Exception)
            {
                // Temporäre Datei ist unkritisch.
            }
        }
    }
}
