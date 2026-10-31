// SPDX-License-Identifier: MIT
namespace David2Exchange.Setup;

public sealed class MailboxProvisionOptions
{
    public string ConfigPath { get; set; } = "";
    public string Wave { get; set; } = "";
    public string AdminUpn { get; set; } = "";
    public bool DryRun { get; set; }
}
