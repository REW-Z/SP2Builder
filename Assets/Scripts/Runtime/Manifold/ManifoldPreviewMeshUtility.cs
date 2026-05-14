using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	internal static class ManifoldPreviewMeshUtility
	{
		public static PackedManifoldVertex[] BuildVertexArray(PreviewMeshData meshData)
		{
			PackedManifoldVertex[] vertices = new PackedManifoldVertex[meshData.Vertices.Count];
			bool hasNormals = meshData.Normals.Count == meshData.Vertices.Count;
			for (int i = 0; i < vertices.Length; i++)
			{
				Vector3 normal = hasNormals ? meshData.Normals[i] : Vector3.up;
				vertices[i] = new PackedManifoldVertex(meshData.Vertices[i], normal);
			}
			return vertices;
		}

		public static uint[] BuildTriangleArray(PreviewMeshData meshData)
		{
			int totalTriangleIndices = 0;
			for (int i = 0; i < meshData.SubMeshTriangles.Count; i++)
			{
				totalTriangleIndices += meshData.SubMeshTriangles[i].Count;
			}

			uint[] triangles = new uint[totalTriangleIndices];
			int writeIndex = 0;
			for (int subMesh = 0; subMesh < meshData.SubMeshTriangles.Count; subMesh++)
			{
				List<int> source = meshData.SubMeshTriangles[subMesh];
				for (int i = 0; i < source.Count; i++)
				{
					triangles[writeIndex++] = (uint)Mathf.Max(0, source[i]);
				}
			}

			return triangles;
		}

		public static PreviewMeshData ToPreviewMeshData(ManifoldHandle manifold, string meshName)
		{
			if (manifold == null || manifold.Ptr == IntPtr.Zero)
			{
				return null;
			}

			using ManifoldMeshHandle meshGl = ManifoldMeshHandle.CreateFromManifold(manifold);
			if (meshGl == null)
			{
				return null;
			}

			int floatCount = (int)ManifoldNativeMethods.manifold_meshgl_vert_properties_length(meshGl.Ptr);
			int triangleCount = (int)ManifoldNativeMethods.manifold_meshgl_tri_length(meshGl.Ptr);
			PreviewMeshData result = new PreviewMeshData(meshName);
			if (floatCount <= 0 || triangleCount < 0 || floatCount % 6 != 0)
			{
				return result;
			}

			PackedManifoldVertex[] vertices = new PackedManifoldVertex[floatCount / 6];
			uint[] triangles = new uint[triangleCount * 3];
			GCHandle vertexHandle = default;
			GCHandle triangleHandle = default;
			try
			{
				vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
				triangleHandle = GCHandle.Alloc(triangles, GCHandleType.Pinned);
				ManifoldNativeMethods.manifold_meshgl_vert_properties(vertexHandle.AddrOfPinnedObject(), meshGl.Ptr);
				ManifoldNativeMethods.manifold_meshgl_tri_verts(triangleHandle.AddrOfPinnedObject(), meshGl.Ptr);
			}
			finally
			{
				if (triangleHandle.IsAllocated)
				{
					triangleHandle.Free();
				}
				if (vertexHandle.IsAllocated)
				{
					vertexHandle.Free();
				}
			}

			for (int i = 0; i < vertices.Length; i++)
			{
				result.Vertices.Add(vertices[i].Position);
				result.Normals.Add(vertices[i].Normal.sqrMagnitude > 0.000001f ? vertices[i].Normal.normalized : Vector3.up);
			}

			List<int> outputTriangles = result.SubMeshTriangles[0];
			for (int i = 0; i < triangles.Length; i++)
			{
				outputTriangles.Add((int)triangles[i]);
			}

			return result;
		}
	}
}