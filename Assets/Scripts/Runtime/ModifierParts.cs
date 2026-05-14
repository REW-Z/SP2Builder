using System.Collections.Generic;
using UnityEngine;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Keep short face labels local to preview cutter helpers.")]
public interface IFuselageCarver
{
	bool TryBuildCutPreviewData(FuselagePart target, out PreviewMeshData previewMeshData);
}

internal static class FuselageCarverUtility
{
	private const float Epsilon = 0.0001f;

	private const int InflateVertsPerTurn = 20;

	public static Mesh BuildWireframeMesh(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		Mesh mesh = new Mesh
		{
			name = meshName
		};
		if (outline == null || outline.Count < 2)
		{
			return mesh;
		}

		float halfDepth = Mathf.Max(0.01f, depth) * 0.5f;
		List<Vector3> vertices = new List<Vector3>(outline.Count * 2);
		List<int> indices = new List<int>(outline.Count * 6);
		for (int i = 0; i < outline.Count; i++)
		{
			vertices.Add(new Vector3(outline[i].x, outline[i].y, -halfDepth));
		}
		for (int i = 0; i < outline.Count; i++)
		{
			vertices.Add(new Vector3(outline[i].x, outline[i].y, halfDepth));
		}

		for (int i = 0; i < outline.Count; i++)
		{
			int next = (i + 1) % outline.Count;
			indices.Add(i);
			indices.Add(next);
			indices.Add(outline.Count + i);
			indices.Add(outline.Count + next);
			indices.Add(i);
			indices.Add(outline.Count + i);
		}

		mesh.SetVertices(vertices);
		mesh.SetIndices(indices, MeshTopology.Lines, 0);
		mesh.RecalculateBounds();
		return mesh;
	}

	public static PreviewMeshData BuildSolidCutPreviewData(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		return BuildExtrudedSolidMeshData(outline, depth, meshName);
	}

	public static bool CanCarveTarget(Part source, FuselagePart target)
	{
		if (source == null || target == null)
		{
			return false;
		}

		return source.HasExplicitTargets && source.ExplicitlyTargetsPart(target.PartId);
	}

	// 按原版 trapezoid window 语义构建圆角轮廓。 / Build the rounded outline for a trapezoid window using the original semantics.
	public static List<Vector2> BuildWindowOutline(Vector2 upperSpan, Vector2 lowerSpan, float height, float cornerRadius)
	{
		float halfHeight = Mathf.Max(0.01f, height) * 0.5f;
		List<Vector2> polygon = new List<Vector2>(4);
		if (upperSpan.y - upperSpan.x > Epsilon)
		{
			polygon.Add(new Vector2(upperSpan.y, halfHeight));
		}
		if (lowerSpan.y - lowerSpan.x > Epsilon)
		{
			polygon.Add(new Vector2(lowerSpan.y, -halfHeight));
		}
		polygon.Add(new Vector2(lowerSpan.x, -halfHeight));
		polygon.Add(new Vector2(upperSpan.x, halfHeight));
		RemoveNearDuplicateLoopPoints(polygon);
		return BuildRoundedConvexOutline(polygon, cornerRadius);
	}

	// 按原版 simple bay 语义构建圆角矩形轮廓。 / Build the rounded rectangle outline for a simple bay using the original semantics.
	public static List<Vector2> BuildBayOutline(float width, float height, float cornerRadius)
	{
		float halfWidth = Mathf.Max(0.01f, width) * 0.5f;
		return BuildWindowOutline(new Vector2(-halfWidth, halfWidth), new Vector2(-halfWidth, halfWidth), height, cornerRadius);
	}

