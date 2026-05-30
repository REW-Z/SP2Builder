using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace SP2Builder.ManifoldRuntime
{
	internal static class ManifoldMeshExportUtility
	{
		public static Mesh ToMesh(ManifoldHandle manifold, string meshName)
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
			Mesh result = new Mesh
			{
				name = meshName
			};
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

			List<Vector3> outputVertices = new List<Vector3>(vertices.Length);
			List<Vector3> outputNormals = new List<Vector3>(vertices.Length);
			for (int i = 0; i < vertices.Length; i++)
			{
				outputVertices.Add(vertices[i].Position);
				outputNormals.Add(vertices[i].Normal.sqrMagnitude > 0.000001f ? vertices[i].Normal.normalized : Vector3.up);
			}

			List<int> outputTriangles = new List<int>(triangles.Length);
			for (int i = 0; i < triangles.Length; i++)
			{
				outputTriangles.Add((int)triangles[i]);
			}

			result.indexFormat = outputVertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			result.SetVertices(outputVertices);
			result.SetNormals(outputNormals);
			result.SetTriangles(outputTriangles, 0, true);
			result.RecalculateBounds();
			return result;
		}
	}
}
