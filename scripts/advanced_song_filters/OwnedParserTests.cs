using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedSongFilters
{
    public static class OwnedParserTests
    {
        public static int Main()
        {
            try
            {
                TestTwoConcurrentSlots();
                TestTimeoutKeepsUnfinishedSlots();
                TestOldCompletionDoesNotAffectNewCall();
                TestQueuedCancellation();
                TestDrainFailureIsReported();
                Console.WriteLine("OwnedParser tests passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static void TestTwoConcurrentSlots()
        {
            OwnedParser parser = new OwnedParser(null);
            TaskCompletionSource<int> first = new TaskCompletionSource<int>();
            TaskCompletionSource<int> second = new TaskCompletionSource<int>();
            ManualResetEventSlim firstStarted = new ManualResetEventSlim(false);
            ManualResetEventSlim secondStarted = new ManualResetEventSlim(false);
            int active = 0;
            int maximum = 0;

            Task<int> firstRun = parser.Run<int>(delegate
            {
                int value = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, value);
                firstStarted.Set();
                return first.Task;
            }, TimeSpan.FromSeconds(2), CancellationToken.None);

            Task<int> secondRun = parser.Run<int>(delegate
            {
                int value = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, value);
                secondStarted.Set();
                return second.Task;
            }, TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert(firstStarted.Wait(500), "first parser call did not start");
            Assert(secondStarted.Wait(500), "second parser call did not start");
            Assert(maximum == 2, "OwnedParser did not permit the two configured slots");

            first.SetResult(11);
            second.SetResult(22);
            WaitSuccessfully(firstRun, 1000);
            WaitSuccessfully(secondRun, 1000);
            Assert(firstRun.Result == 11 && secondRun.Result == 22,
                "concurrent calls returned the wrong values");
        }

        private static void TestTimeoutKeepsUnfinishedSlots()
        {
            OwnedParser parser = new OwnedParser(null);
            TaskCompletionSource<int> first = new TaskCompletionSource<int>();
            TaskCompletionSource<int> second = new TaskCompletionSource<int>();
            Stopwatch stopwatch = Stopwatch.StartNew();
            Task<int> firstRun = parser.Run<int>(
                delegate { return first.Task; },
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);
            Task<int> secondRun = parser.Run<int>(
                delegate { return second.Task; },
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);

            ExpectException<TimeoutException>(firstRun, 1000, "first timeout");
            ExpectException<TimeoutException>(secondRun, 1000, "second timeout");
            Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                "timed out calls did not return promptly");
            Assert(parser.DrainingCount == 2,
                "unfinished timed-out calls did not remain owned by the parser");

            bool thirdStarted = false;
            Task<int> thirdRun = parser.Run<int>(
                delegate
                {
                    thirdStarted = true;
                    return Task.FromResult(3);
                },
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
            ExpectException<TimeoutException>(thirdRun, 1000, "queued timeout");
            Assert(!thirdStarted, "a queued call started while both parser slots were draining");

            first.SetResult(1);
            second.SetResult(2);
            Assert(WaitUntil(delegate { return parser.DrainingCount == 0; }, 1000),
                "draining slots were not released after the parser tasks finished");
        }

        private static void TestOldCompletionDoesNotAffectNewCall()
        {
            OwnedParser parser = new OwnedParser(null);
            TaskCompletionSource<int> oldTask = new TaskCompletionSource<int>();
            Task<int> oldRun = parser.Run<int>(
                delegate { return oldTask.Task; },
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);
            ExpectException<TimeoutException>(oldRun, 1000, "old timeout");
            Assert(parser.DrainingCount == 1, "old timeout was not registered as a drain");

            TaskCompletionSource<int> currentTask = new TaskCompletionSource<int>();
            ManualResetEventSlim currentStarted = new ManualResetEventSlim(false);
            Task<int> currentRun = parser.Run<int>(
                delegate
                {
                    currentStarted.Set();
                    return currentTask.Task;
                },
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            Assert(currentStarted.Wait(500), "new call did not start on the free slot");

            oldTask.SetResult(101);
            Assert(WaitUntil(delegate { return parser.DrainingCount == 0; }, 1000),
                "old parser call did not finish draining");
            Assert(!currentRun.IsCompleted,
                "old parser completion incorrectly completed the new call");

            currentTask.SetResult(202);
            WaitSuccessfully(currentRun, 1000);
            Assert(currentRun.Result == 202,
                "new call returned a value from the old parser invocation");
        }

        private static void TestQueuedCancellation()
        {
            OwnedParser parser = new OwnedParser(null);
            TaskCompletionSource<int> first = new TaskCompletionSource<int>();
            TaskCompletionSource<int> second = new TaskCompletionSource<int>();
            Task<int> firstRun = parser.Run<int>(
                delegate { return first.Task; },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            Task<int> secondRun = parser.Run<int>(
                delegate { return second.Task; },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            CancellationTokenSource queuedCancellation = new CancellationTokenSource();
            bool queuedStarted = false;
            Task<int> queuedRun = parser.Run<int>(
                delegate
                {
                    queuedStarted = true;
                    return Task.FromResult(3);
                },
                TimeSpan.FromSeconds(5),
                queuedCancellation.Token);

            Thread.Sleep(50);
            queuedCancellation.Cancel();
            ExpectCancellation(queuedRun, 1000, "queued cancellation");
            Assert(!queuedStarted, "a canceled queued call started after cancellation");

            first.SetResult(1);
            second.SetResult(2);
            WaitSuccessfully(firstRun, 1000);
            WaitSuccessfully(secondRun, 1000);
            queuedCancellation.Dispose();
        }

        private static void TestDrainFailureIsReported()
        {
            List<Exception> reports = new List<Exception>();
            object reportLock = new object();
            OwnedParser parser = new OwnedParser(delegate(Exception error)
            {
                lock (reportLock)
                {
                    reports.Add(error);
                }
            });
            TaskCompletionSource<int> failing = new TaskCompletionSource<int>();
            Task<int> timed = parser.Run<int>(
                delegate { return failing.Task; },
                TimeSpan.FromMilliseconds(80),
                CancellationToken.None);
            ExpectException<TimeoutException>(timed, 1000, "drain failure timeout");
            Assert(parser.DrainingCount == 1, "failing parser task was not registered for drain");

            failing.SetException(new InvalidOperationException("drain-boom"));
            Assert(WaitUntil(delegate
            {
                lock (reportLock)
                {
                    return parser.DrainingCount == 0 && reports.Count == 1;
                }
            }, 1000), "drain failure was not observed and reported");
            lock (reportLock)
            {
                Assert(ExceptionContains(reports[0], "drain-boom"),
                    "drain report did not preserve the parser failure");
            }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            for (;;)
            {
                int previous = maximum;
                if (value <= previous || Interlocked.CompareExchange(ref maximum, value, previous) == previous)
                {
                    return;
                }
            }
        }

        private static void WaitSuccessfully(Task task, int timeoutMilliseconds)
        {
            Assert(task.Wait(timeoutMilliseconds), "task did not complete within the test timeout");
            Assert(!task.IsFaulted && !task.IsCanceled, "task completed unsuccessfully");
        }

        private static void ExpectException<T>(Task task, int timeoutMilliseconds, string name)
            where T : Exception
        {
            try
            {
                Assert(task.Wait(timeoutMilliseconds), name + " did not complete promptly");
                throw new InvalidOperationException(name + " completed successfully");
            }
            catch (AggregateException error)
            {
                Assert(ExceptionOfType<T>(error), name + " returned the wrong exception: " + error);
            }
        }

        private static void ExpectCancellation(Task task, int timeoutMilliseconds, string name)
        {
            try
            {
                Assert(task.Wait(timeoutMilliseconds), name + " did not complete promptly");
                throw new InvalidOperationException(name + " completed successfully");
            }
            catch (AggregateException error)
            {
                Assert(ExceptionOfType<OperationCanceledException>(error),
                    name + " returned the wrong exception: " + error);
            }
        }

        private static bool ExceptionOfType<T>(Exception error)
            where T : Exception
        {
            Exception current = error;
            while (current != null)
            {
                if (current is T)
                {
                    return true;
                }

                AggregateException aggregate = current as AggregateException;
                if (aggregate != null)
                {
                    for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
                    {
                        if (ExceptionOfType<T>(aggregate.InnerExceptions[i]))
                        {
                            return true;
                        }
                    }
                }

                current = current.InnerException;
            }

            return false;
        }

        private static bool ExceptionContains(Exception error, string text)
        {
            Exception current = error;
            while (current != null)
            {
                if ((current.Message ?? String.Empty).IndexOf(text, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                AggregateException aggregate = current as AggregateException;
                if (aggregate != null)
                {
                    for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
                    {
                        if (ExceptionContains(aggregate.InnerExceptions[i], text))
                        {
                            return true;
                        }
                    }
                }

                current = current.InnerException;
            }

            return false;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return condition();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
