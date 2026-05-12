using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;


[Serializable]
public struct FuselageSectionSettings
{
	public float Width;

	public float Height;

	[Range(-1f, 1f)]
	public float Trapezium;

	[Range(0f, 1f)]
	public float Thickness;

	public Float4Value CornerRadii;

	public Bool4Value CornerStretch;

	[NonSerialized]
	public Float4Value CornerStretchAmount;

	public Float4Value EdgeCurvature;

	public float CutTop;

	public float CutBottom;

	public float CutLeft;

	public float CutRight;

	public Bool4Value CutEnabled;

	public bool Smooth;

	public Int4Value CornerSamples;

	public Int4Value EdgeSamples;

	public Vector2 HalfSize => new Vector2(Width * 0.5f, Height * 0.5f);

	// 返回单个 corner 的有效 stretch 值；如果是 stretched 角但派生字段没初始化，则回退到 1。 / Return the effective stretch amount for one corner, defaulting stretched corners to 1 when the backing field is unset.
	public float GetCornerStretchAmount(int index)
	{
		const float cornerStretchEpsilon = 0.0001f;
		float value = Mathf.Clamp01(CornerStretchAmount[index]);
		if (value <= cornerStretchEpsilon && CornerStretch[index])
		{
			return 1f;
		}

		return value;
	}

	// 以 float mask 形式返回四个 corner 的有效 stretch 值，用于插值。 / Return the four effective stretch amounts as a float mask for interpolation.
	public Float4Value GetCornerStretchMask()
	{
		return new Float4Value(
			GetCornerStretchAmount(0),
			GetCornerStretchAmount(1),
			GetCornerStretchAmount(2),
			GetCornerStretchAmount(3));
	}

	// 按每个 corner 当前的 stretch 模式计算各自的半径上限。 / Compute the current per-corner radius limits using each corner's active stretch mode.
	public Float4Value GetMaxCornerRadii()
	{
		Vector2[] unscaledCorners = new Vector2[4];
		GetOutlineCorners(unscaledCorners);
		Float4Value result = new Float4Value();
		for (int i = 0; i < 4; i++)
		{
			result[i] = GetMaxCornerRadius(unscaledCorners, i, GetCornerStretchAmount(i));
		}
		return result;
	}

	// 在假定所有 corner 都完全 Rounded 或完全 Stretched 的前提下计算半径上限。 / Compute the per-corner radius limits assuming all corners are either fully rounded or fully stretched.
	public Float4Value GetMaxCornerRadii(bool stretched)
	{
		Vector2[] unscaledCorners = new Vector2[4];
		GetOutlineCorners(unscaledCorners);
		Float4Value result = new Float4Value();
		float stretchAmount = stretched ? 1f : 0f;
		for (int i = 0; i < 4; i++)
		{
			result[i] = GetMaxCornerRadius(unscaledCorners, i, stretchAmount);
		}
		return result;
	}

	// 读取指定方向的 cut 启用状态；顺序为 Top/Right/Bottom/Left。 / Read one cut enable flag in Top/Right/Bottom/Left order.
	public readonly bool GetCutEnabled(int index)
	{
		return CutEnabled[index];
	}

	// 读取指定方向的 cut 数值；顺序为 Top/Right/Bottom/Left。 / Read one cut value in Top/Right/Bottom/Left order.
	public readonly float GetCutValue(int index)
	{
		return index switch
		{
			0 => CutTop,
			1 => CutRight,
			2 => CutBottom,
			3 => CutLeft,
			_ => throw new IndexOutOfRangeException()
		};
	}

	// 设置指定方向的 cut 数值；顺序为 Top/Right/Bottom/Left。 / Set one cut value in Top/Right/Bottom/Left order.
	public void SetCutValue(int index, float value)
	{
		switch (index)
		{
			case 0:
				CutTop = value;
				break;
			case 1:
				CutRight = value;
				break;
			case 2:
				CutBottom = value;
				break;
			case 3:
				CutLeft = value;
				break;
			default:
				throw new IndexOutOfRangeException();
		}
	}

	// 按原版 minSlicing 语义返回该截面的 cut 取值范围。 / Return the cut value range for this section using the original minSlicing semantics.
	public readonly void GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting)
	{
		FuselageSectionSettings section = this;
		section.Sanitize();
		FuselageGeometry.GetCuttingRange(section, out minCutting, out maxCutting);
	}

	// 构建未缩放的 corner 方向，供预览 UI 上限和运行时 loft 几何共同复用。 / Build the unscaled corner directions used by both preview UI limits and runtime loft generation.
	private void GetOutlineCorners(Vector2[] unscaledCorners)
	{
		Vector2[] baseCorners =
		{
			new Vector2(1f, 1f),
			new Vector2(1f, -1f),
			new Vector2(-1f, -1f),
			new Vector2(-1f, 1f)
		};

		for (int i = 0; i < 4; i++)
		{
			Vector2 point = baseCorners[i];
			point.x *= 1f + point.y * Trapezium;
			unscaledCorners[i] = point;
		}
	}

	// 在 stretch 和 trapezium 生效后，计算单个 corner 合法的最大圆角半径。 / Compute the maximum legal fillet radius for a single corner after stretch and trapezium are applied.
	private float GetMaxCornerRadius(Vector2[] unscaledCorners, int index, float stretchAmount)
	{
		Vector2 stretchScale = Vector2.Lerp(HalfSize, Vector2.one, Mathf.Clamp01(stretchAmount));
		Vector2 previous = Vector2.Scale(stretchScale, unscaledCorners[(index + 3) % 4]);
		Vector2 current = Vector2.Scale(stretchScale, unscaledCorners[index]);
		Vector2 next = Vector2.Scale(stretchScale, unscaledCorners[(index + 1) % 4]);
		Vector2 inEdge = current - previous;
		Vector2 outEdge = next - current;
		float inLength = inEdge.magnitude;
		float outLength = outEdge.magnitude;
		float inAngle = Mathf.Atan2(inEdge.x, inEdge.y);
		float outAngle = Mathf.Atan2(outEdge.x, outEdge.y);
		float angle = Mathf.Repeat(outAngle - inAngle, Mathf.PI * 2f);
		float tangent = Mathf.Tan((Mathf.PI - angle) * 0.5f);
		if (float.IsNaN(tangent) || float.IsInfinity(tangent))
		{
			return 0f;
		}

		return Mathf.Max(0f, Mathf.Min(inLength, outLength) * 0.5f * tangent);
	}

	// 把截面值钳制到运行时安全范围内，并恢复未序列化的派生字段。 / Clamp section values into safe runtime ranges and restore nonserialized derived fields.
	public void Sanitize()
	{
		const float cornerStretchEpsilon = 0.0001f;
		const float cutEpsilon = 0.0001f;
		Width = Mathf.Max(0.01f, Width);
		Height = Mathf.Max(0.01f, Height);
		Trapezium = Mathf.Clamp(Trapezium, -1f, 1f);
		Thickness = Mathf.Clamp(Thickness, 0f, 0.99f);
		CornerRadii.X = Mathf.Max(0f, CornerRadii.X);
		CornerRadii.Y = Mathf.Max(0f, CornerRadii.Y);
		CornerRadii.Z = Mathf.Max(0f, CornerRadii.Z);
		CornerRadii.W = Mathf.Max(0f, CornerRadii.W);
		CornerStretchAmount.X = Mathf.Clamp01(CornerStretchAmount.X);
		CornerStretchAmount.Y = Mathf.Clamp01(CornerStretchAmount.Y);
		CornerStretchAmount.Z = Mathf.Clamp01(CornerStretchAmount.Z);
		CornerStretchAmount.W = Mathf.Clamp01(CornerStretchAmount.W);
		if (CornerStretch.X && CornerStretchAmount.X <= cornerStretchEpsilon)
		{
			CornerStretchAmount.X = 1f;
		}
		if (CornerStretch.Y && CornerStretchAmount.Y <= cornerStretchEpsilon)
		{
			CornerStretchAmount.Y = 1f;
		}
		if (CornerStretch.Z && CornerStretchAmount.Z <= cornerStretchEpsilon)
		{
			CornerStretchAmount.Z = 1f;
		}
		if (CornerStretch.W && CornerStretchAmount.W <= cornerStretchEpsilon)
		{
			CornerStretchAmount.W = 1f;
		}
		EdgeCurvature.X = Mathf.Clamp01(EdgeCurvature.X);
		EdgeCurvature.Y = Mathf.Clamp01(EdgeCurvature.Y);
		EdgeCurvature.Z = Mathf.Clamp01(EdgeCurvature.Z);
		EdgeCurvature.W = Mathf.Clamp01(EdgeCurvature.W);
		CutTop = float.IsFinite(CutTop) ? Mathf.Clamp(CutTop, -2f, 2f) : 0f;
		CutBottom = float.IsFinite(CutBottom) ? Mathf.Clamp(CutBottom, -2f, 2f) : 0f;
		CutLeft = float.IsFinite(CutLeft) ? Mathf.Clamp(CutLeft, -2f, 2f) : 0f;
		CutRight = float.IsFinite(CutRight) ? Mathf.Clamp(CutRight, -2f, 2f) : 0f;
		if (!CutEnabled.X && !CutEnabled.Y && !CutEnabled.Z && !CutEnabled.W)
		{
			CutEnabled.X = Mathf.Abs(CutTop) > cutEpsilon;
			CutEnabled.Y = Mathf.Abs(CutRight) > cutEpsilon;
			CutEnabled.Z = Mathf.Abs(CutBottom) > cutEpsilon;
			CutEnabled.W = Mathf.Abs(CutLeft) > cutEpsilon;
		}
		CornerSamples.X = Mathf.Max(2, CornerSamples.X == 0 ? 7 : CornerSamples.X);
		CornerSamples.Y = Mathf.Max(2, CornerSamples.Y == 0 ? 7 : CornerSamples.Y);
		CornerSamples.Z = Mathf.Max(2, CornerSamples.Z == 0 ? 7 : CornerSamples.Z);
		CornerSamples.W = Mathf.Max(2, CornerSamples.W == 0 ? 7 : CornerSamples.W);
		EdgeSamples.X = Mathf.Max(0, EdgeSamples.X == 0 ? 7 : EdgeSamples.X);
		EdgeSamples.Y = Mathf.Max(0, EdgeSamples.Y == 0 ? 7 : EdgeSamples.Y);
		EdgeSamples.Z = Mathf.Max(0, EdgeSamples.Z == 0 ? 7 : EdgeSamples.Z);
		EdgeSamples.W = Mathf.Max(0, EdgeSamples.W == 0 ? 7 : EdgeSamples.W);
	}

	// 按原游戏相同的“共享半径 + stretch 模式”模型在两个截面之间插值。 / Interpolate between two sections using the same shared radius-plus-stretch model as the original game.
	public static FuselageSectionSettings Lerp(FuselageSectionSettings a, FuselageSectionSettings b, float t, Int4Value cornerSamples, Int4Value edgeSamples)
	{
		Float4Value cornerStretchMask = Float4Value.Lerp(a.GetCornerStretchMask(), b.GetCornerStretchMask(), t);
		FuselageSectionSettings result = new FuselageSectionSettings
		{
			Width = Mathf.Lerp(a.Width, b.Width, t),
			Height = Mathf.Lerp(a.Height, b.Height, t),
			Trapezium = Mathf.Lerp(a.Trapezium, b.Trapezium, t),
			Thickness = Mathf.Lerp(a.Thickness, b.Thickness, t),
			CornerRadii = Float4Value.Lerp(a.CornerRadii, b.CornerRadii, t),
			CornerStretch = Bool4Value.FromFloatMask(cornerStretchMask),
			CornerStretchAmount = cornerStretchMask,
			EdgeCurvature = Float4Value.Lerp(a.EdgeCurvature, b.EdgeCurvature, t),
			CutTop = Mathf.Lerp(a.CutTop, b.CutTop, t),
			CutBottom = Mathf.Lerp(a.CutBottom, b.CutBottom, t),
			CutLeft = Mathf.Lerp(a.CutLeft, b.CutLeft, t),
			CutRight = Mathf.Lerp(a.CutRight, b.CutRight, t),
			CutEnabled = Bool4Value.FromFloatMask(Float4Value.Lerp(a.CutEnabled.ToFloatMask(), b.CutEnabled.ToFloatMask(), t)),
			Smooth = t < 0.5f ? a.Smooth : b.Smooth,
			CornerSamples = cornerSamples,
			EdgeSamples = edgeSamples
		};
		result.Sanitize();
		return result;
	}
}

