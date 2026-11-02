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

    async Task<IReadOnlyList<OutputResult>> WriteBatchAsync(
        IReadOnlyList<OutputItem> items,
        CancellationToken cancellationToken)
    {
        var results = new List<OutputResult>(items.Count);
        foreach (var item in items)
        {
            try
            {
                await WriteItemAsync(item, cancellationToken);
                results.Add(new OutputResult(item, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new OutputResult(item, false, ex.Message));
            }
        }

        return results;
    }

    Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken);
}