	private static PreviewMeshData BuildExtrudedSolidMeshData(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		PreviewMeshData data = new PreviewMeshData(meshName);
		if (outline == null || outline.Count < 3)
		{
			return data;
		}

		List<Vector2> loop = new List<Vector2>(outline.Count);
		for (int i = 0; i < outline.Count; i++)
		{
			loop.Add(outline[i]);
		}
		RemoveNearDuplicateLoopPoints(loop);
		if (loop.Count < 3)
		{
			return data;
		}

		float halfDepth = Mathf.Max(0.01f, depth) * 0.5f;
		Vector2 centroid2D = ComputePolygonCentroid(loop);
		List<Vector3> vertices = new List<Vector3>(loop.Count * 4);
		List<int> triangles = new List<int>((loop.Count - 2) * 6 + loop.Count * 6);
		int[] backCapIndices = new int[loop.Count];
		int[] backSideIndices = new int[loop.Count];
		int[] frontSideIndices = new int[loop.Count];
		int[] frontCapIndices = new int[loop.Count];
		for (int i = 0; i < loop.Count; i++)
		{
			Vector2 point = loop[i];
			Vector2 radial = point - centroid2D;
			if (radial.sqrMagnitude <= Epsilon * Epsilon)
			{
				Vector2 previous = loop[(i - 1 + loop.Count) % loop.Count];
				Vector2 next = loop[(i + 1) % loop.Count];
				radial = Rotate((next - previous).normalized);
			}
			Vector3 sideNormal = new Vector3(radial.x, radial.y, 0f).normalized;

			backCapIndices[i] = vertices.Count;
			vertices.Add(new Vector3(point.x, point.y, -halfDepth));
			backSideIndices[i] = vertices.Count;
			vertices.Add(new Vector3(point.x, point.y, -halfDepth));
			frontSideIndices[i] = vertices.Count;
			vertices.Add(new Vector3(point.x, point.y, halfDepth));
			frontCapIndices[i] = vertices.Count;
			vertices.Add(new Vector3(point.x, point.y, halfDepth));

			data.MergeFromVertices.Add(backCapIndices[i]);
			data.MergeToVertices.Add(backSideIndices[i]);
			data.MergeFromVertices.Add(frontCapIndices[i]);
			data.MergeToVertices.Add(frontSideIndices[i]);

			data.Normals.Add(Vector3.back);
			data.Normals.Add(sideNormal.sqrMagnitude > Epsilon * Epsilon ? sideNormal : Vector3.right);
			data.Normals.Add(sideNormal.sqrMagnitude > Epsilon * Epsilon ? sideNormal : Vector3.right);
			data.Normals.Add(Vector3.forward);
		}

		for (int i = 1; i < loop.Count - 1; i++)
		{
			AddOrientedMeshTriangle(triangles, vertices, backCapIndices[0], backCapIndices[i + 1], backCapIndices[i], Vector3.back);
			AddOrientedMeshTriangle(triangles, vertices, frontCapIndices[0], frontCapIndices[i], frontCapIndices[i + 1], Vector3.forward);
		}

		for (int i = 0; i < loop.Count; i++)
		{
			int next = (i + 1) % loop.Count;
			int backA = backSideIndices[i];
			int backB = backSideIndices[next];
			int frontA = frontSideIndices[i];
			int frontB = frontSideIndices[next];
			Vector3 expectedNormal = new Vector3(
				((loop[i].x + loop[next].x) * 0.5f) - centroid2D.x,
				((loop[i].y + loop[next].y) * 0.5f) - centroid2D.y,
				0f);
			if (expectedNormal.sqrMagnitude <= Epsilon * Epsilon)
			{
				expectedNormal = Vector3.Cross(vertices[frontA] - vertices[backA], vertices[backB] - vertices[backA]).normalized;
			}

			AddOrientedMeshTriangle(triangles, vertices, backA, backB, frontB, expectedNormal);
			AddOrientedMeshTriangle(triangles, vertices, backA, frontB, frontA, expectedNormal);
		}

		data.Vertices.AddRange(vertices);
		data.SubMeshTriangles[0].AddRange(triangles);
		return data;
	}

