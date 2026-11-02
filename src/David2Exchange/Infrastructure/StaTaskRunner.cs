// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace David2Exchange.Infrastructure;

/// <summary>
/// Führt eine asynchrone Operation auf einem einzelnen STA-Thread aus und
/// pumpt alle await-Fortsetzungen auf demselben Thread. Das ist für die
/// DvApi-COM-Objekte nötig, die an den STA gebunden sind.
/// </summary>
internal static class StaTaskRunner
{
    public static T Run<T>(Func<Task<T>> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            using var context = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                var task = action();
                task.ContinueWith(
                    _ => context.Complete(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                context.RunOnCurrentThread();
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            d(state);
        }

        public void RunOnCurrentThread()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Complete()
        {
            _queue.CompleteAdding();
        }

        public void Dispose()
        {
            _queue.Dispose();
        }
    }
}
