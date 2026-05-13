using System;
using System.Collections.Generic;
using UnityEngine;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Keep short face labels local to preview cutter helpers.")]
public interface IFuselageCarver
{
	bool TryGetCutMesh(FuselagePart target, out Mesh mesh);

	bool TryGetCutPlanes(FuselagePart target, out Plane[] planes);
}

internal static class FuselageCarverUtility
{
	private const float Epsilon = 0.0001f;

	private const int CornerSamples = 10;

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

	public static Mesh BuildSolidCutMesh(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		PreviewMeshData data = BuildExtrudedSolidMeshData(outline, depth, meshName);
		return data.ToMesh();
	}

	public static bool CanCarveTarget(Part source, FuselagePart target)
	{
		if (source == null || target == null)
		{
			return false;
		}

		return source.HasExplicitTargets && source.ExplicitlyTargetsPart(target.PartId);
	}

	public static Plane[] BuildConvexPlanes(Transform targetTransform, Transform sourceTransform, IReadOnlyList<Vector2> outline, float depth)
	{
		if (targetTransform == null || sourceTransform == null || outline == null || outline.Count < 3)
		{
			return Array.Empty<Plane>();
		}

		float halfDepth = Mathf.Max(0.01f, depth) * 0.5f;
		Vector3[] back = new Vector3[outline.Count];
		Vector3[] front = new Vector3[outline.Count];
		Vector3 insidePoint = Vector3.zero;
		for (int i = 0; i < outline.Count; i++)
		{
			Vector3 backLocal = new Vector3(outline[i].x, outline[i].y, -halfDepth);
			Vector3 frontLocal = new Vector3(outline[i].x, outline[i].y, halfDepth);
			back[i] = targetTransform.InverseTransformPoint(sourceTransform.TransformPoint(backLocal));
			front[i] = targetTransform.InverseTransformPoint(sourceTransform.TransformPoint(frontLocal));
			insidePoint += back[i] + front[i];
		}
		insidePoint /= outline.Count * 2f;

		List<Plane> planes = new List<Plane>(outline.Count + 2);
		if (TryCreateLoopPlane(back, false, insidePoint, out Plane backPlane))
		{
			planes.Add(backPlane);
		}
		if (TryCreateLoopPlane(front, true, insidePoint, out Plane frontPlane))
		{
			planes.Add(frontPlane);
		}
		for (int i = 0; i < outline.Count; i++)
		{
			int next = (i + 1) % outline.Count;
			if (TryCreateInwardFacingPlane(back[i], back[next], front[next], insidePoint, out Plane sidePlane))
			{
				planes.Add(sidePlane);
			}
		}
		return planes.ToArray();
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
		return BuildRoundedConvexOutline(polygon, cornerRadius, CornerSamples);
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
		List<Vector3> vertices = new List<Vector3>(loop.Count * 2);
		List<int> triangles = new List<int>((loop.Count - 2) * 6 + loop.Count * 6);
		for (int i = 0; i < loop.Count; i++)
		{
			vertices.Add(new Vector3(loop[i].x, loop[i].y, -halfDepth));
		}
		for (int i = 0; i < loop.Count; i++)
		{
			vertices.Add(new Vector3(loop[i].x, loop[i].y, halfDepth));
		}

		for (int i = 1; i < loop.Count - 1; i++)
		{
			AddOrientedMeshTriangle(triangles, vertices, 0, i + 1, i, Vector3.back);
			AddOrientedMeshTriangle(triangles, vertices, loop.Count, loop.Count + i, loop.Count + i + 1, Vector3.forward);
		}

		for (int i = 0; i < loop.Count; i++)
		{
			int next = (i + 1) % loop.Count;
			int backA = i;
			int backB = next;
			int frontA = loop.Count + i;
			int frontB = loop.Count + next;
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
		data.RecalculateNormals();
		return data;
	}

	private static List<Vector2> BuildRoundedConvexOutline(List<Vector2> polygon, float cornerRadius, int cornerSamples)
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

		return BuildRoundedPolygon(polygon, requestedRadius, Mathf.Max(2, cornerSamples));
	}

	private static List<Vector2> BuildRoundedPolygon(List<Vector2> polygon, float requestedRadius, int cornerSamples)
	{
		polygon = EnsureClockwise(polygon);
		List<Vector2> points = new List<Vector2>(polygon.Count * cornerSamples);
		for (int i = 0; i < polygon.Count; i++)
		{
			Vector2 previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
			Vector2 current = polygon[i];
			Vector2 next = polygon[(i + 1) % polygon.Count];
			Vector2 dirPrev = (previous - current).normalized;
			Vector2 dirNext = (next - current).normalized;
			float angle = Mathf.Acos(Mathf.Clamp(Vector2.Dot(dirPrev, dirNext), -0.9999f, 0.9999f));
			float limit = 0.5f * Mathf.Min(Vector2.Distance(previous, current), Vector2.Distance(current, next)) * Mathf.Tan(angle * 0.5f);
			float radius = Mathf.Min(requestedRadius, limit);
			if (radius <= Epsilon)
			{
				AddUnique(points, current);
				continue;
			}

			float tangentDistance = radius / Mathf.Tan(angle * 0.5f);
			Vector2 start = current + dirPrev * tangentDistance;
			Vector2 end = current + dirNext * tangentDistance;
			Vector2 bisector = (dirPrev + dirNext).normalized;
			float centerDistance = radius / Mathf.Sin(angle * 0.5f);
			Vector2 center = current + bisector * centerDistance;
			float startAngle = Mathf.Atan2(start.y - center.y, start.x - center.x);
			float endAngle = Mathf.Atan2(end.y - center.y, end.x - center.x);
			for (int sample = 0; sample < cornerSamples; sample++)
			{
				float t = sample / (float)(cornerSamples - 1);
				float angleSample = Mathf.LerpAngle(startAngle * Mathf.Rad2Deg, endAngle * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
				AddUnique(points, center + new Vector2(Mathf.Cos(angleSample), Mathf.Sin(angleSample)) * radius);
			}
		}
		RemoveNearDuplicateLoopPoints(points);
		return EnsureClockwise(points);
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

	private static Vector2 Rotate(Vector2 value)
	{
		return new Vector2(value.y, -value.x);
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

	private static Plane CreateInwardFacingPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 insidePoint)
	{
		Plane plane = new Plane(a, b, c);
		if (plane.GetDistanceToPoint(insidePoint) > 0f)
		{
			plane = new Plane(-plane.normal, -plane.distance);
		}
		return plane;
	}

	private static bool TryCreateLoopPlane(IReadOnlyList<Vector3> loop, bool reverse, Vector3 insidePoint, out Plane plane)
	{
		plane = default;
		if (loop == null || loop.Count < 3)
		{
			return false;
		}

		for (int a = 0; a < loop.Count - 2; a++)
		{
			for (int b = a + 1; b < loop.Count - 1; b++)
			{
				for (int c = b + 1; c < loop.Count; c++)
				{
					Vector3 p0 = loop[a];
					Vector3 p1 = reverse ? loop[c] : loop[b];
					Vector3 p2 = reverse ? loop[b] : loop[c];
					if (TryCreateInwardFacingPlane(p0, p1, p2, insidePoint, out plane))
					{
						return true;
					}
				}
			}
		}

		return false;
	}

	private static bool TryCreateInwardFacingPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 insidePoint, out Plane plane)
	{
		plane = CreateInwardFacingPlane(a, b, c, insidePoint);
		return plane.normal.sqrMagnitude > Epsilon * Epsilon;
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
