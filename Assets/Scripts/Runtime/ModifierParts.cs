using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Keep short face labels local to preview cutter helpers.")]
public interface IFuselageCarver
{
	bool TryGetCutMesh(FuselagePart target, out Mesh mesh);

	bool TryGetCutPlanes(FuselagePart target, out Plane[] planes);
}

internal interface IFuselageCarverData
{
	bool TryGetCutMeshData(FuselagePart target, out PreviewMeshData meshData, out Matrix4x4 cutterToTarget);
}

internal static class FuselageCarverUtility
{
	private const int CornerSamples = 10;

	public static Mesh BuildWireframeMesh(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		return MeshBooleanUtility.BuildExtrudedWireframe(outline, depth, meshName);
	}

	public static Mesh BuildSolidCutMesh(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		return MeshBooleanUtility.BuildExtrudedSolidMesh(outline, depth, meshName);
	}

	public static PreviewMeshData BuildSolidCutMeshData(IReadOnlyList<Vector2> outline, float depth, string meshName)
	{
		return MeshBooleanUtility.BuildExtrudedSolidMeshData(outline, depth, meshName);
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
		return MeshBooleanUtility.BuildExtrudedConvexPlanes(targetTransform, sourceTransform, outline, depth);
	}

	// 按原版 trapezoid window 语义构建圆角轮廓。 / Build the rounded outline for a trapezoid window using the original semantics.
	public static List<Vector2> BuildWindowOutline(Vector2 upperSpan, Vector2 lowerSpan, float height, float cornerRadius)
	{
		return MeshBooleanUtility.BuildRoundedTrapezoidOutline(upperSpan, lowerSpan, height, cornerRadius, CornerSamples);
	}

	// 按原版 simple bay 语义构建圆角矩形轮廓。 / Build the rounded rectangle outline for a simple bay using the original semantics.
	public static List<Vector2> BuildBayOutline(float width, float height, float cornerRadius)
	{
		float halfWidth = Mathf.Max(0.01f, width) * 0.5f;
		return BuildWindowOutline(new Vector2(-halfWidth, halfWidth), new Vector2(-halfWidth, halfWidth), height, cornerRadius);
	}
}
