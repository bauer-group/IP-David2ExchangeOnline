// SPDX-License-Identifier: MIT
namespace David2Exchange.Infrastructure;

public static class ToolPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string DefaultConfigPath => Path.Combine(BaseDirectory, "migration.yaml");

    public static string DefaultLogDirectory => Path.Combine(BaseDirectory, "logs");

    public static string Resolve(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(BaseDirectory, path));
    }
}
