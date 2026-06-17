using System;
using System.Collections.Generic;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	internal static class FuselageManifoldUtility
	{
		private const double MinimumValidVolume = 1.1920928955078125E-10d;

		private const float CutMinimumEpsilon = 0.0001f;

		// 把机身 loft 输入转成 manifold，并在需要时执行 section-cutting 相交。 / Convert loft input into a manifold and optionally apply section-cutting intersection.
		public static GeneratedMeshData BuildLoft(GeneratedMeshData source, FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool applySectionCutting, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (!applySectionCutting)
			{
				source.Name = meshName;
				return source;
			}

			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage loft");
				return null;
			}

			GeneratedMeshData cutVolume = null;
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
					return ToMeshData(sourceManifold, meshName);
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

				return ToMeshData(result, meshName);
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
				cutVolume = null;
				source = null;
			}
		}

		// 用 runtime manifold 对机身和 cutter 执行减法布尔。 / Subtract a cutter from a fuselage mesh using the runtime manifold path.
		public static GeneratedMeshData Subtract(GeneratedMeshData source, GeneratedMeshData cutter, Matrix4x4 cutterToSource, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (cutter == null || cutter.VertexCount == 0)
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
		private static GeneratedMeshData ExecuteBoolean(GeneratedMeshData source, GeneratedMeshData cutter, Matrix4x4 cutterToSource, ManifoldOpType operation, string meshName, string context)
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

				GeneratedMeshData output = result.ToMeshData(meshName);
				if (output != null)
				{
					output.Name = meshName;
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
		private static ManifoldHandle CreateBooleanCutterManifold(GeneratedMeshData cutter, Matrix4x4 cutterToSource, string context)
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
			GeneratedMeshData bakedCutter = null;
			try
			{
				bakedCutter = BakeMeshDataTransform(cutter, cutterToSource);
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
				bakedCutter = null;
			}
		}

		private static ManifoldHandle CreateManifold(GeneratedMeshData mesh, out ManifoldError status)
		{
			if (mesh == null)
			{
				status = ManifoldError.INVALID_CONSTRUCTION;
				return null;
			}

			return ManifoldHandle.Create(mesh.Vertices, mesh.Normals, new IReadOnlyList<int>[] { mesh.Triangles }, null, null, out status);
		}

		// 把一个 native manifold 安全地导出成带名字的托管网格数据。 / Safely export a native manifold into named managed mesh data.
		private static GeneratedMeshData ToMeshData(ManifoldHandle manifold, string meshName)
		{
			GeneratedMeshData output = manifold?.ToMeshData(meshName);
			if (output != null)
			{
				output.Name = meshName;
			}
			return output;
		}

		// 把矩阵直接烘到托管网格数据顶点和索引上。 / Bake a matrix directly into managed mesh vertices and triangle winding.
		private static GeneratedMeshData BakeMeshDataTransform(GeneratedMeshData source, Matrix4x4 transform)
		{
			if (source == null)
			{
				return new GeneratedMeshData("PreviewMesh_Baked");
			}

			List<Vector3> vertices = new List<Vector3>(source.Vertices);
			for (int i = 0; i < vertices.Count; i++)
			{
				vertices[i] = transform.MultiplyPoint3x4(vertices[i]);
			}

			bool mirrored = GetLinearDeterminant(transform) < 0f;
			List<int> triangles = new List<int>(source.Triangles?.Count ?? 0);
			if (!mirrored)
			{
				triangles.AddRange(source.Triangles ?? new List<int>());
			}
			else
			{
				List<int> sourceTriangles = source.Triangles ?? new List<int>();
				for (int i = 0; i + 2 < sourceTriangles.Count; i += 3)
				{
					triangles.Add(sourceTriangles[i]);
					triangles.Add(sourceTriangles[i + 2]);
					triangles.Add(sourceTriangles[i + 1]);
				}
			}

			List<Vector3> normals = new List<Vector3>(source.Normals?.Count ?? 0);
			if (source.Normals != null && source.Normals.Count == source.VertexCount)
			{
				for (int i = 0; i < source.Normals.Count; i++)
				{
					normals.Add(transform.MultiplyVector(source.Normals[i]).normalized);
				}
			}

			return new GeneratedMeshData(string.IsNullOrWhiteSpace(source.Name) ? "PreviewMesh_Baked" : source.Name + "_Baked", vertices, normals, triangles);
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
		private static GeneratedMeshData BuildCutVolume(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, string meshName)
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
			CutBounds rearBounds = GetCutBounds(rear, rearCenter, out Bool4Value rearActiveCuts);
			CutBounds frontBounds = GetCutBounds(front, frontCenter, out Bool4Value frontActiveCuts);
			if (!HasActiveCutting(rearActiveCuts) && !HasActiveCutting(frontActiveCuts))
			{
				return null;
			}

			ExpandSharedUncutSides(ref rearBounds, ref frontBounds, rearActiveCuts, frontActiveCuts, rear, front, offset);
			return BuildCutVolumeData(
				new Vector2(rearBounds.MinX, rearBounds.MinY),
				new Vector2(rearBounds.MaxX, rearBounds.MaxY),
				new Vector2(frontBounds.MinX, frontBounds.MinY),
				new Vector2(frontBounds.MaxX, frontBounds.MaxY),
				zOffset,
				meshName);
		}

		// 从两端截面的矩形包围变化构建一个可 manifold 化的裁切闭体。 / Construct a manifold-friendly clipping volume from the changing rectangular bounds of both end sections.
		private static GeneratedMeshData BuildCutVolumeData(Vector2 min1, Vector2 max1, Vector2 min2, Vector2 max2, float zOffset, string meshName)
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

			return builder.ToMeshData();
		}

		// 判断一个截面是否启用了任意方向的 cutting。 / Check whether a section enables cutting on any side.
		private static bool HasSectionCutting(FuselageSectionSettings section)
		{
			return section.GetCutEnabled(0)
				|| section.GetCutEnabled(1)
				|| section.GetCutEnabled(2)
				|| section.GetCutEnabled(3);
		}

		// 判断四个方向中是否有真实推进过最小值的 cutting。 / Check whether any side has a real cutting value beyond the minimum.
		private static bool HasActiveCutting(Bool4Value activeCuts)
		{
			return activeCuts.X || activeCuts.Y || activeCuts.Z || activeCuts.W;
		}

		// 两端同一侧都处于最小值时，该侧按原版语义不参与 cut-volume，避免中段曲面被贴边裁切面擦削。 / If both ends stay at the minimum on one side, keep that side out of the cut-volume so the middle surface is not shaved by the boundary plane.
		private static void ExpandSharedUncutSides(ref CutBounds rearBounds, ref CutBounds frontBounds, Bool4Value rearActiveCuts, Bool4Value frontActiveCuts, FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset)
		{
			float padding = GetCutBoundsPadding(rearBounds, frontBounds, rear, front, offset);
			if (!rearActiveCuts.W && !frontActiveCuts.W)
			{
				float minX = Mathf.Min(rearBounds.MinX, frontBounds.MinX) - padding;
				rearBounds = new CutBounds(minX, rearBounds.MinY, rearBounds.MaxX, rearBounds.MaxY);
				frontBounds = new CutBounds(minX, frontBounds.MinY, frontBounds.MaxX, frontBounds.MaxY);
			}
			if (!rearActiveCuts.Y && !frontActiveCuts.Y)
			{
				float maxX = Mathf.Max(rearBounds.MaxX, frontBounds.MaxX) + padding;
				rearBounds = new CutBounds(rearBounds.MinX, rearBounds.MinY, maxX, rearBounds.MaxY);
				frontBounds = new CutBounds(frontBounds.MinX, frontBounds.MinY, maxX, frontBounds.MaxY);
			}
			if (!rearActiveCuts.Z && !frontActiveCuts.Z)
			{
				float minY = Mathf.Min(rearBounds.MinY, frontBounds.MinY) - padding;
				rearBounds = new CutBounds(rearBounds.MinX, minY, rearBounds.MaxX, rearBounds.MaxY);
				frontBounds = new CutBounds(frontBounds.MinX, minY, frontBounds.MaxX, frontBounds.MaxY);
			}
			if (!rearActiveCuts.X && !frontActiveCuts.X)
			{
				float maxY = Mathf.Max(rearBounds.MaxY, frontBounds.MaxY) + padding;
				rearBounds = new CutBounds(rearBounds.MinX, rearBounds.MinY, rearBounds.MaxX, maxY);
				frontBounds = new CutBounds(frontBounds.MinX, frontBounds.MinY, frontBounds.MaxX, maxY);
			}
		}

		// 生成足够大的外扩距离，让被跳过的方向稳定落在机身外侧。 / Build a generous expansion distance so skipped directions stay outside the fuselage.
		private static float GetCutBoundsPadding(CutBounds rearBounds, CutBounds frontBounds, FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset)
		{
			float span = Mathf.Max(
				Mathf.Abs(offset.x),
				Mathf.Abs(offset.y),
				Mathf.Abs(offset.z),
				Mathf.Abs(rear.Width),
				Mathf.Abs(rear.Height),
				Mathf.Abs(front.Width),
				Mathf.Abs(front.Height),
				Mathf.Abs(rearBounds.MaxX - rearBounds.MinX),
				Mathf.Abs(rearBounds.MaxY - rearBounds.MinY),
				Mathf.Abs(frontBounds.MaxX - frontBounds.MinX),
				Mathf.Abs(frontBounds.MaxY - frontBounds.MinY),
				1f);
			return span * 4f + 1f;
		}

		// 计算一个截面在本地 2D 平面中的有效 cutting 边界。 / Compute the effective local 2D cutting bounds for one section.
		private static CutBounds GetCutBounds(FuselageSectionSettings section, Vector2 center, out Bool4Value activeCuts)
		{
			section.GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting);
			float cutTop = GetEffectiveCutValue(section, 0, minCutting.X, maxCutting.X, out bool topActive);
			float cutRight = GetEffectiveCutValue(section, 1, minCutting.Y, maxCutting.Y, out bool rightActive);
			float cutBottom = GetEffectiveCutValue(section, 2, minCutting.Z, maxCutting.Z, out bool bottomActive);
			float cutLeft = GetEffectiveCutValue(section, 3, minCutting.W, maxCutting.W, out bool leftActive);
			activeCuts = new Bool4Value(topActive, rightActive, bottomActive, leftActive);
			float minX = center.x + (-0.5f + cutLeft) * section.Width;
			float minY = center.y + (-0.5f + cutBottom) * section.Height;
			float maxX = center.x + (0.5f - cutRight) * section.Width;
			float maxY = center.y + (0.5f - cutTop) * section.Height;

			return new CutBounds(minX, minY, maxX, maxY);
		}

		// 把小于等于 minCutting 的值解释为未切割，匹配原版 slider 左端写 null 的行为。 / Treat values at or below minCutting as uncut, matching the original slider writing null at the left edge.
		private static float GetEffectiveCutValue(FuselageSectionSettings section, int side, float minCutting, float maxCutting, out bool active)
		{
			if (!section.GetCutEnabled(side))
			{
				active = false;
				return minCutting;
			}

			float value = Mathf.Clamp(section.GetCutValue(side), minCutting, maxCutting);
			active = value > minCutting + CutMinimumEpsilon;
			return active ? value : minCutting;
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

			// 把累计的 cut-volume 三角形输出为托管网格数据。 / Export the accumulated cut-volume triangles as managed mesh data.
			public GeneratedMeshData ToMeshData()
			{
				if (_triangles.Count == 0)
				{
					return null;
				}

				List<Vector3> normals = new List<Vector3>(_vertices.Count);
				for (int i = 0; i < _vertices.Count; i++)
				{
					normals.Add(Vector3.zero);
				}

				for (int i = 0; i + 2 < _triangles.Count; i += 3)
				{
					int a = _triangles[i];
					int b = _triangles[i + 1];
					int c = _triangles[i + 2];
					Vector3 normal = Vector3.Cross(_vertices[b] - _vertices[a], _vertices[c] - _vertices[a]);
					if (normal.sqrMagnitude <= 0.0000001f)
					{
						continue;
					}

					normal.Normalize();
					normals[a] += normal;
					normals[b] += normal;
					normals[c] += normal;
				}

				for (int i = 0; i < normals.Count; i++)
				{
					normals[i] = normals[i].sqrMagnitude > 0.0000001f ? normals[i].normalized : Vector3.up;
				}

				return new GeneratedMeshData(_meshName, _vertices, normals, _triangles);
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
