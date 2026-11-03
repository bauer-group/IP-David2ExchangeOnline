// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using David2Exchange.Output;
using MimeKit;

namespace David2Exchange.Tests.Runner;

/// <summary>Zielsystem-Attrappe: liest die EML beim Senden, wie es auch EWS täte.</summary>
internal sealed class RecordingOutput : IOutputTarget
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _current;
    private int _maximum;

    public int HoldUntilConcurrency { get; set; }

    public ConcurrentBag<Delivered> Items { get; } = new();

    public ConcurrentBag<IReadOnlyList<OutputItem>> Batches { get; } = new();

    public ConcurrentDictionary<int, ApartmentState> Threads { get; } = new();

    public TimeSpan SendDelay { get; set; } = TimeSpan.Zero;

    public Func<OutputItem, OutputResult?>? Behavior { get; set; }

    public Func<IReadOnlyList<OutputItem>, Exception?>? BatchFailure { get; set; }

    public int MaxConcurrency => Volatile.Read(ref _maximum);

    public string Name { get; set; } = "recording";

    public Task OpenAsync(OutputContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteItemAsync(OutputItem item, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<IReadOnlyList<OutputResult>> WriteBatchAsync(
        IReadOnlyList<OutputItem> items,
        CancellationToken cancellationToken)
    {
        Threads[Environment.CurrentManagedThreadId] = Thread.CurrentThread.GetApartmentState();
        var current = Interlocked.Increment(ref _current);
        InterlockedMax(ref _maximum, current);

        try
        {
            Batches.Add(items);
            if (HoldUntilConcurrency > 0)
            {
                // Hält alle Batches an, bis die gewünschte Parallelität tatsächlich erreicht ist.
                if (current >= HoldUntilConcurrency)
                {
                    _released.TrySetResult();
                }

                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            if (SendDelay > TimeSpan.Zero)
            {
                await Task.Delay(SendDelay, cancellationToken);
            }

            var failure = BatchFailure?.Invoke(items);
            if (failure is not null)
            {
                throw failure;
            }

            var results = new List<OutputResult>(items.Count);
            foreach (var item in items)
            {
                var result = Behavior?.Invoke(item) ?? new OutputResult(item, true, null);
                if (result.Success && !result.AlreadyPresent)
                {
                    Items.Add(Delivered.From(item));
                }

                results.Add(result);
            }

            return results;
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    public Task CompleteAsync(OutputSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }
}

internal sealed record Delivered(OutputItem Item, string? EmlSubject, MimeMessage? Message)
{
    public static Delivered From(OutputItem item)
    {
        if (string.IsNullOrWhiteSpace(item.StagedEmlPath))
        {
            return new Delivered(item, null, null);
        }

        var message = MimeMessage.Load(item.StagedEmlPath);
        return new Delivered(item, message.Subject, message);
    }
}
