using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

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

		// 直接从 Unity Mesh 构造 native MeshGL。 / Build native MeshGL directly from a Unity Mesh.
		public static ManifoldMeshHandle Create(Mesh mesh)
		{
			if (mesh == null || mesh.vertexCount < 3)
			{
				return null;
			}

			List<Vector3> vertices = new List<Vector3>(mesh.vertexCount);
			List<Vector3> normals = new List<Vector3>(mesh.vertexCount);
			List<IReadOnlyList<int>> subMeshTriangles = new List<IReadOnlyList<int>>(Mathf.Max(1, mesh.subMeshCount));
			mesh.GetVertices(vertices);
			mesh.GetNormals(normals);
			if (normals.Count != vertices.Count)
			{
				normals.Clear();
			}

			for (int subMesh = 0; subMesh < Mathf.Max(1, mesh.subMeshCount); subMesh++)
			{
				List<int> triangles = new List<int>();
				mesh.GetTriangles(triangles, subMesh);
				subMeshTriangles.Add(triangles);
			}

			return Create(vertices, normals, subMeshTriangles);
		}

		// 直接从托管网格数组构造 native MeshGL。 / Build native MeshGL directly from managed mesh arrays.
		public static ManifoldMeshHandle Create(
			IReadOnlyList<Vector3> vertexData,
			IReadOnlyList<Vector3> normalData,
			IReadOnlyList<IReadOnlyList<int>> subMeshTriangles,
			IReadOnlyList<int> mergeFromVertices = null,
			IReadOnlyList<int> mergeToVertices = null)
		{
			if (vertexData == null || vertexData.Count < 3)
			{
				return null;
			}

			PackedManifoldVertex[] vertices = BuildVertexArray(vertexData, normalData);
			uint[] triangles = BuildTriangleArray(subMeshTriangles);
			if (triangles.Length == 0)
			{
				return null;
			}

			uint[] runOriginalIds = { 0u };
			uint[] runIndices = { 0u, (uint)(triangles.Length / 3) };
			uint[] mergeFrom = null;
			uint[] mergeTo = null;
			if (mergeFromVertices != null && mergeToVertices != null && mergeFromVertices.Count > 0 && mergeFromVertices.Count == mergeToVertices.Count)
			{
				mergeFrom = new uint[mergeFromVertices.Count];
				mergeTo = new uint[mergeToVertices.Count];
				for (int i = 0; i < mergeFrom.Length; i++)
				{
					mergeFrom[i] = (uint)Math.Max(0, mergeFromVertices[i]);
					mergeTo[i] = (uint)Math.Max(0, mergeToVertices[i]);
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

		private static PackedManifoldVertex[] BuildVertexArray(IReadOnlyList<Vector3> vertexData, IReadOnlyList<Vector3> normalData)
		{
			PackedManifoldVertex[] vertices = new PackedManifoldVertex[vertexData.Count];
			bool hasNormals = normalData != null && normalData.Count == vertexData.Count;
			for (int i = 0; i < vertices.Length; i++)
			{
				Vector3 normal = hasNormals ? normalData[i] : Vector3.up;
				vertices[i] = new PackedManifoldVertex(vertexData[i], normal);
			}
			return vertices;
		}

		private static uint[] BuildTriangleArray(IReadOnlyList<IReadOnlyList<int>> subMeshTriangles)
		{
			if (subMeshTriangles == null || subMeshTriangles.Count == 0)
			{
				return Array.Empty<uint>();
			}

			int totalTriangleIndices = 0;
			for (int i = 0; i < subMeshTriangles.Count; i++)
			{
				totalTriangleIndices += subMeshTriangles[i]?.Count ?? 0;
			}

			uint[] triangles = new uint[totalTriangleIndices];
			int writeIndex = 0;
			for (int subMesh = 0; subMesh < subMeshTriangles.Count; subMesh++)
			{
				IReadOnlyList<int> source = subMeshTriangles[subMesh];
				if (source == null)
				{
					continue;
				}

				for (int i = 0; i < source.Count; i++)
				{
					triangles[writeIndex++] = (uint)Mathf.Max(0, source[i]);
				}
			}

			return triangles;
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

		// 释放 native MeshGL 句柄及其托管外内存。 / Release the native MeshGL handle and its unmanaged storage.
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
