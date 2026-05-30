using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SP2Builder.ManifoldRuntime
{
	internal static class FuselageManifoldUtility
	{
		private const double MinimumValidVolume = 1.1920928955078125E-10d;

		// 把机身 loft 输入转成 manifold，并在需要时执行 section-cutting 相交。 / Convert loft input into a manifold and optionally apply section-cutting intersection.
		public static Mesh BuildLoft(Mesh source, FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool applySectionCutting, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (!applySectionCutting)
			{
				source.name = meshName;
				return source;
			}

			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage loft");
				DestroyGeneratedMesh(source);
				return null;
			}

			Mesh cutVolume = null;
			try
			{
				using ManifoldHandle sourceManifold = CreateManifold(source, out ManifoldError sourceStatus);
				if (!IsUsable(sourceManifold, sourceStatus))
				{
					Debug.LogWarning($"manifold source build failed during fuselage loft: {sourceStatus}");
					return null;
				}

				cutVolume = BuildCutVolume(rear, front, offset, meshName + "_Volume");
				if (cutVolume == null)
				{
					return ToMesh(sourceManifold, meshName);
				}

				using ManifoldHandle cutManifold = CreateManifold(cutVolume, out ManifoldError cutStatus);
				if (!IsUsable(cutManifold, cutStatus))
				{
					Debug.LogWarning($"manifold cutter build failed during fuselage section cutting: {cutStatus}");
					return null;
				}

				using ManifoldHandle result = sourceManifold.Intersect(cutManifold);
				ManifoldError resultStatus = result?.Status ?? ManifoldError.INVALID_CONSTRUCTION;
				if (!IsUsable(result, resultStatus))
				{
					Debug.LogWarning($"manifold INTERSECT failed during fuselage section cutting: {resultStatus}");
					return null;
				}

				return ToMesh(result, meshName);
			}
			catch (Exception exception) when (
				exception is DllNotFoundException
				|| exception is EntryPointNotFoundException
				|| exception is BadImageFormatException)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage loft");
				return null;
			}
			finally
			{
				DestroyGeneratedMesh(cutVolume);
				DestroyGeneratedMesh(source);
			}
		}

		// 用 runtime manifold 对机身和 cutter 执行减法布尔。 / Subtract a cutter from a fuselage mesh using the runtime manifold path.
		public static Mesh Subtract(Mesh source, Mesh cutter, Matrix4x4 cutterToSource, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (cutter == null || cutter.vertexCount == 0)
			{
				return source;
			}

			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage targeted cutter boolean");
				return null;
			}

			return ExecuteBoolean(source, cutter, cutterToSource, ManifoldOpType.SUBTRACT, meshName, "targeted cutter boolean");
		}

		// 统一封装 runtime manifold 的 source/cutter 构造和布尔执行。 / Share the source/cutter construction and boolean execution path for runtime manifold operations.
		private static Mesh ExecuteBoolean(Mesh source, Mesh cutter, Matrix4x4 cutterToSource, ManifoldOpType operation, string meshName, string context)
		{
			try
			{
				using ManifoldHandle sourceManifold = CreateManifold(source, out ManifoldError sourceStatus);
				if (!IsUsable(sourceManifold, sourceStatus))
				{
					Debug.LogWarning($"manifold source build failed during {context}: {sourceStatus}");
					return null;
				}

				using ManifoldHandle cutterManifold = CreateBooleanCutterManifold(cutter, cutterToSource, context);
				if (cutterManifold == null)
				{
					return null;
				}

				using ManifoldHandle result = operation == ManifoldOpType.INTERSECT
					? sourceManifold.Intersect(cutterManifold)
					: sourceManifold.Subtract(cutterManifold);

				ManifoldError resultStatus = result?.Status ?? ManifoldError.INVALID_CONSTRUCTION;
				if (result == null || resultStatus != ManifoldError.NO_ERROR)
				{
					Debug.LogWarning($"manifold {operation} failed during {context}: {resultStatus}");
					return null;
				}

				Mesh output = result.ToMesh(meshName);
				if (output != null)
				{
					output.name = meshName;
				}
				return output;
			}
			catch (Exception exception) when (
				exception is DllNotFoundException
				|| exception is EntryPointNotFoundException
				|| exception is BadImageFormatException)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce(context);
				return null;
			}
		}

		// 过滤掉空体、错误状态或近似零体积的 native manifold。 / Filter out native manifolds that are empty, invalid, or effectively zero-volume.
		private static bool IsUsable(ManifoldHandle manifold, ManifoldError status)
		{
			return manifold != null
				&& status == ManifoldError.NO_ERROR
				&& !manifold.IsEmpty
				&& manifold.Volume >= MinimumValidVolume;
		}

		// 为布尔运算准备 cutter manifold，必要时先把变换烘焙进 Mesh。 / Prepare the cutter manifold for booleans, baking the transform into a Mesh when needed.
		private static ManifoldHandle CreateBooleanCutterManifold(Mesh cutter, Matrix4x4 cutterToSource, string context)
		{
			using ManifoldHandle cutterLocalManifold = CreateManifold(cutter, out ManifoldError cutterStatus);
			if (!IsUsable(cutterLocalManifold, cutterStatus))
			{
				Debug.LogWarning($"manifold cutter build failed during {context}: {cutterStatus}");
				return null;
			}

			ManifoldHandle transformedCutter = cutterLocalManifold.Transform(cutterToSource);
			ManifoldError transformStatus = transformedCutter?.Status ?? ManifoldError.INVALID_CONSTRUCTION;
			if (IsUsable(transformedCutter, transformStatus))
			{
				return transformedCutter;
			}

			transformedCutter?.Dispose();
			Mesh bakedCutter = null;
			try
			{
				bakedCutter = BakeMeshTransform(cutter, cutterToSource);
				ManifoldHandle bakedCutterManifold = CreateManifold(bakedCutter, out ManifoldError bakedStatus);
				if (IsUsable(bakedCutterManifold, bakedStatus))
				{
					return bakedCutterManifold;
				}

				bakedCutterManifold?.Dispose();
				Debug.LogWarning($"manifold cutter transform failed during {context}: {transformStatus}; baked transform fallback failed: {bakedStatus}");
				return null;
			}
			finally
			{
				DestroyGeneratedMesh(bakedCutter);
			}
		}

		private static ManifoldHandle CreateManifold(Mesh mesh, out ManifoldError status)
		{
			if (mesh == null)
			{
				status = ManifoldError.INVALID_CONSTRUCTION;
				return null;
			}

			return ManifoldHandle.Create(mesh, out status);
		}

		// 把一个 native manifold 安全地导出成带名字的 Mesh。 / Safely export a native manifold into a named Mesh instance.
		private static Mesh ToMesh(ManifoldHandle manifold, string meshName)
		{
			Mesh output = manifold?.ToMesh(meshName);
			if (output != null)
			{
				output.name = meshName;
			}
			return output;
		}

		// 把矩阵直接烘到 Mesh 顶点和索引上。 / Bake a matrix directly into Mesh vertices and triangle winding.
		private static Mesh BakeMeshTransform(Mesh source, Matrix4x4 transform)
		{
			Mesh transformed = new Mesh
			{
				name = string.IsNullOrWhiteSpace(source?.name) ? "PreviewMesh" : source.name + "_Baked"
			};
			if (source == null)
			{
				return transformed;
			}

			List<Vector3> vertices = new List<Vector3>(source.vertexCount);
			source.GetVertices(vertices);
			for (int i = 0; i < vertices.Count; i++)
			{
				vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
			}
			transformed.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			transformed.SetVertices(vertices);

			bool mirrored = GetLinearDeterminant(transform) < 0f;
			transformed.subMeshCount = Mathf.Max(1, source.subMeshCount);
			for (int subMesh = 0; subMesh < source.subMeshCount; subMesh++)
			{
				List<int> sourceTriangles = new List<int>();
				source.GetTriangles(sourceTriangles, subMesh);
				if (!mirrored)
				{
					transformed.SetTriangles(sourceTriangles, subMesh, true);
					continue;
				}

				List<int> targetTriangles = new List<int>(sourceTriangles.Count);
				for (int i = 0; i + 2 < sourceTriangles.Count; i += 3)
				{
					targetTriangles.Add(sourceTriangles[i]);
					targetTriangles.Add(sourceTriangles[i + 2]);
					targetTriangles.Add(sourceTriangles[i + 1]);
				}
				transformed.SetTriangles(targetTriangles, subMesh, true);
			}

			transformed.RecalculateNormals();
			transformed.RecalculateBounds();
			return transformed;
		}

		// 销毁 manifold 运算期间生成的临时 Mesh。 / Destroy temporary meshes generated during manifold operations.
		private static void DestroyGeneratedMesh(Mesh mesh)
		{
			if (mesh == null)
			{
				return;
			}

			UnityEngine.Object.DestroyImmediate(mesh);
		}

		// 计算矩阵线性部分的行列式，以判断是否发生镜像翻转。 / Compute the determinant of the matrix linear part to detect mirrored transforms.
		private static float GetLinearDeterminant(Matrix4x4 matrix)
		{
			Vector3 x = matrix.GetColumn(0);
			Vector3 y = matrix.GetColumn(1);
			Vector3 z = matrix.GetColumn(2);
			return Vector3.Dot(x, Vector3.Cross(y, z));
		}

		// 根据前后截面的 cutting 范围生成用于相交的闭体 cut-volume。 / Build the closed cut-volume used to intersect the fuselage against front and rear cutting ranges.
		private static Mesh BuildCutVolume(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, string meshName)
		{
			if (!HasSectionCutting(rear) && !HasSectionCutting(front))
			{
				return null;
			}

			float zOffset = offset.z * 0.5f;
			if (zOffset < 1.4E-44f)
			{
				return null;
			}

			Vector2 rearCenter = -new Vector2(offset.x, offset.y) * 0.5f;
			Vector2 frontCenter = new Vector2(offset.x, offset.y) * 0.5f;
			CutBounds rearBounds = GetCutBounds(rear, rearCenter);
			CutBounds frontBounds = GetCutBounds(front, frontCenter);
			return BuildCutVolumeData(
				new Vector2(rearBounds.MinX, rearBounds.MinY),
				new Vector2(rearBounds.MaxX, rearBounds.MaxY),
				new Vector2(frontBounds.MinX, frontBounds.MinY),
				new Vector2(frontBounds.MaxX, frontBounds.MaxY),
				zOffset,
				meshName);
		}

		// 从两端截面的矩形包围变化构建一个可 manifold 化的裁切闭体。 / Construct a manifold-friendly clipping volume from the changing rectangular bounds of both end sections.
		private static Mesh BuildCutVolumeData(Vector2 min1, Vector2 max1, Vector2 min2, Vector2 max2, float zOffset, string meshName)
		{
			if (zOffset < 1.4E-44f)
			{
				return null;
			}

			Vector4 span = new Vector4(max1.x - min1.x, max1.y - min1.y, max2.x - min2.x, max2.y - min2.y);
			if (span.x <= float.Epsilon && span.y <= float.Epsilon && span.z <= float.Epsilon && span.w <= float.Epsilon)
			{
				return null;
			}

			float maxZ = zOffset * 2f;
			float minZ = -maxZ;
			Vector2 minAverage = 0.5f * (min1 + min2);
			Vector2 maxAverage = 0.5f * (max1 + max2);
			Vector2 minSlope = 0.5f * (min2 - min1) / zOffset;
			Vector2 maxSlope = 0.5f * (max2 - max1) / zOffset;
			Vector2 deltaSlope = maxSlope - minSlope;
			Vector2 overlapZ = new Vector2((maxAverage.x - minAverage.x) / -deltaSlope.x, (maxAverage.y - minAverage.y) / -deltaSlope.y);
			if (float.IsNaN(overlapZ.x) || float.IsNaN(overlapZ.y))
			{
				return null;
			}

			if (deltaSlope.x > float.Epsilon)
			{
				minZ = Mathf.Max(minZ, overlapZ.x);
				maxZ = Mathf.Max(maxZ, overlapZ.x);
			}
			else if (deltaSlope.x < -1E-45f)
			{
				minZ = Mathf.Min(minZ, overlapZ.x);
				maxZ = Mathf.Min(maxZ, overlapZ.x);
			}

			if (deltaSlope.y > float.Epsilon)
			{
				minZ = Mathf.Max(minZ, overlapZ.y);
				maxZ = Mathf.Max(maxZ, overlapZ.y);
			}
			else if (deltaSlope.y < -1E-45f)
			{
				minZ = Mathf.Min(minZ, overlapZ.y);
				maxZ = Mathf.Min(maxZ, overlapZ.y);
			}

			Vector2 startMin = minAverage + minSlope * minZ;
			Vector2 startMax = ComponentMax(startMin, maxAverage + maxSlope * minZ);
			Vector2 endMin = minAverage + minSlope * maxZ;
			Vector2 endMax = ComponentMax(endMin, maxAverage + maxSlope * maxZ);
			Vector2 startSpan = startMax - startMin;
			Vector2 endSpan = endMax - endMin;

			if (minZ == overlapZ.x)
			{
				startMin.x = startMax.x;
				startSpan.x = 0f;
			}
			if (minZ == overlapZ.y)
			{
				startMin.y = startMax.y;
				startSpan.y = 0f;
			}
			if (maxZ == overlapZ.x)
			{
				endMin.x = endMax.x;
				endSpan.x = 0f;
			}
			if (maxZ == overlapZ.y)
			{
				endMin.y = endMax.y;
				endSpan.y = 0f;
			}

			if (minZ >= maxZ || ((startSpan.x == 0f && endSpan.x == 0f) || (startSpan.y == 0f && endSpan.y == 0f)))
			{
				return null;
			}

			CutVolumeBuilder builder = new CutVolumeBuilder(meshName);
			if (startSpan.x > 0f && startSpan.y > 0f)
			{
				builder.RQuad(
					new Vector3(startMin.x, startMin.y, minZ),
					new Vector3(startMax.x, startMin.y, minZ),
					new Vector3(startMax.x, startMax.y, minZ),
					new Vector3(startMin.x, startMax.y, minZ));
			}

			if (endSpan.x > 0f && endSpan.y > 0f)
			{
				builder.Quad(
					new Vector3(endMin.x, endMin.y, maxZ),
					new Vector3(endMax.x, endMin.y, maxZ),
					new Vector3(endMax.x, endMax.y, maxZ),
					new Vector3(endMin.x, endMax.y, maxZ));
			}

			if (startSpan.x > 0f)
			{
				if (endSpan.x > 0f)
				{
					builder.RQuad(
						new Vector3(startMin.x, startMax.y, minZ),
						new Vector3(startMax.x, startMax.y, minZ),
						new Vector3(endMax.x, endMax.y, maxZ),
						new Vector3(endMin.x, endMax.y, maxZ));
					builder.Quad(
						new Vector3(startMin.x, startMin.y, minZ),
						new Vector3(startMax.x, startMin.y, minZ),
						new Vector3(endMax.x, endMin.y, maxZ),
						new Vector3(endMin.x, endMin.y, maxZ));
				}
				else
				{
					builder.RTri(
						new Vector3(startMin.x, startMax.y, minZ),
						new Vector3(startMax.x, startMax.y, minZ),
						new Vector3(endMax.x, endMax.y, maxZ));
					builder.Tri(
						new Vector3(startMin.x, startMin.y, minZ),
						new Vector3(startMax.x, startMin.y, minZ),
						new Vector3(endMax.x, endMin.y, maxZ));
				}
			}
			else if (endSpan.x > 0f)
			{
				builder.RTri(
					new Vector3(startMin.x, startMax.y, minZ),
					new Vector3(endMax.x, endMax.y, maxZ),
					new Vector3(endMin.x, endMax.y, maxZ));
				builder.Tri(
					new Vector3(startMin.x, startMin.y, minZ),
					new Vector3(endMax.x, endMin.y, maxZ),
					new Vector3(endMin.x, endMin.y, maxZ));
			}

			if (startSpan.y > 0f)
			{
				if (endSpan.y > 0f)
				{
					builder.RQuad(
						new Vector3(startMax.x, startMin.y, minZ),
						new Vector3(endMax.x, endMin.y, maxZ),
						new Vector3(endMax.x, endMax.y, maxZ),
						new Vector3(startMax.x, startMax.y, minZ));
					builder.Quad(
						new Vector3(startMin.x, startMin.y, minZ),
						new Vector3(endMin.x, endMin.y, maxZ),
						new Vector3(endMin.x, endMax.y, maxZ),
						new Vector3(startMin.x, startMax.y, minZ));
				}
				else
				{
					builder.RTri(
						new Vector3(startMax.x, startMin.y, minZ),
						new Vector3(endMax.x, endMin.y, maxZ),
						new Vector3(startMax.x, startMax.y, minZ));
					builder.Tri(
						new Vector3(startMin.x, startMin.y, minZ),
						new Vector3(endMin.x, endMin.y, maxZ),
						new Vector3(startMin.x, startMax.y, minZ));
				}
			}
			else if (endSpan.y > 0f)
			{
				builder.RTri(
					new Vector3(startMax.x, startMin.y, minZ),
					new Vector3(endMax.x, endMin.y, maxZ),
					new Vector3(endMax.x, endMax.y, maxZ));
				builder.Tri(
					new Vector3(startMin.x, startMin.y, minZ),
					new Vector3(endMin.x, endMin.y, maxZ),
					new Vector3(endMin.x, endMax.y, maxZ));
			}

			return builder.ToMesh();
		}

		// 判断一个截面是否启用了任意方向的 cutting。 / Check whether a section enables cutting on any side.
		private static bool HasSectionCutting(FuselageSectionSettings section)
		{
			return section.GetCutEnabled(0)
				|| section.GetCutEnabled(1)
				|| section.GetCutEnabled(2)
				|| section.GetCutEnabled(3);
		}

		// 计算一个截面在本地 2D 平面中的有效 cutting 边界。 / Compute the effective local 2D cutting bounds for one section.
		private static CutBounds GetCutBounds(FuselageSectionSettings section, Vector2 center)
		{
			section.GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting);
			float cutTop = section.GetCutEnabled(0) ? Mathf.Clamp(section.CutTop, minCutting.X, maxCutting.X) : minCutting.X;
			float cutRight = section.GetCutEnabled(1) ? Mathf.Clamp(section.CutRight, minCutting.Y, maxCutting.Y) : minCutting.Y;
			float cutBottom = section.GetCutEnabled(2) ? Mathf.Clamp(section.CutBottom, minCutting.Z, maxCutting.Z) : minCutting.Z;
			float cutLeft = section.GetCutEnabled(3) ? Mathf.Clamp(section.CutLeft, minCutting.W, maxCutting.W) : minCutting.W;
			float minX = center.x + (-0.5f + cutLeft) * section.Width;
			float minY = center.y + (-0.5f + cutBottom) * section.Height;
			float maxX = center.x + (0.5f - cutRight) * section.Width;
			float maxY = center.y + (0.5f - cutTop) * section.Height;

			return new CutBounds(minX, minY, maxX, maxY);
		}

		// 对两个 Vector2 逐分量取最大值。 / Take the component-wise maximum of two Vector2 values.
		private static Vector2 ComponentMax(Vector2 a, Vector2 b)
		{
			return new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
		}

		private readonly struct CutBounds
		{
			// 保存一个截面的二维 cutting 包围盒。 / Store the 2D cutting bounds for one section.
			public CutBounds(float minX, float minY, float maxX, float maxY)
			{
				MinX = minX;
				MinY = minY;
				MaxX = maxX;
				MaxY = maxY;
			}

			public float MinX { get; }

			public float MinY { get; }

			public float MaxX { get; }

			public float MaxY { get; }
		}

		private sealed class CutVolumeBuilder
		{
			private readonly List<Vector3> _vertices = new List<Vector3>();
			private readonly List<int> _triangles = new List<int>();
			private readonly string _meshName;

			// 创建一个累积 cut-volume 三角面的临时构建器。 / Create a temporary builder that accumulates cut-volume triangles.
			public CutVolumeBuilder(string meshName)
			{
				_meshName = meshName;
			}

			// 追加一个按当前 winding 写入的三角形。 / Append one triangle using the current winding order.
			public void Tri(Vector3 a, Vector3 b, Vector3 c)
			{
				AddTriangle(a, b, c);
			}

			// 追加一个反向 winding 的三角形。 / Append one triangle with reversed winding.
			public void RTri(Vector3 a, Vector3 b, Vector3 c)
			{
				AddTriangle(a, c, b);
			}

			// 追加一个按当前 winding 拆分的四边形。 / Append one quad split into triangles using the current winding order.
			public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				AddTriangle(a, b, c);
				AddTriangle(a, c, d);
			}

			// 追加一个反向 winding 的四边形。 / Append one quad with reversed winding.
			public void RQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				AddTriangle(a, c, b);
				AddTriangle(a, d, c);
			}

			// 把累计的 cut-volume 三角形输出为 Mesh。 / Export the accumulated cut-volume triangles as a Mesh.
			public Mesh ToMesh()
			{
				if (_triangles.Count == 0)
				{
					return null;
				}

				Mesh result = new Mesh
				{
					name = _meshName
				};
				result.indexFormat = _vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
				result.SetVertices(_vertices);
				result.SetTriangles(_triangles, 0, true);
				result.RecalculateNormals();
				result.RecalculateBounds();
				return result;
			}

			// 以独立顶点的方式追加一个三角面。 / Append one triangle face using independent vertices.
			private void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
			{
				int start = _vertices.Count;
				_vertices.Add(a);
				_vertices.Add(b);
				_vertices.Add(c);
				_triangles.Add(start);
				_triangles.Add(start + 1);
				_triangles.Add(start + 2);
			}
		}
	}
}
