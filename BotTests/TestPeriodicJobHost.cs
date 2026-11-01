using HambotV2.Maintenance;
using HambotV2.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BotTests
{
    [TestClass]
    public sealed class TestPeriodicJobHost
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>A counting job over a fake clock: records ticks, resume hooks, and can fail every tick.</summary>
        private sealed class CountingJob
        {
            private int _runs;
            private int _resumeHooks;

            public FakeTimeProvider Time { get; } = new(Start);
            public MaintenanceMode Maintenance { get; } = new();
            public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
            public bool FailEveryTick { get; set; }

            public int Runs => Volatile.Read(ref _runs);
            public int ResumeHooks => Volatile.Read(ref _resumeHooks);

            public PeriodicJobHost CreateHost() => new(
                "test-job",
                () => Interval,
                (_, _) =>
                {
                    Interlocked.Increment(ref _runs);
                    if (FailEveryTick)
                        throw new InvalidOperationException("simulated tick failure");
                    return Task.CompletedTask;
                },
                Time,
                NullLogger.Instance,
                Maintenance,
                onMaintenanceResumed: () =>
                {
                    Interlocked.Increment(ref _resumeHooks);
                    return Task.CompletedTask;
                });
        }

        private static async Task StopAsync(Task loop, CancellationTokenSource stop)
        {
            stop.Cancel();
            Task completed = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.AreSame(loop, completed, "the loop must stop when the token is cancelled");
        }

        [TestMethod]
        public async Task First_Tick_Runs_Immediately_And_The_Fallback_Poll_Does_Not_Run_Early()
        {
            var job = new CountingJob();
            using var stop = new CancellationTokenSource();
            Task loop = job.CreateHost().RunAsync(stop.Token);

            // The first tick runs immediately at start, without any clock advance.
            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 1, TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, job.Runs);

            // The 10 s fallback poll wakes the loop but must not run the tick early.
            for (int i = 0; i < 29; i++)
            {
                job.Time.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(25);
            }
            Assert.AreEqual(1, job.Runs, "at 4:50 the 5-minute interval has not elapsed yet");

            job.Time.Advance(TimeSpan.FromSeconds(10));
            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 2, TimeSpan.FromSeconds(10));
            Assert.AreEqual(2, job.Runs, "the tick runs once the interval has elapsed");

            await StopAsync(loop, stop);
        }

        [TestMethod]
        public async Task The_Interval_Is_Re_Read_Every_Tick()
        {
            var job = new CountingJob { Interval = TimeSpan.FromMinutes(1) };
            using var stop = new CancellationTokenSource();
            Task loop = job.CreateHost().RunAsync(stop.Token);

            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 1, TimeSpan.FromSeconds(10));

            // A runtime interval change applies from the next tick on.
            job.Interval = TimeSpan.FromMinutes(5);
            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 2, TimeSpan.FromSeconds(10), maxSteps: 10);

            for (int i = 0; i < 3; i++)
            {
                job.Time.Advance(TimeSpan.FromMinutes(1));
                await Task.Delay(25);
            }
            Assert.AreEqual(2, job.Runs, "the new 5-minute interval governs the next tick");

            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 3, TimeSpan.FromMinutes(1), maxSteps: 10);

            await StopAsync(loop, stop);
        }

        [TestMethod]
        public async Task A_Failing_Tick_Does_Not_Crash_Or_Spin_The_Loop()
        {
            var job = new CountingJob { Interval = TimeSpan.FromMinutes(1), FailEveryTick = true };
            using var stop = new CancellationTokenSource();
            Task loop = job.CreateHost().RunAsync(stop.Token);

            // One attempt per interval, not a hot retry loop.
            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 4, TimeSpan.FromSeconds(10), maxSteps: 200);
            Assert.AreEqual(4, job.Runs, "each failed tick is retried only at the next interval");

            await StopAsync(loop, stop);
        }

        [TestMethod]
        public async Task Maintenance_Pauses_The_Run_And_The_Resume_Hook_Fires_Once()
        {
            var job = new CountingJob();
            job.Maintenance.Enter();
            using var stop = new CancellationTokenSource();
            Task loop = job.CreateHost().RunAsync(stop.Token);

            for (int i = 0; i < 3; i++)
            {
                job.Time.Advance(TimeSpan.FromSeconds(10));
                await Task.Delay(25);
            }
            Assert.AreEqual(0, job.Runs, "no tick runs while maintenance mode is active");
            Assert.AreEqual(0, job.ResumeHooks);

            job.Maintenance.Exit();
            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 1 && job.ResumeHooks == 1, TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, job.ResumeHooks, "the resume hook fires once when maintenance ends");

            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 2, TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, job.ResumeHooks, "the hook does not fire again on later ticks");

            await StopAsync(loop, stop);
        }

        [TestMethod]
        public async Task Cancellation_Stops_The_Loop()
        {
            var job = new CountingJob();
            using var stop = new CancellationTokenSource();
            Task loop = job.CreateHost().RunAsync(stop.Token);

            await DiscordTestDoubles.AdvanceUntilAsync(job.Time, () => job.Runs == 1, TimeSpan.FromSeconds(10));

            await StopAsync(loop, stop);
        }
    }
}
