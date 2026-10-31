// SPDX-License-Identifier: MIT
namespace David2Exchange.Output;

/// <summary>
/// Ausgabeport der Migration. Implementierungen: lokale Dateien,
/// später Graph oder PST. Der Port kennt weder EWS noch Graph.
/// </summary>
public interface IOutputTarget : IAsyncDisposable
{
    string Name { get; }

    Task OpenAsync(OutputContext context, CancellationToken cancellationToken);

    Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken);

    Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken);
}
