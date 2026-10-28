using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedSongFilters
{
    // Unity's parser cannot cancel in-flight work. A timed-out caller may stop
    // waiting, but the owned drain keeps its slot until the parser really ends.
    public sealed class OwnedParser
    {
        readonly SemaphoreSlim slots = new SemaphoreSlim(2, 2);
        readonly ConcurrentDictionary<long, Task> drains = new ConcurrentDictionary<long, Task>();
        readonly Action<Exception> report;
        long sequence;
        public OwnedParser(Action<Exception> reportError) { report = reportError; }
        public int DrainingCount { get { return drains.Count; } }

        public async Task<T> Run<T>(Func<Task<T>> start, TimeSpan timeout, CancellationToken token)
        {
            if (!await slots.WaitAsync(timeout, token)) throw new TimeoutException("Previous parser calls are still draining");
            bool ownsSlot = true;
            try {
                token.ThrowIfCancellationRequested();
                var task = start();
                if (task == null) throw new InvalidOperationException("Parser returned no task");
                using (var timerCancel = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    var timer = Task.Delay(timeout, timerCancel.Token);
                    try {
                        if (await Task.WhenAny(task, timer) != task) {
                            Drain(task);
                            ownsSlot = false;
                            token.ThrowIfCancellationRequested();
                            throw new TimeoutException("Beatmap statistics parsing timed out");
                        }
                        return await task;
                    } finally { timerCancel.Cancel(); }
                }
            } finally { if (ownsSlot) slots.Release(); }
        }

        void Drain(Task task)
        {
            var id = Interlocked.Increment(ref sequence);
            var drain = task.ContinueWith(delegate(Task finished) {
                try {
                    if (finished.IsFaulted && report != null) report(finished.Exception);
                } catch (Exception ex) { Console.Error.WriteLine("Parser drain diagnostic failed: " + ex); }
                finally {
                    slots.Release();
                    Task removed;
                    drains.TryRemove(id, out removed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            drains[id] = drain;
            if (drain.IsCompleted) { Task removed; drains.TryRemove(id, out removed); }
        }
    }
}
