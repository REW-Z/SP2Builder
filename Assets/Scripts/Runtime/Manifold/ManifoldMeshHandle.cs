using System;
using System.Runtime.InteropServices;

namespace SP2Builder.ManifoldRuntime
{
	internal sealed class ManifoldMeshHandle : IDisposable
	{
		private IntPtr _ptr;

		public IntPtr Ptr => _ptr;

		private ManifoldMeshHandle(IntPtr ptr)
		{
			_ptr = ptr;
		}

		public static ManifoldMeshHandle Create(PreviewMeshData meshData)
		{
			if (meshData == null || meshData.Vertices.Count < 3)
			{
				return null;
			}

			PackedManifoldVertex[] vertices = ManifoldPreviewMeshUtility.BuildVertexArray(meshData);
			uint[] triangles = ManifoldPreviewMeshUtility.BuildTriangleArray(meshData);
			if (triangles.Length == 0)
			{
				return null;
			}

			uint[] runOriginalIds = { 0u };
			uint[] runIndices = { 0u, (uint)(triangles.Length / 3) };
			uint[] mergeFrom = null;
			uint[] mergeTo = null;
			if (meshData.MergeFromVertices.Count > 0 && meshData.MergeFromVertices.Count == meshData.MergeToVertices.Count)
			{
				mergeFrom = new uint[meshData.MergeFromVertices.Count];
				mergeTo = new uint[meshData.MergeToVertices.Count];
				for (int i = 0; i < mergeFrom.Length; i++)
				{
					mergeFrom[i] = (uint)Math.Max(0, meshData.MergeFromVertices[i]);
					mergeTo[i] = (uint)Math.Max(0, meshData.MergeToVertices[i]);
				}
			}
			IntPtr storage = Marshal.AllocHGlobal((int)ManifoldNativeMethods.manifold_meshgl_size());
			GCHandle vertexHandle = default;
			GCHandle triangleHandle = default;
			GCHandle originalIdHandle = default;
			GCHandle runIndexHandle = default;
			GCHandle mergeFromHandle = default;
			GCHandle mergeToHandle = default;
			try
			{
				vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
				triangleHandle = GCHandle.Alloc(triangles, GCHandleType.Pinned);
				originalIdHandle = GCHandle.Alloc(runOriginalIds, GCHandleType.Pinned);
				runIndexHandle = GCHandle.Alloc(runIndices, GCHandleType.Pinned);
				if (mergeFrom != null)
				{
					mergeFromHandle = GCHandle.Alloc(mergeFrom, GCHandleType.Pinned);
					mergeToHandle = GCHandle.Alloc(mergeTo, GCHandleType.Pinned);
				}

				MeshGLOptions options = new MeshGLOptions
				{
					run_original_ids = originalIdHandle.AddrOfPinnedObject(),
					run_original_ids_length = (UIntPtr)runOriginalIds.Length,
					run_indices = runIndexHandle.AddrOfPinnedObject(),
					run_indices_length = (UIntPtr)runIndices.Length,
					merge_from_vert = mergeFromHandle.IsAllocated ? mergeFromHandle.AddrOfPinnedObject() : IntPtr.Zero,
					merge_to_vert = mergeToHandle.IsAllocated ? mergeToHandle.AddrOfPinnedObject() : IntPtr.Zero,
					merge_verts_length = mergeFromHandle.IsAllocated ? (UIntPtr)mergeFrom.Length : UIntPtr.Zero,
					halfedge_tangents = IntPtr.Zero
				};

				IntPtr ptr = ManifoldNativeMethods.manifold_meshgl_w_options(
					storage,
					vertexHandle.AddrOfPinnedObject(),
					(UIntPtr)vertices.Length,
					(UIntPtr)6u,
					triangleHandle.AddrOfPinnedObject(),
					(UIntPtr)(triangles.Length / 3),
					ref options);

				if (ptr == IntPtr.Zero)
				{
					Marshal.FreeHGlobal(storage);
					return null;
				}

				IntPtr mergedStorage = Marshal.AllocHGlobal((int)ManifoldNativeMethods.manifold_meshgl_size());
				IntPtr mergedPtr = IntPtr.Zero;
				try
				{
					mergedPtr = ManifoldNativeMethods.manifold_meshgl_merge(mergedStorage, ptr);
					if (mergedPtr == IntPtr.Zero)
					{
						ManifoldNativeMethods.manifold_destruct_meshgl(ptr);
						Marshal.FreeHGlobal(storage);
						Marshal.FreeHGlobal(mergedStorage);
						return null;
					}

					if (mergedPtr == ptr)
					{
						Marshal.FreeHGlobal(mergedStorage);
						return new ManifoldMeshHandle(ptr);
					}

					ManifoldNativeMethods.manifold_destruct_meshgl(ptr);
					Marshal.FreeHGlobal(storage);
					return new ManifoldMeshHandle(mergedPtr);
				}
				catch
				{
					if (mergedPtr != IntPtr.Zero && mergedPtr != ptr)
					{
						ManifoldNativeMethods.manifold_destruct_meshgl(mergedPtr);
					}
					else if (mergedStorage != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(mergedStorage);
					}

					if (ptr != IntPtr.Zero)
					{
						ManifoldNativeMethods.manifold_destruct_meshgl(ptr);
						Marshal.FreeHGlobal(storage);
					}
					throw;
				}
			}
			catch
			{
				Marshal.FreeHGlobal(storage);
				throw;
			}
			finally
			{
				if (mergeToHandle.IsAllocated)
				{
					mergeToHandle.Free();
				}
				if (mergeFromHandle.IsAllocated)
				{
					mergeFromHandle.Free();
				}
				if (runIndexHandle.IsAllocated)
				{
					runIndexHandle.Free();
				}
				if (originalIdHandle.IsAllocated)
				{
					originalIdHandle.Free();
				}
				if (triangleHandle.IsAllocated)
				{
					triangleHandle.Free();
				}
				if (vertexHandle.IsAllocated)
				{
					vertexHandle.Free();
				}
			}
		}

		public static ManifoldMeshHandle CreateFromManifold(ManifoldHandle manifold)
		{
			if (manifold == null || manifold.Ptr == IntPtr.Zero)
			{
				return null;
			}

			IntPtr storage = Marshal.AllocHGlobal((int)ManifoldNativeMethods.manifold_meshgl_size());
			try
			{
				IntPtr ptr = ManifoldNativeMethods.manifold_get_meshgl_w_normals(storage, manifold.Ptr, 0);
				if (ptr == IntPtr.Zero)
				{
					Marshal.FreeHGlobal(storage);
					return null;
				}

				return new ManifoldMeshHandle(ptr);
			}
			catch
			{
				Marshal.FreeHGlobal(storage);
				throw;
			}
		}

		public void Dispose()
		{
			if (_ptr == IntPtr.Zero)
			{
				return;
			}

			ManifoldNativeMethods.manifold_destruct_meshgl(_ptr);
			Marshal.FreeHGlobal(_ptr);
			_ptr = IntPtr.Zero;
		}
	}
}