internal static class FuselageGeometry
{
	private const float Epsilon = 0.0001f;

	private readonly struct ClipBounds
	{
		public ClipBounds(float minX, float minY, float maxX, float maxY)
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

		public static ClipBounds Lerp(ClipBounds a, ClipBounds b, float t)
		{
			return new ClipBounds(
				Mathf.Lerp(a.MinX, b.MinX, t),
				Mathf.Lerp(a.MinY, b.MinY, t),
				Mathf.Lerp(a.MaxX, b.MaxX, t),
				Mathf.Lerp(a.MaxY, b.MaxY, t));
		}

	}

	private sealed class RingProfile
	{
		public RingProfile(List<Vector2> points, Vector3 center, List<Vector2> tangents = null)
		{
			Center = center;
			Points = new List<Vector2>(points.Count);
			InTangents = new List<Vector2>(points.Count);
			OutTangents = new List<Vector2>(points.Count);
			Sharp = new List<bool>(points.Count);

			for (int i = 0; i < points.Count; i++)
			{
				Vector2 point = points[i];
				Vector2 inTangent = GetSourceTangent(points, tangents, i);
				Vector2 outTangent = inTangent;
				bool sharp = false;
				if (i + 1 < points.Count && Vector2.Distance(point, points[i + 1]) <= Epsilon)
				{
					outTangent = GetSourceTangent(points, tangents, i + 1);
					sharp = true;
					i++;
				}

				AddLogicalPoint(point, inTangent, outTangent, sharp);
			}

			if (Points.Count > 1 && Vector2.Distance(Points[0], Points[^1]) <= Epsilon)
			{
				Points.RemoveAt(Points.Count - 1);
				InTangents.RemoveAt(InTangents.Count - 1);
				OutTangents.RemoveAt(OutTangents.Count - 1);
				Sharp.RemoveAt(Sharp.Count - 1);
			}

			Fractions = ComputeFractions(Points);
		}

		public Vector3 Center { get; }

		public List<float> Fractions { get; }

		public List<Vector2> Points { get; }

		public List<Vector2> InTangents { get; }

		public List<Vector2> OutTangents { get; }

		public List<bool> Sharp { get; }

		public int[] MeshIndicesIn { get; set; } = Array.Empty<int>();

		public int[] MeshIndicesOut { get; set; } = Array.Empty<int>();

		public int Count => Points.Count;

		public int GetInIndex(int index)
		{
			return MeshIndicesIn != null && index >= 0 && index < MeshIndicesIn.Length ? MeshIndicesIn[index] : -1;
		}

		public int GetOutIndex(int index)
		{
			return MeshIndicesOut != null && index >= 0 && index < MeshIndicesOut.Length ? MeshIndicesOut[index] : GetInIndex(index);
		}

		private void AddLogicalPoint(Vector2 point, Vector2 inTangent, Vector2 outTangent, bool sharp)
		{
			if (Points.Count > 0 && Vector2.Distance(Points[^1], point) <= Epsilon)
			{
				OutTangents[^1] = NormalizeTangent(outTangent);
				Sharp[^1] = Sharp[^1] || sharp;
				return;
			}

			Points.Add(point);
			InTangents.Add(NormalizeTangent(inTangent));
			OutTangents.Add(NormalizeTangent(outTangent));
			Sharp.Add(sharp);
		}

		private static Vector2 GetSourceTangent(List<Vector2> points, List<Vector2> tangents, int index)
		{
			if (tangents != null && tangents.Count == points.Count && tangents[index].sqrMagnitude > Epsilon)
			{
				return tangents[index];
			}

			Vector2 previous = points[(index - 1 + points.Count) % points.Count];
			Vector2 next = points[(index + 1) % points.Count];
			Vector2 tangent = next - previous;
			if (tangent.sqrMagnitude <= Epsilon)
			{
				tangent = next - points[index];
			}
			if (tangent.sqrMagnitude <= Epsilon)
			{
				tangent = points[index] - previous;
			}
			return tangent;
		}

		private static Vector2 NormalizeTangent(Vector2 tangent)
		{
			return tangent.sqrMagnitude <= Epsilon ? Vector2.right : tangent.normalized;
		}
	}

	private readonly struct CornerSample
	{
		public CornerSample(Vector2 position, Vector2 tangent)
		{
			Position = position;
			Tangent = tangent;
		}

		public Vector2 Position { get; }

		public Vector2 Tangent { get; }
	}

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

		// 默认启用前后两个端盖，构建完整 loft。 / Build a fully capped loft using both end caps by default.
	public static Mesh BuildLoft(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow)
	{
		return BuildLoft(rear, front, offset, hollow, capRear: true, capFront: true);
	}

	// 构建完整机身 loft，包括外壁、可选内壁以及前后端盖。 / Build the full fuselage loft, including sidewalls, optional hollow inner walls, and selected end caps.
	public static Mesh BuildLoft(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow, bool capRear, bool capFront)
	{
		return BuildLoft(rear, front, offset, hollow, capRear, capFront, applySectionCutting: true);
	}

	public static Mesh BuildLoft(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow, bool capRear, bool capFront, bool applySectionCutting)
	{
		return BuildLoftData(rear, front, offset, hollow, capRear, capFront, applySectionCutting).ToMesh();
	}

	internal static PreviewMeshData BuildLoftData(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow, bool capRear, bool capFront)
	{
		return BuildLoftData(rear, front, offset, hollow, capRear, capFront, applySectionCutting: false);
	}

	internal static PreviewMeshData BuildLoftData(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow, bool capRear, bool capFront, bool applySectionCutting)
	{
		PreviewMeshData data = BuildRawLoftData(rear, front, offset, hollow, capRear, capFront);
		if (applySectionCutting && TryBuildSectionCutPlanes(rear, front, offset, out Plane[] cutPlanes))
		{
			data = IntersectConvexVolume(data, cutPlanes, data.Name + "_Cut");
		}
		return data;
	}

