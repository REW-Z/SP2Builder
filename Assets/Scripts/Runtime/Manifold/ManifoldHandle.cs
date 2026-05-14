using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	internal sealed class ManifoldHandle : IDisposable
	{
		private IntPtr _ptr;

		public IntPtr Ptr => _ptr;

		public bool IsEmpty => _ptr == IntPtr.Zero || ManifoldNativeMethods.manifold_is_empty(_ptr) != 0;

		public ManifoldError Status => _ptr == IntPtr.Zero ? ManifoldError.INVALID_CONSTRUCTION : ManifoldNativeMethods.manifold_status(_ptr);

		public double Volume => _ptr == IntPtr.Zero ? 0d : ManifoldNativeMethods.manifold_volume(_ptr);

		private ManifoldHandle(IntPtr ptr)
		{
			_ptr = ptr;
		}

		// 把 PreviewMeshData 变成 native manifold 句柄，并返回构造状态。 / Convert PreviewMeshData into a native manifold handle and return the construction status.
		public static ManifoldHandle Create(PreviewMeshData meshData, out ManifoldError status)
		{
			status = ManifoldError.INVALID_CONSTRUCTION;
			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				return null;
			}

			using ManifoldMeshHandle meshGl = ManifoldMeshHandle.Create(meshData);
			if (meshGl == null)
			{
				return null;
			}

			IntPtr storage = Marshal.AllocHGlobal((int)ManifoldNativeMethods.manifold_manifold_size());
			try
			{
				IntPtr ptr = ManifoldNativeMethods.manifold_of_meshgl(storage, meshGl.Ptr);
				if (ptr == IntPtr.Zero)
				{
					Marshal.FreeHGlobal(storage);
					return null;
				}

				ManifoldHandle handle = new ManifoldHandle(ptr);
				status = handle.Status;
				return handle;
			}
			catch
			{
				Marshal.FreeHGlobal(storage);
				throw;
			}
		}

		// 复制一个独立的 native manifold 实例。 / Create an independent copy of the native manifold.
		public ManifoldHandle Copy()
		{
			return CreateFromNative(storage => ManifoldNativeMethods.manifold_copy(storage, _ptr));
		}

		// 对当前 manifold 应用一个 4x4 变换并返回新实例。 / Apply a 4x4 transform to the current manifold and return the transformed copy.
		public ManifoldHandle Transform(Matrix4x4 matrix)
		{
			Vector4 c0 = matrix.GetColumn(0);
			Vector4 c1 = matrix.GetColumn(1);
			Vector4 c2 = matrix.GetColumn(2);
			Vector4 c3 = matrix.GetColumn(3);
			return CreateFromNative(storage => ManifoldNativeMethods.manifold_transform(
				storage,
				_ptr,
				c0.x,
				c0.y,
				c0.z,
				c1.x,
				c1.y,
				c1.z,
				c2.x,
				c2.y,
				c2.z,
				c3.x,
				c3.y,
				c3.z));
		}

	// 对当前 manifold 执行减法布尔。 / Run a subtract boolean against the current manifold.
		public ManifoldHandle Subtract(ManifoldHandle other)
		{
			return Boolean(other, ManifoldOpType.SUBTRACT);
		}

	// 对当前 manifold 执行相交布尔。 / Run an intersect boolean against the current manifold.
		public ManifoldHandle Intersect(ManifoldHandle other)
		{
			return Boolean(other, ManifoldOpType.INTERSECT);
		}

	// 读取 native manifold 的包围盒。 / Read the bounding box of the native manifold.
		public Bounds BoundingBox()
		{
			NativeBox box = default;
			if (_ptr == IntPtr.Zero)
			{
				return default;
			}

			ManifoldNativeMethods.manifold_bounding_box(ref box, _ptr);
			Vector3 min = new Vector3((float)box.min.x, (float)box.min.y, (float)box.min.z);
			Vector3 max = new Vector3((float)box.max.x, (float)box.max.y, (float)box.max.z);
			return new Bounds((min + max) * 0.5f, max - min);
		}

		// 把当前 native manifold 重新导出成 PreviewMeshData。 / Export the current native manifold back into PreviewMeshData.
		public PreviewMeshData ToPreviewMeshData(string meshName)
		{
			return ManifoldPreviewMeshUtility.ToPreviewMeshData(this, meshName);
		}

		// 释放 native manifold 占用的托管外资源。 / Release the unmanaged resources held by the native manifold.
		public void Dispose()
		{
			if (_ptr == IntPtr.Zero)
			{
				return;
			}

			ManifoldNativeMethods.manifold_destruct_manifold(_ptr);
			Marshal.FreeHGlobal(_ptr);
			_ptr = IntPtr.Zero;
		}

		// 统一封装 subtract/intersect 这类二元布尔调用。 / Share the common native path for binary boolean operations.
		private ManifoldHandle Boolean(ManifoldHandle other, ManifoldOpType operation)
		{
			if (_ptr == IntPtr.Zero || other == null || other._ptr == IntPtr.Zero)
			{
				return null;
			}

			return CreateFromNative(storage => ManifoldNativeMethods.manifold_boolean(storage, _ptr, other._ptr, operation));
		}

	// 从一个 native 工厂函数创建托管包装句柄。 / Create the managed wrapper from a native manifold factory callback.
		private static ManifoldHandle CreateFromNative(Func<IntPtr, IntPtr> factory)
		{
			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				return null;
			}

			IntPtr storage = Marshal.AllocHGlobal((int)ManifoldNativeMethods.manifold_manifold_size());
			try
			{
				IntPtr ptr = factory(storage);
				if (ptr == IntPtr.Zero)
				{
					Marshal.FreeHGlobal(storage);
					return null;
				}

				return new ManifoldHandle(ptr);
			}
			catch
			{
				Marshal.FreeHGlobal(storage);
				throw;
			}
		}
	}
}