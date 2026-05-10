using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;


// 复用凸裁切、封口和凸轮廓挤出辅助，供机身 slice 与 hole cutting 共用。 / Shared convex clipping, capping, and convex-outline extrusion helpers used by both fuselage slicing and hole cutting.
internal static class MeshBooleanUtility
{
	private const float Epsilon = 0.0001f;

	private const float LoopConnectionTolerance = 0.0001f;

	private static bool _holeCapDiagnosticsReset;

	private readonly struct ClippedVertex
	{
		public ClippedVertex(Vector3 position, Vector3 normal)
		{
			Position = position;
			Normal = normal.sqrMagnitude <= Epsilon ? Vector3.forward : normal.normalized;
		}

		public Vector3 Position { get; }

		public Vector3 Normal { get; }
	}

	private readonly struct ClipSegment
	{
		public ClipSegment(ClippedVertex a, ClippedVertex b)
		{
			A = a;
			B = b;
		}

		public ClippedVertex A { get; }

		public ClippedVertex B { get; }
	}

	private readonly struct LoopEdge
	{
		public LoopEdge(int a, int b)
		{
			A = a;
			B = b;
		}

		public int A { get; }

		public int B { get; }
	}

	private readonly struct DirectedLoopEdge
	{
		public DirectedLoopEdge(int edgeIndex, int fromNode)
		{
			EdgeIndex = edgeIndex;
			FromNode = fromNode;
		}

		public int EdgeIndex { get; }

		public int FromNode { get; }
	}

	private readonly struct BoundaryAnchor
	{
		public BoundaryAnchor(int edgeIndex, float parameter, Vector2 point)
		{
			EdgeIndex = edgeIndex;
			Parameter = parameter;
			Point = point;
		}

		public int EdgeIndex { get; }

		public float Parameter { get; }

		public Vector2 Point { get; }
	}

	private sealed class ProjectedLoop
	{
		public ProjectedLoop(List<ClippedVertex> points3D, List<Vector2> points2D)
		{
			Points3D = points3D;
			Points2D = points2D;
			Fractions = ComputeFractions(points2D);
			Area = Mathf.Abs(SignedArea(points2D));
			Centroid = ComputePolygonCentroid(points2D);
		}

		public float Area { get; }

		public Vector2 Centroid { get; }

		public List<float> Fractions { get; }

		public int Count => Points3D.Count;

		public List<Vector2> Points2D { get; }

		public List<ClippedVertex> Points3D { get; }
	}

	// 用凸保留体真正裁切网格，并在所有裁切平面上补齐平截面。 / Intersect a mesh with a convex keep volume and generate planar caps on every cut plane.
	public static Mesh IntersectConvexVolume(Mesh source, Plane[] planes, string meshName)
	{
		if (source == null || planes == null || planes.Length == 0)
		{
			return source;
		}

		List<List<ClippedVertex>> polygons = BuildSourcePolygons(source);
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
		{
			Plane plane = planes[planeIndex];
			List<List<ClippedVertex>> nextPolygons = new List<List<ClippedVertex>>(polygons.Count + 16);
			List<ClipSegment> segments = new List<ClipSegment>();
			for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
			{
				SplitPolygonByPlane(polygons[polygonIndex], plane, out _, out List<ClippedVertex> inside, out ClipSegment segment, out bool hasSegment);
				if (inside.Count >= 3)
				{
					nextPolygons.Add(inside);
				}
				if (hasSegment)
				{
					segments.Add(segment);
				}
			}

			nextPolygons.AddRange(BuildCapPolygons(segments, plane.normal));
			polygons = nextPolygons;
			if (polygons.Count == 0)
			{
				break;
			}
		}

		return BuildMesh(polygons, meshName);
	}

	public static PreviewMeshData IntersectConvexVolume(PreviewMeshData source, Plane[] planes, string meshName)
	{
		if (source == null || planes == null || planes.Length == 0)
		{
			return source;
		}

		List<List<ClippedVertex>> polygons = BuildSourcePolygons(source);
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
		{
			Plane plane = planes[planeIndex];
			List<List<ClippedVertex>> nextPolygons = new List<List<ClippedVertex>>(polygons.Count + 16);
			List<ClipSegment> segments = new List<ClipSegment>();
			for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
			{
				SplitPolygonByPlane(polygons[polygonIndex], plane, out _, out List<ClippedVertex> inside, out ClipSegment segment, out bool hasSegment);
				if (inside.Count >= 3)
				{
					nextPolygons.Add(inside);
				}
				if (hasSegment)
				{
					segments.Add(segment);
				}
			}

			nextPolygons.AddRange(BuildCapPolygons(segments, plane.normal));
			polygons = nextPolygons;
			if (polygons.Count == 0)
			{
				break;
			}
		}

		return BuildMeshData(polygons, meshName);
	}

	// 从源网格中减去一个或多个凸体，并为洞口补出新的截面壳壁。 / Subtract one or more convex volumes from a mesh and cap the new hole walls.
	public static Mesh SubtractConvexVolumes(Mesh source, IReadOnlyList<Plane[]> volumes, string meshName)
	{
		if (source == null || volumes == null || volumes.Count == 0)
		{
			return source;
		}

		List<List<ClippedVertex>> polygons = BuildSourcePolygons(source);
		for (int volumeIndex = 0; volumeIndex < volumes.Count; volumeIndex++)
		{
			Plane[] planes = volumes[volumeIndex];
			if (planes == null || planes.Length == 0)
			{
				continue;
			}

			polygons = SubtractSingleConvexVolume(polygons, planes);
			if (polygons.Count == 0)
			{
				break;
			}
		}

		return BuildMesh(polygons, meshName);
	}

	// 按原版 0..1 CornerRadius 语义构建圆角矩形轮廓。 / Build a rounded rectangle outline using the original 0..1 CornerRadius semantics.
	public static List<Vector2> BuildRoundedRectangleOutline(float width, float height, float cornerRadius, int cornerSamples)
	{
		List<Vector2> polygon = new List<Vector2>
		{
			new Vector2(width * 0.5f, height * 0.5f),
			new Vector2(width * 0.5f, -height * 0.5f),
			new Vector2(-width * 0.5f, -height * 0.5f),
			new Vector2(-width * 0.5f, height * 0.5f)
		};
		return BuildRoundedConvexOutline(polygon, cornerRadius, cornerSamples);
	}

