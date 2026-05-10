using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif


internal static class PreviewMeshBuildScheduler
{
	private const int MaxParallelJobs = 2;

	private static readonly object Gate = new object();

	private static readonly Queue<Job> PendingJobs = new Queue<Job>();

	private static readonly ConcurrentQueue<Result> CompletedJobs = new ConcurrentQueue<Result>();

	private static readonly Dictionary<int, int> LatestVersionByOwner = new Dictionary<int, int>();

	private static readonly Dictionary<int, CancellationTokenSource> ActiveTokensByOwner = new Dictionary<int, CancellationTokenSource>();

	private static int _activeJobCount;

	private static bool _drainRegistered;

	public static void Schedule(
		int ownerId,
		int version,
		Func<CancellationToken, PreviewMeshData> build,
		Action<int, PreviewMeshData> complete,
		Action<int, Exception> fail = null)
	{
		if (build == null || complete == null)
		{
			return;
		}

		Job job = new Job(ownerId, version, build, complete, fail);
		lock (Gate)
		{
			LatestVersionByOwner[ownerId] = version;
			if (ActiveTokensByOwner.TryGetValue(ownerId, out CancellationTokenSource activeToken))
			{
				activeToken.Cancel();
			}
			PendingJobs.Enqueue(job);
			EnsureDrainRegistered();
			StartAvailableJobsLocked();
		}
	}

	public static void Cancel(int ownerId)
	{
		lock (Gate)
		{
			LatestVersionByOwner[ownerId] = int.MaxValue;
			if (ActiveTokensByOwner.TryGetValue(ownerId, out CancellationTokenSource activeToken))
			{
				activeToken.Cancel();
			}
		}
	}

	public static void CancelAll()
	{
		lock (Gate)
		{
			foreach (CancellationTokenSource activeToken in ActiveTokensByOwner.Values)
			{
				activeToken.Cancel();
			}

			PendingJobs.Clear();
			LatestVersionByOwner.Clear();
			ActiveTokensByOwner.Clear();
			_activeJobCount = 0;
			UnregisterDrain();
		}
	}

	private static void StartAvailableJobsLocked()
	{
		while (_activeJobCount < MaxParallelJobs && PendingJobs.Count > 0)
		{
			Job job = PendingJobs.Dequeue();
			if (!IsLatestLocked(job.OwnerId, job.Version))
			{
				continue;
			}

			CancellationTokenSource cancellation = new CancellationTokenSource();
			ActiveTokensByOwner[job.OwnerId] = cancellation;
			_activeJobCount++;
			Task.Run(() => Execute(job, cancellation));
		}
	}

	private static void Execute(Job job, CancellationTokenSource cancellation)
	{
		CancellationToken token = cancellation.Token;
		PreviewMeshData meshData = null;
		Exception exception = null;
		try
		{
			if (!token.IsCancellationRequested)
			{
				meshData = job.Build(token);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			exception = ex;
		}

		CompletedJobs.Enqueue(new Result(job, meshData, exception, token.IsCancellationRequested, cancellation));
	}

	private static bool IsLatestLocked(int ownerId, int version)
	{
		return LatestVersionByOwner.TryGetValue(ownerId, out int latestVersion) && latestVersion == version;
	}

	private static void DrainCompletedJobs()
	{
		while (CompletedJobs.TryDequeue(out Result result))
		{
			lock (Gate)
			{
				_activeJobCount = Math.Max(0, _activeJobCount - 1);
				if (ActiveTokensByOwner.TryGetValue(result.Job.OwnerId, out CancellationTokenSource activeToken) && ReferenceEquals(activeToken, result.Cancellation))
				{
					ActiveTokensByOwner.Remove(result.Job.OwnerId);
				}
				result.Cancellation.Dispose();
				StartAvailableJobsLocked();
			}

			bool latest;
			lock (Gate)
			{
				latest = IsLatestLocked(result.Job.OwnerId, result.Job.Version);
			}

			if (!latest || result.Cancelled)
			{
				continue;
			}

			if (result.Exception != null)
			{
				result.Job.Fail?.Invoke(result.Job.Version, result.Exception);
				continue;
			}

			if (result.MeshData != null)
			{
				result.Job.Complete(result.Job.Version, result.MeshData);
			}
		}

		lock (Gate)
		{
			if (_activeJobCount == 0 && PendingJobs.Count == 0 && CompletedJobs.IsEmpty)
			{
				UnregisterDrain();
			}
		}
	}

	private static void EnsureDrainRegistered()
	{
	#if UNITY_EDITOR
		if (_drainRegistered)
		{
			return;
		}

		_drainRegistered = true;
		EditorApplication.update -= DrainCompletedJobs;
		EditorApplication.update += DrainCompletedJobs;
	#endif
	}

	private static void UnregisterDrain()
	{
	#if UNITY_EDITOR
		if (!_drainRegistered)
		{
			return;
		}

		_drainRegistered = false;
		EditorApplication.update -= DrainCompletedJobs;
	#endif
	}

	private readonly struct Job
	{
		public Job(int ownerId, int version, Func<CancellationToken, PreviewMeshData> build, Action<int, PreviewMeshData> complete, Action<int, Exception> fail)
		{
			OwnerId = ownerId;
			Version = version;
			Build = build;
			Complete = complete;
			Fail = fail;
		}

		public int OwnerId { get; }

		public int Version { get; }

		public Func<CancellationToken, PreviewMeshData> Build { get; }

		public Action<int, PreviewMeshData> Complete { get; }

		public Action<int, Exception> Fail { get; }
	}

	private readonly struct Result
	{
		public Result(Job job, PreviewMeshData meshData, Exception exception, bool cancelled, CancellationTokenSource cancellation)
		{
			Job = job;
			MeshData = meshData;
			Exception = exception;
			Cancelled = cancelled;
			Cancellation = cancellation;
		}

		public Job Job { get; }

		public PreviewMeshData MeshData { get; }

		public Exception Exception { get; }

		public bool Cancelled { get; }

		public CancellationTokenSource Cancellation { get; }
	}
}

internal static class PreviewMeshPool
{
	private const int MaxPooledMeshes = 64;

	private static readonly Stack<Mesh> Meshes = new Stack<Mesh>();

	public static Mesh CreateMesh(PreviewMeshData data)
	{
		Mesh mesh = Rent();
		data.CopyToMesh(mesh);
		return mesh;
	}

	public static void Release(Mesh mesh)
	{
		if (mesh == null || Meshes.Count >= MaxPooledMeshes)
		{
			Destroy(mesh);
			return;
		}

		mesh.Clear(false);
		Meshes.Push(mesh);
	}

	private static Mesh Rent()
	{
		while (Meshes.Count > 0)
		{
			Mesh mesh = Meshes.Pop();
			if (mesh != null)
			{
				return mesh;
			}
		}

		return new Mesh();
	}

	private static void Destroy(Mesh mesh)
	{
		if (mesh == null)
		{
			return;
		}

		if (Application.isPlaying)
		{
			UnityEngine.Object.Destroy(mesh);
		}
		else
		{
			UnityEngine.Object.DestroyImmediate(mesh);
		}
	}
}
