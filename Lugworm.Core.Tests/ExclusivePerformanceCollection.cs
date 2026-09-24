using System;
using System.Diagnostics;
using Xunit;

namespace Lugworm.Core.Tests;

// All wall-clock-budget test classes (D5, 2026-09-14) share this collection. Run
// it without competing test classes so a budget measures the workload under test,
// not unrelated slicing/power/optimizer work racing it for CPU. No tests are
// skipped or budgets relaxed.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusivePerformanceCollection
{
    public const string Name = "Exclusive performance";
}

/// <summary>
/// A wall-clock budget expressed as a fixed number of seconds fails whenever the
/// machine happens to be busy (another build, another test run, CI noise) - the
/// D5 evidence is <c>Optimize_AntiCorrelatedResponses_HugeFront_WithinBudget</c>
/// taking 24.4s against a 20s budget solely because an analyzer build ran
/// alongside it. Every wall-clock budget in this suite is instead expressed as
/// <c>K * PerfReference.MeasureMs()</c>: a fixed, deterministic, single-threaded
/// CPU workload of known cost, measured once per test class immediately before
/// its budget-checked workloads run. If the machine is under load, this reference
/// workload slows down by roughly the same factor as the workload under test, so
/// the budget (a multiple of the reference) widens automatically instead of
/// tripping. See docs/4-unit-tests/TESTING.md for the convention and per-test K.
/// </summary>
public static class PerfReference
{
    // ~100-150ms of pure CPU work on the dev machines this suite runs on
    // (measured 2026-09-14): enough resolution that scheduler/GC jitter doesn't
    // dominate the measurement, cheap enough that paying for it once per test
    // class is not itself a budget risk.
    const int Iterations = 20_000_000;

    /// <summary>Runs a warmup pass (untimed, so JIT tiering doesn't skew the
    /// measurement) then times one pass of the reference workload.</summary>
    public static double MeasureMs()
    {
        Workload();
        var sw = Stopwatch.StartNew();
        Workload();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    static double Workload()
    {
        double acc = 0;
        for (int i = 1; i <= Iterations; i++)
            acc += Math.Sqrt(i) * Math.Sin(i * 0.0001);
        return acc;
    }
}