	private static List<Vector2> BuildRoundedConvexOutline(List<Vector2> polygon, float cornerRadius)
	{
		polygon = EnsureClockwise(polygon);
		RemoveNearDuplicateLoopPoints(polygon);
		if (polygon.Count < 3)
		{
			return polygon;
		}

		float requestedRadius = Mathf.Clamp01(cornerRadius) * EstimateMaxInset(polygon);
		if (requestedRadius <= Epsilon)
		{
			return polygon;
		}

		List<Vector2> insetPolygon = new List<Vector2>(polygon);
		float actualInset = InsetConvexLoop(insetPolygon, requestedRadius, 0f);
		if (actualInset <= Epsilon || insetPolygon.Count < 3)
		{
			return polygon;
		}

		List<Vector2> rounded = InflateConvexLoop(insetPolygon, actualInset, InflateVertsPerTurn);
		CollapseTinyEdges(rounded, Mathf.Max(actualInset * 0.2f, ComputeLoopMaxExtent(polygon) * 0.0025f));
		RemoveNearDuplicateLoopPoints(rounded);
		return rounded.Count >= 3 ? EnsureClockwise(rounded) : polygon;
	}

	private static float InsetConvexLoop(List<Vector2> points, float insetBy, float minSize)
	{
		if (points == null || points.Count < 3 || insetBy <= Epsilon)
		{
			return 0f;
		}

		float remaining = insetBy;
		const int maxIterations = 64;
		for (int iteration = 0; iteration < maxIterations && remaining > Epsilon && points.Count >= 3; iteration++)
		{
			RemoveNearDuplicateLoopPoints(points);
			if (points.Count < 3)
			{
				break;
			}

			int count = points.Count;
			float[] shrinkage = new float[count];
			Vector2[] velocity = new Vector2[count];
			for (int i = 0; i < count; i++)
			{
				Vector2 current = points[i];
				Vector2 previous = points[(i - 1 + count) % count];
				Vector2 next = points[(i + 1) % count];
				Vector2 inVec = current - previous;
				Vector2 outVec = next - current;
				if (inVec.sqrMagnitude <= Epsilon * Epsilon || outVec.sqrMagnitude <= Epsilon * Epsilon)
				{
					shrinkage[i] = 0f;
					velocity[i] = Vector2.zero;
					continue;
				}

				shrinkage[i] = ComputePointShrinkage(inVec, outVec);
				velocity[i] = ComputePointVelocity(shrinkage[i], inVec);
			}

			float step = remaining;
			float maxEdgeLimit = 0f;
			List<int> edgesToMerge = new List<int>();
			for (int i = 0; i < count; i++)
			{
				int next = (i + 1) % count;
				float shrink = shrinkage[i] + shrinkage[next];
				if (shrink <= Epsilon)
				{
					continue;
				}

				float edgeLimit = Vector2.Distance(points[i], points[next]) / shrink;
				maxEdgeLimit = Mathf.Max(maxEdgeLimit, edgeLimit);
				if (edgeLimit <= Epsilon)
				{
					step = 0f;
					edgesToMerge.Add(i);
					continue;
				}

				if (step + Epsilon >= edgeLimit)
				{
					if (edgeLimit < step - 0.00001f)
					{
						edgesToMerge.Clear();
					}
					step = Mathf.Min(step, edgeLimit);
					edgesToMerge.Add(i);
				}
			}

			if (minSize > Epsilon && maxEdgeLimit > Epsilon)
			{
				float minSizeStep = maxEdgeLimit - minSize;
				if (minSizeStep <= Epsilon)
				{
					break;
				}

				if (step > minSizeStep - Epsilon)
				{
					step = minSizeStep;
					edgesToMerge.Clear();
				}
			}

			if (step > Epsilon)
			{
				for (int i = 0; i < count; i++)
				{
					points[i] += velocity[i] * step;
				}
			}

			remaining -= step;
			if (edgesToMerge.Count > 0)
			{
				MergeEdges(points, edgesToMerge);
			}

			if (step <= Epsilon && edgesToMerge.Count == 0)
			{
				break;
			}
		}

		RemoveNearDuplicateLoopPoints(points);
		return insetBy - Mathf.Max(0f, remaining);
	}

