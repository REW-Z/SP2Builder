using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

internal class MeshProcessJob
{
	public Matrix4x4 worldMatrix;

	public FuselageSectionSettings rearSection;
	public FuselageSectionSettings frontSection;
	public Vector3 offset;
	public bool capRear;
	public bool capFront;
	public bool applySectionCutting;
	public bool hollow;
	public bool cone;
	public float noseconeRoundness;
	public int version;

	//所有的裁切体网格数据
	public List<GeneratedMeshData> cutterMeshList;
	public List<Matrix4x4> cutterMeshMatrixList;

	//计算结果
	public GeneratedMeshData resultMeshData;
	public Exception error;
}

internal sealed class GeneratedMeshData
{
	public string Name;
	public List<Vector3> Vertices;
	public List<Vector3> Normals;
	public List<int> Triangles;

	public GeneratedMeshData(string name)
	{
		Name = string.IsNullOrWhiteSpace(name) ? "PreviewMesh" : name;
		Vertices = new List<Vector3>();
		Normals = new List<Vector3>();
		Triangles = new List<int>();
	}

	public GeneratedMeshData(string name, IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3> normals, IReadOnlyList<int> triangles)
	{
		Name = string.IsNullOrWhiteSpace(name) ? "PreviewMesh" : name;
		Vertices = vertices is List<Vector3> vertexList ? vertexList : new List<Vector3>(vertices ?? Array.Empty<Vector3>());
		Normals = normals is List<Vector3> normalList ? normalList : new List<Vector3>(normals ?? Array.Empty<Vector3>());
		Triangles = triangles is List<int> triangleList ? triangleList : new List<int>(triangles ?? Array.Empty<int>());
	}

	public int VertexCount => Vertices?.Count ?? 0;

	// 只在主线程调用，把托管网格数据提交给 Unity Mesh。 / Main-thread only: upload managed mesh data into a Unity Mesh.
	public Mesh ToMesh()
	{
		Mesh mesh = new Mesh
		{
			name = string.IsNullOrWhiteSpace(Name) ? "PreviewMesh" : Name
		};
		if (Vertices == null || Vertices.Count == 0)
		{
			return mesh;
		}

		mesh.indexFormat = Vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
		mesh.SetVertices(Vertices);
		if (Normals != null && Normals.Count == Vertices.Count)
		{
			mesh.SetNormals(Normals);
		}
		mesh.SetTriangles(Triangles ?? new List<int>(), 0, true);
		mesh.RecalculateBounds();
		return mesh;
	}

	// 只在主线程调用，把临时 Unity Mesh 拍平成后台可读的托管数据。 / Main-thread only: snapshot a temporary Unity Mesh into managed data for background use.
	public static GeneratedMeshData FromMesh(Mesh mesh)
	{
		if (mesh == null)
		{
			return null;
		}

		List<Vector3> vertices = new List<Vector3>(mesh.vertexCount);
		List<Vector3> normals = new List<Vector3>(mesh.vertexCount);
		List<int> triangles = new List<int>();
		mesh.GetVertices(vertices);
		mesh.GetNormals(normals);
		mesh.GetTriangles(triangles, 0);
		return new GeneratedMeshData(mesh.name, vertices, normals, triangles);
	}
}
