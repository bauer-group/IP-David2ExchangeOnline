// SPDX-License-Identifier: MIT
namespace David2Exchange.Runner;

/// <summary>
/// Begrenzt die Bytes, die alle Worker zusammen gleichzeitig senden. Ein gesendetes Byte belegt im
/// x86-Prozess gemessen rund das Sechs- bis Siebenfache (EML, Base64, XML, Large Object Heap).
/// <para>
/// Die Reihenfolge bleibt erhalten, damit laufend kleine Batches einen großen nicht aushungern. Was
/// größer ist als das Budget, belegt es ganz und läuft dadurch allein.
/// </para>
/// </summary>
internal sealed class ByteBudget : IDisposable
{
    private readonly long _capacity;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly object _gate = new();
    private long _available;
    private TaskCompletionSource _released = NewSignal();

    public ByteBudget(long capacity)
    {
        _capacity = capacity;
        _available = capacity;
    }

    /// <summary>Wartet auf freies Budget und liefert den Wert, den <see cref="Release"/> zurückgibt.</summary>
    public async Task<long> AcquireAsync(long bytes, CancellationToken cancellationToken)
    {
        var reserved = Math.Min(bytes, _capacity);

        // Nur der Vorderste wartet auf Budget; ohne ihn könnten sich zwei Teilreservierungen blockieren.
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task released;
                lock (_gate)
                {
                    if (_available >= reserved)
                    {
                        _available -= reserved;
                        return reserved;
                    }

                    released = _released.Task;
                }

                await released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Release(long reserved)
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            _available += reserved;
            released = _released;
            _released = NewSignal();
        }

        released.TrySetResult();
    }

    public void Dispose()
    {
        _turn.Dispose();
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
