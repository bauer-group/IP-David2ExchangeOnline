// SPDX-License-Identifier: MIT
namespace David2Exchange.Config;

public sealed class ConfigCreateOptions
{
    public string ConfigPath { get; set; } = "";
    public string Domain { get; set; } = "";
    public string? TargetsFile { get; set; }
    public string? SourcesFile { get; set; }
    public string? UnverteiltUpn { get; set; }
    public string AdminUpn { get; set; } = "";
    public string ProgId { get; set; } = "DVOBJAPILib.DvISEAPI";
    public string Logon { get; set; } = "AUTH";
    public string DavidServer { get; set; } = "david.example.local";
    public string ArchiveRoot { get; set; } = "";
    public bool DryRun { get; set; }
    public string Wave { get; set; } = "default";
    public bool CreateIfMissing { get; set; }
}