	private static List<Vector2> InflateConvexLoop(List<Vector2> points, float radius, int vertsPerTurn)
	{
		List<Vector2> result = new List<Vector2>(Mathf.Max(points.Count * 4, 8));
		if (points == null || points.Count == 0 || radius <= Epsilon)
		{
			return result;
		}

		float maxExtent = ComputeLoopMaxExtent(points);

		Vector2 previous = points[^1];
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			Vector2 inDir = (current - previous).normalized;
			Vector2 outDir = (next - current).normalized;
			if (inDir.sqrMagnitude <= Epsilon * Epsilon || outDir.sqrMagnitude <= Epsilon * Epsilon)
			{
				previous = current;
				continue;
			}

			Vector2 startNormal = RotateCounterClockwise(inDir);
			Vector2 endNormal = RotateCounterClockwise(outDir);
			float startAngle = Mathf.Atan2(startNormal.y, startNormal.x);
			float endAngle = Mathf.Atan2(endNormal.y, endNormal.x);
			float delta = Mathf.DeltaAngle(startAngle * Mathf.Rad2Deg, endAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
			if (delta > 0f)
			{
				delta -= Mathf.PI * 2f;
			}

			int angleSampleCount = Mathf.Max(2, Mathf.RoundToInt(vertsPerTurn * Mathf.Abs(delta) / (Mathf.PI * 2f)));
			int radiusSampleCount = Mathf.Max(2, Mathf.CeilToInt(radius / Mathf.Max(maxExtent * 0.05f, Epsilon)) + 1);
			int sampleCount = Mathf.Min(angleSampleCount, radiusSampleCount);
			for (int sample = 0; sample < sampleCount; sample++)
			{
				float t = sampleCount <= 1 ? 0f : sample / (float)(sampleCount - 1);
				float angle = startAngle + delta * t;
				AddUnique(result, current + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
			}

			previous = current;
		}

		if (result.Count == 0)
		{
			float step = Mathf.PI * -2f / Mathf.Max(1, vertsPerTurn - 1);
			for (int i = 0; i < vertsPerTurn; i++)
			{
				float angle = i * step;
				AddUnique(result, points[0] + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
			}
		}

		RemoveNearDuplicateLoopPoints(result);
		return result;
	}

	private static float ComputeLoopMaxExtent(IReadOnlyList<Vector2> points)
	{
		if (points == null || points.Count == 0)
		{
			return 0f;
		}

		float minX = float.PositiveInfinity;
		float minY = float.PositiveInfinity;
		float maxX = float.NegativeInfinity;
		float maxY = float.NegativeInfinity;
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 point = points[i];
			minX = Mathf.Min(minX, point.x);
			minY = Mathf.Min(minY, point.y);
			maxX = Mathf.Max(maxX, point.x);
			maxY = Mathf.Max(maxY, point.y);
		}

		return Mathf.Max(maxX - minX, maxY - minY);
	}

	private static float EstimateMaxInset(IReadOnlyList<Vector2> points)
	{
		float[] shrinkage = new float[points.Count];
		Vector2 previous = points[^1];
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			shrinkage[i] = ComputePointShrinkage(current - previous, next - current);
			previous = current;
		}

		float maxInset = float.PositiveInfinity;
		for (int i = 0; i < points.Count; i++)
		{
			int next = (i + 1) % points.Count;
			float edgeLength = Vector2.Distance(points[i], points[next]);
			float shrink = shrinkage[i] + shrinkage[next];
			if (Mathf.Abs(shrink) <= Epsilon)
			{
				continue;
			}
			maxInset = Mathf.Min(maxInset, edgeLength / shrink);
		}

		return float.IsFinite(maxInset) ? Mathf.Max(0f, maxInset) : 0f;
	}

	private static float ComputePointShrinkage(Vector2 inVec, Vector2 outVec)
	{
		Vector2 a = inVec.normalized;
		Vector2 b = outVec.normalized;
		Vector2 normalA = Rotate(a);
		Vector2 normalB = Rotate(b);
		float numerator = Vector2.Dot(normalA - normalB, normalA);
		float denominator = Vector2.Dot(normalA, b);
		if (Mathf.Abs(denominator) > Epsilon)
		{
			return numerator / denominator;
		}
		return 0f;
	}

	private static Vector2 ComputePointVelocity(float pointShrinkage, Vector2 inVec)
	{
		Vector2 inDir = inVec.normalized;
		return Rotate(inDir) - inDir * pointShrinkage;
	}

