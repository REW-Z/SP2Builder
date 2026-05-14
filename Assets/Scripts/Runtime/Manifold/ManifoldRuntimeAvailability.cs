using System;
using System.Threading;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	internal static class ManifoldRuntimeAvailability
	{
		private static int _probed;
		private static int _warningLogged;
		private static bool _isAvailable;
		private static Exception _loadException;

		public static bool IsAvailable
		{
			get
			{
				EnsureProbed();
				return _isAvailable;
			}
		}

		public static Exception LoadException
		{
			get
			{
				EnsureProbed();
				return _loadException;
			}
		}

		public static void LogUnavailableOnce(string context)
		{
			EnsureProbed();
			if (_isAvailable || Interlocked.Exchange(ref _warningLogged, 1) != 0)
			{
				return;
			}

			Debug.LogWarning($"manifoldc is unavailable during {context}. {_loadException?.Message}");
		}

		private static void EnsureProbed()
		{
			if (Interlocked.CompareExchange(ref _probed, 1, 0) != 0)
			{
				return;
			}

			try
			{
				ManifoldNativeMethods.manifold_manifold_size();
				_isAvailable = true;
			}
			catch (Exception exception) when (
				exception is DllNotFoundException
				|| exception is EntryPointNotFoundException
				|| exception is BadImageFormatException)
			{
				_isAvailable = false;
				_loadException = exception;
			}
		}
	}
}