// SPDX-License-Identifier: MIT
namespace David2Exchange.Cli;

public sealed class CliException : Exception
{
    public CliException(string message) : base(message)
    {
    }
}