	// 按原版 upper/lower span + CornerRadius 语义构建圆角梯形轮廓。 / Build a rounded trapezoid outline using the original upper/lower span plus CornerRadius semantics.
	public static List<Vector2> BuildRoundedTrapezoidOutline(Vector2 upperSpan, Vector2 lowerSpan, float height, float cornerRadius, int cornerSamples)
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
		return BuildRoundedConvexOutline(polygon, cornerRadius, cornerSamples);
	}

	// 把 2D 凸轮廓沿深度方向挤出成线框，便于 hole-cutting 预览。 / Extrude a 2D convex outline into a line mesh for hole-cutting previews.
	public static Mesh BuildExtrudedWireframe(IReadOnlyList<Vector2> outline, float depth, string meshName)
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

	// 把 2D 凸轮廓沿深度方向挤出成闭合实体网格，供 MeshBoolean 直接做体布尔。 / Extrude a 2D convex outline into a closed solid mesh so MeshBoolean can use it directly for volumetric subtraction.
	public static Mesh BuildExtrudedSolidMesh(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		PreviewMeshData data = BuildExtrudedSolidMeshData(outline, depth, meshName);
		return data.ToMesh();
	}

	public static PreviewMeshData BuildExtrudedSolidMeshData(IReadOnlyList<Vector2> outline, float depth, string meshName)
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

	// 用挤出的凸轮廓生成一组 inward-facing 平面，供 hole cutting 与 slice cutting 共用。 / Build inward-facing planes from an extruded convex outline for shared hole and slice cutting use.
	public static Plane[] BuildExtrudedConvexPlanes(Transform targetTransform, Transform sourceTransform, IReadOnlyList<Vector2> outline, float depth)
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

	// 按期望法线修正三角形绕序，保证实体 cutter 的法线一致朝外。 / Correct triangle winding against an expected normal so the solid cutter keeps a consistent outward orientation.
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

	// 针对单个凸挖空体执行减法，并在各平面上补出 hole wall。 / Subtract a single convex volume and add the corresponding hole-wall caps on its planes.
	private static List<List<ClippedVertex>> SubtractSingleConvexVolume(List<List<ClippedVertex>> sourcePolygons, Plane[] planes)
	{
		List<List<ClippedVertex>> result = new List<List<ClippedVertex>>(sourcePolygons.Count);
		List<List<ClippedVertex>> workingSource = sourcePolygons;
		List<List<ClippedVertex>> workingCaps = new List<List<ClippedVertex>>();
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
		{
			Plane plane = planes[planeIndex];
			List<List<ClippedVertex>> nextWorkingSource = new List<List<ClippedVertex>>(workingSource.Count);
			List<List<ClippedVertex>> nextWorkingCaps = new List<List<ClippedVertex>>(workingCaps.Count + 8);
			List<ClipSegment> segments = new List<ClipSegment>();
			List<ClipSegment> coplanarCapBoundary = new List<ClipSegment>();
			List<List<ClippedVertex>> deferredCoplanarCaps = new List<List<ClippedVertex>>();
			for (int polygonIndex = 0; polygonIndex < workingSource.Count; polygonIndex++)
			{
				SplitPolygonByPlane(workingSource[polygonIndex], plane, out List<ClippedVertex> outside, out List<ClippedVertex> inside, out ClipSegment segment, out bool hasSegment);
				if (outside.Count >= 3)
				{
					result.Add(outside);
				}
				if (inside.Count >= 3)
				{
					nextWorkingSource.Add(inside);
				}
				if (hasSegment)
				{
					segments.Add(segment);
				}
			}

			for (int polygonIndex = 0; polygonIndex < workingCaps.Count; polygonIndex++)
			{
				SplitPolygonByPlane(workingCaps[polygonIndex], plane, out _, out List<ClippedVertex> inside, out ClipSegment segment, out bool hasSegment);
				if (inside.Count >= 3)
				{
					if (IsPolygonCoplanar(inside, plane))
					{
						deferredCoplanarCaps.Add(inside);
						TogglePolygonBoundarySegments(coplanarCapBoundary, inside);
					}
					else
					{
						nextWorkingCaps.Add(inside);
					}
				}
				if (hasSegment)
				{
					segments.Add(segment);
				}
			}

			if (coplanarCapBoundary.Count > 0)
			{
				segments.AddRange(coplanarCapBoundary);
			}

			// 对减法产生的洞壁，截面法线应背离剩余实体、朝向被挖掉的 cavity，因此要与 inward-facing plane normal 反向。 / Hole-wall caps from subtraction should point away from the remaining solid and into the carved cavity, so they must oppose the inward-facing plane normal.
			List<List<ClippedVertex>> rebuiltCaps = BuildCapPolygons(segments, -plane.normal, $"subtract plane {planeIndex}");
			if (rebuiltCaps.Count == 0)
			{
				TryBuildSingleChainCapFromPlaneBoundary(rebuiltCaps, planes, planeIndex, segments, -plane.normal);
			}

			if (rebuiltCaps.Count > 0 || deferredCoplanarCaps.Count == 0)
			{
				nextWorkingCaps.AddRange(rebuiltCaps);
			}
			else
			{
				nextWorkingCaps.AddRange(deferredCoplanarCaps);
			}

			workingSource = nextWorkingSource;
			workingCaps = nextWorkingCaps;
			if (workingSource.Count == 0 && workingCaps.Count == 0)
			{
				break;
			}
		}

		result.AddRange(workingCaps);
		return result;
	}

	// 当当前平面只有一条开链时，用 cutter face 自身的边界路径补出缺失的另一条链。 / When the current plane yields only one open chain, recover the missing mate from the cutter-face boundary itself.
	private static bool TryBuildSingleChainCapFromPlaneBoundary(List<List<ClippedVertex>> polygons, Plane[] planes, int planeIndex, List<ClipSegment> segments, Vector3 capNormal)
	{
		List<List<ClippedVertex>> openChains = BuildOpenChains(segments);
		if (openChains.Count != 1 || openChains[0].Count < 2)
		{
			LogHoleCapDetail($"single-chain plane {planeIndex} skip openChains={openChains.Count}");
			return false;
		}

		List<ClippedVertex> boundaryChain = BuildPlaneBoundaryChain(
			planes,
			planeIndex,
			segments,
			openChains[0],
			capNormal,
			out float forwardArea,
			out float backwardArea,
			out float forwardDistanceScore,
			out float backwardDistanceScore,
			out bool selectedForward,
			out int faceVertexCount);
		if (boundaryChain.Count < 2)
		{
			LogHoleCapDetail($"single-chain plane {planeIndex} no-boundary open={openChains[0].Count} faceVerts={faceVertexCount} forwardArea={forwardArea:F4} backwardArea={backwardArea:F4} forwardDist={forwardDistanceScore:F4} backwardDist={backwardDistanceScore:F4}");
			return false;
		}

		int polygonCountBefore = polygons.Count;
		AddOpenChainCapPolygons(polygons, openChains[0], boundaryChain, capNormal);
		int addedPolygons = polygons.Count - polygonCountBefore;
		LogHoleCapDetail($"single-chain plane {planeIndex} open={openChains[0].Count} boundary={boundaryChain.Count} faceVerts={faceVertexCount} forwardArea={forwardArea:F4} backwardArea={backwardArea:F4} forwardDist={forwardDistanceScore:F4} backwardDist={backwardDistanceScore:F4} selected={(selectedForward ? "forward" : "backward")} added={addedPolygons}");
		return addedPolygons > 0;
	}

	// 计算当前 cutter plane 在其余 half-spaces 内的凸面边界，并取与开链围成较小区域的那条边界路径。 / Compute the current cutter-plane face inside the remaining half-spaces and keep the boundary path that encloses the smaller region with the open chain.
	private static List<ClippedVertex> BuildPlaneBoundaryChain(Plane[] planes, int planeIndex, List<ClipSegment> segments, List<ClippedVertex> openChain, Vector3 capNormal, out float forwardArea, out float backwardArea, out float forwardDistanceScore, out float backwardDistanceScore, out bool selectedForward, out int faceVertexCount)
	{
		forwardArea = float.PositiveInfinity;
		backwardArea = float.PositiveInfinity;
		forwardDistanceScore = float.PositiveInfinity;
		backwardDistanceScore = float.PositiveInfinity;
		selectedForward = true;
		faceVertexCount = 0;

		Plane plane = planes[planeIndex];
		Vector3 planeOrigin = -plane.normal * plane.distance;
		BuildPlaneBasis(plane.normal, out Vector3 axisX, out Vector3 axisY);
		List<Vector2> facePolygon = BuildPlaneFacePolygon2D(planes, planeIndex, segments, planeOrigin, axisX, axisY);
		faceVertexCount = facePolygon.Count;
		if (facePolygon.Count < 2)
		{
			return new List<ClippedVertex>();
		}

		List<float> cumulativeLengths = BuildPolygonCumulativeLengths(facePolygon, out float perimeter);
		if (perimeter <= Epsilon)
		{
			return new List<ClippedVertex>();
		}

		Vector2 chainStart = ProjectPointToPlane2D(openChain[0].Position, planeOrigin, axisX, axisY);
		Vector2 chainEnd = ProjectPointToPlane2D(openChain[^1].Position, planeOrigin, axisX, axisY);
		if (!TryFindBoundaryAnchor(facePolygon, cumulativeLengths, perimeter, chainStart, out BoundaryAnchor startAnchor)
			|| !TryFindBoundaryAnchor(facePolygon, cumulativeLengths, perimeter, chainEnd, out BoundaryAnchor endAnchor))
		{
			return new List<ClippedVertex>();
		}

		List<Vector2> forwardPath = BuildBoundaryPath(facePolygon, cumulativeLengths, perimeter, startAnchor, endAnchor);
		List<Vector2> backwardPath = BuildBoundaryPath(facePolygon, cumulativeLengths, perimeter, endAnchor, startAnchor);
		backwardPath.Reverse();

		List<Vector2> openChain2D = new List<Vector2>(openChain.Count);
		for (int i = 0; i < openChain.Count; i++)
		{
			openChain2D.Add(ProjectPointToPlane2D(openChain[i].Position, planeOrigin, axisX, axisY));
		}

		forwardArea = ComputeOpenChainEnclosedArea(openChain2D, forwardPath);
		backwardArea = ComputeOpenChainEnclosedArea(openChain2D, backwardPath);
		forwardDistanceScore = MeasurePathDistanceToOpenChain(forwardPath, openChain2D);
		backwardDistanceScore = MeasurePathDistanceToOpenChain(backwardPath, openChain2D);
		List<Vector2> selectedPath = SelectPreferredBoundaryPath(forwardPath, backwardPath, forwardArea, backwardArea, forwardDistanceScore, backwardDistanceScore);
		selectedForward = ReferenceEquals(selectedPath, forwardPath);

		List<ClippedVertex> chain = new List<ClippedVertex>(selectedPath.Count);
		for (int i = 0; i < selectedPath.Count; i++)
		{
			chain.Add(new ClippedVertex(planeOrigin + axisX * selectedPath[i].x + axisY * selectedPath[i].y, capNormal));
		}
		return chain;
	}

	// 在当前裁切平面上构建 cutter face 的 2D 凸多边形。 / Build the cutter-face convex polygon in 2D on the current clipping plane.
	private static List<Vector2> BuildPlaneFacePolygon2D(Plane[] planes, int planeIndex, List<ClipSegment> segments, Vector3 planeOrigin, Vector3 axisX, Vector3 axisY)
	{
		float extent = 1f;
		for (int i = 0; i < segments.Count; i++)
		{
			Vector2 a = ProjectPointToPlane2D(segments[i].A.Position, planeOrigin, axisX, axisY);
			Vector2 b = ProjectPointToPlane2D(segments[i].B.Position, planeOrigin, axisX, axisY);
			extent = Mathf.Max(extent, Mathf.Abs(a.x));
			extent = Mathf.Max(extent, Mathf.Abs(a.y));
			extent = Mathf.Max(extent, Mathf.Abs(b.x));
			extent = Mathf.Max(extent, Mathf.Abs(b.y));
		}
		extent = Mathf.Max(1f, extent * 4f + 0.5f);

		List<Vector2> polygon = new List<Vector2>(4)
		{
			new Vector2(-extent, -extent),
			new Vector2(extent, -extent),
			new Vector2(extent, extent),
			new Vector2(-extent, extent)
		};

		for (int i = 0; i < planes.Length; i++)
		{
			if (i == planeIndex)
			{
				continue;
			}

			Plane other = planes[i];
			float a = Vector3.Dot(other.normal, axisX);
			float b = Vector3.Dot(other.normal, axisY);
			float c = other.GetDistanceToPoint(planeOrigin) - Epsilon;
			polygon = ClipPolygonWithHalfPlane2D(polygon, a, b, c);
			if (polygon.Count == 0)
			{
				break;
			}
		}

		RemoveNearDuplicateLoopPoints(polygon);
		return polygon;
	}

	// 把凸 2D 多边形裁到一个半平面内。 / Clip a convex 2D polygon against one half-plane.
	private static List<Vector2> ClipPolygonWithHalfPlane2D(List<Vector2> polygon, float a, float b, float c)
	{
		List<Vector2> result = new List<Vector2>(polygon.Count + 1);
		for (int i = 0; i < polygon.Count; i++)
		{
			Vector2 previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
			Vector2 current = polygon[i];
			float previousValue = a * previous.x + b * previous.y + c;
			float currentValue = a * current.x + b * current.y + c;
			bool previousInside = previousValue <= 0f;
			bool currentInside = currentValue <= 0f;

			if (previousInside != currentInside)
			{
				float t = previousValue / (previousValue - currentValue);
				AddUnique(result, Vector2.Lerp(previous, current, Mathf.Clamp01(t)));
			}

			if (currentInside)
			{
				AddUnique(result, current);
			}
		}

		RemoveNearDuplicateLoopPoints(result);
		return result;
	}

	// 计算闭合 2D 多边形沿周长的累计长度。 / Compute cumulative perimeter lengths along a closed 2D polygon.
	private static List<float> BuildPolygonCumulativeLengths(IReadOnlyList<Vector2> polygon, out float perimeter)
	{
		List<float> lengths = new List<float>(polygon.Count);
		perimeter = 0f;
		for (int i = 0; i < polygon.Count; i++)
		{
			lengths.Add(perimeter);
			perimeter += Vector2.Distance(polygon[i], polygon[(i + 1) % polygon.Count]);
		}
		return lengths;
	}

	// 在 cutter face 边界上找到离目标点最近的锚点参数。 / Find the nearest anchor parameter for a target point on the cutter-face boundary.
	private static bool TryFindBoundaryAnchor(IReadOnlyList<Vector2> polygon, IReadOnlyList<float> cumulativeLengths, float perimeter, Vector2 target, out BoundaryAnchor anchor)
	{
		anchor = default;
		float bestDistanceSquared = float.PositiveInfinity;
		for (int i = 0; i < polygon.Count; i++)
		{
			Vector2 a = polygon[i];
			Vector2 b = polygon[(i + 1) % polygon.Count];
			float edgeLength = Vector2.Distance(a, b);
			if (edgeLength <= Epsilon)
			{
				continue;
			}

			Vector2 closest = ClosestPointOnSegment(target, a, b, out float fraction);
			float distanceSquared = (closest - target).sqrMagnitude;
			if (distanceSquared >= bestDistanceSquared)
			{
				continue;
			}

			bestDistanceSquared = distanceSquared;
			anchor = new BoundaryAnchor(i, cumulativeLengths[i] + edgeLength * fraction, closest);
		}

		return float.IsFinite(bestDistanceSquared);
	}

	// 沿闭合边界从起点锚走到终点锚，返回对应的 2D 边界路径。 / Walk the closed boundary from a start anchor to an end anchor and return the corresponding 2D boundary path.
	private static List<Vector2> BuildBoundaryPath(IReadOnlyList<Vector2> polygon, IReadOnlyList<float> cumulativeLengths, float perimeter, BoundaryAnchor start, BoundaryAnchor end)
	{
		List<Vector2> path = new List<Vector2>(polygon.Count + 2)
		{
			start.Point
		};

		float endParameter = end.Parameter;
		if (endParameter < start.Parameter + Epsilon)
		{
			endParameter += perimeter;
		}

		int index = (start.EdgeIndex + 1) % polygon.Count;
		for (int step = 0; step < polygon.Count; step++)
		{
			float vertexParameter = cumulativeLengths[index];
			if (vertexParameter < start.Parameter)
			{
				vertexParameter += perimeter;
			}

			if (vertexParameter >= endParameter - Epsilon)
			{
				break;
			}

			AddUnique(path, polygon[index]);
			index = (index + 1) % polygon.Count;
		}

		AddUnique(path, end.Point);
		return path;
	}

	// 评估一条 boundary path 与开链围成的面积，优先保留较小的那一侧。 / Measure the area enclosed by one boundary path and the open chain, preferring the smaller side.
	private static float ComputeOpenChainEnclosedArea(IReadOnlyList<Vector2> openChain, IReadOnlyList<Vector2> boundaryPath)
	{
		List<Vector2> loop = new List<Vector2>(openChain.Count + boundaryPath.Count);
		for (int i = 0; i < openChain.Count; i++)
		{
			AddUnique(loop, openChain[i]);
		}

		for (int i = boundaryPath.Count - 1; i >= 0; i--)
		{
			AddUnique(loop, boundaryPath[i]);
		}

		RemoveNearDuplicateLoopPoints(loop);
		return loop.Count >= 3 ? Mathf.Abs(SignedArea(loop)) : float.PositiveInfinity;
	}

	// 在两条候选边界路径中选出与开链围成较小区域的那条；面积相同则选更短路径。 / Pick the boundary path that encloses the smaller region with the open chain; break ties by choosing the shorter path.
	private static List<Vector2> SelectPreferredBoundaryPath(List<Vector2> forwardPath, List<Vector2> backwardPath, float forwardArea, float backwardArea, float forwardDistanceScore, float backwardDistanceScore)
	{
		if (Mathf.Abs(forwardDistanceScore - backwardDistanceScore) > Epsilon)
		{
			return forwardDistanceScore < backwardDistanceScore ? forwardPath : backwardPath;
		}

		if (!float.IsFinite(forwardArea))
		{
			return backwardPath;
		}

		if (!float.IsFinite(backwardArea))
		{
			return forwardPath;
		}

		if (Mathf.Abs(forwardArea - backwardArea) > Epsilon)
		{
			return forwardArea < backwardArea ? forwardPath : backwardPath;
		}

		return EstimatePathLength(forwardPath) <= EstimatePathLength(backwardPath) ? forwardPath : backwardPath;
	}

	// 评估一条 boundary path 相对开链的“远离程度”，优先选择整体更贴近开链的路径。 / Measure how far a boundary path strays from the open chain so the closer side can be preferred.
	private static float MeasurePathDistanceToOpenChain(IReadOnlyList<Vector2> path, IReadOnlyList<Vector2> openChain)
	{
		if (path.Count == 0 || openChain.Count < 2)
		{
			return float.PositiveInfinity;
		}

		float maxDistance = 0f;
		float totalDistance = 0f;
		int sampleCount = 0;
		for (int i = 0; i < path.Count; i++)
		{
			AccumulatePathDistanceSample(path[i], openChain, ref maxDistance, ref totalDistance, ref sampleCount);
			if (i + 1 < path.Count)
			{
				AccumulatePathDistanceSample((path[i] + path[i + 1]) * 0.5f, openChain, ref maxDistance, ref totalDistance, ref sampleCount);
			}
		}

		return sampleCount == 0 ? float.PositiveInfinity : maxDistance * 4f + totalDistance / sampleCount;
	}

	// 记录一个采样点到开链的最近距离。 / Record the nearest distance from one sample point to the open chain.
	private static void AccumulatePathDistanceSample(Vector2 point, IReadOnlyList<Vector2> openChain, ref float maxDistance, ref float totalDistance, ref int sampleCount)
	{
		float distance = DistanceToOpenChain(point, openChain);
		if (!float.IsFinite(distance))
		{
			return;
		}

		maxDistance = Mathf.Max(maxDistance, distance);
		totalDistance += distance;
		sampleCount++;
	}

	// 计算一个点到开链折线的最近距离。 / Compute the nearest distance from one point to the open-chain polyline.
	private static float DistanceToOpenChain(Vector2 point, IReadOnlyList<Vector2> openChain)
	{
		float bestDistanceSquared = float.PositiveInfinity;
		for (int i = 1; i < openChain.Count; i++)
		{
			Vector2 closest = ClosestPointOnSegment(point, openChain[i - 1], openChain[i], out _);
			float distanceSquared = (closest - point).sqrMagnitude;
			if (distanceSquared < bestDistanceSquared)
			{
				bestDistanceSquared = distanceSquared;
			}
		}

		return float.IsFinite(bestDistanceSquared) ? Mathf.Sqrt(bestDistanceSquared) : float.PositiveInfinity;
	}

	// 计算一条 open path 的总长度。 / Compute the total length of an open path.
	private static float EstimatePathLength(IReadOnlyList<Vector2> path)
	{
		float length = 0f;
		for (int i = 1; i < path.Count; i++)
		{
			length += Vector2.Distance(path[i - 1], path[i]);
		}
		return length;
	}

	// 把 3D 点投到当前平面的 2D 基底上。 / Project a 3D point onto the active plane basis in 2D.
	private static Vector2 ProjectPointToPlane2D(Vector3 point, Vector3 planeOrigin, Vector3 axisX, Vector3 axisY)
	{
		Vector3 offset = point - planeOrigin;
		return new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY));
	}

	// 求一个点到 2D 线段的最近点及其参数。 / Compute the closest point on a 2D segment and the corresponding edge fraction.
	private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 a, Vector2 b, out float fraction)
	{
		Vector2 edge = b - a;
		float lengthSquared = edge.sqrMagnitude;
		if (lengthSquared <= Epsilon * Epsilon)
		{
			fraction = 0f;
			return a;
		}

		fraction = Mathf.Clamp01(Vector2.Dot(point - a, edge) / lengthSquared);
		return a + edge * fraction;
	}

	// 检查一个多边形是否整体落在当前裁切平面上。 / Check whether a polygon lies entirely on the current clipping plane.
	private static bool IsPolygonCoplanar(List<ClippedVertex> polygon, Plane plane)
	{
		for (int i = 0; i < polygon.Count; i++)
		{
			if (Mathf.Abs(plane.GetDistanceToPoint(polygon[i].Position)) > Epsilon)
			{
				return false;
			}
		}

		return polygon.Count >= 3;
	}

	// 把 coplanar cap 三角片的共享边抵消掉，只留下该平面上真实的外边界。 / Cancel shared edges between coplanar cap triangles so only the true outer boundary on this plane remains.
	private static void TogglePolygonBoundarySegments(List<ClipSegment> segments, List<ClippedVertex> polygon)
	{
		for (int i = 0; i < polygon.Count; i++)
		{
			ClippedVertex a = polygon[i];
			ClippedVertex b = polygon[(i + 1) % polygon.Count];
			if ((b.Position - a.Position).sqrMagnitude <= Epsilon * Epsilon)
			{
				continue;
			}

			bool removed = false;
			for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
			{
				if (!ClipSegmentsEquivalent(segments[segmentIndex], a, b))
				{
					continue;
				}

				segments.RemoveAt(segmentIndex);
				removed = true;
				break;
			}

			if (!removed)
			{
				segments.Add(new ClipSegment(a, b));
			}
		}
	}

	// 判断两条段是否代表同一条无向边，允许端点顺序相反。 / Check whether two segments represent the same undirected edge, allowing reversed endpoints.
	private static bool ClipSegmentsEquivalent(ClipSegment segment, ClippedVertex a, ClippedVertex b)
	{
		return (Vector3.Distance(segment.A.Position, a.Position) <= LoopConnectionTolerance && Vector3.Distance(segment.B.Position, b.Position) <= LoopConnectionTolerance)
			|| (Vector3.Distance(segment.A.Position, b.Position) <= LoopConnectionTolerance && Vector3.Distance(segment.B.Position, a.Position) <= LoopConnectionTolerance);
	}

	// 仅保留位于剩余 half-spaces 内部的 cap 多边形片段。 / Keep only the portions of cap polygons that remain inside the remaining half-spaces.
	private static List<List<ClippedVertex>> ClipPolygonsInside(List<List<ClippedVertex>> polygons, Plane[] planes, int startPlaneIndex)
	{
		for (int planeIndex = startPlaneIndex; planeIndex < planes.Length; planeIndex++)
		{
			List<List<ClippedVertex>> nextPolygons = new List<List<ClippedVertex>>(polygons.Count);
			for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
			{
				SplitPolygonByPlane(polygons[polygonIndex], planes[planeIndex], out _, out List<ClippedVertex> inside, out _, out _);
				if (inside.Count >= 3)
				{
					nextPolygons.Add(inside);
				}
			}
			polygons = nextPolygons;
			if (polygons.Count == 0)
			{
				break;
			}
		}
		return polygons;
	}

	// 从一组交线段组装 cap 轮廓，并把单环或双环转换成扁平三角面。 / Assemble cap contours from cut segments and turn single-loop or two-loop cases into flat triangles.
	private static List<List<ClippedVertex>> BuildCapPolygons(List<ClipSegment> segments, Vector3 capNormal, string diagnosticsLabel = null)
	{
		List<List<ClippedVertex>> polygons = new List<List<ClippedVertex>>();
		List<List<ClippedVertex>> loops3D = BuildLoops(segments, capNormal);
		if (loops3D.Count == 0)
		{
			List<List<ClippedVertex>> openChains = BuildOpenChains(segments);
			AddOpenChainCapPolygons(polygons, openChains, capNormal);

			LogHoleCapDiagnostics(diagnosticsLabel, segments, loops3D, polygons.Count);
			return polygons;
		}

		Vector3 origin = loops3D[0][0].Position;
		BuildPlaneBasis(capNormal, out Vector3 axisX, out Vector3 axisY);
		List<ProjectedLoop> loops = new List<ProjectedLoop>(loops3D.Count);
		for (int i = 0; i < loops3D.Count; i++)
		{
			List<ClippedVertex> loop3D = loops3D[i];
			List<Vector2> loop2D = new List<Vector2>(loop3D.Count);
			for (int pointIndex = 0; pointIndex < loop3D.Count; pointIndex++)
			{
				Vector3 offset = loop3D[pointIndex].Position - origin;
				loop2D.Add(new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY)));
			}
			if (SignedArea(loop2D) < 0f)
			{
				loop3D.Reverse();
				loop2D.Reverse();
			}
			loops.Add(new ProjectedLoop(loop3D, loop2D));
		}

		loops.Sort((a, b) => b.Area.CompareTo(a.Area));
		bool[] used = new bool[loops.Count];
		for (int i = 0; i < loops.Count; i++)
		{
			if (used[i] || loops[i].Count < 3 || loops[i].Area <= Epsilon)
			{
				continue;
			}

			int innerIndex = -1;
			for (int j = i + 1; j < loops.Count; j++)
			{
				if (used[j] || loops[j].Count < 3)
				{
					continue;
				}
				if (PointInPolygon(loops[i].Points2D, loops[j].Centroid))
				{
					innerIndex = j;
					break;
				}
			}

			if (innerIndex >= 0)
			{
				AddRimCapPolygons(polygons, loops[i], loops[innerIndex], origin, axisX, axisY, capNormal);
				used[i] = true;
				used[innerIndex] = true;
				continue;
			}

			AddFanCapPolygons(polygons, loops[i], origin, axisX, axisY, capNormal);
			used[i] = true;
		}

		LogHoleCapDiagnostics(diagnosticsLabel, segments, loops3D, polygons.Count);

		return polygons;
	}

	// 当交线图不是闭环而是两条开链时，按端点排序恢复它们，用于 side-plane 洞壁桥接。 / When the intersection graph yields two open chains instead of loops, recover them in endpoint order for side-plane hole-wall bridging.
	private static List<List<ClippedVertex>> BuildOpenChains(List<ClipSegment> segments)
	{
		List<ClippedVertex> nodes = new List<ClippedVertex>(segments.Count * 2);
		List<int> nodeWeights = new List<int>(segments.Count * 2);
		List<LoopEdge> edges = new List<LoopEdge>(segments.Count);
		List<List<int>> adjacency = new List<List<int>>(segments.Count * 2);
		for (int i = 0; i < segments.Count; i++)
		{
			int a = GetOrAddLoopNode(segments[i].A, nodes, nodeWeights, adjacency);
			int b = GetOrAddLoopNode(segments[i].B, nodes, nodeWeights, adjacency);
			TryAddLoopEdge(a, b, edges, adjacency);
		}

		List<List<ClippedVertex>> chains = new List<List<ClippedVertex>>();
		bool[] visitedNodes = new bool[nodes.Count];
		Queue<int> queue = new Queue<int>();
		for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			if (visitedNodes[nodeIndex] || adjacency[nodeIndex].Count == 0)
			{
				continue;
			}

			List<int> component = new List<int>();
			queue.Enqueue(nodeIndex);
			visitedNodes[nodeIndex] = true;
			while (queue.Count > 0)
			{
				int current = queue.Dequeue();
				component.Add(current);
				for (int i = 0; i < adjacency[current].Count; i++)
				{
					int next = GetOtherNode(edges[adjacency[current][i]], current);
					if (visitedNodes[next])
					{
						continue;
					}

					visitedNodes[next] = true;
					queue.Enqueue(next);
				}
			}

			List<int> endpoints = new List<int>(2);
			bool validChain = true;
			HashSet<int> componentSet = new HashSet<int>(component);
			for (int i = 0; i < component.Count; i++)
			{
				int degree = 0;
				for (int j = 0; j < adjacency[component[i]].Count; j++)
				{
					if (componentSet.Contains(GetOtherNode(edges[adjacency[component[i]][j]], component[i])))
					{
						degree++;
					}
				}

				if (degree == 1)
				{
					endpoints.Add(component[i]);
				}
				else if (degree != 2)
				{
					validChain = false;
					break;
				}
			}

			if (!validChain || endpoints.Count != 2)
			{
				continue;
			}

			List<ClippedVertex> chain = new List<ClippedVertex>(component.Count);
			int previous = -1;
			int currentNode = endpoints[0];
			for (int step = 0; step < component.Count; step++)
			{
				chain.Add(nodes[currentNode]);
				if (currentNode == endpoints[1])
				{
					break;
				}

				int nextNode = -1;
				for (int i = 0; i < adjacency[currentNode].Count; i++)
				{
					int candidate = GetOtherNode(edges[adjacency[currentNode][i]], currentNode);
					if (!componentSet.Contains(candidate) || candidate == previous)
					{
						continue;
					}

					nextNode = candidate;
					break;
				}

				if (nextNode < 0)
				{
					chain.Clear();
					break;
				}

				previous = currentNode;
				currentNode = nextNode;
			}

			if (chain.Count >= 2)
			{
				chains.Add(chain);
			}
		}

		return chains;
	}

	// 对多条开链按最近端点成对桥接，避免只支持单一 two-chain 情况。 / Pair multiple open chains by nearest endpoints so side-plane recovery is not limited to a single two-chain case.
	private static void AddOpenChainCapPolygons(List<List<ClippedVertex>> polygons, List<List<ClippedVertex>> openChains, Vector3 capNormal)
	{
		if (openChains == null || openChains.Count < 2)
		{
			return;
		}

		bool[] used = new bool[openChains.Count];
		while (true)
		{
			int bestA = -1;
			int bestB = -1;
			float bestCost = float.PositiveInfinity;
			for (int i = 0; i < openChains.Count; i++)
			{
				if (used[i] || openChains[i].Count < 2)
				{
					continue;
				}

				for (int j = i + 1; j < openChains.Count; j++)
				{
					if (used[j] || openChains[j].Count < 2)
					{
						continue;
					}

					float sameDirection = Vector3.Distance(openChains[i][0].Position, openChains[j][0].Position) + Vector3.Distance(openChains[i][^1].Position, openChains[j][^1].Position);
					float reversedDirection = Vector3.Distance(openChains[i][0].Position, openChains[j][^1].Position) + Vector3.Distance(openChains[i][^1].Position, openChains[j][0].Position);
					float pairingCost = Mathf.Min(sameDirection, reversedDirection);
					if (pairingCost >= bestCost)
					{
						continue;
					}

					bestCost = pairingCost;
					bestA = i;
					bestB = j;
				}
			}

			if (bestA < 0 || bestB < 0)
			{
				break;
			}

			used[bestA] = true;
			used[bestB] = true;
			AddOpenChainCapPolygons(polygons, openChains[bestA], openChains[bestB], capNormal);
		}
	}

	// 把两条 open chains 桥接成洞壁三角带，适用于 window/bay side-plane 截面。 / Bridge two open chains into a triangle strip for window/bay side-plane hole walls.
	private static void AddOpenChainCapPolygons(List<List<ClippedVertex>> polygons, List<ClippedVertex> chainA, List<ClippedVertex> chainB, Vector3 capNormal)
	{
		if (chainA.Count < 2 || chainB.Count < 2)
		{
			return;
		}

		if (EstimateChainLength(chainB) > EstimateChainLength(chainA))
		{
			(chainA, chainB) = (chainB, chainA);
		}

		float sameDirection = Vector3.Distance(chainA[0].Position, chainB[0].Position) + Vector3.Distance(chainA[^1].Position, chainB[^1].Position);
		float reversedDirection = Vector3.Distance(chainA[0].Position, chainB[^1].Position) + Vector3.Distance(chainA[^1].Position, chainB[0].Position);
		if (reversedDirection < sameDirection)
		{
			chainB = new List<ClippedVertex>(chainB);
			chainB.Reverse();
		}

		List<float> fractionsA = ComputeOpenFractions(chainA);
		List<float> fractionsB = ComputeOpenFractions(chainB);
		int[] linksA = FindClosestOpenLinks(fractionsA, fractionsB);
		int[] linksB = FindClosestOpenLinks(fractionsB, fractionsA);

		int indexA = 0;
		int indexB = 0;
		int stepLimit = chainA.Count + chainB.Count + 4;
		int steps = 0;
		while ((indexA < chainA.Count - 1 || indexB < chainB.Count - 1) && steps++ < stepLimit)
		{
			int nextA = Mathf.Min(indexA + 1, chainA.Count - 1);
			int nextB = Mathf.Min(indexB + 1, chainB.Count - 1);
			bool canAdvanceA = indexA < chainA.Count - 1 && (indexB == chainB.Count - 1 || linksB[indexB] == nextA || linksA[nextA] == indexB);
			bool canAdvanceB = indexB < chainB.Count - 1 && (indexA == chainA.Count - 1 || linksA[indexA] == nextB || linksB[nextB] == indexA);

			if (canAdvanceA && !canAdvanceB)
			{
				AddTrianglePolygon(polygons, chainA[indexA].Position, chainB[indexB].Position, chainA[nextA].Position, capNormal);
				indexA = nextA;
				continue;
			}

			if (canAdvanceB && !canAdvanceA)
			{
				AddTrianglePolygon(polygons, chainA[indexA].Position, chainB[nextB].Position, chainB[indexB].Position, capNormal);
				indexB = nextB;
				continue;
			}

			if (indexA == chainA.Count - 1)
			{
				AddTrianglePolygon(polygons, chainA[indexA].Position, chainB[nextB].Position, chainB[indexB].Position, capNormal);
				indexB = nextB;
				continue;
			}

			if (indexB == chainB.Count - 1)
			{
				AddTrianglePolygon(polygons, chainA[indexA].Position, chainB[indexB].Position, chainA[nextA].Position, capNormal);
				indexA = nextA;
				continue;
			}

			AddTrianglePolygon(polygons, chainA[indexA].Position, chainB[indexB].Position, chainA[nextA].Position, capNormal);
			AddTrianglePolygon(polygons, chainA[nextA].Position, chainB[indexB].Position, chainB[nextB].Position, capNormal);
			indexA = nextA;
			indexB = nextB;
		}
	}

	// 计算 open chain 上的归一化累计长度，供两条链做最近参数配对。 / Compute normalized cumulative lengths on an open chain so two chains can be paired by nearest parameters.
	private static List<float> ComputeOpenFractions(List<ClippedVertex> chain)
	{
		List<float> fractions = new List<float>(chain.Count);
		if (chain.Count == 0)
		{
			return fractions;
		}

		float total = 0f;
		fractions.Add(0f);
		for (int i = 1; i < chain.Count; i++)
		{
			total += Vector3.Distance(chain[i - 1].Position, chain[i].Position);
			fractions.Add(total);
		}

		if (total <= Epsilon)
		{
			for (int i = 0; i < fractions.Count; i++)
			{
				fractions[i] = chain.Count <= 1 ? 0f : i / (float)(chain.Count - 1);
			}
			return fractions;
		}

		for (int i = 0; i < fractions.Count; i++)
		{
			fractions[i] /= total;
		}
		return fractions;
	}

	// 在 open chain 上做最近参数匹配，不需要闭环距离。 / Match nearest parameters along open chains without circular wrap-around.
	private static int[] FindClosestOpenLinks(List<float> source, List<float> target)
	{
		int[] links = new int[source.Count];
		for (int i = 0; i < source.Count; i++)
		{
			float bestDistance = float.PositiveInfinity;
			int bestIndex = 0;
			for (int j = 0; j < target.Count; j++)
			{
				float distance = Mathf.Abs(source[i] - target[j]);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestIndex = j;
				}
			}
			links[i] = bestIndex;
		}
		return links;
	}

	// 估计一条 open chain 的总长度，便于优先把更长的一侧当作 outer chain。 / Estimate the total length of an open chain so the longer side can be treated as the outer chain.
	private static float EstimateChainLength(List<ClippedVertex> chain)
	{
		float length = 0f;
		for (int i = 1; i < chain.Count; i++)
		{
			length += Vector3.Distance(chain[i - 1].Position, chain[i].Position);
		}
		return length;
	}

	// 把 subtraction cap 的中间统计落到 Temp 文件，便于在 Unity 内复现后离线读取。 / Persist subtraction-cap diagnostics to a Temp file so Unity-side reproductions can be inspected from the workspace.
	private static void LogHoleCapDiagnostics(string diagnosticsLabel, List<ClipSegment> segments, List<List<ClippedVertex>> loops3D, int polygonCount)
	{
		if (string.IsNullOrEmpty(diagnosticsLabel))
		{
			return;
		}

		try
		{
			string path = GetHoleCapDiagnosticsPath();
			if (!_holeCapDiagnosticsReset)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
				File.WriteAllText(path, string.Empty);
				_holeCapDiagnosticsReset = true;
			}

			List<string> loopSummaries = new List<string>(loops3D.Count);
			for (int i = 0; i < loops3D.Count; i++)
			{
				List<ClippedVertex> loop = loops3D[i];
				loopSummaries.Add($"{i}:count={loop.Count}");
			}

			BuildLoopGraphStats(segments, out int nodeCount, out int edgeCount, out int degreeOneCount, out int degreeOtherCount);
			AppendHoleCapDiagnosticsLine(path, $"{DateTime.Now:HH:mm:ss.fff} {diagnosticsLabel} segments={segments.Count} nodes={nodeCount} edges={edgeCount} deg1={degreeOneCount} degOther={degreeOtherCount} loops={loops3D.Count} caps={polygonCount} {string.Join(" | ", loopSummaries)}");
		}
		catch (Exception exception)
		{
			Debug.LogWarning($"Hole cap diagnostics failed: {exception.Message}");
		}
	}

	// 把更细粒度的 fallback 选择信息写到同一份诊断文件，便于对照具体 plane。 / Append finer-grained fallback selection details to the same diagnostics file so individual planes can be compared.
	private static void LogHoleCapDetail(string detail)
	{
		if (string.IsNullOrEmpty(detail))
		{
			return;
		}

		try
		{
			string path = GetHoleCapDiagnosticsPath();
			AppendHoleCapDiagnosticsLine(path, $"{DateTime.Now:HH:mm:ss.fff} {detail}");
		}
		catch (Exception exception)
		{
			Debug.LogWarning($"Hole cap detail diagnostics failed: {exception.Message}");
		}
	}

	// 统一管理诊断文件初始化与逐行追加。 / Centralize diagnostics file initialization and line appends.
	private static void AppendHoleCapDiagnosticsLine(string path, string line)
	{
		if (!_holeCapDiagnosticsReset)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
			File.WriteAllText(path, string.Empty);
			_holeCapDiagnosticsReset = true;
		}

		File.AppendAllText(path, line + Environment.NewLine);
	}

	// 诊断文件固定落在工程 Temp 目录，方便工作区直接读取。 / Write the diagnostics file into the project Temp folder so it can be read directly from the workspace.
	private static string GetHoleCapDiagnosticsPath()
	{
		return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp", "hole-cap-diagnostics.log"));
	}

	// 复用当前的节点吸附与边去重规则统计交线图形态，判断问题出在端点合并还是追环。 / Rebuild the same snapped segment graph to see whether failures come from endpoint merging or from loop tracing.
	private static void BuildLoopGraphStats(List<ClipSegment> segments, out int nodeCount, out int edgeCount, out int degreeOneCount, out int degreeOtherCount)
	{
		nodeCount = 0;
		edgeCount = 0;
		degreeOneCount = 0;
		degreeOtherCount = 0;
		if (segments == null)
		{
			return;
		}

		List<ClippedVertex> nodes = new List<ClippedVertex>(segments.Count * 2);
		List<int> nodeWeights = new List<int>(segments.Count * 2);
		List<LoopEdge> edges = new List<LoopEdge>(segments.Count);
		List<List<int>> adjacency = new List<List<int>>(segments.Count * 2);
		for (int i = 0; i < segments.Count; i++)
		{
			int a = GetOrAddLoopNode(segments[i].A, nodes, nodeWeights, adjacency);
			int b = GetOrAddLoopNode(segments[i].B, nodes, nodeWeights, adjacency);
			TryAddLoopEdge(a, b, edges, adjacency);
		}

		nodeCount = nodes.Count;
		edgeCount = edges.Count;
		for (int i = 0; i < adjacency.Count; i++)
		{
			if (adjacency[i].Count == 1)
			{
				degreeOneCount++;
			}
			else if (adjacency[i].Count != 2)
			{
				degreeOtherCount++;
			}
		}
	}

	// 用质心扇形方式封住单个凸环截面。 / Cap one convex loop by fan-triangulating around its centroid.
	private static void AddFanCapPolygons(List<List<ClippedVertex>> polygons, ProjectedLoop loop, Vector3 origin, Vector3 axisX, Vector3 axisY, Vector3 capNormal)
	{
		if (loop.Count < 3)
		{
			return;
		}

		Vector2 centroid2D = ComputePolygonCentroid(loop.Points2D);
		Vector3 centroid3D = origin + axisX * centroid2D.x + axisY * centroid2D.y;
		for (int i = 0; i < loop.Count; i++)
		{
			int next = (i + 1) % loop.Count;
			AddTrianglePolygon(polygons, centroid3D, loop.Points3D[i].Position, loop.Points3D[next].Position, capNormal);
		}
	}

	// 用公共中心的径向条带把外环和内环完整缝合，避免不同采样密度时漏掉大块 annulus。 / Stitch an annulus completely with radial strips around a shared center so mismatched loop sampling does not drop large patches.
	private static void AddRimCapPolygons(List<List<ClippedVertex>> polygons, ProjectedLoop outer, ProjectedLoop inner, Vector3 origin, Vector3 axisX, Vector3 axisY, Vector3 capNormal)
	{
		if (outer.Count < 2 || inner.Count < 2)
		{
			return;
		}

		int[] linksOuter = FindClosestLinks(outer.Fractions, inner.Fractions);
		int[] linksInner = FindClosestLinks(inner.Fractions, outer.Fractions);
		int outerIndex = 0;
		int innerIndex = linksOuter[0];
		int walkedOuter = 0;
		int walkedInner = 0;
		int stepLimit = outer.Count + inner.Count + 4;
		int steps = 0;
		while ((walkedOuter < outer.Count || walkedInner < inner.Count) && steps++ < stepLimit)
		{
			int nextOuter = (outerIndex + 1) % outer.Count;
			int nextInner = (innerIndex + 1) % inner.Count;
			bool advanceOuter = walkedOuter < outer.Count && (walkedInner == inner.Count || linksInner[innerIndex] == nextOuter || linksOuter[nextOuter] == innerIndex);
			bool advanceInner = walkedInner < inner.Count && (walkedOuter == outer.Count || linksOuter[outerIndex] == nextInner || linksInner[nextInner] == outerIndex);

			if (advanceOuter && !advanceInner)
			{
				AddTrianglePolygon(polygons, outer.Points3D[outerIndex].Position, inner.Points3D[innerIndex].Position, outer.Points3D[nextOuter].Position, capNormal);
				outerIndex = nextOuter;
				walkedOuter++;
				continue;
			}

			if (advanceInner && !advanceOuter)
			{
				AddTrianglePolygon(polygons, outer.Points3D[outerIndex].Position, inner.Points3D[nextInner].Position, inner.Points3D[innerIndex].Position, capNormal);
				innerIndex = nextInner;
				walkedInner++;
				continue;
			}

			if (walkedOuter == outer.Count)
			{
				AddTrianglePolygon(polygons, outer.Points3D[outerIndex].Position, inner.Points3D[nextInner].Position, inner.Points3D[innerIndex].Position, capNormal);
				innerIndex = nextInner;
				walkedInner++;
				continue;
			}

			if (walkedInner == inner.Count)
			{
				AddTrianglePolygon(polygons, outer.Points3D[outerIndex].Position, inner.Points3D[innerIndex].Position, outer.Points3D[nextOuter].Position, capNormal);
				outerIndex = nextOuter;
				walkedOuter++;
				continue;
			}

			AddTrianglePolygon(polygons, outer.Points3D[outerIndex].Position, inner.Points3D[innerIndex].Position, outer.Points3D[nextOuter].Position, capNormal);
			AddTrianglePolygon(polygons, outer.Points3D[nextOuter].Position, inner.Points3D[innerIndex].Position, inner.Points3D[nextInner].Position, capNormal);
			outerIndex = nextOuter;
			innerIndex = nextInner;
			walkedOuter++;
			walkedInner++;
		}
	}

	// 用统一的平面法线生成一个朝向正确的扁平三角面。 / Emit one flat triangle with winding corrected against the requested plane normal.
	private static void AddTrianglePolygon(List<List<ClippedVertex>> polygons, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
	{
		Vector3 cross = Vector3.Cross(b - a, c - a);
		if (cross.sqrMagnitude <= Epsilon * Epsilon)
		{
			return;
		}

		if (Vector3.Dot(cross, normal) < 0f)
		{
			(c, b) = (b, c);
		}

		polygons.Add(new List<ClippedVertex>(3)
		{
			new ClippedVertex(a, normal),
			new ClippedVertex(b, normal),
			new ClippedVertex(c, normal)
		});
	}

	// 把裁切交线段拼成闭合环。 / Stitch cut segments into closed loops.
	private static List<List<ClippedVertex>> BuildLoops(List<ClipSegment> segments, Vector3 capNormal)
	{
		List<ClippedVertex> nodes = new List<ClippedVertex>(segments.Count * 2);
		List<int> nodeWeights = new List<int>(segments.Count * 2);
		List<LoopEdge> edges = new List<LoopEdge>(segments.Count);
		List<List<int>> adjacency = new List<List<int>>(segments.Count * 2);
		for (int i = 0; i < segments.Count; i++)
		{
			int a = GetOrAddLoopNode(segments[i].A, nodes, nodeWeights, adjacency);
			int b = GetOrAddLoopNode(segments[i].B, nodes, nodeWeights, adjacency);
			TryAddLoopEdge(a, b, edges, adjacency);
		}

		if (edges.Count == 0)
		{
			return new List<List<ClippedVertex>>();
		}

		Vector3 origin = nodes[0].Position;
		BuildPlaneBasis(capNormal, out Vector3 axisX, out Vector3 axisY);
		List<Vector2> projectedNodes = new List<Vector2>(nodes.Count);
		for (int i = 0; i < nodes.Count; i++)
		{
			Vector3 offset = nodes[i].Position - origin;
			projectedNodes.Add(new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY)));
		}

		for (int nodeIndex = 0; nodeIndex < adjacency.Count; nodeIndex++)
		{
			adjacency[nodeIndex].Sort((left, right) => GetOutgoingAngle(nodeIndex, left, edges, projectedNodes).CompareTo(GetOutgoingAngle(nodeIndex, right, edges, projectedNodes)));
		}

		List<List<ClippedVertex>> loops = new List<List<ClippedVertex>>();
		bool[,] visited = new bool[edges.Count, 2];
		for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
		{
			LoopEdge edge = edges[edgeIndex];
			TryAddLoopFace(loops, edgeIndex, edge.A, edges, adjacency, nodes, projectedNodes, visited);
			TryAddLoopFace(loops, edgeIndex, edge.B, edges, adjacency, nodes, projectedNodes, visited);
		}
		return loops;
	}

	// 沿平面图的有向半边追踪一个面，只保留与 capNormal 一致的真实闭环。 / Trace one face along directed half-edges of the planar graph and keep only real loops oriented with the cap normal.
	private static void TryAddLoopFace(List<List<ClippedVertex>> loops, int startEdgeIndex, int startFromNode, List<LoopEdge> edges, List<List<int>> adjacency, List<ClippedVertex> nodes, List<Vector2> projectedNodes, bool[,] visited)
	{
		int startDirection = GetDirectedEdgeSlot(edges[startEdgeIndex], startFromNode);
		if (visited[startEdgeIndex, startDirection])
		{
			return;
		}

		if (!TryTraceLoop(startEdgeIndex, startFromNode, edges, adjacency, out List<int> loopNodeIds, out List<DirectedLoopEdge> traversed))
		{
			return;
		}

		for (int i = 0; i < traversed.Count; i++)
		{
			DirectedLoopEdge directed = traversed[i];
			visited[directed.EdgeIndex, GetDirectedEdgeSlot(edges[directed.EdgeIndex], directed.FromNode)] = true;
		}

		if (loopNodeIds.Count < 3)
		{
			return;
		}

		float signedArea = 0f;
		for (int i = 0; i < loopNodeIds.Count; i++)
	{
			Vector2 current = projectedNodes[loopNodeIds[i]];
			Vector2 next = projectedNodes[loopNodeIds[(i + 1) % loopNodeIds.Count]];
			signedArea += current.x * next.y - next.x * current.y;
		}
		signedArea *= 0.5f;
		// 这条半边追面路径当前恢复出来的真实 bounded face 与投影基呈相反绕序，保留负面积环，外部面会落在另一符号侧。 / This face-tracing path currently recovers real bounded faces with the opposite winding in the projection basis, so keep negative-area loops and reject the exterior side.
		if (signedArea >= -Epsilon)
		{
			return;
		}

		List<ClippedVertex> loop = new List<ClippedVertex>(loopNodeIds.Count);
		for (int i = 0; i < loopNodeIds.Count; i++)
		{
			loop.Add(nodes[loopNodeIds[i]]);
		}

		RemoveNearDuplicateLoopPoints(loop);
		if (loop.Count >= 3)
		{
			loops.Add(loop);
		}
	}

	// 按“在下一节点取入边顺时针前一条边”的规则沿面追踪，从线段图恢复真实闭环。 / Recover one real polygon loop from the segment graph by following the face immediately to the left of each directed edge.
	private static bool TryTraceLoop(int startEdgeIndex, int startFromNode, List<LoopEdge> edges, List<List<int>> adjacency, out List<int> loopNodeIds, out List<DirectedLoopEdge> traversed)
	{
		loopNodeIds = new List<int>();
		traversed = new List<DirectedLoopEdge>();
		int currentEdgeIndex = startEdgeIndex;
		int currentFromNode = startFromNode;
		int stepLimit = Mathf.Max(4, edges.Count * 2 + 4);
		for (int step = 0; step < stepLimit; step++)
		{
			traversed.Add(new DirectedLoopEdge(currentEdgeIndex, currentFromNode));
			loopNodeIds.Add(currentFromNode);
			int currentToNode = GetOtherNode(edges[currentEdgeIndex], currentFromNode);
			int nextEdgeIndex = GetPreviousEdgeAroundNode(currentToNode, currentEdgeIndex, adjacency);
			if (nextEdgeIndex < 0)
			{
				loopNodeIds.Clear();
				traversed.Clear();
				return false;
			}

			if (nextEdgeIndex == startEdgeIndex && currentToNode == startFromNode)
			{
				return true;
			}

			currentEdgeIndex = nextEdgeIndex;
			currentFromNode = currentToNode;
		}

		loopNodeIds.Clear();
		traversed.Clear();
		return false;
	}

	// 计算某个节点沿指定边离开时的极角，用于在平面图中建立稳定的环绕顺序。 / Compute the outgoing angle of an edge at one node so planar graph adjacency can be sorted consistently.
	private static float GetOutgoingAngle(int nodeIndex, int edgeIndex, List<LoopEdge> edges, List<Vector2> projectedNodes)
	{
		int otherNode = GetOtherNode(edges[edgeIndex], nodeIndex);
		Vector2 direction = projectedNodes[otherNode] - projectedNodes[nodeIndex];
		return Mathf.Atan2(direction.y, direction.x);
	}

	// 在下一节点找到“入边顺时针前一条”边，对应当前半边左侧的面。 / At the next node, pick the edge immediately clockwise from the incoming edge to continue tracing the face on the left.
	private static int GetPreviousEdgeAroundNode(int nodeIndex, int incomingEdgeIndex, List<List<int>> adjacency)
	{
		List<int> nodeEdges = adjacency[nodeIndex];
		if (nodeEdges.Count < 2)
		{
			return -1;
		}

		for (int i = 0; i < nodeEdges.Count; i++)
		{
			if (nodeEdges[i] != incomingEdgeIndex)
			{
				continue;
			}

			return nodeEdges[(i - 1 + nodeEdges.Count) % nodeEdges.Count];
		}

		return -1;
	}

	// 把有向半边映射到 0/1 槽位，便于记录某条边的两个追踪方向是否访问过。 / Map a directed half-edge to slot 0/1 so each undirected edge can track visits in both directions.
	private static int GetDirectedEdgeSlot(LoopEdge edge, int fromNode)
	{
		return edge.A == fromNode ? 0 : 1;
	}

	// 把足够接近的交点吸附到同一个图节点，减少多次裁切后的端点漂移。 / Snap nearby cut points onto shared graph nodes to reduce endpoint drift after repeated clipping.
	private static int GetOrAddLoopNode(ClippedVertex vertex, List<ClippedVertex> nodes, List<int> nodeWeights, List<List<int>> adjacency)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			if (Vector3.Distance(nodes[i].Position, vertex.Position) > LoopConnectionTolerance)
			{
				continue;
			}

			int weight = nodeWeights[i] + 1;
			Vector3 normal = (nodes[i].Normal * nodeWeights[i] + vertex.Normal) / weight;
			nodes[i] = new ClippedVertex(nodes[i].Position, normal);
			nodeWeights[i] = weight;
			return i;
		}

		int index = nodes.Count;
		nodes.Add(vertex);
		nodeWeights.Add(1);
		adjacency.Add(new List<int>(4));
		return index;
	}

	// 向交线图里追加一条无向边，并剔除重复边。 / Add one undirected edge to the cut graph while removing duplicate edges.
	private static void TryAddLoopEdge(int a, int b, List<LoopEdge> edges, List<List<int>> adjacency)
	{
		if (a == b)
		{
			return;
		}

		for (int i = 0; i < edges.Count; i++)
		{
			LoopEdge existing = edges[i];
			if ((existing.A == a && existing.B == b) || (existing.A == b && existing.B == a))
			{
				return;
			}
		}

		int edgeIndex = edges.Count;
		edges.Add(new LoopEdge(a, b));
		adjacency[a].Add(edgeIndex);
		adjacency[b].Add(edgeIndex);
	}

	// 返回一条无向边相对于当前节点的另一端节点索引。 / Return the opposite endpoint of an undirected loop edge relative to the current node.
	private static int GetOtherNode(LoopEdge edge, int currentNode)
	{
		return edge.A == currentNode ? edge.B : edge.A;
	}

	// 从 Unity Mesh 三角面展开成裁切器可处理的多边形列表。 / Expand a Unity mesh into per-triangle polygons for clipping.
	private static List<List<ClippedVertex>> BuildSourcePolygons(Mesh source)
	{
		Vector3[] sourceVertices = source.vertices;
		Vector3[] sourceNormals = source.normals;
		int[] sourceTriangles = source.triangles;
		List<List<ClippedVertex>> polygons = new List<List<ClippedVertex>>(sourceTriangles.Length / 3);
		for (int i = 0; i < sourceTriangles.Length; i += 3)
		{
			int index0 = sourceTriangles[i];
			int index1 = sourceTriangles[i + 1];
			int index2 = sourceTriangles[i + 2];
			Vector3 fallbackNormal = Vector3.Cross(sourceVertices[index1] - sourceVertices[index0], sourceVertices[index2] - sourceVertices[index0]).normalized;
			polygons.Add(new List<ClippedVertex>(3)
			{
				new ClippedVertex(sourceVertices[index0], GetSourceNormal(sourceNormals, sourceVertices, index0, fallbackNormal)),
				new ClippedVertex(sourceVertices[index1], GetSourceNormal(sourceNormals, sourceVertices, index1, fallbackNormal)),
				new ClippedVertex(sourceVertices[index2], GetSourceNormal(sourceNormals, sourceVertices, index2, fallbackNormal))
			});
		}
		return polygons;
	}

	private static List<List<ClippedVertex>> BuildSourcePolygons(PreviewMeshData source)
	{
		List<Vector3> sourceVertices = source.Vertices;
		List<Vector3> sourceNormals = source.Normals;
		int triangleCount = 0;
		for (int subMesh = 0; subMesh < source.SubMeshTriangles.Count; subMesh++)
		{
			triangleCount += source.SubMeshTriangles[subMesh].Count / 3;
		}

		List<List<ClippedVertex>> polygons = new List<List<ClippedVertex>>(triangleCount);
		for (int subMesh = 0; subMesh < source.SubMeshTriangles.Count; subMesh++)
		{
			List<int> sourceTriangles = source.SubMeshTriangles[subMesh];
			for (int i = 0; i + 2 < sourceTriangles.Count; i += 3)
			{
				int index0 = sourceTriangles[i];
				int index1 = sourceTriangles[i + 1];
				int index2 = sourceTriangles[i + 2];
				Vector3 fallbackNormal = Vector3.Cross(sourceVertices[index1] - sourceVertices[index0], sourceVertices[index2] - sourceVertices[index0]).normalized;
				polygons.Add(new List<ClippedVertex>(3)
				{
					new ClippedVertex(sourceVertices[index0], GetSourceNormal(sourceNormals, sourceVertices, index0, fallbackNormal)),
					new ClippedVertex(sourceVertices[index1], GetSourceNormal(sourceNormals, sourceVertices, index1, fallbackNormal)),
					new ClippedVertex(sourceVertices[index2], GetSourceNormal(sourceNormals, sourceVertices, index2, fallbackNormal))
				});
			}
		}
		return polygons;
	}

	// 把裁切后的多边形重新写回 Unity Mesh。 / Write the clipped polygons back into a Unity Mesh.
	private static Mesh BuildMesh(List<List<ClippedVertex>> polygons, string meshName)
	{
		Mesh result = new Mesh
		{
			name = meshName,
			indexFormat = IndexFormat.UInt32
		};
		List<Vector3> vertices = new List<Vector3>();
		List<Vector3> normals = new List<Vector3>();
		List<int> triangles = new List<int>();
		for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
		{
			List<ClippedVertex> polygon = polygons[polygonIndex];
			if (polygon.Count < 3)
			{
				continue;
			}

			int startIndex = vertices.Count;
			for (int i = 0; i < polygon.Count; i++)
			{
				vertices.Add(polygon[i].Position);
				normals.Add(polygon[i].Normal);
			}
			for (int i = 1; i < polygon.Count - 1; i++)
			{
				triangles.Add(startIndex);
				triangles.Add(startIndex + i);
				triangles.Add(startIndex + i + 1);
			}
		}

		result.SetVertices(vertices);
		result.SetNormals(normals);
		result.SetTriangles(triangles, 0);
		result.RecalculateBounds();
		return result;
	}

	private static PreviewMeshData BuildMeshData(List<List<ClippedVertex>> polygons, string meshName)
	{
		List<Vector3> vertices = new List<Vector3>();
		List<Vector3> normals = new List<Vector3>();
		List<int> triangles = new List<int>();
		for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
		{
			List<ClippedVertex> polygon = polygons[polygonIndex];
			if (polygon.Count < 3)
			{
				continue;
			}

			int startIndex = vertices.Count;
			for (int i = 0; i < polygon.Count; i++)
			{
				vertices.Add(polygon[i].Position);
				normals.Add(polygon[i].Normal);
			}
			for (int i = 1; i < polygon.Count - 1; i++)
			{
				triangles.Add(startIndex);
				triangles.Add(startIndex + i);
				triangles.Add(startIndex + i + 1);
			}
		}

		return new PreviewMeshData(meshName, vertices, normals, triangles);
	}

	// 按同一平面把一个多边形拆成 outside/inside 两部分，并记录它与该平面的交线段。 / Split one polygon into outside/inside parts against a plane and capture the cut segment on that plane.
	private static void SplitPolygonByPlane(List<ClippedVertex> polygon, Plane plane, out List<ClippedVertex> outside, out List<ClippedVertex> inside, out ClipSegment segment, out bool hasSegment)
	{
		outside = new List<ClippedVertex>(polygon.Count + 1);
		inside = new List<ClippedVertex>(polygon.Count + 1);
		List<ClippedVertex> cutPoints = new List<ClippedVertex>(polygon.Count + 1);
		bool hasNonCoplanarPoint = false;
		for (int i = 0; i < polygon.Count; i++)
		{
			ClippedVertex previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
			ClippedVertex current = polygon[i];
			float previousDistance = plane.GetDistanceToPoint(previous.Position);
			float currentDistance = plane.GetDistanceToPoint(current.Position);
			bool previousOnPlane = Mathf.Abs(previousDistance) <= Epsilon;
			bool currentOnPlane = Mathf.Abs(currentDistance) <= Epsilon;
			bool previousOutside = previousDistance > Epsilon;
			bool currentOutside = currentDistance > Epsilon;
			if (!currentOnPlane)
			{
				hasNonCoplanarPoint = true;
			}

			if (previousOnPlane)
			{
				AddUnique(cutPoints, previous);
			}

			if (previousOutside != currentOutside)
			{
				float t = previousDistance / (previousDistance - currentDistance);
				ClippedVertex intersection = new ClippedVertex(
					Vector3.Lerp(previous.Position, current.Position, Mathf.Clamp01(t)),
					Vector3.Lerp(previous.Normal, current.Normal, Mathf.Clamp01(t)).normalized);
				AddUnique(outside, intersection);
				AddUnique(inside, intersection);
				AddUnique(cutPoints, intersection);
			}

			if (currentOnPlane)
			{
				AddUnique(cutPoints, current);
			}

			if (currentOutside)
			{
				AddUnique(outside, current);
			}
			else
			{
				AddUnique(inside, current);
			}
		}

		RemoveNearDuplicateLoopPoints(outside);
		RemoveNearDuplicateLoopPoints(inside);
		segment = default;
		hasSegment = hasNonCoplanarPoint && TryBuildCutSegment(cutPoints, out segment);
		if (!hasSegment)
		{
			segment = default;
		}
	}

	// 从落在裁切平面上的交点/共面点里恢复该多边形贡献的真实截线段。 / Recover the actual cut segment contributed by one polygon from its on-plane intersections and coplanar boundary points.
	private static bool TryBuildCutSegment(List<ClippedVertex> cutPoints, out ClipSegment segment)
	{
		segment = default;
		if (cutPoints == null || cutPoints.Count < 2)
		{
			return false;
		}

		int bestA = -1;
		int bestB = -1;
		float bestDistanceSquared = Epsilon * Epsilon;
		for (int i = 0; i < cutPoints.Count - 1; i++)
		{
			for (int j = i + 1; j < cutPoints.Count; j++)
			{
				float distanceSquared = (cutPoints[j].Position - cutPoints[i].Position).sqrMagnitude;
				if (distanceSquared <= bestDistanceSquared)
				{
					continue;
				}

				bestDistanceSquared = distanceSquared;
				bestA = i;
				bestB = j;
			}
		}

		if (bestA < 0 || bestB < 0)
		{
			return false;
		}

		segment = new ClipSegment(cutPoints[bestA], cutPoints[bestB]);
		return true;
	}

	// 从源法线里取点法线；若源网格没提供则退回三角面法线。 / Read a point normal from the source mesh, falling back to the triangle normal when absent.
	private static Vector3 GetSourceNormal(Vector3[] sourceNormals, Vector3[] sourceVertices, int index, Vector3 fallbackNormal)
	{
		if (sourceNormals != null && sourceNormals.Length == sourceVertices.Length)
		{
			return sourceNormals[index];
		}
		return fallbackNormal;
	}

	private static Vector3 GetSourceNormal(List<Vector3> sourceNormals, List<Vector3> sourceVertices, int index, Vector3 fallbackNormal)
	{
		if (sourceNormals != null && sourceNormals.Count == sourceVertices.Count)
		{
			return sourceNormals[index];
		}
		return fallbackNormal;
	}

	// 用与原版 SimpleInset 一致的最大 inset 估计，把 0..1 圆角量换成真实半径。 / Convert the original 0..1 corner-radius factor into a real radius using the same max-inset estimate as SimpleInset.
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

	// 对凸多边形按真实半径采样圆角弧。 / Sample rounded corners on a convex polygon using a real radius.
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

	// 估计当前凸轮廓允许的最大 inset 半径。 / Estimate the maximum inset radius currently allowed by a convex outline.
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

	// 与原版 SimpleInset.ComputePointShrinkage 保持一致。 / Match the original SimpleInset.ComputePointShrinkage implementation.
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

	// 把 2D 向量顺时针旋转 90 度。 / Rotate a 2D vector by 90 degrees clockwise.
	private static Vector2 Rotate(Vector2 value)
	{
		return new Vector2(value.y, -value.x);
	}

	// 为当前裁切平面建立一个稳定的 2D 投影基。 / Build a stable 2D projection basis for the active clipping plane.
	private static void BuildPlaneBasis(Vector3 normal, out Vector3 axisX, out Vector3 axisY)
	{
		Vector3 helper = Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right;
		axisX = Vector3.Cross(helper, normal).normalized;
		axisY = Vector3.Cross(normal, axisX).normalized;
	}

	// 构造一个“内部距离为非正”的平面，便于统一 inside/outside 判断。 / Create a plane whose interior evaluates to a non-positive distance for consistent inside tests.
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

	// 收集内外环围绕公共中心的所有关键角度，供径向条带三角化使用。 / Collect all key angles around the shared center so radial-strip triangulation can cover the full annulus.
	private static List<float> CollectSortedAngles(Vector2 center, IReadOnlyList<Vector2> outer, IReadOnlyList<Vector2> inner)
	{
		List<float> angles = new List<float>(outer.Count + inner.Count);
		for (int i = 0; i < outer.Count; i++)
		{
			AddUniqueAngle(angles, Mathf.Atan2(outer[i].y - center.y, outer[i].x - center.x));
		}
		for (int i = 0; i < inner.Count; i++)
		{
			AddUniqueAngle(angles, Mathf.Atan2(inner[i].y - center.y, inner[i].x - center.x));
		}

		angles.Sort();
		for (int i = angles.Count - 1; i > 0; i--)
		{
			if (Mathf.Abs(angles[i] - angles[i - 1]) <= Epsilon)
			{
				angles.RemoveAt(i);
			}
		}
		return angles;
	}

	// 沿给定方向从公共中心发射射线，求它与凸闭环的首个正向交点。 / Shoot a ray from the shared center and find its first forward intersection with a convex loop.
	private static bool TryIntersectConvexLoopRay(IReadOnlyList<Vector2> loop, Vector2 rayOrigin, float angle, out Vector2 hit)
	{
		Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
		float bestDistance = float.PositiveInfinity;
		bool found = false;
		hit = default;
		for (int i = 0; i < loop.Count; i++)
		{
			Vector2 a = loop[i];
			Vector2 b = loop[(i + 1) % loop.Count];
			Vector2 edge = b - a;
			float denominator = Cross(direction, edge);
			if (Mathf.Abs(denominator) <= Epsilon)
			{
				continue;
			}

			Vector2 delta = a - rayOrigin;
			float distance = Cross(delta, edge) / denominator;
			float edgeFraction = Cross(delta, direction) / denominator;
			if (distance < -Epsilon || edgeFraction < -Epsilon || edgeFraction > 1f + Epsilon)
			{
				continue;
			}

			if (distance < bestDistance)
			{
				bestDistance = distance;
				hit = rayOrigin + direction * Mathf.Max(0f, distance);
				found = true;
			}
		}
		return found;
	}

	// 计算闭环在周长上的归一化累计位置，供桥接环时做最近匹配。 / Compute normalized perimeter fractions for one loop so ring bridging can match nearest points.
	private static List<float> ComputeFractions(IReadOnlyList<Vector2> points)
	{
		List<float> fractions = new List<float>(points.Count);
		if (points.Count == 0)
		{
			return fractions;
		}

		float perimeter = 0f;
		fractions.Add(0f);
		for (int i = 1; i < points.Count; i++)
		{
			perimeter += Vector2.Distance(points[i - 1], points[i]);
			fractions.Add(perimeter);
		}
		perimeter += Vector2.Distance(points[^1], points[0]);
		if (perimeter <= Epsilon)
		{
			for (int i = 0; i < fractions.Count; i++)
			{
				fractions[i] = points.Count <= 1 ? 0f : i / (float)points.Count;
			}
			return fractions;
		}

		for (int i = 0; i < fractions.Count; i++)
		{
			fractions[i] /= perimeter;
		}
		return fractions;
	}

	// 仅当新角度不与已有角度重合时才追加，避免条带退化。 / Append an angle only when it does not duplicate an existing sector boundary.
	private static void AddUniqueAngle(List<float> angles, float angle)
	{
		angle = Mathf.Repeat(angle, Mathf.PI * 2f);
		for (int i = 0; i < angles.Count; i++)
		{
			if (Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, angles[i] * Mathf.Rad2Deg)) <= 0.01f)
			{
				return;
			}
		}
		angles.Add(angle);
	}

	// 对两个闭环的参数化周长做最近邻匹配。 / Find the nearest perimeter-parameter matches between two closed loops.
	private static int[] FindClosestLinks(List<float> source, List<float> target)
	{
		int[] links = new int[source.Count];
		for (int i = 0; i < source.Count; i++)
		{
			float bestDistance = float.PositiveInfinity;
			int bestIndex = 0;
			for (int j = 0; j < target.Count; j++)
			{
				float distance = CircularDistance(source[i], target[j]);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestIndex = j;
				}
			}
			links[i] = bestIndex;
		}
		return links;
	}

	// 计算闭环参数上的最短环形距离。 / Compute the shortest circular distance between two loop parameters.
	private static float CircularDistance(float a, float b)
	{
		float delta = Mathf.Abs(a - b);
		return Mathf.Min(delta, 1f - delta);
	}

	// 计算 2D 多边形质心，用于 fan cap 的中心点。 / Compute a 2D polygon centroid for fan-style capping.
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

	// 计算 2D 多边形的有向面积，负值表示顺时针。 / Compute the signed area of a 2D polygon; negative means clockwise.
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

	// 测试一个点是否落在闭合多边形内部。 / Test whether a point lies inside a closed polygon.
	private static bool PointInPolygon(IReadOnlyList<Vector2> polygon, Vector2 point)
	{
		bool inside = false;
		for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
		{
			bool intersects = ((polygon[i].y > point.y) != (polygon[j].y > point.y))
				&& (point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / Mathf.Max(Epsilon, polygon[j].y - polygon[i].y) + polygon[i].x);
			if (intersects)
			{
				inside = !inside;
			}
		}
		return inside;
	}

	// 保证轮廓顶点顺时针，便于复用现有圆角和桥接逻辑。 / Ensure one outline is clockwise so the existing rounding and loop-bridging logic stays consistent.
	private static List<Vector2> EnsureClockwise(List<Vector2> points)
	{
		if (points.Count >= 3 && SignedArea(points) > 0f)
		{
			points.Reverse();
		}
		return points;
	}

	// 删除 2D 轮廓上相邻重复点。 / Remove adjacent duplicate points from a 2D loop.
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

	// 删除 3D 轮廓上相邻重复点。 / Remove adjacent duplicate points from a 3D loop.
	private static void RemoveNearDuplicateLoopPoints(List<ClippedVertex> points)
	{
		for (int i = points.Count - 1; i >= 0; i--)
		{
			Vector3 current = points[i].Position;
			Vector3 next = points[(i + 1) % points.Count].Position;
			if (Vector3.Distance(current, next) <= Epsilon)
			{
				points.RemoveAt(i);
			}
		}
	}

	// 仅当新点不与末尾点重合时才追加 2D 顶点。 / Append a 2D point only when it is not a duplicate of the current tail.
	private static void AddUnique(List<Vector2> points, Vector2 point)
	{
		if (points.Count > 0 && Vector2.Distance(points[^1], point) <= Epsilon)
		{
			return;
		}
		points.Add(point);
	}

	// 仅当新点不与末尾点重合时才追加 3D 顶点。 / Append a 3D point only when it is not a duplicate of the current tail.
	private static void AddUnique(List<ClippedVertex> points, ClippedVertex point)
	{
		if (points.Count > 0 && Vector3.Distance(points[^1].Position, point.Position) <= Epsilon)
		{
			return;
		}
		points.Add(point);
	}

	// 计算 2D 叉积。 / Compute the 2D cross product.
	private static float Cross(Vector2 a, Vector2 b)
	{
		return a.x * b.y - a.y * b.x;
	}
}
