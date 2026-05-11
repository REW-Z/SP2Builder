using System;
using UnityEngine;



public struct SampleProfiler : IDisposable
{
    public SampleProfiler(string name)
    {
        UnityEngine.Profiling.Profiler.BeginSample(name);
    }

    public void Dispose()
    {
        UnityEngine.Profiling.Profiler.EndSample();
    }
}



public struct StopWatch : IDisposable
{
    private readonly System.Diagnostics.Stopwatch stopwatch;

    private readonly string name;
    public StopWatch(string name)
    {
        this.name = name;
        stopwatch = System.Diagnostics.Stopwatch.StartNew();
    }
    public void Dispose()
    {
        stopwatch.Stop();
        Debug.Log($"{name} time: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.ElapsedTicks} ticks)");
    }
}