	private static PreviewMeshData BuildRawLoftData(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, bool hollow, bool capRear, bool capFront)
	{
		using var _ = new SampleProfiler("BuildRawLoftData");//GC和大量耗时  

		rear.Sanitize();
		front.Sanitize();
		Vector3 axis = offset.sqrMagnitude <= Epsilon ? Vector3.forward : offset.normalized;
		Int4Value cornerSamples = Int4Value.Max(rear.CornerSamples, front.CornerSamples);
		Int4Value edgeSamples = Int4Value.Max(rear.EdgeSamples, front.EdgeSamples);
		int sliceCount = Mathf.Clamp(4 + cornerSamples.MaxComponent() / 2 + edgeSamples.MaxComponent(), 4, 24);
		List<RingProfile> outerRings = new List<RingProfile>(sliceCount + 1);
		List<RingProfile> innerRings = hollow ? new List<RingProfile>(sliceCount + 1) : null;
		float rearInsetDistance = hollow ? ComputeThicknessInsetDistance(rear) : 0f;
		float frontInsetDistance = hollow ? ComputeThicknessInsetDistance(front) : 0f;

		// 沿 loft 轴线采样插值截面；真正的 slice cutting 在完整网格生成后统一做 3D 裁切。 / Sample interpolated sections along the loft axis; true slice cutting is applied later as a 3D clip on the finished mesh.
		for (int i = 0; i <= sliceCount; i++)
		{
			float t = i / (float)sliceCount;
			FuselageSectionSettings section = FuselageSectionSettings.Lerp(rear, front, t, cornerSamples, edgeSamples);
			Vector3 center = Vector3.Lerp(-offset * 0.5f, offset * 0.5f, t);
			List<Vector2> outerTangents;
			List<Vector2> outer = BuildSectionOutline(section, null, out outerTangents);
			if (outer.Count < 3)
			{
				outer = BuildFallbackOutline(section);
				outerTangents = BuildLoopTangents(outer);
			}
			outerRings.Add(new RingProfile(outer, center, outerTangents));

			if (!hollow)
			{
				continue;
			}

			float insetDistance = Mathf.Lerp(rearInsetDistance, frontInsetDistance, t);
			if (!TryBuildInnerOutline(outer, section, insetDistance, out List<Vector2> inner) || inner.Count < 3)
			{
				inner = BuildScaledInnerOutline(outer, section, insetDistance);
			}

			innerRings!.Add(new RingProfile(inner, center, BuildLoopTangents(inner)));
		}

		// 先把所有 ring 发射出来，便于后续按连续索引拼接侧壁和端盖。 / Emit all rings first so cap and wall stitching can address them by contiguous indices.
		string meshName = hollow ? "FuselageHollow" : "FuselageBody";
		List<Vector3> vertices = new List<Vector3>();
		List<int> triangles = new List<int>();

		foreach (RingProfile ring in outerRings)
		{
			AddRing(vertices, ring);
		}

		for (int i = 0; i < outerRings.Count - 1; i++)
		{
			ConnectRings(triangles, outerRings[i], outerRings[i + 1], reverseQuads: false);
		}

		if (hollow && innerRings != null)
		{
			foreach (RingProfile ring in innerRings)
			{
				AddRing(vertices, ring);
			}

			for (int i = 0; i < innerRings.Count - 1; i++)
			{
				ConnectRings(triangles, innerRings[i + 1], innerRings[i], reverseQuads: true);
			}
		}

		List<Vector3> normals = new List<Vector3>(vertices.Count);
		for (int i = 0; i < vertices.Count; i++)
		{
			normals.Add(Vector3.zero);
		}

		// 先生成平滑侧壁法线，再补平面的前后端盖法线。 / Generate smooth side normals before adding flat-shaded front and rear caps.
		ApplyLoftNormals(normals, outerRings, invert: false, axis);
		if (hollow && innerRings != null)
		{
			ApplyLoftNormals(normals, innerRings, invert: true, axis);
		}

		if (hollow && innerRings != null)
		{
			if (capRear)
			{
				CapRimFlat(vertices, normals, triangles, outerRings[0], innerRings[0], Vector3.back, flip: false);
			}
			if (capFront)
			{
				CapRimFlat(vertices, normals, triangles, outerRings[^1], innerRings[^1], Vector3.forward, flip: true);
			}
		}
		else
		{
			if (capRear)
			{
				CapRingFlat(vertices, normals, triangles, outerRings[0], Vector3.back, flip: false);
			}
			if (capFront)
			{
				CapRingFlat(vertices, normals, triangles, outerRings[^1], Vector3.forward, flip: true);
			}
		}

		PreviewMeshData meshData = new PreviewMeshData(meshName, vertices, normals, triangles);

		return meshData;
	}

	// 当裁切把曲线截面削得过多时，回退到简单梯形轮廓。 / Fall back to a plain trapezium outline when clipping removes too much of the curved profile.
	private static List<Vector2> BuildFallbackOutline(FuselageSectionSettings section)
	{
		return EnsureClockwise(new List<Vector2>
		{
			ApplyTrapezium(new Vector2(1f, 1f), section),
			ApplyTrapezium(new Vector2(1f, -1f), section),
			ApplyTrapezium(new Vector2(-1f, -1f), section),
			ApplyTrapezium(new Vector2(-1f, 1f), section)
		});
	}

	// 当调用方不需要 cut-volume 裁切时，构建未裁切的截面轮廓。 / Build an unclipped section outline when the caller does not need cut-volume clipping.
	private static List<Vector2> BuildSectionOutline(FuselageSectionSettings section)
	{
		return BuildSectionOutline(section, null, out _);
	}

	// 构建单个截面轮廓，包括 corner 圆弧、弯曲边以及可选 cut 裁切。 / Build one section outline, including corner arcs, curved edges, and optional cut clipping.
	private static List<Vector2> BuildSectionOutline(FuselageSectionSettings section, ClipBounds? clipBounds, out List<Vector2> tangents)
	{
		section.Sanitize();
		tangents = new List<Vector2>();
		bool hasCutting = clipBounds.HasValue;
		bool preserveSharpCornerNormals = !hasCutting;
		Float4Value clampedCornerRadii = ClampCornerRadii(section);
		Vector2[] unscaledCorners = new Vector2[4];
		Vector2[] corners = new Vector2[4];
		GetOutlineShape(unscaledCorners, corners, section);
		Vector2[] cornerCenters = new Vector2[4];
		Vector2[] cornerScales = new Vector2[4];
		float[] cornerAngles = new float[4];
		float[] cornerStartAngles = new float[4];
		bool[] cornerStartMaxed = new bool[4];
		bool[] cornerEndMaxed = new bool[4];
		// 先把所有 corner 圆弧参数预计算好，减少采样主循环里的分支。 / Precompute all corner arc parameters once so the sampling loop can stay branch-light.
		for (int i = 0; i < 4; i++)
		{
			if (clampedCornerRadii[i] <= Epsilon)
			{
				cornerCenters[i] = corners[i];
				cornerScales[i] = Vector2.one;
				cornerAngles[i] = 0f;
				cornerStartAngles[i] = 0f;
				cornerStartMaxed[i] = false;
				cornerEndMaxed[i] = false;
				continue;
			}

			GetCurveParams(i, unscaledCorners, section, clampedCornerRadii[i], out cornerCenters[i], out cornerAngles[i], out cornerStartAngles[i], out _, out cornerScales[i], out cornerStartMaxed[i], out cornerEndMaxed[i]);
		}

		float[] edgeCurvature =
		{
			Mathf.Clamp01(section.EdgeCurvature.X),
			Mathf.Clamp01(section.EdgeCurvature.Y),
			Mathf.Clamp01(section.EdgeCurvature.Z),
			Mathf.Clamp01(section.EdgeCurvature.W)
		};
		float[] arcTrimStart =
		{
			edgeCurvature[3] * 0.5f,
			edgeCurvature[0] * 0.5f,
			edgeCurvature[1] * 0.5f,
			edgeCurvature[2] * 0.5f
		};
		float[] arcTrimEnd =
		{
			1f - edgeCurvature[0] * 0.5f,
			1f - edgeCurvature[1] * 0.5f,
			1f - edgeCurvature[2] * 0.5f,
			1f - edgeCurvature[3] * 0.5f
		};

		List<Vector2> points = new List<Vector2>();
		// 逐个采样 corner 圆弧，并在需要时用二次曲线替换直边。 / Sample each corner arc and optionally replace straight edges with quadratic edge bulges.
		for (int i = 0; i < 4; i++)
		{
			if (clampedCornerRadii[i] <= Epsilon)
			{
				if (preserveSharpCornerNormals)
				{
					Vector2 incomingTangent = (corners[i] - corners[(i + 3) % 4]).normalized;
					Vector2 outgoingTangent = (corners[(i + 1) % 4] - corners[i]).normalized;
					AddSharpCorner(points, tangents, corners[i], incomingTangent, outgoingTangent);
				}
				else
				{
					Vector2 tangent = (corners[(i + 1) % 4] - corners[(i + 3) % 4]).normalized;
					AddUnique(points, tangents, corners[i], tangent);
				}
			}
			else
			{
				int sampleCount = Mathf.Max(2, section.CornerSamples[i]);
				float sampleStep = 1f / (sampleCount - 1);
				CornerSample startCorner = SampleCorner(i, arcTrimStart[i], section, clampedCornerRadii[i], cornerCenters[i], cornerStartAngles[i], cornerAngles[i], cornerScales[i]);
				AddUnique(points, tangents, startCorner.Position, startCorner.Tangent);
				for (int sample = 1; sample < sampleCount - 1; sample++)
				{
					float arcT = sampleStep * sample;
					if (arcT <= arcTrimStart[i] + 0.001f)
					{
						continue;
					}
					if (arcT >= arcTrimEnd[i] - 0.001f)
					{
						break;
					}
					CornerSample corner = SampleCorner(i, arcT, section, clampedCornerRadii[i], cornerCenters[i], cornerStartAngles[i], cornerAngles[i], cornerScales[i]);
					AddUnique(points, tangents, corner.Position, corner.Tangent);
				}

				int nextCorner = (i + 1) % 4;
				if (arcTrimEnd[i] > arcTrimStart[i] && (!cornerEndMaxed[i] || !cornerStartMaxed[nextCorner]))
				{
					CornerSample endCorner = SampleCorner(i, arcTrimEnd[i], section, clampedCornerRadii[i], cornerCenters[i], cornerStartAngles[i], cornerAngles[i], cornerScales[i]);
					AddUnique(points, tangents, endCorner.Position, endCorner.Tangent);
				}
			}

			if (edgeCurvature[i] <= Epsilon)
			{
				continue;
			}

			int next = (i + 1) % 4;
			CornerSample edgeStart = clampedCornerRadii[i] <= Epsilon
				? new CornerSample(corners[i], (corners[next] - corners[i]).normalized)
				: SampleCorner(i, arcTrimEnd[i], section, clampedCornerRadii[i], cornerCenters[i], cornerStartAngles[i], cornerAngles[i], cornerScales[i]);
			CornerSample edgeEnd = clampedCornerRadii[next] <= Epsilon
				? new CornerSample(corners[next], (corners[next] - corners[i]).normalized)
				: SampleCorner(next, arcTrimStart[next], section, clampedCornerRadii[next], cornerCenters[next], cornerStartAngles[next], cornerAngles[next], cornerScales[next]);

			Vector2 control = GetCurvedEdgeControl(edgeStart, edgeEnd);
			int edgeSampleCount = Mathf.Max(0, section.EdgeSamples[i]);
			if (edgeSampleCount <= 3)
			{
				AddUnique(points, tangents, control, (edgeEnd.Position - edgeStart.Position).normalized);
				continue;
			}

			for (int sample = 1; sample < edgeSampleCount - 1; sample++)
			{
				float t = sample / (float)(edgeSampleCount - 1);
				Vector2 tangent = Vector2.Lerp(control - edgeStart.Position, edgeEnd.Position - control, t).normalized;
				AddUnique(points, tangents, EvaluateQuadratic(edgeStart.Position, control, edgeEnd.Position, t), tangent);
			}
		}

		// 裁切后原始切线会失效，所以这里重新构建一遍切线。 / Rebuild tangents after clipping because polygon clipping invalidates the original sampling tangents.
		if (clipBounds.HasValue)
		{
			points = ClipSection(points, clipBounds.Value);
		}
		if (hasCutting)
		{
			tangents = BuildLoopTangents(points);
		}
		points = EnsureClockwise(points, tangents);
		if (!preserveSharpCornerNormals)
		{
			RemoveNearDuplicateLoopPoints(points, tangents);
		}
		if (tangents == null || tangents.Count != points.Count)
		{
			tangents = BuildLoopTangents(points);
		}
		return points;
	}

