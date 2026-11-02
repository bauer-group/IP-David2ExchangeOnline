// SPDX-License-Identifier: MIT
using David2Exchange.Config;
using David2Exchange.Infrastructure;
using David2Exchange.Probe;

namespace David2Exchange.Cli;

public static class AppCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 3 : 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "setup" => RunSetup(args[1..]),
            "config" => RunConfig(args[1..]),
            "mailboxes" => RunMailboxes(args[1..]),
            "sources" => RunSources(args[1..]),
            "targets" => RunTargets(args[1..]),
            "map" => RunMap(args[1..]),
            "probe" => RunProbe(args[1..]),
            "discover" => RunDiscover(args[1..]),
            "run" => RunMigration(args[1..]),
            "verify" => RunVerify(args[1..]),
            "report" => RunReport(args[1..]),
            "version" or "--version" => PrintVersion(),
            _ => UnknownCommand(args[0])
        };
    }

    private static int RunProbe(string[] args)
    {
        var options = new ProbeOptions();

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--server":
                        options.Server = NextValue(args, ref i, "--server");
                        break;
                    case "--progid":
                        options.ProgId = NextValue(args, ref i, "--progid");
                        break;
                    case "--logon":
                        options.Logon = NextValue(args, ref i, "--logon");
                        break;
                    case "--archive":
                        options.Archives.Add(NextValue(args, ref i, "--archive"));
                        break;
                    case "--exclude-path":
                        options.ExcludePaths.Add(NextValue(args, ref i, "--exclude-path"));
                        break;
                    case "--type":
                        options.Types.Add(NextValue(args, ref i, "--type"));
                        break;
                    case "--exclude-type":
                        options.ExcludeTypes.Add(NextValue(args, ref i, "--exclude-type"));
                        break;
                    case "--out":
                        options.OutputDirectory = NextValue(args, ref i, "--out");
                        break;
                    case "--output":
                        options.OutputKind = NextValue(args, ref i, "--output");
                        break;
                    case "--target-upn":
                        options.TargetUpn = NextValue(args, ref i, "--target-upn");
                        break;
                    case "--ews-url":
                        options.EwsUrl = NextValue(args, ref i, "--ews-url");
                        break;
                    case "--tenant-id":
                        options.TenantId = NextValue(args, ref i, "--tenant-id");
                        break;
                    case "--client-id":
                        options.ClientId = NextValue(args, ref i, "--client-id");
                        break;
                    case "--cert-thumbprint":
                        options.CertThumbprint = NextValue(args, ref i, "--cert-thumbprint");
                        break;
                    case "--ews-folder":
                        options.EwsFolder = NextValue(args, ref i, "--ews-folder");
                        break;
                    case "--max-items":
                        options.MaxItemsPerArchive = ParseInt(NextValue(args, ref i, "--max-items"), "--max-items", 0);
                        break;
                    case "--export-eml":
                        options.ExportEmlPerArchive = ParseInt(NextValue(args, ref i, "--export-eml"), "--export-eml", 0);
                        break;
                    case "--no-recursive":
                        options.Recursive = false;
                        break;
                    case "--list-members":
                        options.ListMembers = true;
                        break;
                    case "--counts":
                        options.Counts = true;
                        break;
                    case "--full-scan":
                        options.FullScan = true;
                        break;
                    case "--mock":
                        options.Mock = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintProbeHelp();
                        return 0;
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            if (options.Archives.Count == 0)
            {
                options.Archives.Add(@"user\@me");
                options.Archives.Add(@"common\unverteilt");
            }

            options.Validate();
            return new ProbeRunner().RunAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            Console.Error.WriteLine("Hilfe: david2exchange probe --help");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 2;
        }
    }

    private static int RunSetup(string[] args)
    {
        try
        {
            var options = new Setup.SetupOptions();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        options.ConfigPath = NextValue(args, ref i, "--config");
                        break;
                    case "--check":
                        options.CheckOnly = true;
                        break;
                    case "--remove":
                        options.Remove = true;
                        break;
                    case "--extract-only":
                        options.ExtractOnly = true;
                        break;
                    case "--app-name":
                        options.AppName = NextValue(args, ref i, "--app-name");
                        break;
                    case "--cert-thumbprint":
                        options.CertificateThumbprint = NextValue(args, ref i, "--cert-thumbprint");
                        break;
                    case "--policy-group":
                        options.AccessPolicyGroup = NextValue(args, ref i, "--policy-group");
                        break;
                    case "--help":
                    case "-h":
                        Console.WriteLine(
                            """
                            david2exchange setup --config <yaml> [--check | --remove]
                                                 [--app-name <name>] [--cert-thumbprint <t>]
                                                 [--policy-group <group>]
                            david2exchange setup --extract-only
                            """);
                        return 0;
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            if (!options.ExtractOnly && string.IsNullOrWhiteSpace(options.ConfigPath))
            {
                options.ConfigPath = ToolPaths.DefaultConfigPath;
            }

            return new Setup.SetupRunner().RunAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunConfig(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine(
                    """
                    david2exchange config create --config <yaml> [--domain <domain>]
                                                 [--targets-file <targets.json>]
                                                 [--sources-file <sources.json>]
                                                 [--unverteilt-upn <upn>] [--admin-upn <upn>]
                                                 [--progid <progid>] [--logon AUTH]
                                                 [--server <name>] [--archive-root <path>]
                                                 [--wave <name>] [--create-if-missing] [--dry-run]
                    """);
                return args.Length == 0 ? 3 : 0;
            }

            if (!args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Unbekanntes config-Kommando '{args[0]}'.");
            }

            var options = new Config.ConfigCreateOptions();
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        options.ConfigPath = NextValue(args, ref i, "--config");
                        break;
                    case "--domain":
                        options.Domain = NextValue(args, ref i, "--domain");
                        break;
                    case "--targets-file":
                        options.TargetsFile = NextValue(args, ref i, "--targets-file");
                        break;
                    case "--sources-file":
                        options.SourcesFile = NextValue(args, ref i, "--sources-file");
                        break;
                    case "--unverteilt-upn":
                        options.UnverteiltUpn = NextValue(args, ref i, "--unverteilt-upn");
                        break;
                    case "--admin-upn":
                        options.AdminUpn = NextValue(args, ref i, "--admin-upn");
                        break;
                    case "--progid":
                        options.ProgId = NextValue(args, ref i, "--progid");
                        break;
                    case "--logon":
                        options.Logon = NextValue(args, ref i, "--logon");
                        break;
                    case "--server":
                        options.DavidServer = NextValue(args, ref i, "--server");
                        break;
                    case "--archive-root":
                        options.ArchiveRoot = NextValue(args, ref i, "--archive-root");
                        break;
                    case "--wave":
                        options.Wave = NextValue(args, ref i, "--wave");
                        break;
                    case "--create-if-missing":
                        options.CreateIfMissing = true;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--help":
                    case "-h":
                        return RunConfig(new[] { "--help" });
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            return Runner.ConfigCreateRunner.RunAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunMailboxes(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.WriteLine(
                    """
                    david2exchange mailboxes ensure --config <yaml> [--wave <name>]
                                                    [--admin-upn <upn>] [--dry-run]
                    """);
                return args.Length == 0 ? 3 : 0;
            }

            if (!args[0].Equals("ensure", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"Unbekanntes mailboxes-Kommando '{args[0]}'.");
            }

            var options = new Setup.MailboxProvisionOptions();
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        options.ConfigPath = NextValue(args, ref i, "--config");
                        break;
                    case "--wave":
                        options.Wave = NextValue(args, ref i, "--wave");
                        break;
                    case "--admin-upn":
                        options.AdminUpn = NextValue(args, ref i, "--admin-upn");
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--help":
                    case "-h":
                        return RunMailboxes(new[] { "--help" });
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            return Setup.MailboxProvisioner.RunAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunSources(string[] args)
    {
        try
        {
            var config = "";
            var json = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        config = NextValue(args, ref i, "--config");
                        break;
                    case "--json":
                        json = true;
                        break;
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(config))
            {
                config = ToolPaths.DefaultConfigPath;
            }

            return Runner.SourceLister.Run(config, json);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunTargets(string[] args)
    {
        try
        {
            var json = false;
            string? outputPath = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--json":
                        json = true;
                        break;
                    case "--out":
                        outputPath = NextValue(args, ref i, "--out");
                        break;
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            return Runner.TargetLister
                .RunAsync(json, outputPath)
                .GetAwaiter()
                .GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunMap(string[] args)
    {
        try
        {
            var config = "";
            string? source = null;
            string? target = null;
            string? id = null;
            string? kind = null;
            string? sourceEmail = null;
            string? wave = null;
            var list = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        config = NextValue(args, ref i, "--config");
                        break;
                    case "--source":
                        source = NextValue(args, ref i, "--source");
                        break;
                    case "--target":
                        target = NextValue(args, ref i, "--target");
                        break;
                    case "--id":
                        id = NextValue(args, ref i, "--id");
                        break;
                    case "--kind":
                        kind = NextValue(args, ref i, "--kind");
                        break;
                    case "--source-email":
                        sourceEmail = NextValue(args, ref i, "--source-email");
                        break;
                    case "--wave":
                        wave = NextValue(args, ref i, "--wave");
                        break;
                    case "--list":
                        list = true;
                        break;
                    default:
                        throw new CliException($"Unbekannte Option: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(config))
            {
                config = ToolPaths.DefaultConfigPath;
            }

            return Runner.MappingRunner.Run(
                config,
                source,
                target,
                id,
                kind,
                list,
                sourceEmail,
                wave);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunDiscover(string[] args)
    {
        try
        {
            var options = ParseRunOptions(args, allowDryRun: false);
            return new Runner.MigrationRunner().DiscoverAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunMigration(string[] args)
    {
        try
        {
            var options = ParseRunOptions(args, allowDryRun: true);
            return new Runner.MigrationRunner().RunAsync(options).GetAwaiter().GetResult();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunVerify(string[] args)
    {
        try
        {
            var options = ParseRunOptions(args, allowDryRun: false);
            return new Runner.MigrationRunner().Verify(options);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static int RunReport(string[] args)
    {
        try
        {
            var options = ParseRunOptions(args, allowDryRun: false);
            return new Runner.MigrationRunner().Report(options);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }
    }

    private static Runner.RunOptions ParseRunOptions(string[] args, bool allowDryRun)
    {
        var options = new Runner.RunOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    options.ConfigPath = NextValue(args, ref i, "--config");
                    break;
                case "--mailbox":
                    options.MailboxId = NextValue(args, ref i, "--mailbox");
                    break;
                case "--wave":
                    options.Wave = NextValue(args, ref i, "--wave");
                    break;
                case "--limit":
                    options.Limit = ParseInt(NextValue(args, ref i, "--limit"), "--limit", 0);
                    break;
                case "--all":
                    options.Limit = 0;
                    break;
                case "--newest":
                    options.Newest = true;
                    break;
                case "--oldest":
                    options.Newest = false;
                    break;
                case "--dry-run":
                    if (!allowDryRun)
                    {
                        throw new CliException("--dry-run ist nur für 'run' erlaubt.");
                    }

                    options.DryRun = true;
                    break;
                case "--output":
                    options.OutputKind = NextValue(args, ref i, "--output");
                    break;
                case "--out":
                    options.ReportPath = NextValue(args, ref i, "--out");
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        """
                        david2exchange discover --config <yaml> [--mailbox <id>] [--wave <name>] [--limit N|--all]
                        david2exchange run      --config <yaml> [--mailbox <id>] [--wave <name>] [--limit N|--all]
                                                [--newest|--oldest] [--dry-run] [--output local|ews]
                        david2exchange verify   --config <yaml> [--mailbox <id>] [--wave <name>]
                        david2exchange report   --config <yaml> [--mailbox <id>] [--out report.html]
                        """);
                    Environment.Exit(0);
                    break;
                default:
                    throw new CliException($"Unbekannte Option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            options.ConfigPath = ToolPaths.DefaultConfigPath;
        }

        return options;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new CliException($"Option {option} erwartet einen Wert.");
        }

        index++;
        return args[index];
    }

    private static int ParseInt(string value, string option, int minimum)
    {
        if (!int.TryParse(value, out var result) || result < minimum)
        {
            throw new CliException($"Option {option} erwartet eine ganze Zahl >= {minimum}.");
        }

        return result;
    }

    private static bool IsHelp(string value)
    {
        return value is "-h" or "--help" or "help" or "/?";
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"david2exchange {ToolInfo.Version}");
        Console.WriteLine($"Prozess: {(Environment.Is64BitProcess ? "x64" : "x86")} · {Environment.OSVersion}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unbekanntes Kommando: {command}");
        PrintHelp();
        return 3;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            david2exchange – David → Exchange-Migrationstool (frühe Probe-Version)

            Verwendung:
              david2exchange setup --config <yaml> [--check|--remove]
              david2exchange config create --config <yaml> [--targets-file <json>]
              david2exchange mailboxes ensure --config <yaml> [--admin-upn <upn>] [--dry-run]
              david2exchange sources --config <yaml> [--json]
              david2exchange targets [--json] [--out targets.json]
              david2exchange map --config <yaml> [--list | --source <p> --target <upn> --id <id>]
              david2exchange discover --config <yaml> [--limit N|--all]
              david2exchange run --config <yaml> [--limit N|--all] [--dry-run] [--output local|ews]
              david2exchange report --config <yaml>
              david2exchange probe [Optionen]     David-COM testen, Objekte lokal schreiben
              david2exchange version              Version und Prozessbitbreite
              david2exchange --help               Diese Hilfe

            Der Befehl "probe" schreibt nur lokal. Es gibt in dieser Version
            keine EWS-Verbindung und keinen Versand.

            Beispiele:
              david2exchange probe --mock
              david2exchange probe --server SRV-BAUER --archive "user\@me" --archive "common\unverteilt"
              david2exchange probe --export-eml 5 --max-items 100 --out probe-out
              david2exchange probe --archive "user\@me" --type 2 --export-eml 3 --output ews --target-upn test@example.com ...
            """);
    }

    private static void PrintProbeHelp()
    {
        Console.WriteLine(
            """
            david2exchange probe – DvApi32-Zugriff und Objektabruf prüfen

            Optionen:
              --server <name>        Nur für den Report; DvApi nutzt den installierten Client.
              --progid <progid>      COM-ProgID (Default: DVOBJAPILib.DvISEAPI)
              --logon <modus>        David-Logon (Default: AUTH = angemeldeter Windows-Benutzer)
              --archive <pfad>       Archivpfad, mehrfach angebbar.
                                     Default: user\@me und common\unverteilt
                                     @me = eigenes Benutzerarchiv aus Account.User.ID
              --exclude-path <text>  Items überspringen, deren ArchivePath den Text enthält
              --type <code>          Nur diesen Item-Typ lesen (mehrfach; 2 = Mail, 1 = Adresse)
              --exclude-type <code>  Diesen Item-Typ überspringen (mehrfach)
              --out <verzeichnis>    Ausgabeverzeichnis (Default: probe-out)
              --output <art>         Ausgabeport: local (Default), ews, graph/pst geplant
              --target-upn <upn>     Zielpostfach für EWS-Impersonation
              --ews-url <url>        EWS-Endpunkt (Default: Exchange Online)
              --tenant-id <id>       Entra-Tenant für MSAL
              --client-id <id>       App-ID für MSAL
              --cert-thumbprint <t>  Zertifikat in CurrentUser\My
              --ews-folder <name>    Zielordner (Default: David-Probe)
              --max-items <n>        Maximal n Beispiel-Items je Archiv (Default: 50)
              --export-eml <n>       Maximal n Mails je Archiv als EML exportieren (Default: 5)
              --no-recursive         Unterarchive nicht durchlaufen
              --list-members         COM-Member inventarisieren (Diagnose)
              --counts               AllItems.Count je Archiv lesen (kann langsam sein)
              --full-scan            Alle Items lesen (statt nur --max-items je Archiv)
              --mock                 Ohne David-Client mit Beispieldaten laufen

            Exit-Codes:
              0 = alles gelesen
              1 = Warnungen (z. B. einzelne Items/EML)
              2 = kein Archiv geöffnet / FATAL
              3 = Aufruffehler

            Typische Reihenfolge auf dem David-Rechner:
              1. probe --mock
              2. probe --list-members --max-items 5
              3. probe --archive "user\@me" --archive "common\unverteilt"
              4. probe --export-eml 5
            """);
    }
}
