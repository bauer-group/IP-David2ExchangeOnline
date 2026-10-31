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

        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)
                            ?? throw new CliException("pwsh konnte nicht gestartet werden.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(stderr);
            throw new CliException($"Postfach-Liste endete mit Exit-Code {process.ExitCode}.");
        }

        var jsonText = ExtractJson(stdout);
        return JsonSerializer.Deserialize<List<MailboxTarget>>(jsonText, JsonOptions)
               ?? new List<MailboxTarget>();
    }

    private static string ExtractJson(string output)
    {
        var span = output.AsSpan();
        var start = span.IndexOf('[');
        var end = span.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            throw new CliException("Postfach-Liste lieferte kein JSON-Array.");
        }

        return output[start..(end + 1)];
    }
}
