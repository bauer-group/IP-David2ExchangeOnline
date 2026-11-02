// SPDX-License-Identifier: MIT
using David2Exchange.Config;
using David2Exchange.Source;
using David2Exchange.Source.David;

namespace David2Exchange.Runner;

public sealed class UnpackOptions
{
    public string ConfigPath { get; set; } = "";
    public bool DryRun { get; set; }
}

public static class UnpackRunner
{
    public static Task<int> RunAsync(
        UnpackOptions options,
        CancellationToken cancellationToken = default)
    {
        var config = ConfigLoader.Load(options.ConfigPath);

        using var session = new DavidComSession(config.David.ProgId);
        session.Logon(config.David.Logon);

        var roots = session.ListArchives(maxDepth: 2)
            .Where(descriptor =>
                (descriptor.Kind == "COMMON" && descriptor.Depth == 0) ||
                (descriptor.Kind == "USER" && descriptor.Depth == 1))
            .Select(descriptor => descriptor.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            Console.Error.WriteLine("Keine COMMON-/USER-Archive gefunden.");
            return Task.FromResult(2);
        }

        Console.WriteLine(
            "WARNUNG: Faxe werden nur migriert, wenn die Archive entpackt sind.");
        Console.WriteLine(
            "Nach diesem Befehl muss die David-Datenbereinigung/Archiv-Optimierung " +
            "durch den David-Administrator laufen.");
        Console.WriteLine();

        var archives = 0;
        var changed = 0;
        var failed = 0;

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archive = session.OpenArchive(root);
            Recurse(archive, options.DryRun, ref archives, ref changed, ref failed);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Archive: {archives} · geändert: {changed} · Fehler: {failed}" +
            (options.DryRun ? " · Dry-Run" : ""));

        if (options.DryRun)
        {
            Console.WriteLine("Dry-Run: Es wurden keine Änderungen gespeichert.");
        }
        else
        {
            Console.WriteLine(
                "Jetzt die David-Datenbereinigung/Archiv-Optimierung starten. " +
                "Erst danach die Faxmigration erneut ausführen.");
        }

        return Task.FromResult(failed == 0 ? 0 : 1);
    }

    private static void Recurse(
        IDavidArchive archive,
        bool dryRun,
        ref int archives,
        ref int changed,
        ref int failed)
    {
        archives++;
        if (dryRun)
        {
            Console.WriteLine($"  DRY-RUN {archive.ResolvedPath}");
        }
        else if (archive.TrySetUnpacked(out var error))
        {
            changed++;
            Console.WriteLine($"  OK      {archive.ResolvedPath}");
        }
        else
        {
            failed++;
            Console.Error.WriteLine($"  FEHLER  {archive.ResolvedPath}: {error}");
        }

        foreach (var child in archive.SubArchives)
        {
            Recurse(child, dryRun, ref archives, ref changed, ref failed);
        }
    }
}
