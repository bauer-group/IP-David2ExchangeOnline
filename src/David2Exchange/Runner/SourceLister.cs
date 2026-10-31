// SPDX-License-Identifier: MIT
using System.Text.Json;
using David2Exchange.Config;
using David2Exchange.Source.David;

namespace David2Exchange.Runner;

public static class SourceLister
{
    public static int Run(string configPath, bool json)
    {
        var config = ConfigLoader.Load(configPath);
        config.Validate(requireM365: false);

        using var session = new DavidComSession(config.David.ProgId);
        session.Logon(config.David.Logon);
        var archives = session.ListArchives(maxDepth: 2)
            .Where(archive => archive.Depth <= 1 && archive.Kind is "USER" or "COMMON")
            .ToList();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(archives, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return 0;
        }

        Console.WriteLine($"{"Art",-10} {"Pfad",-38} {"E-Mail",-38} {"Anzeigename"}");
        Console.WriteLine(new string('-', 120));
        // Benutzer und gemeinsame Archive; ADDRESS/SYSTEM werden für die Migration nicht gelistet.
        foreach (var archive in archives)
        {
            var email = string.IsNullOrWhiteSpace(archive.Email) ? "(keine)" : archive.Email;
            Console.WriteLine(
                $"{archive.Kind,-10} {archive.Path,-38} {email,-38} {archive.DisplayName}");
        }

        Console.WriteLine();
        Console.WriteLine($"Gesamt: {archives.Count} Archive");
        return 0;
    }
}