	// 按几何真实可承受的上限钳制 corner 半径请求值。 / Clamp requested corner radii against the geometry each corner can actually support.
	private static Float4Value ClampCornerRadii(FuselageSectionSettings section)
	{
		Float4Value maxCornerRadii = section.GetMaxCornerRadii();
		Float4Value result = new Float4Value();
		for (int i = 0; i < 4; i++)
		{
			result[i] = Mathf.Min(section.CornerRadii[i], maxCornerRadii[i]);
		}
		return result;
	}

	// 当几何 inset 对薄壁 hollow 截面失败时，退化成简单缩放的内环。 / Build a simple scaled inner loop when geometric insetting fails for thin hollow sections.
	private static float ComputeThicknessInsetDistance(FuselageSectionSettings section)
	{
		return Mathf.Min(section.Width, section.Height) * 0.5f * Mathf.Clamp(section.Thickness, 0.01f, 0.99f);
	}

	private static List<Vector2> BuildScaledInnerOutline(List<Vector2> outer, FuselageSectionSettings section, float insetDistance)
	{
		List<Vector2> source = BuildCapLoopPoints(outer);
		if (source.Count < 3)
		{
			source = outer;
		}
		bool[] sharpCornerFlags = BuildSharpCornerFlags(outer, source);

		Vector2 center = ComputeAverage(source);
		float halfMinSize = Mathf.Max(Epsilon, Mathf.Min(section.Width, section.Height) * 0.5f);
		float scale = Mathf.Clamp01(1f - Mathf.Clamp(insetDistance / halfMinSize, 0.01f, 0.95f));
		List<Vector2> inner = new List<Vector2>(source.Count);
		for (int i = 0; i < source.Count; i++)
		{
			inner.Add(center + (source[i] - center) * scale);
		}
		inner = DuplicateSharpLoopPoints(inner, sharpCornerFlags);
		return EnsureClockwise(inner);
	}

	// 尝试为 hollow 机身构建真实的内缩轮廓。 / Try to build a proper inset inner loop for hollow fuselages.
	private static bool TryBuildInnerOutline(List<Vector2> outer, FuselageSectionSettings section, float insetDistance, out List<Vector2> inner)
	{
		List<Vector2> insetSource = BuildCapLoopPoints(outer);
		if (insetSource.Count < 3)
		{
			insetSource = outer;
		}
		bool[] sharpCornerFlags = BuildSharpCornerFlags(outer, insetSource);
		float minInnerEdgeLength = Mathf.Min(section.Width, section.Height) * 0.5f * 0.01f;

		if (!TryInsetLoop(insetSource, insetDistance, minInnerEdgeLength, out List<Vector2> cleanInner))
		{
			inner = new List<Vector2>();
			return false;
		}

		inner = cleanInner.Count == sharpCornerFlags.Length
			? DuplicateSharpLoopPoints(cleanInner, sharpCornerFlags)
			: cleanInner;
		return inner.Count >= 3 && IsInsetLoopInside(insetSource, cleanInner);
	}

	// 按原游戏 SimpleInset 的做法逐步内缩，并在边被压扁时合并点，避免厚空心非圆截面翻出外表面。
	// Inset like the game's SimpleInset: move points incrementally and merge collapsed edges instead of letting thick hollow sections invert.
	private static bool TryInsetLoop(List<Vector2> polygon, float inset, float minEdgeLength, out List<Vector2> result)
	{
		result = new List<Vector2>();
		if (polygon.Count < 3 || inset <= Epsilon)
		{
			result.AddRange(polygon);
			return result.Count >= 3;
		}

		List<Vector2> clockwise = EnsureClockwise(new List<Vector2>(polygon));
		RemoveNearDuplicateLoopPoints(clockwise);
		if (clockwise.Count < 3)
		{
			return false;
		}

		List<Vector2> points = new List<Vector2>(clockwise);
		float remaining = inset;
		const int maxIterations = 64;
		bool stopAtMinSize = false;
		for (int iteration = 0; iteration < maxIterations && remaining > Epsilon && points.Count >= 3; iteration++)
		{
			MergeTinyEdges(points);
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

			if (minEdgeLength > Epsilon && maxEdgeLimit > Epsilon)
			{
				float minSizeStep = maxEdgeLimit - minEdgeLength;
				if (minSizeStep <= Epsilon)
				{
					break;
				}

				if (step > minSizeStep - Epsilon)
				{
					step = minSizeStep;
					edgesToMerge.Clear();
					stopAtMinSize = true;
				}
			}

			if (step <= Epsilon)
			{
				if (edgesToMerge.Count == 0)
				{
					break;
				}

				MergeEdges(points, edgesToMerge);
				continue;
			}

			for (int i = 0; i < count; i++)
			{
				points[i] += velocity[i] * step;
			}
			remaining -= step;

			if (edgesToMerge.Count > 0)
			{
				MergeEdges(points, edgesToMerge);
			}

			RemoveNearDuplicateLoopPoints(points);
			if (stopAtMinSize)
			{
				break;
			}
		}

		RemoveNearDuplicateLoopPoints(points);
		result = EnsureClockwise(points);
		return result.Count >= 3 && IsInsetLoopInside(clockwise, result);
	}

	private static float ComputePointShrinkage(Vector2 inVec, Vector2 outVec)
	{
		Vector2 inDir = inVec.normalized;
		Vector2 outDir = outVec.normalized;
		Vector2 inNormal = RotateClockwise(inDir);
		Vector2 outNormal = RotateClockwise(outDir);
		float denominator = Vector2.Dot(inNormal, outDir);
		if (Mathf.Abs(denominator) <= Epsilon)
		{
			return 0f;
		}

		return Vector2.Dot(inNormal - outNormal, inNormal) / denominator;
	}

	private static Vector2 ComputePointVelocity(float pointShrinkage, Vector2 inVec)
	{
		Vector2 inDir = inVec.normalized;
		return RotateClockwise(inDir) - inDir * pointShrinkage;
	}

	private static Vector2 RotateClockwise(Vector2 value)
	{
		return new Vector2(value.y, -value.x);
	}

	private static bool IsInsetLoopInside(List<Vector2> outer, List<Vector2> inner)
	{
		if (outer == null || inner == null || outer.Count < 3 || inner.Count < 3)
		{
			return false;
		}

		List<Vector2> clockwiseOuter = EnsureClockwise(new List<Vector2>(outer));
		for (int i = 0; i < inner.Count; i++)
		{
			if (!IsInsideClockwiseConvex(clockwiseOuter, inner[i]))
			{
				return false;
			}
		}

		return Mathf.Abs(SignedArea(inner)) > Epsilon * Epsilon;
	}

	private static bool IsInsideClockwiseConvex(List<Vector2> clockwise, Vector2 point)
	{
		const float tolerance = 0.001f;
		for (int i = 0; i < clockwise.Count; i++)
		{
			Vector2 a = clockwise[i];
			Vector2 b = clockwise[(i + 1) % clockwise.Count];
			if (Cross(b - a, point - a) > tolerance)
			{
				return false;
			}
		}

		return true;
	}

	private static void MergeTinyEdges(List<Vector2> points)
	{
		for (int i = 0; i < points.Count && points.Count >= 3; i++)
		{
			int next = (i + 1) % points.Count;
			if ((points[i] - points[next]).sqrMagnitude > Epsilon * Epsilon)
			{
				continue;
			}

			RemoveEdgeNextPoint(points, i);
			i = Mathf.Max(-1, i - 1);
		}
	}

