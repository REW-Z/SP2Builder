using System;
using UnityEngine;



public class ProfilerSample : IDisposable
{
    public ProfilerSample(string name)
    {
        UnityEngine.Profiling.Profiler.BeginSample(name);
    }

    public void Dispose()
    {
        UnityEngine.Profiling.Profiler.EndSample();
    }
}
