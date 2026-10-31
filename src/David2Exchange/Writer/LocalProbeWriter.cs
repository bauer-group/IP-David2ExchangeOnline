// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using David2Exchange.Domain;

namespace David2Exchange.Writer;

public sealed class LocalProbeWriter : IProbeWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _outputDirectory;

    public LocalProbeWriter(string outputDirectory)
    {
        _outputDirectory = Path.GetFullPath(outputDirectory);
    }

    public void Write(ProbeReport report)
    {
        Directory.CreateDirectory(_outputDirectory);

        WriteText("probe-report.json", JsonSerializer.Serialize(report, JsonOptions));
        WriteText("archive-tree.txt", BuildArchiveTree(report));
        WriteText("environment.txt", BuildEnvironment(report));

        if (report.Members.Count > 0)
        {
            WriteText("members.txt", string.Join(Environment.NewLine, report.Members));
        }
    }

    private void WriteText(string fileName, string content)
    {
        var path = Path.Combine(_outputDirectory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildArchiveTree(ProbeReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"David-Probe {report.ToolVersion} · {report.StartedUtc:O}");
        builder.AppendLine($"Mock: {report.Mock} · Recursive: {report.Recursive}");
        builder.AppendLine();

        foreach (var archive in report.Archives)
        {
            builder.AppendLine(
                $"{archive.RequestedPath} | opened={archive.Opened} | " +
                $"items={archive.ScannedItemCount} | subarchives={archive.SubArchiveCount} | " +
                $"elapsed={archive.DurationMs} ms");
            foreach (var error in archive.Errors)
            {
                builder.AppendLine($"  ! {error}");
            }
        }

        return builder.ToString();
    }

    private static string BuildEnvironment(ProbeReport report)
    {
        return $"""
            Tool:       {report.ToolVersion}
            StartedUtc: {report.StartedUtc:O}
            FinishedUtc: {report.FinishedUtc:O}
            Machine:    {report.MachineName}
            User:       {report.UserName}
            Process:    {(report.Is64BitProcess ? "x64" : "x86")}
            OS:         {report.OsVersion}
            .NET:       {report.RuntimeVersion}
            Server:     {report.Server}
            ProgID:     {report.ProgId}
            Logon:      {report.Logon}
            Mock:       {report.Mock}
            Items:      {report.TotalItems}
            Sampled:    {report.TotalSampled}
            EML:        {report.TotalEmlExported}
            """;
    }
}
