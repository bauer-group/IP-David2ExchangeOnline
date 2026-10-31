// SPDX-License-Identifier: MIT
using David2Exchange.Cli;
using David2Exchange.Config;

namespace David2Exchange.Output;

public static class OutputTargetFactory
{
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
                settings.FolderName)),
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
