using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshTools
{
    [Flags]
    public enum MeshToolAttributeMask
    {
        None = 0,
        Normals = 1 << 0,
        Tangents = 1 << 1,
        Uv = 1 << 2,
        Uv2 = 1 << 3,
        Colors = 1 << 4
    }

    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Uv;
        public Vector2 Uv2;
        public Color Color;

        /// <summary>
        /// 在两点之间插值生成新顶点，用于平面切割或 CSG 分割边时补出交点顶点。
        /// </summary>
        public static Vertex Lerp(Vertex a, Vertex b, float t)
        {
            Vertex vertex = new Vertex
            {
                Position = Vector3.LerpUnclamped(a.Position, b.Position, t),
                Normal = Vector3.LerpUnclamped(a.Normal, b.Normal, t),
                Uv = Vector2.LerpUnclamped(a.Uv, b.Uv, t),
                Uv2 = Vector2.LerpUnclamped(a.Uv2, b.Uv2, t),
                Color = Color.LerpUnclamped(a.Color, b.Color, t)
            };

            // 法线插值后需要归一化，否则光照会随切割次数逐渐变暗或变形。
            if (vertex.Normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr)
            {
                vertex.Normal.Normalize();
            }

            // Tangent 是方向加 handedness，方向可以插值，w 不能当普通分量线性混合。
            Vector3 tangentDirection = Vector3.LerpUnclamped(
                new Vector3(a.Tangent.x, a.Tangent.y, a.Tangent.z),
                new Vector3(b.Tangent.x, b.Tangent.y, b.Tangent.z),
                t);

            if (tangentDirection.sqrMagnitude <= MeshToolGeometry.EpsilonSqr)
            {
                tangentDirection = Vector3.right;
            }
            else
            {
                tangentDirection.Normalize();
            }

            // handedness 使用离交点更近的端点，避免插值到 0 导致副切线方向丢失。
            float handedness = t < 0.5f ? a.Tangent.w : b.Tangent.w;
            if (Mathf.Abs(handedness) <= MeshToolGeometry.Epsilon)
            {
                handedness = 1f;
            }

            vertex.Tangent = new Vector4(tangentDirection.x, tangentDirection.y, tangentDirection.z, Mathf.Sign(handedness));
            return vertex;
        }

        /// <summary>
        /// 返回一个替换法线后的顶点，保留位置、UV、颜色等其它属性。
        /// </summary>
        public Vertex WithNormal(Vector3 normal)
        {
            Vertex vertex = this;
            vertex.Normal = normal.normalized;
            return vertex;
        }

        /// <summary>
        /// 返回翻面后的顶点，主要用于 CSG 反转实体内外侧。
        /// </summary>
        public Vertex Flipped()
        {
            Vertex vertex = this;
            vertex.Normal = -vertex.Normal;
            vertex.Tangent = new Vector4(vertex.Tangent.x, vertex.Tangent.y, vertex.Tangent.z, -vertex.Tangent.w);
            return vertex;
        }
    }

    public struct Triangle
    {
        public Vertex A;
        public Vertex B;
        public Vertex C;
        public int SubMesh;

        /// <summary>
        /// 创建一个带子网格索引的三角形记录。
        /// </summary>
        public Triangle(Vertex a, Vertex b, Vertex c, int subMesh)
        {
            A = a;
            B = b;
            C = c;
            SubMesh = subMesh;
        }
    }

    public struct MeshData
    {
        public List<Triangle> Triangles;
        public MeshToolAttributeMask Attributes;
        public int SubMeshCount;
    }

    public static class MeshToolGeometry
    {
        public const float Epsilon = 0.00001f;
        public const float EpsilonSqr = Epsilon * Epsilon;

        /// <summary>
        /// 读取 Unity Mesh 的三角形，并把顶点从源空间变换到结果空间。
        /// </summary>
        public static MeshData ParseTriangles(Mesh mesh, Matrix4x4 meshToResult)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] uvs = mesh.uv;
            Vector2[] uv2s = mesh.uv2;
            Color[] colors = mesh.colors;

            bool hasNormals = normals != null && normals.Length == positions.Length;
            bool hasTangents = tangents != null && tangents.Length == positions.Length;
            bool hasUvs = uvs != null && uvs.Length == positions.Length;
            bool hasUv2s = uv2s != null && uv2s.Length == positions.Length;
            bool hasColors = colors != null && colors.Length == positions.Length;

            // 记录源 Mesh 真正拥有的属性，重建结果 Mesh 时只写回这些通道。
            MeshToolAttributeMask attributes = MeshToolAttributeMask.Normals;
            if (hasTangents)
            {
                attributes |= MeshToolAttributeMask.Tangents;
            }

            if (hasUvs)
            {
                attributes |= MeshToolAttributeMask.Uv;
            }

            if (hasUv2s)
            {
                attributes |= MeshToolAttributeMask.Uv2;
            }

            if (hasColors)
            {
                attributes |= MeshToolAttributeMask.Colors;
            }

            Matrix4x4 normalMatrix = meshToResult.inverse.transpose;
            int sourceSubMeshCount = Mathf.Max(1, mesh.subMeshCount);
            List<Triangle> triangles = new List<Triangle>();

            // 按子网格读取索引，方便布尔或切割后保留原来的材质槽。
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] indices = mesh.GetTriangles(subMesh);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    Vertex a = ReadVertex(indices[i], positions, normals, tangents, uvs, uv2s, colors,
                        hasNormals, hasTangents, hasUvs, hasUv2s, hasColors, meshToResult, normalMatrix);
                    Vertex b = ReadVertex(indices[i + 1], positions, normals, tangents, uvs, uv2s, colors,
                        hasNormals, hasTangents, hasUvs, hasUv2s, hasColors, meshToResult, normalMatrix);
                    Vertex c = ReadVertex(indices[i + 2], positions, normals, tangents, uvs, uv2s, colors,
                        hasNormals, hasTangents, hasUvs, hasUv2s, hasColors, meshToResult, normalMatrix);

                    Vector3 faceNormal;
                    if (!TryGetFaceNormal(a.Position, b.Position, c.Position, out faceNormal))
                    {
                        continue;
                    }

                    // 如果源 Mesh 没有法线，或某个法线退化，就用面法线补齐。
                    if (!hasNormals)
                    {
                        a.Normal = faceNormal;
                        b.Normal = faceNormal;
                        c.Normal = faceNormal;
                    }
                    else
                    {
                        if (a.Normal.sqrMagnitude <= EpsilonSqr)
                        {
                            a.Normal = faceNormal;
                        }

                        if (b.Normal.sqrMagnitude <= EpsilonSqr)
                        {
                            b.Normal = faceNormal;
                        }

                        if (c.Normal.sqrMagnitude <= EpsilonSqr)
                        {
                            c.Normal = faceNormal;
                        }
                    }

                    triangles.Add(new Triangle(a, b, c, subMesh));
                }
            }

            return new MeshData
            {
                Triangles = triangles,
                Attributes = attributes,
                SubMeshCount = sourceSubMeshCount
            };
        }

        public static MeshData ParseTriangles(PreviewMeshData mesh, Matrix4x4 meshToResult)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            Matrix4x4 normalMatrix = meshToResult.inverse.transpose;
            int sourceSubMeshCount = Mathf.Max(1, mesh.SubMeshTriangles.Count);
            List<Triangle> triangles = new List<Triangle>();
            for (int subMesh = 0; subMesh < sourceSubMeshCount; subMesh++)
            {
                List<int> indices = mesh.SubMeshTriangles[subMesh];
                for (int i = 0; i + 2 < indices.Count; i += 3)
                {
                    Vertex a = ReadVertex(indices[i], mesh.Vertices, mesh.Normals, meshToResult, normalMatrix);
                    Vertex b = ReadVertex(indices[i + 1], mesh.Vertices, mesh.Normals, meshToResult, normalMatrix);
                    Vertex c = ReadVertex(indices[i + 2], mesh.Vertices, mesh.Normals, meshToResult, normalMatrix);

                    if (!TryGetFaceNormal(a.Position, b.Position, c.Position, out Vector3 faceNormal))
                    {
                        continue;
                    }

                    if (a.Normal.sqrMagnitude <= EpsilonSqr)
                    {
                        a.Normal = faceNormal;
                    }
                    if (b.Normal.sqrMagnitude <= EpsilonSqr)
                    {
                        b.Normal = faceNormal;
                    }
                    if (c.Normal.sqrMagnitude <= EpsilonSqr)
                    {
                        c.Normal = faceNormal;
                    }

                    triangles.Add(new Triangle(a, b, c, subMesh));
                }
            }

            return new MeshData
            {
                Triangles = triangles,
                Attributes = MeshToolAttributeMask.Normals,
                SubMeshCount = sourceSubMeshCount
            };
        }

        /// <summary>
        /// 用矩阵变换平面，并保持 Plane.distance 的 Unity 约定。
        /// </summary>
        public static Plane TransformPlane(Plane plane, Matrix4x4 transform)
        {
            Vector3 normal = plane.normal;
            if (normal.sqrMagnitude <= EpsilonSqr)
            {
                throw new ArgumentException("Plane normal cannot be zero.", nameof(plane));
            }

            normal.Normalize();
            Vector3 pointOnPlane = -plane.distance * normal;
            Vector3 transformedPoint = transform.MultiplyPoint3x4(pointOnPlane);
            // 平面法线必须用逆转置矩阵变换，才能正确处理非等比缩放。
            Vector3 transformedNormal = transform.inverse.transpose.MultiplyVector(normal);

            if (transformedNormal.sqrMagnitude <= EpsilonSqr)
            {
                transformedNormal = transform.MultiplyVector(normal);
            }

            if (transformedNormal.sqrMagnitude <= EpsilonSqr)
            {
                throw new ArgumentException("Plane transform produced a zero normal.", nameof(transform));
            }

            transformedNormal.Normalize();
            return new Plane(transformedNormal, transformedPoint);
        }

        /// <summary>
        /// 尝试根据三个点计算面法线，退化三角形会返回 false。
        /// </summary>
        public static bool TryGetFaceNormal(Vector3 a, Vector3 b, Vector3 c, out Vector3 normal)
        {
            normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude <= EpsilonSqr)
            {
                normal = Vector3.zero;
                return false;
            }

            normal.Normalize();
            return true;
        }

        /// <summary>
        /// 判断三个点是否能组成非退化三角形。
        /// </summary>
        public static bool IsValidTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 cross = Vector3.Cross(b - a, c - a);
            return cross.sqrMagnitude > EpsilonSqr;
        }

        /// <summary>
        /// 按工具容差判断两个位置是否可视为同一点。
        /// </summary>
        public static bool SamePosition(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude <= EpsilonSqr;
        }

        /// <summary>
        /// 从源 Mesh 的顶点数组中读取单个顶点，并完成位置、法线、切线的空间变换。
        /// </summary>
        private static Vertex ReadVertex(
            int index,
            Vector3[] positions,
            Vector3[] normals,
            Vector4[] tangents,
            Vector2[] uvs,
            Vector2[] uv2s,
            Color[] colors,
            bool hasNormals,
            bool hasTangents,
            bool hasUvs,
            bool hasUv2s,
            bool hasColors,
            Matrix4x4 meshToResult,
            Matrix4x4 normalMatrix)
        {
            Vertex vertex = new Vertex
            {
                Position = meshToResult.MultiplyPoint3x4(positions[index]),
                Normal = Vector3.zero,
                Tangent = new Vector4(1f, 0f, 0f, 1f),
                Uv = hasUvs ? uvs[index] : Vector2.zero,
                Uv2 = hasUv2s ? uv2s[index] : Vector2.zero,
                Color = hasColors ? colors[index] : Color.white
            };

            // 法线与位置不同，使用 normalMatrix 才能在非等比缩放下保持正确方向。
            if (hasNormals)
            {
                vertex.Normal = normalMatrix.MultiplyVector(normals[index]);
                if (vertex.Normal.sqrMagnitude > EpsilonSqr)
                {
                    vertex.Normal.Normalize();
                }
            }

            // 切线是方向向量，不受平移影响；w 保留源 Mesh 的副切线 handedness。
            if (hasTangents)
            {
                Vector4 sourceTangent = tangents[index];
                Vector3 tangentDirection = meshToResult.MultiplyVector(new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z));
                if (tangentDirection.sqrMagnitude > EpsilonSqr)
                {
                    tangentDirection.Normalize();
                    vertex.Tangent = new Vector4(tangentDirection.x, tangentDirection.y, tangentDirection.z, sourceTangent.w);
                }
            }

            return vertex;
        }

        private static Vertex ReadVertex(
            int index,
            List<Vector3> positions,
            List<Vector3> normals,
            Matrix4x4 meshToResult,
            Matrix4x4 normalMatrix)
        {
            Vector3 normal = normals != null && normals.Count == positions.Count ? normalMatrix.MultiplyVector(normals[index]) : Vector3.zero;
            if (normal.sqrMagnitude > EpsilonSqr)
            {
                normal.Normalize();
            }

            return new Vertex
            {
                Position = meshToResult.MultiplyPoint3x4(positions[index]),
                Normal = normal,
                Tangent = new Vector4(1f, 0f, 0f, 1f),
                Uv = Vector2.zero,
                Uv2 = Vector2.zero,
                Color = Color.white
            };
        }
    }

    public sealed class MeshBuilder
    {
        private readonly MeshToolAttributeMask attributes;
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector4> tangents = new List<Vector4>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<Vector2> uv2s = new List<Vector2>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<List<int>> subMeshTriangles = new List<List<int>>();

        /// <summary>
        /// 创建结果 Mesh 构建器，并预先准备需要的子网格列表。
        /// </summary>
        public MeshBuilder(MeshToolAttributeMask attributes, int subMeshCount)
        {
            this.attributes = attributes | MeshToolAttributeMask.Normals;
            int safeSubMeshCount = Mathf.Max(1, subMeshCount);
            for (int i = 0; i < safeSubMeshCount; i++)
            {
                subMeshTriangles.Add(new List<int>());
            }
        }

        /// <summary>
        /// 当前构建器会写入结果 Mesh 的属性通道。
        /// </summary>
        public MeshToolAttributeMask Attributes
        {
            get { return attributes; }
        }

        /// <summary>
        /// 当前构建器里可写入的子网格数量。
        /// </summary>
        public int SubMeshCount
        {
            get { return subMeshTriangles.Count; }
        }

        /// <summary>
        /// 向指定子网格追加一个三角形，退化三角形会被跳过。
        /// </summary>
        public void AddTriangle(Vertex a, Vertex b, Vertex c, int subMesh)
        {
            if (!MeshToolGeometry.IsValidTriangle(a.Position, b.Position, c.Position))
            {
                return;
            }

            int safeSubMesh = Mathf.Max(0, subMesh);
            EnsureSubMesh(safeSubMesh);

            int ia = AddVertex(a);
            int ib = AddVertex(b);
            int ic = AddVertex(c);

            subMeshTriangles[safeSubMesh].Add(ia);
            subMeshTriangles[safeSubMesh].Add(ib);
            subMeshTriangles[safeSubMesh].Add(ic);
        }

        /// <summary>
        /// 用扇形三角化方式追加一个凸多边形或近似凸多边形。
        /// </summary>
        public void AddPolygon(IList<Vertex> polygon, int subMesh)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return;
            }

            Vertex anchor = polygon[0];
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                AddTriangle(anchor, polygon[i], polygon[i + 1], subMesh);
            }
        }

        /// <summary>
        /// 把收集到的顶点属性和索引写成 Unity Mesh。
        /// </summary>
        public Mesh ToMesh(string meshName)
        {
            Mesh mesh = new Mesh
            {
                name = meshName
            };

            // Unity 默认 16 位索引最多 65535 个顶点，超过后切到 UInt32。
            if (positions.Count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.SetVertices(positions);

            // 只写入构建器声明支持的通道，避免给结果 Mesh 塞入空属性。
            if ((attributes & MeshToolAttributeMask.Normals) != 0)
            {
                mesh.SetNormals(normals);
            }

            if ((attributes & MeshToolAttributeMask.Tangents) != 0)
            {
                mesh.SetTangents(tangents);
            }

            if ((attributes & MeshToolAttributeMask.Uv) != 0)
            {
                mesh.SetUVs(0, uvs);
            }

            if ((attributes & MeshToolAttributeMask.Uv2) != 0)
            {
                mesh.SetUVs(1, uv2s);
            }

            if ((attributes & MeshToolAttributeMask.Colors) != 0)
            {
                mesh.SetColors(colors);
            }

            mesh.subMeshCount = Mathf.Max(1, subMeshTriangles.Count);
            for (int i = 0; i < subMeshTriangles.Count; i++)
            {
                mesh.SetTriangles(subMeshTriangles[i], i, true);
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // 把 MeshData 内部缓存重新导出成 PreviewMeshData。 / Export the internal MeshData buffers back into PreviewMeshData.
        public PreviewMeshData ToPreviewMeshData(string meshName)
        {
            PreviewMeshData data = new PreviewMeshData(meshName);
            data.Vertices.AddRange(positions);
            if ((attributes & MeshToolAttributeMask.Normals) != 0 && normals.Count == positions.Count)
            {
                data.Normals.AddRange(normals);
            }
            else
            {
                for (int i = 0; i < positions.Count; i++)
                {
                    data.Normals.Add(Vector3.up);
                }
            }

            data.SubMeshTriangles.Clear();
            for (int i = 0; i < subMeshTriangles.Count; i++)
            {
                data.SubMeshTriangles.Add(new List<int>(subMeshTriangles[i]));
            }
            if (data.SubMeshTriangles.Count == 0)
            {
                data.SubMeshTriangles.Add(new List<int>());
            }
            return data;
        }

        /// <summary>
        /// 追加一个顶点到各属性数组，并返回它的新索引。
        /// </summary>
        private int AddVertex(Vertex vertex)
        {
            int index = positions.Count;
            positions.Add(vertex.Position);

            if ((attributes & MeshToolAttributeMask.Normals) != 0)
            {
                normals.Add(vertex.Normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr ? vertex.Normal.normalized : Vector3.up);
            }

            if ((attributes & MeshToolAttributeMask.Tangents) != 0)
            {
                tangents.Add(vertex.Tangent);
            }

            if ((attributes & MeshToolAttributeMask.Uv) != 0)
            {
                uvs.Add(vertex.Uv);
            }

            if ((attributes & MeshToolAttributeMask.Uv2) != 0)
            {
                uv2s.Add(vertex.Uv2);
            }

            if ((attributes & MeshToolAttributeMask.Colors) != 0)
            {
                colors.Add(vertex.Color);
            }

            return index;
        }

        /// <summary>
        /// 确保指定子网格存在，子网格索引可能来自源 Mesh 或 capSubMesh。
        /// </summary>
        private void EnsureSubMesh(int subMesh)
        {
            while (subMeshTriangles.Count <= subMesh)
            {
                subMeshTriangles.Add(new List<int>());
            }
        }
    }
}
