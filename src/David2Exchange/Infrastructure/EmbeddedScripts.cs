// SPDX-License-Identifier: MIT
using System.Reflection;

namespace David2Exchange.Infrastructure;

/// <summary>
/// Extrahiert die eingebetteten Setup-Skripte zur Laufzeit. Dadurch bleibt die
/// veröffentlichte Single-File-EXE eigenständig; es muss kein Setup-Ordner
/// neben der EXE liegen.
/// </summary>
public static class EmbeddedScripts
{
    public static string GetScriptPath(string fileName)
    {
        var assembly = typeof(EmbeddedScripts).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                $"Eingebettetes Skript '{fileName}' nicht gefunden. " +
                $"Vorhanden: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "david2exchange", ToolInfo.Version);
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, fileName);

        using var resource = assembly.GetManifestResourceStream(resourceName)
                             ?? throw new InvalidOperationException($"Ressource '{resourceName}' nicht lesbar.");
        using var file = File.Create(targetPath);
        resource.CopyTo(file);

        return targetPath;
    }
}