	private static void MergeEdges(List<Vector2> points, List<int> edgesToMerge)
	{
		for (int i = 0; i < edgesToMerge.Count && points.Count >= 3; i++)
		{
			int edge = Mathf.Clamp(edgesToMerge[i] - i, 0, points.Count - 1);
			RemoveEdgeNextPoint(points, edge);
		}
	}

	private static void RemoveEdgeNextPoint(List<Vector2> points, int edge)
	{
		if (points.Count < 3)
		{
			return;
		}

		int clampedEdge = Mathf.Clamp(edge, 0, points.Count - 1);
		int next = clampedEdge + 1;
		points.RemoveAt(next >= points.Count ? points.Count - 1 : next);
	}

	// 把多边形裁到当前 loft slice 的插值 cut 边界内。 / Clip a polygon against the interpolated cut bounds for one loft slice.
	private static List<Vector2> ClipSection(List<Vector2> polygon, ClipBounds bounds)
	{
		if (polygon.Count < 3)
		{
			return polygon;
		}

		List<Vector2> clipped = new List<Vector2>(polygon);
		clipped = ClipPolygon(clipped, point => point.y - bounds.MaxY);
		clipped = ClipPolygon(clipped, point => point.x - bounds.MaxX);
		clipped = ClipPolygon(clipped, point => bounds.MinY - point.y);
		clipped = ClipPolygon(clipped, point => bounds.MinX - point.x);
		return clipped;
	}

