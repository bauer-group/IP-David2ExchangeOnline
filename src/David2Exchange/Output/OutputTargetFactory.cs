// SPDX-License-Identifier: MIT
using David2Exchange.Cli;
using David2Exchange.Config;

namespace David2Exchange.Output;

public static class OutputTargetFactory
{
    public static string CanonicalKind(string kind)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        return normalized == "file" ? "local" : normalized;
    }

    /// <summary>
    /// Kennung des Übertragungsziels im Manifest. Bei EWS gehört das Postfach dazu: Nach dem
    /// Wechsel des Zielpostfachs zählt ein früherer Import nicht mehr als übertragen.
    /// </summary>
    public static string TargetName(string kind, string targetUpn)
    {
        return CanonicalKind(kind) switch
        {
            "local" => "local",
            "ews" => $"ews:{targetUpn.Trim().ToLowerInvariant()}",
            _ => throw new CliException($"Unbekannte Ausgabe '{kind}'. Erlaubt: local, ews.")
        };
    }

    public static IOutputTarget Create(ProbeOptions options)
    {
        return Create(new OutputSettings(
            options.OutputKind,
            options.TargetUpn,
            options.EwsUrl,
            options.TenantId,
            options.ClientId,
            options.CertThumbprint,
            options.EwsFolder));
    }

    public static IOutputTarget Create(OutputSettings settings)
    {
        return settings.Kind.Trim().ToLowerInvariant() switch
        {
            "local" or "file" => new LocalFileOutputTarget(),
            "ews" => new EwsOutputTarget(new EwsOutputOptions(
                settings.TargetUpn,
                settings.EwsUrl,
                settings.TenantId,
                settings.ClientId,
                settings.CertificateThumbprint,
                settings.FolderName,
                settings.TimeZone,
                settings.Workers)),
            "graph" => throw new CliException(
                "Ausgabe 'graph' ist noch nicht implementiert. " +
                "Erweiterungspunkt: IOutputTarget implementieren und hier registrieren."),
            "pst" => throw new CliException(
                "Ausgabe 'pst' ist noch nicht implementiert. " +
                "Erweiterungspunkt: IOutputTarget implementieren und hier registrieren."),
            _ => throw new CliException(
                $"Unbekannte Ausgabe '{settings.Kind}'. Erlaubt: local, ews (graph/pst geplant).")
        };
    }
}
