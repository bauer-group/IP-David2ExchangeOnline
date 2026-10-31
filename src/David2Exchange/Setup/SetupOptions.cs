// SPDX-License-Identifier: MIT
namespace David2Exchange.Setup;

public sealed class SetupOptions
{
    public string ConfigPath { get; set; } = "";
    public bool CheckOnly { get; set; }
    public bool Remove { get; set; }

    public bool ExtractOnly { get; set; }
    public string AppName { get; set; } = "david2exchange-migration";
    public string CertificateThumbprint { get; set; } = "";
    public string AccessPolicyGroup { get; set; } = "";
}
