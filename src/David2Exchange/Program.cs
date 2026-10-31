// SPDX-License-Identifier: MIT
using System.Text;
using David2Exchange.Cli;
using David2Exchange.Infrastructure;

namespace David2Exchange;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.Initialize();

        try
        {
            return AppCli.Run(args);
        }
        finally
        {
            Log.Shutdown();
        }
    }
}
