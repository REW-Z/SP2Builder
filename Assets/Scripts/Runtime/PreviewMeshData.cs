using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;


internal sealed class PreviewMeshData
{
	public string Name;

	public readonly List<Vector3> Vertices = new List<Vector3>();

	public readonly List<Vector3> Normals = new List<Vector3>();

	public readonly List<List<int>> SubMeshTriangles = new List<List<int>>();

	public PreviewMeshData(string name = "PreviewMesh")
	{
		Name = name;
		SubMeshTriangles.Add(new List<int>());
	}

	public PreviewMeshData(string name, IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3> normals, IReadOnlyList<int> triangles)
		: this(name)
	{
		Vertices.AddRange(vertices);
		if (normals != null && normals.Count == vertices.Count)
		{
			Normals.AddRange(normals);
		}
		else
		{
			for (int i = 0; i < vertices.Count; i++)
			{
				Normals.Add(Vector3.up);
			}
		}
		SubMeshTriangles[0].AddRange(triangles);
	}

	public Mesh ToMesh()
	{
		Mesh mesh = new Mesh();
		CopyToMesh(mesh);
		return mesh;
	}

	public void CopyToMesh(Mesh mesh)
	{
		if (mesh == null)
		{
			return;
		}

		mesh.Clear(false);
		mesh.name = string.IsNullOrWhiteSpace(Name) ? "PreviewMesh" : Name;
		if (Vertices.Count > 65535)
		{
			mesh.indexFormat = IndexFormat.UInt32;
		}
		else
		{
			mesh.indexFormat = IndexFormat.UInt16;
		}

		mesh.SetVertices(Vertices);
		if (Normals.Count == Vertices.Count)
		{
			mesh.SetNormals(Normals);
		}
		mesh.subMeshCount = Mathf.Max(1, SubMeshTriangles.Count);
		for (int i = 0; i < mesh.subMeshCount; i++)
		{
			List<int> triangles = i < SubMeshTriangles.Count ? SubMeshTriangles[i] : null;
			mesh.SetTriangles(triangles ?? new List<int>(), i, true);
		}
		mesh.RecalculateBounds();
	}

	public void RecalculateNormals()
	{
		Normals.Clear();
		for (int i = 0; i < Vertices.Count; i++)
		{
			Normals.Add(Vector3.zero);
		}

		for (int subMesh = 0; subMesh < SubMeshTriangles.Count; subMesh++)
		{
			List<int> triangles = SubMeshTriangles[subMesh];
			for (int i = 0; i + 2 < triangles.Count; i += 3)
			{
				int a = triangles[i];
				int b = triangles[i + 1];
				int c = triangles[i + 2];
				if (a < 0 || b < 0 || c < 0 || a >= Vertices.Count || b >= Vertices.Count || c >= Vertices.Count)
				{
					continue;
				}

				Vector3 normal = Vector3.Cross(Vertices[b] - Vertices[a], Vertices[c] - Vertices[a]);
				if (normal.sqrMagnitude <= 0.000001f)
				{
					continue;
				}

				Normals[a] += normal;
				Normals[b] += normal;
				Normals[c] += normal;
			}
		}

		for (int i = 0; i < Normals.Count; i++)
		{
			Normals[i] = Normals[i].sqrMagnitude > 0.000001f ? Normals[i].normalized : Vector3.up;
		}
	}
}
