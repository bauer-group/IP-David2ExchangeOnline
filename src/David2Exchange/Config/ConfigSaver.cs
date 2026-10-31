// SPDX-License-Identifier: MIT
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace David2Exchange.Config;

public static class ConfigSaver
{
    public static void Save(MigrationConfig config, string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var backup = fullPath + ".bak";
        if (File.Exists(fullPath))
        {
            File.Copy(fullPath, backup, overwrite: true);
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        File.WriteAllText(fullPath, serializer.Serialize(config));
    }
}