	private static Vector2 Rotate(Vector2 value)
	{
		return new Vector2(value.y, -value.x);
	}

	private static Vector2 RotateCounterClockwise(Vector2 value)
	{
		return new Vector2(-value.y, value.x);
	}

	private static void AddOrientedMeshTriangle(List<int> triangles, List<Vector3> vertices, int a, int b, int c, Vector3 expectedNormal)
	{
		Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
		if (cross.sqrMagnitude <= Epsilon * Epsilon)
		{
			return;
		}

		if (expectedNormal.sqrMagnitude > Epsilon * Epsilon && Vector3.Dot(cross, expectedNormal) < 0f)
		{
			(c, b) = (b, c);
		}

		triangles.Add(a);
		triangles.Add(b);
		triangles.Add(c);
	}

	private static Vector2 ComputePolygonCentroid(IReadOnlyList<Vector2> points)
	{
		float twiceArea = 0f;
		Vector2 centroid = Vector2.zero;
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			float cross = Cross(current, next);
			twiceArea += cross;
			centroid += (current + next) * cross;
		}
		if (Mathf.Abs(twiceArea) <= Epsilon)
		{
			Vector2 average = Vector2.zero;
			for (int i = 0; i < points.Count; i++)
			{
				average += points[i];
			}
			return average / Mathf.Max(1, points.Count);
		}
		return centroid / (3f * twiceArea);
	}

	private static float SignedArea(IReadOnlyList<Vector2> points)
	{
		float area = 0f;
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			area += current.x * next.y - next.x * current.y;
		}
		return area * 0.5f;
	}

	private static List<Vector2> EnsureClockwise(List<Vector2> points)
	{
		if (points.Count >= 3 && SignedArea(points) > 0f)
		{
			points.Reverse();
		}
		return points;
	}

	private static void RemoveNearDuplicateLoopPoints(List<Vector2> points)
	{
		for (int i = points.Count - 1; i >= 0; i--)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			if (Vector2.Distance(current, next) <= Epsilon)
			{
				points.RemoveAt(i);
			}
		}
	}

	private static void CollapseTinyEdges(List<Vector2> points, float minEdgeLength)
	{
		if (points == null || points.Count < 4 || minEdgeLength <= Epsilon)
		{
			return;
		}

		for (int iteration = 0; iteration < 64 && points.Count > 3; iteration++)
		{
			int removeIndex = -1;
			float shortestEdge = float.PositiveInfinity;
			for (int i = 0; i < points.Count; i++)
			{
				int next = (i + 1) % points.Count;
				float edgeLength = Vector2.Distance(points[i], points[next]);
				if (edgeLength < shortestEdge)
				{
					shortestEdge = edgeLength;
					removeIndex = next;
				}
			}

			if (shortestEdge >= minEdgeLength || removeIndex < 0)
			{
				break;
			}

			points.RemoveAt(removeIndex);
		}
	}

	private static void MergeEdges(List<Vector2> points, List<int> edgesToMerge)
	{
		if (points == null || points.Count < 3 || edgesToMerge == null || edgesToMerge.Count == 0)
		{
			return;
		}

		HashSet<int> removeIndices = new HashSet<int>();
		int count = points.Count;
		for (int i = 0; i < edgesToMerge.Count; i++)
		{
			removeIndices.Add((edgesToMerge[i] + 1) % count);
		}

		List<int> sortedIndices = new List<int>(removeIndices);
		sortedIndices.Sort((a, b) => b.CompareTo(a));
		for (int i = 0; i < sortedIndices.Count && points.Count >= 3; i++)
		{
			points.RemoveAt(sortedIndices[i]);
		}

		RemoveNearDuplicateLoopPoints(points);
	}

	private static void AddUnique(List<Vector2> points, Vector2 point)
	{
		if (points.Count > 0 && Vector2.Distance(points[^1], point) <= Epsilon)
		{
			return;
		}
		points.Add(point);
	}

	private static float Cross(Vector2 a, Vector2 b)
	{
		return a.x * b.y - a.y * b.x;
	}
}
