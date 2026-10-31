// SPDX-License-Identifier: MIT
namespace David2Exchange.Runner;

public sealed class RunOptions
{
    public string ConfigPath { get; set; } = "";

    public string? MailboxId { get; set; }

    public string? Wave { get; set; }

    public int Limit { get; set; }

    public bool Newest { get; set; } = true;

    public bool DryRun { get; set; }

    public string? OutputKind { get; set; }

    public string? ReportPath { get; set; }

    public bool DiscoverOnly { get; set; }
}
