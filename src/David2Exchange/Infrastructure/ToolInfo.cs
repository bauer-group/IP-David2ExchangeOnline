// SPDX-License-Identifier: MIT
namespace David2Exchange.Infrastructure;

public static class ToolInfo
{
    public static string Version =>
        typeof(ToolInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
