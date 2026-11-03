// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace David2Exchange.Output;

public sealed class LocalFileOutputTarget : IOutputTarget
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _writeGate = new();
    private string _outputDirectory = "";
    private string _emlDirectory = "";
    private StreamWriter? _itemsWriter;

    public string Name => "local";

    public Task OpenAsync(OutputContext context, CancellationToken cancellationToken)
    {
        _outputDirectory = Path.GetFullPath(context.OutputDirectory);
        _emlDirectory = Path.Combine(_outputDirectory, "eml");

        Directory.CreateDirectory(_outputDirectory);
        Directory.CreateDirectory(_emlDirectory);

        _itemsWriter = new StreamWriter(
            Path.Combine(_outputDirectory, "items.jsonl"),
            append: context.AppendItems,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        return Task.CompletedTask;
    }

    public Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken)
    {
        if (_itemsWriter is null)
        {
            throw new InvalidOperationException("OutputTarget wurde nicht geöffnet.");
        }

        var line = JsonSerializer.Serialize(item, JsonOptions);
        lock (_writeGate)
        {
            _itemsWriter.WriteLine(line);
        }

        if (!string.IsNullOrWhiteSpace(item.StagedEmlPath) && File.Exists(item.StagedEmlPath))
        {
            var fileName = Path.GetFileName(item.EmlFile ?? item.StagedEmlPath);
            var destination = Path.Combine(_emlDirectory, fileName);
            File.Copy(item.StagedEmlPath, destination, overwrite: true);
        }

        return Task.CompletedTask;
    }

    public async Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken)
    {
        if (_itemsWriter is not null)
        {
            await _itemsWriter.FlushAsync(cancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(_outputDirectory, "output-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_itemsWriter is not null)
        {
            await _itemsWriter.DisposeAsync();
            _itemsWriter = null;
        }
    }
}
