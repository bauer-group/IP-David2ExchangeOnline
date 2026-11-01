// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using David2Exchange.Cli;
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace David2Exchange.Setup;

public sealed class SetupRunner
{
    public async Task<int> RunAsync(SetupOptions options, CancellationToken cancellationToken = default)
    {
        if (options.ExtractOnly)
        {
            foreach (var name in new[] { "tenant-setup.ps1", "tenant-remove.ps1", "list-targets.ps1" })
            {
                Console.WriteLine(EmbeddedScripts.GetScriptPath(name));
            }

            return 0;
        }

        if (!File.Exists(options.ConfigPath))
        {
            throw new CliException($"Config '{options.ConfigPath}' nicht gefunden.");
        }

        var scriptName = options.Remove ? "tenant-remove.ps1" : "tenant-setup.ps1";
        var scriptPath = EmbeddedScripts.GetScriptPath(scriptName);
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"david2exchange-setup-{Guid.NewGuid():N}.json");

        var arguments = new List<string>
        {
            "-NoProfile",
            "-File", scriptPath,
            "-ConfigPath", Path.GetFullPath(options.ConfigPath),
            "-AppName", options.AppName,
            "-OutputJson", resultPath
        };

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
        {
            arguments.Add("-CertificateThumbprint");
            arguments.Add(options.CertificateThumbprint);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessPolicyGroup))
        {
            arguments.Add("-AccessPolicyGroup");
            arguments.Add(options.AccessPolicyGroup);
        }

        if (options.CheckOnly)
        {
            arguments.Add("-CheckOnly");
        }

        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new CliException("pwsh konnte nicht gestartet werden.");

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new CliException($"Setup-Skript endete mit Exit-Code {process.ExitCode}.");
            }

            if (!File.Exists(resultPath))
            {
                throw new CliException("Setup-Skript lieferte kein JSON-Ergebnis.");
            }

            var json = await File.ReadAllTextAsync(resultPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

        if (options.Remove)
        {
            ClearM365(options.ConfigPath);
            Console.WriteLine("m365-Werte aus der Config entfernt.");
            return 0;
        }

        if (options.CheckOnly)
        {
            return root.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
        }

        if (root.TryGetProperty("clientId", out var clientId) &&
            root.TryGetProperty("tenantId", out var tenantId) &&
            root.TryGetProperty("certThumbprint", out var thumbprint))
        {
            UpdateM365(
                options.ConfigPath,
                tenantId.GetString() ?? "",
                clientId.GetString() ?? "",
                thumbprint.GetString() ?? "");
            Console.WriteLine("m365-Werte in die Config geschrieben.");
        }

            return 0;
        }
        finally
        {
            try
            {
                File.Delete(resultPath);
            }
            catch (Exception)
            {
                // Temporäre Ergebnisdatei ist unkritisch.
            }
        }
    }

    private static void UpdateM365(string configPath, string tenantId, string clientId, string thumbprint)
    {
        var config = ConfigLoader.Load(configPath);
        config.M365.TenantId = tenantId;
        config.M365.ClientId = clientId;
        config.M365.CertThumbprint = thumbprint;
        Save(config, configPath);
    }

    private static void ClearM365(string configPath)
    {
        var config = ConfigLoader.Load(configPath);
        config.M365.TenantId = "";
        config.M365.ClientId = "";
        config.M365.CertThumbprint = "";
        Save(config, configPath);
    }

    private static void Save(MigrationConfig config, string configPath)
    {
        var backup = configPath + ".bak";
        File.Copy(configPath, backup, overwrite: true);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        File.WriteAllText(configPath, serializer.Serialize(config));
    }
}
