// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using David2Exchange.Infrastructure;
using Xunit;

namespace David2Exchange.Tests.Runner;

public sealed class EnsureMailboxesScriptTests
{
    private static string Script() => File.ReadAllText(EmbeddedScripts.GetScriptPath("ensure-mailboxes.ps1"));

    [Fact]
    public void New_shared_mailbox_only_uses_parameters_of_the_shared_set()
    {
        // Laut Microsoft-Doku enthält der Parametersatz von -Shared kein -MicrosoftOnlineServicesID.
        var call = Regex.Match(Script(), @"New-Mailbox\s.*?-WhatIf:\$WhatIf", RegexOptions.Singleline).Value;

        Assert.Contains("-Shared", call, StringComparison.Ordinal);
        Assert.DoesNotContain("-MicrosoftOnlineServicesID", call, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_in_name_is_set_on_existing_mailboxes_and_confirmed_by_reading_it_back()
    {
        var script = Script();

        Assert.Equal(3, Regex.Count(script, "Set-SignInName"));
        Assert.Contains("UserPrincipalName -eq $address", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Warning_text_is_not_concatenated_in_argument_mode()
    {
        // Write-Warning "a" + "b" bricht in PowerShell mit einem Parameterfehler ab.
        Assert.DoesNotMatch(@"Write-Warning\s+""[^""]*""\s*\+", Script());
    }
}
