// SPDX-License-Identifier: MIT
namespace David2Exchange.Runner;

public sealed class RunOptions
{
    public string ConfigPath { get; set; } = "";

    public string? MailboxId { get; set; }

    public string? Wave { get; set; }

    /// <summary>-1 = use limits.limit from migration.yaml; 0 = all.</summary>
    public int Limit { get; set; } = -1;

    public bool Newest { get; set; } = true;

    public bool DryRun { get; set; }

    public string? OutputKind { get; set; }

    public string? ReportPath { get; set; }

    public bool DiscoverOnly { get; set; }

    /// <summary>Discover and export EMLs into staging/LiteDB, but do not import.</summary>
    public bool Prepare { get; set; }
}
