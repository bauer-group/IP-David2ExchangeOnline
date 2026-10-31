// SPDX-License-Identifier: MIT
using David2Exchange.Cli;

namespace David2Exchange.Config;

public sealed class ProbeOptions
{
    public string Server { get; set; } = "(installierter David-Client)";

    public string ProgId { get; set; } = "DVOBJAPILib.DvISEAPI";

    public string Logon { get; set; } = "AUTH";

    public List<string> Archives { get; } = new();

    public List<string> ExcludePaths { get; } = new();

    public List<string> Types { get; } = new();

    public List<string> ExcludeTypes { get; } = new();

    public string OutputDirectory { get; set; } = "probe-out";

    public string OutputKind { get; set; } = "local";

    public string TargetUpn { get; set; } = "";

    public string EwsUrl { get; set; } = "https://outlook.office365.com/EWS/Exchange.asmx";

    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string CertThumbprint { get; set; } = "";

    public string EwsFolder { get; set; } = "David-Probe";

    public int MaxItemsPerArchive { get; set; } = 50;

    public int ExportEmlPerArchive { get; set; } = 5;

    public bool Recursive { get; set; } = true;

    public bool ListMembers { get; set; }

    public bool Counts { get; set; }

    public bool FullScan { get; set; }

    public bool Mock { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProgId))
        {
            throw new CliException("--progid darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(Logon))
        {
            throw new CliException("--logon darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new CliException("--out darf nicht leer sein.");
        }

        if (string.IsNullOrWhiteSpace(OutputKind))
        {
            throw new CliException("--output darf nicht leer sein.");
        }

        if (Archives.Count == 0)
        {
            throw new CliException("Mindestens ein --archive angeben.");
        }

        if (Archives.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException("--archive darf nicht leer sein.");
        }

        if (ExcludePaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException("--exclude-path darf nicht leer sein.");
        }

        if (Types.Any(string.IsNullOrWhiteSpace) || ExcludeTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new CliException("--type/--exclude-type darf nicht leer sein.");
        }

        if (MaxItemsPerArchive < 1)
        {
            throw new CliException("--max-items muss >= 1 sein.");
        }

        if (ExportEmlPerArchive < 0)
        {
            throw new CliException("--export-eml muss >= 0 sein.");
        }

        if (OutputKind.Equals("ews", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(TargetUpn) ||
                string.IsNullOrWhiteSpace(TenantId) ||
                string.IsNullOrWhiteSpace(ClientId) ||
                string.IsNullOrWhiteSpace(CertThumbprint) ||
                string.IsNullOrWhiteSpace(EwsUrl))
            {
                throw new CliException(
                    "Ausgabe 'ews' benötigt --target-upn, --tenant-id, --client-id, " +
                    "--cert-thumbprint und --ews-url.");
            }

            if (ExportEmlPerArchive < 1)
            {
                throw new CliException(
                    "Ausgabe 'ews' benötigt --export-eml > 0 (EWS importiert EML-Dateien). " +
                    "Für Mail: --type 2 --export-eml 5.");
            }
        }
    }
}
