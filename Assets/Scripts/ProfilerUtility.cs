using System;
using UnityEngine;



public struct SampleProfiler : IDisposable
{
	// 在构造时开启一个 Unity Profiler sample。 / Begin a Unity Profiler sample when constructed.
    public SampleProfiler(string name)
    {
        UnityEngine.Profiling.Profiler.BeginSample(name);
    }

	// 在 using 结束时关闭对应的 Unity Profiler sample。 / End the matching Unity Profiler sample when leaving a using scope.
    public void Dispose()
    {
        UnityEngine.Profiling.Profiler.EndSample();
    }
}



public struct StopWatch : IDisposable
{
    private readonly System.Diagnostics.Stopwatch stopwatch;

    private readonly string name;
	// 启动一个命名的 Stopwatch，用于打印耗时日志。 / Start a named Stopwatch that will later log elapsed time.
    public StopWatch(string name)
    {
        this.name = name;
        stopwatch = System.Diagnostics.Stopwatch.StartNew();
    }
	// 停止 Stopwatch 并把累计耗时输出到日志。 / Stop the Stopwatch and log the accumulated elapsed time.
    public void Dispose()
    {
        stopwatch.Stop();
        Debug.Log($"{name} time: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.ElapsedTicks} ticks)");
    }
}