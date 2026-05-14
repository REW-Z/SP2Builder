using System;
using System.Collections.Generic;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	internal static class FuselageManifoldUtility
	{
		private const double MinimumValidVolume = 1.1920928955078125E-07d;

		public static PreviewMeshData BuildLoft(PreviewMeshData source, FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool applySectionCutting, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (!ManifoldRuntimeAvailability.IsAvailable)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage loft");
				return null;
			}

			try
			{
				using ManifoldHandle sourceManifold = ManifoldHandle.Create(source, out ManifoldError sourceStatus);
				if (!IsUsable(sourceManifold, sourceStatus))
				{
					Debug.LogWarning($"manifold source build failed during fuselage loft: {sourceStatus}");
					return null;
				}

				if (!applySectionCutting)
				{
					return ToPreviewMeshData(sourceManifold, meshName);
				}

				PreviewMeshData cutVolume = BuildCutVolume(rear, front, offset, meshName + "_Volume");
				if (cutVolume == null)
				{
					return ToPreviewMeshData(sourceManifold, meshName);
				}

				using ManifoldHandle cutManifold = ManifoldHandle.Create(cutVolume, out ManifoldError cutStatus);
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

				return ToPreviewMeshData(result, meshName);
			}
			catch (Exception exception) when (
				exception is DllNotFoundException
				|| exception is EntryPointNotFoundException
				|| exception is BadImageFormatException)
			{
				ManifoldRuntimeAvailability.LogUnavailableOnce("fuselage loft");
				return null;
			}
		}

		public static PreviewMeshData Subtract(PreviewMeshData source, PreviewMeshData cutter, Matrix4x4 cutterToSource, string meshName)
		{
			if (source == null)
			{
				return null;
			}

			if (cutter == null || cutter.Vertices.Count == 0)
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

		private static PreviewMeshData ExecuteBoolean(PreviewMeshData source, PreviewMeshData cutter, Matrix4x4 cutterToSource, ManifoldOpType operation, string meshName, string context)
		{
			try
			{
				using ManifoldHandle sourceManifold = ManifoldHandle.Create(source, out ManifoldError sourceStatus);
				if (!IsUsable(sourceManifold, sourceStatus))
				{
					Debug.LogWarning($"manifold source build failed during {context}: {sourceStatus}");
					return null;
				}

				using ManifoldHandle cutterLocalManifold = ManifoldHandle.Create(cutter, out ManifoldError cutterStatus);
				if (!IsUsable(cutterLocalManifold, cutterStatus))
				{
					Debug.LogWarning($"manifold cutter build failed during {context}: {cutterStatus}");
					return null;
				}

				using ManifoldHandle cutterManifold = cutterLocalManifold.Transform(cutterToSource);
				if (!IsUsable(cutterManifold, cutterManifold?.Status ?? ManifoldError.INVALID_CONSTRUCTION))
				{
					Debug.LogWarning($"manifold cutter transform failed during {context}: {cutterManifold?.Status ?? ManifoldError.INVALID_CONSTRUCTION}");
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

				PreviewMeshData output = result.ToPreviewMeshData(meshName);
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

		private static bool IsUsable(ManifoldHandle manifold, ManifoldError status)
		{
			return manifold != null
				&& status == ManifoldError.NO_ERROR
				&& !manifold.IsEmpty
				&& manifold.Volume >= MinimumValidVolume;
		}

		private static PreviewMeshData ToPreviewMeshData(ManifoldHandle manifold, string meshName)
		{
			PreviewMeshData output = manifold?.ToPreviewMeshData(meshName);
			if (output != null)
			{
				output.Name = meshName;
			}
			return output;
		}

		private static PreviewMeshData BuildCutVolume(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, string meshName)
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

		private static PreviewMeshData BuildCutVolumeData(Vector2 min1, Vector2 max1, Vector2 min2, Vector2 max2, float zOffset, string meshName)
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

			return builder.ToPreviewMeshData();
		}

		private static bool HasSectionCutting(FuselageSectionSettings section)
		{
			return section.GetCutEnabled(0)
				|| section.GetCutEnabled(1)
				|| section.GetCutEnabled(2)
				|| section.GetCutEnabled(3);
		}

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

			if (minX >= maxX)
			{
				float midX = 0.5f * (minX + maxX);
				minX = midX - 0.0001f;
				maxX = midX + 0.0001f;
			}
			if (minY >= maxY)
			{
				float midY = 0.5f * (minY + maxY);
				minY = midY - 0.0001f;
				maxY = midY + 0.0001f;
			}

			return new CutBounds(minX, minY, maxX, maxY);
		}

		private static Vector2 ComponentMax(Vector2 a, Vector2 b)
		{
			return new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
		}

		private readonly struct CutBounds
		{
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

			public CutVolumeBuilder(string meshName)
			{
				_meshName = meshName;
			}

			public void Tri(Vector3 a, Vector3 b, Vector3 c)
			{
				AddTriangle(a, b, c);
			}

			public void RTri(Vector3 a, Vector3 b, Vector3 c)
			{
				AddTriangle(a, c, b);
			}

			public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				AddTriangle(a, b, c);
				AddTriangle(a, c, d);
			}

			public void RQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			{
				AddTriangle(a, c, b);
				AddTriangle(a, d, c);
			}

			public PreviewMeshData ToPreviewMeshData()
			{
				if (_triangles.Count == 0)
				{
					return null;
				}

				PreviewMeshData result = new PreviewMeshData(_meshName, _vertices, null, _triangles);
				result.RecalculateNormals();
				return result;
			}

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