	// 对一个半空间执行一次 Sutherland-Hodgman 裁切。 / Perform a single Sutherland-Hodgman clipping pass against one half-space.
	private static List<Vector2> ClipPolygon(List<Vector2> polygon, Func<Vector2, float> signedDistance)
	{
		if (polygon.Count == 0)
		{
			return polygon;
		}

		List<Vector2> result = new List<Vector2>(polygon.Count + 4);
		for (int i = 0; i < polygon.Count; i++)
		{
			Vector2 previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
			Vector2 current = polygon[i];
			float previousDistance = signedDistance(previous);
			float currentDistance = signedDistance(current);
			bool previousInside = previousDistance <= Epsilon;
			bool currentInside = currentDistance <= Epsilon;
			if (previousInside != currentInside)
			{
				float t = previousDistance / (previousDistance - currentDistance);
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

	// 采样一个 stretched 或 rounded 的 corner 圆弧，并返回位置与切线。 / Sample a stretched or rounded corner arc and return both position and tangent.
	private static CornerSample SampleCorner(int cornerIndex, float t, FuselageSectionSettings section, float clampedRadius, Vector2 cornerCenter, float cornerStartAngle, float cornerAngle, Vector2 cornerScale)
	{
		float angle = cornerStartAngle + t * cornerAngle;
		Vector2 circle = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
		Vector2 tangent = Vector2.Scale(new Vector2(circle.y, -circle.x), cornerScale).normalized;
		Vector2 position = Vector2.Scale(cornerCenter + circle * clampedRadius, cornerScale);
		return new CornerSample(position, tangent);
	}

	// 求出同时匹配两端切线的二次 Bezier 控制点。 / Find the quadratic Bezier control point that matches the corner tangents on both ends.
	private static Vector2 GetCurvedEdgeControl(CornerSample start, CornerSample end)
	{
		Vector2 normal = new Vector2(end.Tangent.y, -end.Tangent.x);
		float dot = Vector2.Dot(start.Tangent, normal);
		if (Mathf.Abs(dot) <= Epsilon)
		{
			return 0.5f * (start.Position + end.Position);
		}
		float distance = Vector2.Dot(end.Position - start.Position, normal) / dot;
		return start.Position + start.Tangent * distance;
	}

	// 计算二次 Bezier 在参数 t 处的点，用于弯边采样。 / Evaluate a quadratic Bezier point for curved edge sampling.
	private static Vector2 EvaluateQuadratic(Vector2 a, Vector2 b, Vector2 c, float t)
	{
		float omt = 1f - t;
		return omt * omt * a + 2f * omt * t * b + t * t * c;
	}

	// 计算 stretch 生效后采样单个 corner 圆弧所需的几何参数。 / Compute the geometric parameters needed to sample one corner arc after stretch is applied.
	private static void GetCurveParams(int index, Vector2[] unscaledCorners, FuselageSectionSettings section, float radius, out Vector2 center, out float angle, out float startAngle, out float endAngle, out Vector2 postScale, out bool startMaxed, out bool endMaxed)
	{
		float stretch = section.GetCornerStretchAmount(index);
		Vector2 stretchScale = Vector2.Lerp(section.HalfSize, Vector2.one, stretch);
		Vector2 previous = Vector2.Scale(stretchScale, unscaledCorners[(index + 3) % 4]);
		Vector2 current = Vector2.Scale(stretchScale, unscaledCorners[index]);
		Vector2 next = Vector2.Scale(stretchScale, unscaledCorners[(index + 1) % 4]);
		Vector2 inEdge = current - previous;
		Vector2 outEdge = next - current;
		float inAngle = Mathf.Atan2(inEdge.x, inEdge.y);
		float outAngle = Mathf.Atan2(outEdge.x, outEdge.y);
		angle = Mathf.Repeat(outAngle - inAngle, Mathf.PI * 2f);
		startAngle = Mathf.Repeat(inAngle + Mathf.PI * 1.5f, Mathf.PI * 2f);
		endAngle = Mathf.Repeat(outAngle + Mathf.PI * 1.5f, Mathf.PI * 2f);
		postScale = new Vector2(
			Mathf.Approximately(stretchScale.x, 0f) ? 1f : section.HalfSize.x / stretchScale.x,
			Mathf.Approximately(stretchScale.y, 0f) ? 1f : section.HalfSize.y / stretchScale.y);
		float innerAngle = Mathf.PI - angle;
		float tangentDistance = radius / Mathf.Tan(innerAngle * 0.5f);
		startMaxed = tangentDistance >= inEdge.magnitude * 0.4999f;
		endMaxed = tangentDistance >= outEdge.magnitude * 0.4999f;
		float centerDistance = radius / Mathf.Sin(innerAngle * 0.5f);
		float bisectorAngle = outAngle + innerAngle * 0.5f;
		center = current + centerDistance * new Vector2(Mathf.Sin(bisectorAngle), Mathf.Cos(bisectorAngle));
	}

	// 构建施加 trapezium 后的 corner 方向和缩放角点。 / Build the trapezium-adjusted corner directions and scaled corner points for a section.
	private static void GetOutlineShape(Vector2[] unscaledCorners, Vector2[] corners, FuselageSectionSettings section)
	{
		Vector2[] baseCorners =
		{
			new Vector2(1f, 1f),
			new Vector2(1f, -1f),
			new Vector2(-1f, -1f),
			new Vector2(-1f, 1f)
		};

		for (int i = 0; i < 4; i++)
		{
			Vector2 point = baseCorners[i];
			point.x *= 1f + point.y * section.Trapezium;
			unscaledCorners[i] = point;
			corners[i] = Vector2.Scale(point, section.HalfSize);
		}
	}

	// 对一个归一化点施加当前截面的 trapezium 倾斜。 / Apply the section's trapezium skew to one normalized corner or fallback point.
	private static Vector2 ApplyTrapezium(Vector2 normalized, FuselageSectionSettings section)
	{
		normalized.x *= 1f + normalized.y * section.Trapezium;
		return Vector2.Scale(normalized, section.HalfSize);
	}

	private static void AddRing(List<Vector3> vertices, RingProfile ring)
	{
		ring.MeshIndicesIn = new int[ring.Points.Count];
		ring.MeshIndicesOut = new int[ring.Points.Count];
		for (int i = 0; i < ring.Points.Count; i++)
		{
			int inIndex = vertices.Count;
			vertices.Add(ring.Center + new Vector3(ring.Points[i].x, ring.Points[i].y, 0f));
			int outIndex = inIndex;
			if (ring.Sharp[i])
			{
				outIndex = vertices.Count;
				vertices.Add(ring.Center + new Vector3(ring.Points[i].x, ring.Points[i].y, 0f));
			}

			ring.MeshIndicesIn[i] = inIndex;
			ring.MeshIndicesOut[i] = outIndex;
		}
	}

	private static void ConnectRings(List<int> triangles, RingProfile ringA, RingProfile ringB, bool reverseQuads)
	{
		if (ringA.Count < 2 || ringB.Count < 2)
		{
			return;
		}

		int[] linksA = FindClosestLinks(ringA.Fractions, ringB.Fractions);
		int[] linksB = FindClosestLinks(ringB.Fractions, ringA.Fractions);
		int a = 0;
		int b = linksA[0];
		int walkedA = 0;
		int walkedB = 0;
		int stepLimit = ringA.Count + ringB.Count + 4;
		int steps = 0;

		while ((walkedA < ringA.Count || walkedB < ringB.Count) && steps++ < stepLimit)
		{
			int nextA = (a + 1) % ringA.Count;
			int nextB = (b + 1) % ringB.Count;

			if (walkedA < ringA.Count && (walkedB == ringB.Count || linksB[b] == nextA || linksA[nextA] == b))
			{
				bool useOutB = walkedB == ringB.Count || IsAfter(nextA, linksB[b], ringA.Count);
				AddRingTriangle(triangles, ringA, a, true, ringB, b, useOutB, ringA, nextA, false);
				a = nextA;
				walkedA++;
				continue;
			}

			if (walkedB < ringB.Count && (walkedA == ringA.Count || linksA[a] == nextB || linksB[nextB] == a))
			{
				bool useOutA = walkedA == ringA.Count || IsAfter(nextB, linksA[a], ringB.Count);
				AddRingTriangle(triangles, ringA, a, useOutA, ringB, b, true, ringB, nextB, false);
				b = nextB;
				walkedB++;
				continue;
			}

			if (reverseQuads)
			{
				AddRingTriangle(triangles, ringA, a, true, ringB, b, true, ringB, nextB, false);
				AddRingTriangle(triangles, ringA, a, true, ringB, nextB, false, ringA, nextA, false);
			}
			else
			{
				AddRingTriangle(triangles, ringA, a, true, ringB, b, true, ringA, nextA, false);
				AddRingTriangle(triangles, ringA, nextA, false, ringB, b, true, ringB, nextB, false);
			}
			a = nextA;
			b = nextB;
			walkedA++;
			walkedB++;
		}
	}

	private static bool IsAfter(int test, int against, int length)
	{
		int forward = (test - against + length) % length;
		int backward = (against - test + length) % length;
		return forward < backward;
	}

	private static void AddRingTriangle(
		List<int> triangles,
		RingProfile ringA,
		int pointA,
		bool outA,
		RingProfile ringB,
		int pointB,
		bool outB,
		RingProfile ringC,
		int pointC,
		bool outC,
		bool flip = false)
	{
		if (!TryGetRingVertex(ringA, pointA, out Vector3 va)
			|| !TryGetRingVertex(ringB, pointB, out Vector3 vb)
			|| !TryGetRingVertex(ringC, pointC, out Vector3 vc))
		{
			return;
		}

		if (Vector3.Cross(vb - va, vc - va).sqrMagnitude <= Epsilon * Epsilon)
		{
			return;
		}

		int ia = outA ? ringA.GetOutIndex(pointA) : ringA.GetInIndex(pointA);
		int ib = outB ? ringB.GetOutIndex(pointB) : ringB.GetInIndex(pointB);
		int ic = outC ? ringC.GetOutIndex(pointC) : ringC.GetInIndex(pointC);
		if (ia < 0 || ib < 0 || ic < 0)
		{
			return;
		}

		if (flip)
		{
			triangles.Add(ia);
			triangles.Add(ic);
			triangles.Add(ib);
		}
		else
		{
			triangles.Add(ia);
			triangles.Add(ib);
			triangles.Add(ic);
		}
	}

	private static bool TryGetRingVertex(RingProfile ring, int index, out Vector3 vertex)
	{
		if (ring == null || index < 0 || index >= ring.Count)
		{
			vertex = Vector3.zero;
			return false;
		}

		Vector2 point = ring.Points[index];
		vertex = ring.Center + new Vector3(point.x, point.y, 0f);
		return true;
	}

	private static void ConnectRim(List<int> triangles, RingProfile outer, RingProfile inner, bool flip)
	{
		if (outer.Count != inner.Count)
		{
			ConnectLoopsGeneral(triangles, outer, inner, flip);
			return;
		}

		for (int i = 0; i < outer.Count; i++)
		{
			int next = (i + 1) % outer.Count;
			if (flip)
			{
				AddRingTriangle(triangles, outer, i, false, inner, i, false, inner, next, false);
				AddRingTriangle(triangles, outer, i, false, inner, next, false, outer, next, false);
			}
			else
			{
				AddRingTriangle(triangles, outer, i, false, inner, next, false, inner, i, false);
				AddRingTriangle(triangles, outer, i, false, outer, next, false, inner, next, false);
			}
		}
	}

	private static void ConnectLoopsGeneral(List<int> triangles, RingProfile outer, RingProfile inner, bool flip)
	{
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
				if (flip)
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, innerIndex, false, outer, nextOuter, false);
				}
				else
				{
					AddRingTriangle(triangles, outer, outerIndex, false, outer, nextOuter, false, inner, innerIndex, false);
				}
				outerIndex = nextOuter;
				walkedOuter++;
				continue;
			}
			if (advanceInner && !advanceOuter)
			{
				if (flip)
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, innerIndex, false, inner, nextInner, false);
				}
				else
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, nextInner, false, inner, innerIndex, false);
				}
				innerIndex = nextInner;
				walkedInner++;
				continue;
			}
			if (walkedOuter == outer.Count)
			{
				if (flip)
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, innerIndex, false, inner, nextInner, false);
				}
				else
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, nextInner, false, inner, innerIndex, false);
				}
				innerIndex = nextInner;
				walkedInner++;
				continue;
			}
			if (walkedInner == inner.Count)
			{
				if (flip)
				{
					AddRingTriangle(triangles, outer, outerIndex, false, inner, innerIndex, false, outer, nextOuter, false);
				}
				else
				{
					AddRingTriangle(triangles, outer, outerIndex, false, outer, nextOuter, false, inner, innerIndex, false);
				}
				outerIndex = nextOuter;
				walkedOuter++;
				continue;
			}
			if (flip)
			{
				AddRingTriangle(triangles, outer, outerIndex, false, inner, innerIndex, false, inner, nextInner, false);
				AddRingTriangle(triangles, outer, outerIndex, false, inner, nextInner, false, outer, nextOuter, false);
			}
			else
			{
				AddRingTriangle(triangles, outer, outerIndex, false, inner, nextInner, false, inner, innerIndex, false);
				AddRingTriangle(triangles, outer, outerIndex, false, outer, nextOuter, false, inner, nextInner, false);
			}
			outerIndex = nextOuter;
			innerIndex = nextInner;
			walkedOuter++;
			walkedInner++;
		}
	}

	private static void ApplyLoftNormals(List<Vector3> normals, IReadOnlyList<RingProfile> rings, bool invert, Vector3 fallbackAxis)
	{
		if (rings == null || rings.Count == 0)
		{
			return;
		}

		int[][] previousLinks = new int[rings.Count][];
		int[][] nextLinks = new int[rings.Count][];
		for (int ringIndex = 1; ringIndex < rings.Count; ringIndex++)
		{
			previousLinks[ringIndex] = FindClosestLinks(rings[ringIndex].Fractions, rings[ringIndex - 1].Fractions);
		}
		for (int ringIndex = 0; ringIndex < rings.Count - 1; ringIndex++)
		{
			nextLinks[ringIndex] = FindClosestLinks(rings[ringIndex].Fractions, rings[ringIndex + 1].Fractions);
		}

		for (int ringIndex = 0; ringIndex < rings.Count; ringIndex++)
		{
			RingProfile ring = rings[ringIndex];
			Vector3 ringAxis = Vector3.zero;
			if (ringIndex > 0)
			{
				ringAxis += ring.Center - rings[ringIndex - 1].Center;
			}
			if (ringIndex < rings.Count - 1)
			{
				ringAxis += rings[ringIndex + 1].Center - ring.Center;
			}
			if (ringAxis.sqrMagnitude <= Epsilon)
			{
				ringAxis = fallbackAxis;
			}

			for (int pointIndex = 0; pointIndex < ring.Count; pointIndex++)
			{
				Vector3 current = GetRingVertex(ring, pointIndex);
				Vector3 loftDirection = Vector3.zero;
				if (previousLinks[ringIndex] != null)
				{
					loftDirection += current - GetRingVertex(rings[ringIndex - 1], previousLinks[ringIndex][pointIndex]);
				}
				if (nextLinks[ringIndex] != null)
				{
					loftDirection += GetRingVertex(rings[ringIndex + 1], nextLinks[ringIndex][pointIndex]) - current;
				}
				if (loftDirection.sqrMagnitude <= Epsilon)
				{
					loftDirection = ringAxis;
				}

				Vector3 normalIn = ComputeLoftNormal(ring, pointIndex, loftDirection, useOutTangent: false, invert);
				int inIndex = ring.GetInIndex(pointIndex);
				if (inIndex >= 0 && inIndex < normals.Count)
				{
					normals[inIndex] = normalIn;
				}

				if (ring.Sharp[pointIndex])
				{
					Vector3 normalOut = ComputeLoftNormal(ring, pointIndex, loftDirection, useOutTangent: true, invert);
					int outIndex = ring.GetOutIndex(pointIndex);
					if (outIndex >= 0 && outIndex < normals.Count)
					{
						normals[outIndex] = normalOut;
					}
				}
			}
		}
	}

	private static Vector3 ComputeLoftNormal(RingProfile ring, int pointIndex, Vector3 loftDirection, bool useOutTangent, bool invert)
	{
		Vector3 current = GetRingVertex(ring, pointIndex);
		Vector3 tangent = GetRingTangent(ring, pointIndex, useOutTangent);
		Vector3 normal = Vector3.Cross(loftDirection.normalized, tangent.normalized);
		if (normal.sqrMagnitude <= Epsilon)
		{
			normal = current - ring.Center;
		}
		if (invert)
		{
			normal = -normal;
		}

		return normal.sqrMagnitude <= Epsilon ? Vector3.up : normal.normalized;
	}

	private static Vector3 GetRingVertex(RingProfile ring, int index)
	{
		Vector2 point = ring.Points[index];
		return ring.Center + new Vector3(point.x, point.y, 0f);
	}

	private static Vector3 GetRingTangent(RingProfile ring, int index, bool useOutTangent = false)
	{
		List<Vector2> tangents = useOutTangent ? ring.OutTangents : ring.InTangents;
		if (tangents != null && tangents.Count == ring.Count)
		{
			Vector2 tangent = tangents[index];
			if (tangent.sqrMagnitude > Epsilon)
			{
				tangent.Normalize();
				return new Vector3(tangent.x, tangent.y, 0f);
			}
		}

		return ComputeRingTangent(ring, index);
	}

	private static Vector3 ComputeRingTangent(RingProfile ring, int index)
	{
		Vector2 previous = ring.Points[(index - 1 + ring.Count) % ring.Count];
		Vector2 current = ring.Points[index];
		Vector2 next = ring.Points[(index + 1) % ring.Count];
		Vector2 tangent = next - previous;
		if (tangent.sqrMagnitude <= Epsilon)
		{
			tangent = next - current;
		}
		if (tangent.sqrMagnitude <= Epsilon)
		{
			tangent = current - previous;
		}
		tangent.Normalize();
		return new Vector3(tangent.x, tangent.y, 0f);
	}

	private static void CapRingFlat(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, RingProfile ring, Vector3 normal, bool flip)
	{
		List<Vector2> capPoints = BuildCapLoopPoints(ring.Points);
		if (capPoints.Count < 3)
		{
			return;
		}

		Vector2 centroid2D = ComputePolygonCentroid(capPoints);
		int centerIndex = vertices.Count;
		vertices.Add(ring.Center + new Vector3(centroid2D.x, centroid2D.y, 0f));
		normals.Add(normal.normalized);
		int start = vertices.Count;
		for (int i = 0; i < capPoints.Count; i++)
		{
			vertices.Add(ring.Center + new Vector3(capPoints[i].x, capPoints[i].y, 0f));
			normals.Add(normal.normalized);
		}
		for (int i = 0; i < capPoints.Count; i++)
		{
			int next = (i + 1) % capPoints.Count;
			if (flip)
			{
				triangles.Add(centerIndex); triangles.Add(start + next); triangles.Add(start + i);
			}
			else
			{
				triangles.Add(centerIndex); triangles.Add(start + i); triangles.Add(start + next);
			}
		}
	}

	private static void CapRimFlat(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, RingProfile outer, RingProfile inner, Vector3 normal, bool flip)
	{
		RingProfile outerCap = DuplicateRing(vertices, normals, outer, normal);
		RingProfile innerCap = DuplicateRing(vertices, normals, inner, normal);
		ConnectRim(triangles, outerCap, innerCap, flip);
	}

	private static RingProfile DuplicateRing(List<Vector3> vertices, List<Vector3> normals, RingProfile source, Vector3 normal)
	{
		List<Vector2> capPoints = BuildCapLoopPoints(source.Points);
		if (capPoints.Count < 3)
		{
			capPoints = source.Points;
		}

		RingProfile duplicate = new RingProfile(capPoints, source.Center);
		int firstVertex = vertices.Count;
		AddRing(vertices, duplicate);
		for (int i = firstVertex; i < vertices.Count; i++)
		{
			normals.Add(normal.normalized);
		}
		return duplicate;
	}

	private static List<Vector2> BuildCapLoopPoints(List<Vector2> source)
	{
		List<Vector2> result = new List<Vector2>(source.Count);
		for (int i = 0; i < source.Count; i++)
		{
			if (result.Count > 0 && Vector2.Distance(result[^1], source[i]) <= Epsilon)
			{
				continue;
			}

			result.Add(source[i]);
		}

		if (result.Count > 1 && Vector2.Distance(result[0], result[^1]) <= Epsilon)
		{
			result.RemoveAt(result.Count - 1);
		}

		return result;
	}

	private static bool[] BuildSharpCornerFlags(List<Vector2> source, List<Vector2> clean)
	{
		bool[] flags = new bool[clean.Count];
		for (int cleanIndex = 0; cleanIndex < clean.Count; cleanIndex++)
		{
			for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
			{
				if (Vector2.Distance(clean[cleanIndex], source[sourceIndex]) > Epsilon)
				{
					continue;
				}

				Vector2 previous = source[(sourceIndex - 1 + source.Count) % source.Count];
				Vector2 next = source[(sourceIndex + 1) % source.Count];
				if (Vector2.Distance(source[sourceIndex], previous) <= Epsilon || Vector2.Distance(source[sourceIndex], next) <= Epsilon)
				{
					flags[cleanIndex] = true;
					break;
				}
			}
		}

		return flags;
	}

	private static List<Vector2> DuplicateSharpLoopPoints(List<Vector2> points, bool[] sharpCornerFlags)
	{
		if (points == null || sharpCornerFlags == null || points.Count != sharpCornerFlags.Length)
		{
			return points;
		}

		List<Vector2> result = new List<Vector2>(points.Count * 2);
		for (int i = 0; i < points.Count; i++)
		{
			result.Add(points[i]);
			if (sharpCornerFlags[i])
			{
				result.Add(points[i]);
			}
		}

		return result;
	}

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

	private static float CircularDistance(float a, float b)
	{
		float delta = Mathf.Abs(a - b);
		return Mathf.Min(delta, 1f - delta);
	}

	private static List<float> ComputeFractions(List<Vector2> points)
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

	private static Vector2 ComputeAverage(List<Vector2> points)
	{
		Vector2 sum = Vector2.zero;
		for (int i = 0; i < points.Count; i++)
		{
			sum += points[i];
		}
		return sum / Mathf.Max(1, points.Count);
	}

	private static Vector2 ComputePolygonCentroid(List<Vector2> points)
	{
		float twiceArea = 0f;
		float centroidX = 0f;
		float centroidY = 0f;
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			float cross = current.x * next.y - next.x * current.y;
			twiceArea += cross;
			centroidX += (current.x + next.x) * cross;
			centroidY += (current.y + next.y) * cross;
		}
		if (Mathf.Abs(twiceArea) <= Epsilon)
		{
			return ComputeAverage(points);
		}
		float factor = 1f / (3f * twiceArea);
		return new Vector2(centroidX * factor, centroidY * factor);
	}

	private static float SignedArea(List<Vector2> points)
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

	private static List<Vector2> EnsureClockwise(List<Vector2> points, List<Vector2> tangents)
	{
		if (points.Count >= 3 && SignedArea(points) > 0f)
		{
			points.Reverse();
			if (tangents != null && tangents.Count == points.Count)
			{
				tangents.Reverse();
				for (int i = 0; i < tangents.Count; i++)
				{
					tangents[i] = -tangents[i];
				}
			}
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

	private static void RemoveNearDuplicateLoopPoints(List<Vector2> points, List<Vector2> tangents)
	{
		for (int i = points.Count - 1; i >= 0; i--)
		{
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			if (Vector2.Distance(current, next) <= Epsilon)
			{
				points.RemoveAt(i);
				if (tangents != null && tangents.Count > i)
				{
					tangents.RemoveAt(i);
				}
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

	private static void AddUnique(List<Vector2> points, List<Vector2> tangents, Vector2 point, Vector2 tangent)
	{
		if (points.Count > 0 && Vector2.Distance(points[^1], point) <= Epsilon)
		{
			return;
		}
		points.Add(point);
		tangents?.Add(tangent.sqrMagnitude <= Epsilon ? Vector2.right : tangent.normalized);
	}

	private static void AddSharpCorner(List<Vector2> points, List<Vector2> tangents, Vector2 point, Vector2 incomingTangent, Vector2 outgoingTangent)
	{
		if (points.Count == 0 || Vector2.Distance(points[^1], point) > Epsilon)
		{
			points.Add(point);
			tangents?.Add(incomingTangent.sqrMagnitude <= Epsilon ? Vector2.right : incomingTangent.normalized);
		}

		points.Add(point);
		tangents?.Add(outgoingTangent.sqrMagnitude <= Epsilon ? Vector2.right : outgoingTangent.normalized);
	}

	private static List<Vector2> BuildLoopTangents(List<Vector2> points)
	{
		List<Vector2> tangents = new List<Vector2>(points.Count);
		for (int i = 0; i < points.Count; i++)
		{
			Vector2 previous = points[(i - 1 + points.Count) % points.Count];
			Vector2 current = points[i];
			Vector2 next = points[(i + 1) % points.Count];
			Vector2 tangent = next - previous;
			if (tangent.sqrMagnitude <= Epsilon)
			{
				tangent = next - current;
			}
			if (tangent.sqrMagnitude <= Epsilon)
			{
				tangent = current - previous;
			}
			tangents.Add(tangent.sqrMagnitude <= Epsilon ? Vector2.right : tangent.normalized);
		}
		return tangents;
	}

	private static bool HasSectionCutting(FuselageSectionSettings section)
	{
		return section.GetCutEnabled(0)
			|| section.GetCutEnabled(1)
			|| section.GetCutEnabled(2)
			|| section.GetCutEnabled(3);
	}

	// 复刻原版 minSlicing：用真实轮廓相对名义 Size 的外扩量计算每边最小 cut 值。 / Reproduce original minSlicing by measuring the real outline extents against the nominal section Size.
	internal static void GetCuttingRange(FuselageSectionSettings section, out Float4Value minCutting, out Float4Value maxCutting)
	{
		ClipBounds outlineBounds = GetOutlineBounds(section, Vector2.zero);
		float halfWidth = section.Width * 0.5f;
		float halfHeight = section.Height * 0.5f;

		float topMin = Mathf.Abs(section.Height) <= Epsilon ? 0f : (halfHeight - outlineBounds.MaxY) / section.Height;
		float rightMin = Mathf.Abs(section.Width) <= Epsilon ? 0f : (halfWidth - outlineBounds.MaxX) / section.Width;
		float bottomMin = Mathf.Abs(section.Height) <= Epsilon ? 0f : (outlineBounds.MinY + halfHeight) / section.Height;
		float leftMin = Mathf.Abs(section.Width) <= Epsilon ? 0f : (outlineBounds.MinX + halfWidth) / section.Width;

		if (!float.IsFinite(topMin))
		{
			topMin = 0f;
		}
		if (!float.IsFinite(rightMin))
		{
			rightMin = 0f;
		}
		if (!float.IsFinite(bottomMin))
		{
			bottomMin = 0f;
		}
		if (!float.IsFinite(leftMin))
		{
			leftMin = 0f;
		}

		minCutting = new Float4Value(topMin, rightMin, bottomMin, leftMin);
		maxCutting = new Float4Value(1f - bottomMin, 1f - leftMin, 1f - topMin, 1f - rightMin);
	}

	// 原版会先根据真实截面轮廓算出 minSlicing，再让 cutting 从轮廓最外缘开始推进；这里直接取当前轮廓外包围来复现同一语义。 / The original computes minSlicing from the actual section outline so cutting starts from the true outer silhouette; use the live outline bounds here to reproduce that behavior.
	private static ClipBounds GetOutlineBounds(FuselageSectionSettings section, Vector2 center)
	{
		List<Vector2> outline = BuildSectionOutline(section, null, out _);
		if (outline == null || outline.Count < 3)
		{
			outline = BuildFallbackOutline(section);
		}

		float minX = float.PositiveInfinity;
		float minY = float.PositiveInfinity;
		float maxX = float.NegativeInfinity;
		float maxY = float.NegativeInfinity;
		for (int i = 0; i < outline.Count; i++)
		{
			Vector2 point = outline[i] + center;
			minX = Mathf.Min(minX, point.x);
			minY = Mathf.Min(minY, point.y);
			maxX = Mathf.Max(maxX, point.x);
			maxY = Mathf.Max(maxY, point.y);
		}

		if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY))
		{
			return new ClipBounds(
				center.x - section.Width * 0.5f,
				center.y - section.Height * 0.5f,
				center.x + section.Width * 0.5f,
				center.y + section.Height * 0.5f);
		}

		return new ClipBounds(minX, minY, maxX, maxY);
	}

	private static ClipBounds GetCutBounds(FuselageSectionSettings section, Vector2 center)
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
			minX = midX - Epsilon;
			maxX = midX + Epsilon;
		}
		if (minY >= maxY)
		{
			float midY = 0.5f * (minY + maxY);
			minY = midY - Epsilon;
			maxY = midY + Epsilon;
		}

		return new ClipBounds(minX, minY, maxX, maxY);
	}

   // 根据前后两个截面的 cutting 参数构建真实 3D 切割体的四个侧面平面。 / Build the four side planes of the true 3D cutting volume from the rear and front section cutting parameters.
	private static bool TryBuildSectionCutPlanes(FuselageSectionSettings rear, FuselageSectionSettings front, Vector3 offset, out Plane[] planes)
	{
		planes = null;
		if (!HasSectionCutting(rear) && !HasSectionCutting(front))
		{
			return false;
		}

		Vector2 rearCenter = -new Vector2(offset.x, offset.y) * 0.5f;
		Vector2 frontCenter = new Vector2(offset.x, offset.y) * 0.5f;
		ClipBounds rearBounds = GetCutBounds(rear, rearCenter);
		ClipBounds frontBounds = GetCutBounds(front, frontCenter);
		float rearZ = -offset.z * 0.5f;
		float frontZ = offset.z * 0.5f;

		Vector3[] vertices =
		{
			new Vector3(rearBounds.MaxX, rearBounds.MaxY, rearZ),
			new Vector3(rearBounds.MaxX, rearBounds.MinY, rearZ),
			new Vector3(rearBounds.MinX, rearBounds.MinY, rearZ),
			new Vector3(rearBounds.MinX, rearBounds.MaxY, rearZ),
			new Vector3(frontBounds.MaxX, frontBounds.MaxY, frontZ),
			new Vector3(frontBounds.MaxX, frontBounds.MinY, frontZ),
			new Vector3(frontBounds.MinX, frontBounds.MinY, frontZ),
			new Vector3(frontBounds.MinX, frontBounds.MaxY, frontZ)
		};

		Vector3 insidePoint = Vector3.zero;
		for (int i = 0; i < vertices.Length; i++)
		{
			insidePoint += vertices[i];
		}
		insidePoint /= vertices.Length;

       List<Plane> cutPlanes = new List<Plane>(4);
		if (rear.GetCutEnabled(1) || front.GetCutEnabled(1))
		{
			cutPlanes.Add(CreateInwardFacingPlane(vertices[0], vertices[4], vertices[5], insidePoint));
		}
		if (rear.GetCutEnabled(2) || front.GetCutEnabled(2))
		{
			cutPlanes.Add(CreateInwardFacingPlane(vertices[1], vertices[5], vertices[6], insidePoint));
		}
		if (rear.GetCutEnabled(3) || front.GetCutEnabled(3))
		{
			cutPlanes.Add(CreateInwardFacingPlane(vertices[2], vertices[6], vertices[7], insidePoint));
		}
		if (rear.GetCutEnabled(0) || front.GetCutEnabled(0))
		{
			cutPlanes.Add(CreateInwardFacingPlane(vertices[3], vertices[7], vertices[4], insidePoint));
		}
		planes = cutPlanes.ToArray();
		return true;
	}

	// 生成一个朝向体积内部为负距离的平面，便于后续统一做 inside clipping。 / Create a plane whose interior evaluates to non-positive distance so inside clipping can use one consistent test.
	private static Plane CreateInwardFacingPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 insidePoint)
	{
		Plane plane = new Plane(a, b, c);
		if (plane.GetDistanceToPoint(insidePoint) > 0f)
		{
			plane = new Plane(-plane.normal, -plane.distance);
		}
		return plane;
	}

	// 用凸切割体对网格做真正的保留式裁切，得到真实切口而不是截面挤压。 / Intersect a mesh with a convex keep-volume to produce a true cut instead of squeezing the section profile.
	public static Mesh IntersectConvexVolume(Mesh source, Plane[] planes, string meshName)
	{
     if (source == null || planes == null || planes.Length == 0)
		{
			return source;
		}

		Mesh working = source;
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
		{
			// 按 keep-volume 的每个 inward-facing plane 逐次保留 negative side，并让 MeshPlaneSlicer 在每一步补 cap。 / Keep the negative side of each inward-facing plane in sequence and let MeshPlaneSlicer cap each cut.
			Mesh next = MeshTools.MeshPlaneSlicer.Cut(working, planes[planeIndex], keepPositive: false, cap: true);
			if (next == null)
			{
				break;
			}

			if (!ReferenceEquals(working, source) && !ReferenceEquals(next, working))
			{
				DestroyGeneratedMesh(working);
			}

			working = next;
		}

		if (!ReferenceEquals(working, source))
		{
			working.name = meshName;
		}

		return working;
	}

	public static PreviewMeshData IntersectConvexVolume(PreviewMeshData source, Plane[] planes, string meshName)
	{
     if (source == null || planes == null || planes.Length == 0)
		{
			return source;
		}

		PreviewMeshData working = source;
		for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
		{
			PreviewMeshData next = MeshTools.MeshPlaneSlicer.Cut(working, planes[planeIndex], keepPositive: false, cap: true);
			if (next == null)
			{
				break;
			}

			working = next;
		}

		if (working != null)
		{
			working.Name = meshName;
		}

		return working;
	}

	// 统一销毁运行期生成的中间 Mesh，避免顺序切平面时堆积临时网格。 / Destroy intermediate runtime-generated meshes consistently so sequential plane slicing does not leak temporary meshes.
	private static void DestroyGeneratedMesh(Mesh mesh)
	{
		if (mesh == null)
		{
			return;
		}

		UnityEngine.Object.DestroyImmediate(mesh);
	}

	public static Mesh SubtractConvexVolumes(Mesh source, IReadOnlyList<Plane[]> volumes, string meshName)
	{
		return MeshBooleanUtility.SubtractConvexVolumes(source, volumes, meshName);
	}

	private static void SubtractConvexPolygon(List<ClippedVertex> polygon, Plane[] planes, int planeIndex, List<List<ClippedVertex>> output)
	{
		if (polygon.Count < 3)
		{
			return;
		}
		if (planeIndex >= planes.Length)
		{
			return;
		}

		SplitPolygonByPlane(polygon, planes[planeIndex], out List<ClippedVertex> outside, out List<ClippedVertex> inside);
		if (outside.Count >= 3)
		{
			output.Add(outside);
		}
		if (inside.Count >= 3)
		{
			SubtractConvexPolygon(inside, planes, planeIndex + 1, output);
		}
	}

	private static void SplitPolygonByPlane(List<ClippedVertex> polygon, Plane plane, out List<ClippedVertex> outside, out List<ClippedVertex> inside)
	{
		outside = new List<ClippedVertex>(polygon.Count + 1);
		inside = new List<ClippedVertex>(polygon.Count + 1);
		for (int i = 0; i < polygon.Count; i++)
		{
			ClippedVertex previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
			ClippedVertex current = polygon[i];
			float previousDistance = plane.GetDistanceToPoint(previous.Position);
			float currentDistance = plane.GetDistanceToPoint(current.Position);
			bool previousOutside = previousDistance > Epsilon;
			bool currentOutside = currentDistance > Epsilon;

			if (previousOutside != currentOutside)
			{
				float t = previousDistance / (previousDistance - currentDistance);
				ClippedVertex intersection = new ClippedVertex(
					Vector3.Lerp(previous.Position, current.Position, Mathf.Clamp01(t)),
					Vector3.Lerp(previous.Normal, current.Normal, Mathf.Clamp01(t)).normalized);
				AddUnique(outside, intersection);
				AddUnique(inside, intersection);
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
	}

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

	private static void AddUnique(List<ClippedVertex> points, ClippedVertex point)
	{
		if (points.Count > 0 && Vector3.Distance(points[^1].Position, point.Position) <= Epsilon)
		{
			return;
		}
		points.Add(point);
	}

	private static void RemoveNearDuplicateLoopPoints(List<Vector3> points)
	{
		for (int i = points.Count - 1; i >= 0; i--)
		{
			Vector3 current = points[i];
			Vector3 next = points[(i + 1) % points.Count];
			if (Vector3.Distance(current, next) <= Epsilon)
			{
				points.RemoveAt(i);
			}
		}
	}

	private static void AddUnique(List<Vector3> points, Vector3 point)
	{
		if (points.Count > 0 && Vector3.Distance(points[^1], point) <= Epsilon)
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
