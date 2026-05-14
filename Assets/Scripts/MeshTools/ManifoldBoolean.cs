using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MeshTools
{
    public enum ManifoldBooleanOperation
    {
        Union,
        Subtract,
        Intersect
    }

    /// <summary>
    /// Thin managed wrapper around the native manifoldc boolean library.
    /// </summary>
    public static class ManifoldBoolean
    {
        private const int PositionPropertyCount = 3;
        private const int InputVertexPropertyCount = 3;
        private const int NormalPropertyOffset = 3;
        private const float WeldEpsilon = 0.00001f;

        public static PreviewMeshData Subtract(
            PreviewMeshData lhs,
            Matrix4x4 lhsToResult,
            PreviewMeshData rhs,
            Matrix4x4 rhsToResult)
        {
            return Evaluate(lhs, lhsToResult, rhs, rhsToResult, ManifoldBooleanOperation.Subtract);
        }

        public static PreviewMeshData Intersect(
            PreviewMeshData lhs,
            Matrix4x4 lhsToResult,
            PreviewMeshData rhs,
            Matrix4x4 rhsToResult)
        {
            return Evaluate(lhs, lhsToResult, rhs, rhsToResult, ManifoldBooleanOperation.Intersect);
        }

        public static Mesh Intersect(Mesh lhs, Matrix4x4 lhsToResult, Mesh rhs, Matrix4x4 rhsToResult)
        {
            return Evaluate(lhs, lhsToResult, rhs, rhsToResult, ManifoldBooleanOperation.Intersect);
        }

        public static Mesh Evaluate(
            Mesh lhs,
            Matrix4x4 lhsToResult,
            Mesh rhs,
            Matrix4x4 rhsToResult,
            ManifoldBooleanOperation operation)
        {
            if (lhs == null || rhs == null)
            {
                return null;
            }

            using (NativeManifold lhsManifold = CreateManifold(MeshToolGeometry.ParseTriangles(lhs, lhsToResult)))
            using (NativeManifold rhsManifold = CreateManifold(MeshToolGeometry.ParseTriangles(rhs, rhsToResult)))
            using (NativeManifold result = ApplyBoolean(lhsManifold, rhsManifold, operation))
            {
                return ToPreviewMeshData(result, "ManifoldBoolean_" + operation, Mathf.Max(lhs.subMeshCount, rhs.subMeshCount)).ToMesh();
            }
        }

        public static PreviewMeshData Evaluate(
            PreviewMeshData lhs,
            Matrix4x4 lhsToResult,
            PreviewMeshData rhs,
            Matrix4x4 rhsToResult,
            ManifoldBooleanOperation operation,
            string meshName = null)
        {
            if (lhs == null || rhs == null)
            {
                return null;
            }

            MeshData lhsData = MeshToolGeometry.ParseTriangles(lhs, lhsToResult);
            MeshData rhsData = MeshToolGeometry.ParseTriangles(rhs, rhsToResult);
            using (NativeManifold lhsManifold = CreateManifold(lhsData))
            using (NativeManifold rhsManifold = CreateManifold(rhsData))
            using (NativeManifold result = ApplyBoolean(lhsManifold, rhsManifold, operation))
            {
                return ToPreviewMeshData(result, meshName ?? ("ManifoldBoolean_" + operation), Mathf.Max(lhsData.SubMeshCount, rhsData.SubMeshCount));
            }
        }

        public static Mesh TrimByPlanes(Mesh source, Plane[] planes, string meshName)
        {
            if (source == null || planes == null || planes.Length == 0)
            {
                return source;
            }

            using (NativeManifold result = TrimByPlanes(MeshToolGeometry.ParseTriangles(source, Matrix4x4.identity), planes))
            {
                return ToPreviewMeshData(result, meshName, source.subMeshCount).ToMesh();
            }
        }

        public static PreviewMeshData TrimByPlanes(PreviewMeshData source, Plane[] planes, string meshName)
        {
            if (source == null || planes == null || planes.Length == 0)
            {
                return source;
            }

            MeshData sourceData = MeshToolGeometry.ParseTriangles(source, Matrix4x4.identity);
            using (NativeManifold result = TrimByPlanes(sourceData, planes))
            {
                return ToPreviewMeshData(result, meshName, sourceData.SubMeshCount);
            }
        }

        private static NativeManifold TrimByPlanes(MeshData source, Plane[] planes)
        {
            NativeManifold current = CreateManifold(source);
            try
            {
                for (int i = 0; i < planes.Length; i++)
                {
                    NativeManifold next = TrimByPlane(current, planes[i]);
                    current.Dispose();
                    current = next;
                }

                NativeManifold result = current;
                current = null;
                return result;
            }
            finally
            {
                current?.Dispose();
            }
        }

        private static NativeManifold CreateManifold(MeshData meshData)
        {
            meshData = Sanitize(meshData);
            if (meshData.Triangles == null || meshData.Triangles.Count == 0)
            {
                return CreateEmptyManifold();
            }

            WeldedMesh welded = WeldMesh(meshData);
            float[] vertexProperties = new float[welded.Vertices.Count * InputVertexPropertyCount];
            List<int> runIndices = new List<int>(Mathf.Max(2, meshData.SubMeshCount + 1));
            List<int> runOriginalIds = new List<int>(Mathf.Max(1, meshData.SubMeshCount));

            for (int i = 0; i < welded.Vertices.Count; i++)
            {
                WriteVertex(vertexProperties, i, welded.Vertices[i]);
            }

            int currentRunSubMesh = int.MinValue;
            for (int i = 0; i < meshData.Triangles.Count; i++)
            {
                Triangle triangle = meshData.Triangles[i];
                if (triangle.SubMesh != currentRunSubMesh)
                {
                    currentRunSubMesh = triangle.SubMesh;
                    runIndices.Add(i * 3);
                    runOriginalIds.Add(Mathf.Max(0, triangle.SubMesh));
                }
            }

            if (runOriginalIds.Count > 0)
            {
                runIndices.Add(welded.Indices.Length);
            }

            IntPtr meshGl = CreateMeshGl(vertexProperties, welded.Vertices.Count, welded.Indices, meshData.Triangles.Count, runIndices, runOriginalIds);
            try
            {
                return CreateManifoldFromMeshGl(meshGl);
            }
            finally
            {
                NativeMethods.manifold_destruct_meshgl(meshGl);
            }
        }

        private static void WriteVertex(float[] target, int index, Vertex vertex)
        {
            int offset = index * InputVertexPropertyCount;
            target[offset] = vertex.Position.x;
            target[offset + 1] = vertex.Position.y;
            target[offset + 2] = vertex.Position.z;
        }

        /// <summary>
        /// 按位置焊接三角形顶点，避免把共享边闭合体错误地提交成未连接的面片集合。
        /// </summary>
        private static WeldedMesh WeldMesh(MeshData meshData)
        {
            Dictionary<PosKey, int> map = new Dictionary<PosKey, int>();
            List<Vertex> verts = new List<Vertex>(meshData.Triangles.Count * 3);
            List<Vector3> normalSums = new List<Vector3>(meshData.Triangles.Count * 3);
            int[] indices = new int[meshData.Triangles.Count * 3];

            for (int i = 0; i < meshData.Triangles.Count; i++)
            {
                Triangle triangle = meshData.Triangles[i];
                indices[i * 3] = AddWeldedVertex(map, verts, normalSums, triangle.A);
                indices[i * 3 + 1] = AddWeldedVertex(map, verts, normalSums, triangle.B);
                indices[i * 3 + 2] = AddWeldedVertex(map, verts, normalSums, triangle.C);
            }

            for (int i = 0; i < verts.Count; i++)
            {
                Vertex vertex = verts[i];
                Vector3 normal = normalSums[i];
                vertex.Normal = normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr ? normal.normalized : Vector3.up;
                verts[i] = vertex;
            }

            return new WeldedMesh(verts, indices);
        }

        /// <summary>
        /// 添加或复用一个按位置焊接后的顶点，并累计法线供 native manifold 拓扑重建使用。
        /// </summary>
        private static int AddWeldedVertex(Dictionary<PosKey, int> map, List<Vertex> verts, List<Vector3> normalSums, Vertex vertex)
        {
            PosKey key = new PosKey(vertex.Position);
            if (map.TryGetValue(key, out int index))
            {
                normalSums[index] += vertex.Normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr ? vertex.Normal.normalized : Vector3.zero;
                return index;
            }

            index = verts.Count;
            Vertex stored = vertex;
            stored.Normal = Vector3.zero;
            verts.Add(stored);
            normalSums.Add(vertex.Normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr ? vertex.Normal.normalized : Vector3.zero);
            map.Add(key, index);
            return index;
        }

        private static NativeManifold ApplyBoolean(NativeManifold lhs, NativeManifold rhs, ManifoldBooleanOperation operation)
        {
            IntPtr storage = AllocManifold();
            IntPtr result = NativeMethods.manifold_boolean(storage, lhs.Ptr, rhs.Ptr, ToNativeOperation(operation));
            if (result == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_manifold(storage);
                throw new InvalidOperationException("manifold_boolean returned null.");
            }

            NativeManifold manifold = new NativeManifold(result);
            EnsureNoError(manifold, "manifold_boolean");
            return manifold;
        }

        private static NativeManifold TrimByPlane(NativeManifold source, Plane plane)
        {
            Plane keepPlane = new Plane(-plane.normal, -plane.distance);
            IntPtr storage = AllocManifold();
            double offset = -keepPlane.distance;
            IntPtr result = NativeMethods.manifold_trim_by_plane(
                storage,
                source.Ptr,
                keepPlane.normal.x,
                keepPlane.normal.y,
                keepPlane.normal.z,
                offset);

            if (result == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_manifold(storage);
                throw new InvalidOperationException("manifold_trim_by_plane returned null.");
            }

            NativeManifold manifold = new NativeManifold(result);
            EnsureNoError(manifold, "manifold_trim_by_plane");
            return manifold;
        }

        private static NativeManifold CreateEmptyManifold()
        {
            IntPtr storage = AllocManifold();
            IntPtr result = NativeMethods.manifold_empty(storage);
            if (result == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_manifold(storage);
                throw new InvalidOperationException("manifold_empty returned null.");
            }

            return new NativeManifold(result);
        }

        private static NativeManifold CreateManifoldFromMeshGl(IntPtr meshGl)
        {
            IntPtr mergeStorage = AllocMeshGl();
            IntPtr mergedMeshGl = NativeMethods.manifold_meshgl_merge(mergeStorage, meshGl);
            if (mergedMeshGl == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_meshgl(mergeStorage);
                throw new InvalidOperationException("manifold_meshgl_merge returned null.");
            }

            if (mergedMeshGl == meshGl)
            {
                NativeMethods.manifold_destruct_meshgl(mergeStorage);
            }

            IntPtr storage = AllocManifold();
            IntPtr result = NativeMethods.manifold_of_meshgl(storage, mergedMeshGl);
            if (mergedMeshGl != meshGl)
            {
                NativeMethods.manifold_destruct_meshgl(mergedMeshGl);
            }

            if (result == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_manifold(storage);
                throw new InvalidOperationException("manifold_of_meshgl returned null.");
            }

            NativeManifold manifold = new NativeManifold(result);
            EnsureNoError(manifold, "manifold_of_meshgl");
            return manifold;
        }

        private static IntPtr CreateMeshGl(
            float[] vertexProperties,
            int vertexCount,
            int[] triangleIndices,
            int triangleCount,
            List<int> runIndices,
            List<int> runOriginalIds)
        {
            IntPtr runIndicesPtr = IntPtr.Zero;
            IntPtr runOriginalIdsPtr = IntPtr.Zero;
            try
            {
                MeshGLOptions options = default;
                if (runIndices != null && runOriginalIds != null && runOriginalIds.Count > 0)
                {
                    if (runIndices.Count != runOriginalIds.Count + 1)
                    {
                        throw new InvalidOperationException("Manifold MeshGL run indices must contain one trailing end index.");
                    }

                    runIndicesPtr = CopyToNative(runIndices);
                    runOriginalIdsPtr = CopyToNative(runOriginalIds);
                    options.run_indices = runIndicesPtr;
                    options.run_indices_length = new UIntPtr((uint)runIndices.Count);
                    options.run_original_ids = runOriginalIdsPtr;
                    options.run_original_ids_length = new UIntPtr((uint)runOriginalIds.Count);
                }

                IntPtr storage = AllocMeshGl();
                IntPtr result = NativeMethods.manifold_meshgl_w_options(
                    storage,
                    vertexProperties,
                    new UIntPtr((uint)vertexCount),
                    new UIntPtr(InputVertexPropertyCount),
                    triangleIndices,
                    new UIntPtr((uint)triangleCount),
                    ref options);

                if (result == IntPtr.Zero)
                {
                    NativeMethods.manifold_destruct_meshgl(storage);
                    throw new InvalidOperationException("manifold_meshgl_w_options returned null.");
                }

                return result;
            }
            finally
            {
                if (runIndicesPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(runIndicesPtr);
                }

                if (runOriginalIdsPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(runOriginalIdsPtr);
                }
            }
        }

        private static PreviewMeshData ToPreviewMeshData(NativeManifold manifold, string meshName, int minimumSubMeshCount)
        {
            EnsureNoError(manifold, "ToPreviewMeshData");
            PreviewMeshData data = new PreviewMeshData(meshName);
            if (NativeMethods.manifold_num_tri(manifold.Ptr).ToUInt64() == 0UL)
            {
                return data;
            }

            IntPtr meshGlStorage = AllocMeshGl();
            IntPtr meshGl = NativeMethods.manifold_get_meshgl_w_normals(meshGlStorage, manifold.Ptr, NormalPropertyOffset);
            if (meshGl == IntPtr.Zero)
            {
                NativeMethods.manifold_destruct_meshgl(meshGlStorage);
                throw new InvalidOperationException("manifold_get_meshgl_w_normals returned null.");
            }

            try
            {
                int numVertices = checked((int)NativeMethods.manifold_meshgl_num_vert(meshGl).ToUInt64());
                int numProps = checked((int)NativeMethods.manifold_meshgl_num_prop(meshGl).ToUInt64());
                int propLength = checked((int)NativeMethods.manifold_meshgl_vert_properties_length(meshGl).ToUInt64());
                float[] properties = new float[propLength];
                NativeMethods.manifold_meshgl_vert_properties(properties, meshGl);

                for (int i = 0; i < numVertices; i++)
                {
                    int offset = i * numProps;
                    if (offset + PositionPropertyCount > properties.Length)
                    {
                        break;
                    }

                    data.Vertices.Add(new Vector3(properties[offset], properties[offset + 1], properties[offset + 2]));
                    if (offset + NormalPropertyOffset + 2 < properties.Length)
                    {
                        Vector3 normal = new Vector3(
                            properties[offset + NormalPropertyOffset],
                            properties[offset + NormalPropertyOffset + 1],
                            properties[offset + NormalPropertyOffset + 2]);
                        data.Normals.Add(normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr ? normal.normalized : Vector3.up);
                    }
                }

                EnsureSubMeshCount(data, Mathf.Max(1, minimumSubMeshCount));
                int indexCount = checked((int)NativeMethods.manifold_meshgl_tri_length(meshGl).ToUInt64());
                int[] indices = new int[indexCount];
                NativeMethods.manifold_meshgl_tri_verts(indices, meshGl);

                int runCount = checked((int)NativeMethods.manifold_meshgl_run_original_id_length(meshGl).ToUInt64());
                int runIndexCount = checked((int)NativeMethods.manifold_meshgl_run_index_length(meshGl).ToUInt64());
                if (runCount <= 0 || runIndexCount <= 0)
                {
                    data.SubMeshTriangles[0].AddRange(indices);
                }
                else
                {
                    int[] runIndices = new int[runIndexCount];
                    int[] runOriginalIds = new int[runCount];
                    NativeMethods.manifold_meshgl_run_index(runIndices, meshGl);
                    NativeMethods.manifold_meshgl_run_original_id(runOriginalIds, meshGl);
                    for (int i = 0; i < runCount; i++)
                    {
                        int subMesh = Mathf.Max(0, runOriginalIds[i]);
                        EnsureSubMeshCount(data, subMesh + 1);
                        int start = Mathf.Clamp(i < runIndexCount ? runIndices[i] : 0, 0, indexCount);
                        int end = Mathf.Clamp(i + 1 < runIndexCount ? runIndices[i + 1] : indexCount, start, indexCount);
                        List<int> target = data.SubMeshTriangles[subMesh];
                        for (int index = start; index < end; index++)
                        {
                            target.Add(indices[index]);
                        }
                    }
                }

                if (data.Normals.Count != data.Vertices.Count)
                {
                    data.RecalculateNormals();
                }

                return data;
            }
            finally
            {
                NativeMethods.manifold_destruct_meshgl(meshGl);
            }
        }

        private static void EnsureSubMeshCount(PreviewMeshData data, int count)
        {
            while (data.SubMeshTriangles.Count < count)
            {
                data.SubMeshTriangles.Add(new List<int>());
            }
        }

        private static IntPtr CopyToNative(List<int> values)
        {
            int byteCount = sizeof(int) * values.Count;
            IntPtr ptr = Marshal.AllocHGlobal(byteCount);
            int[] array = values.ToArray();
            Marshal.Copy(array, 0, ptr, array.Length);
            return ptr;
        }

        /// <summary>
        /// 在送入 native manifold 前剔除按位置量化后完全重叠的三角形，减少重复面导致的非流形输入。
        /// </summary>
        private static MeshData Sanitize(MeshData meshData)
        {
            if (meshData.Triangles == null || meshData.Triangles.Count == 0)
            {
                return meshData;
            }

            HashSet<TriKey> seen = new HashSet<TriKey>();
            List<Triangle> triangles = new List<Triangle>(meshData.Triangles.Count);
            for (int i = 0; i < meshData.Triangles.Count; i++)
            {
                Triangle triangle = meshData.Triangles[i];
                TriKey key = new TriKey(triangle);
                if (!seen.Add(key))
                {
                    continue;
                }

                triangles.Add(triangle);
            }

            meshData.Triangles = triangles;
            return meshData;
        }

        private static IntPtr AllocManifold()
        {
            IntPtr ptr = NativeMethods.manifold_alloc_manifold();
            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException("manifold_alloc_manifold returned null.");
            }

            return ptr;
        }

        private static IntPtr AllocMeshGl()
        {
            IntPtr ptr = NativeMethods.manifold_alloc_meshgl();
            if (ptr == IntPtr.Zero)
            {
                throw new InvalidOperationException("manifold_alloc_meshgl returned null.");
            }

            return ptr;
        }

        private static NativeOpType ToNativeOperation(ManifoldBooleanOperation operation)
        {
            switch (operation)
            {
                case ManifoldBooleanOperation.Union:
                    return NativeOpType.ADD;
                case ManifoldBooleanOperation.Subtract:
                    return NativeOpType.SUBTRACT;
                case ManifoldBooleanOperation.Intersect:
                    return NativeOpType.INTERSECT;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private static void EnsureNoError(NativeManifold manifold, string operation)
        {
            NativeError status = NativeMethods.manifold_status(manifold.Ptr);
            if (status != NativeError.NO_ERROR)
            {
                throw new InvalidOperationException(operation + " failed: " + status);
            }
        }

        private readonly struct WeldedMesh
        {
            public WeldedMesh(List<Vertex> vertices, int[] indices)
            {
                Vertices = vertices;
                Indices = indices;
            }

            public List<Vertex> Vertices { get; }

            public int[] Indices { get; }
        }

        private readonly struct PosKey : IEquatable<PosKey>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public PosKey(Vector3 position)
            {
                _x = Quantize(position.x);
                _y = Quantize(position.y);
                _z = Quantize(position.z);
            }

            public bool Equals(PosKey other)
            {
                return _x == other._x && _y == other._y && _z == other._z;
            }

            public override bool Equals(object obj)
            {
                return obj is PosKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _x;
                    hash = (hash * 397) ^ _y;
                    hash = (hash * 397) ^ _z;
                    return hash;
                }
            }

            public int CompareTo(PosKey other)
            {
                int compare = _x.CompareTo(other._x);
                if (compare != 0)
                {
                    return compare;
                }

                compare = _y.CompareTo(other._y);
                if (compare != 0)
                {
                    return compare;
                }

                return _z.CompareTo(other._z);
            }

            /// <summary>
            /// 把浮点位置量化到工具容差内，避免极小误差阻止应当共享的边被焊接。
            /// </summary>
            private static int Quantize(float value)
            {
                return Mathf.RoundToInt(value / WeldEpsilon);
            }
        }

        private readonly struct TriKey : IEquatable<TriKey>
        {
            private readonly PosKey _a;
            private readonly PosKey _b;
            private readonly PosKey _c;
            private readonly int _subMesh;

            public TriKey(Triangle triangle)
            {
                _subMesh = Mathf.Max(0, triangle.SubMesh);
                PosKey a = new PosKey(triangle.A.Position);
                PosKey b = new PosKey(triangle.B.Position);
                PosKey c = new PosKey(triangle.C.Position);
                Sort(ref a, ref b, ref c);
                _a = a;
                _b = b;
                _c = c;
            }

            public bool Equals(TriKey other)
            {
                return _subMesh == other._subMesh
                    && _a.Equals(other._a)
                    && _b.Equals(other._b)
                    && _c.Equals(other._c);
            }

            public override bool Equals(object obj)
            {
                return obj is TriKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _subMesh;
                    hash = (hash * 397) ^ _a.GetHashCode();
                    hash = (hash * 397) ^ _b.GetHashCode();
                    hash = (hash * 397) ^ _c.GetHashCode();
                    return hash;
                }
            }

            /// <summary>
            /// 让三角形 key 与顶点顺序无关，这样正反向重叠面都会被视为重复面。
            /// </summary>
            private static void Sort(ref PosKey a, ref PosKey b, ref PosKey c)
            {
                if (Compare(a, b) > 0)
                {
                    Swap(ref a, ref b);
                }
                if (Compare(b, c) > 0)
                {
                    Swap(ref b, ref c);
                }
                if (Compare(a, b) > 0)
                {
                    Swap(ref a, ref b);
                }
            }

            /// <summary>
            /// 用量化后的位置字典序比较两个顶点 key。
            /// </summary>
            private static int Compare(PosKey a, PosKey b)
            {
                return a.CompareTo(b);
            }

            /// <summary>
            /// 交换两个顶点 key，供三点排序复用。
            /// </summary>
            private static void Swap(ref PosKey a, ref PosKey b)
            {
                PosKey temp = a;
                a = b;
                b = temp;
            }
        }

        private sealed class NativeManifold : IDisposable
        {
            public IntPtr Ptr { get; private set; }

            public NativeManifold(IntPtr ptr)
            {
                Ptr = ptr;
            }

            public void Dispose()
            {
                if (Ptr == IntPtr.Zero)
                {
                    return;
                }

                NativeMethods.manifold_destruct_manifold(Ptr);
                Ptr = IntPtr.Zero;
            }
        }

        private enum NativeOpType
        {
            ADD,
            SUBTRACT,
            INTERSECT
        }

        private enum NativeError
        {
            NO_ERROR,
            NON_FINITE_VERTEX,
            NOT_MANIFOLD,
            VERTEX_INDEX_OUT_OF_BOUNDS,
            PROPERTIES_WRONG_LENGTH,
            MISSING_POSITION_PROPERTIES,
            MERGE_VECTORS_DIFFERENT_LENGTHS,
            MERGE_INDEX_OUT_OF_BOUNDS,
            TRANSFORM_WRONG_LENGTH,
            RUN_INDEX_WRONG_LENGTH,
            FACE_ID_WRONG_LENGTH,
            INVALID_CONSTRUCTION,
            RESULT_TOO_LARGE
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MeshGLOptions
        {
            public IntPtr run_indices;
            public UIntPtr run_indices_length;
            public IntPtr run_original_ids;
            public UIntPtr run_original_ids_length;
            public IntPtr merge_from_vert;
            public IntPtr merge_to_vert;
            public UIntPtr merge_verts_length;
            public IntPtr halfedge_tangents;
        }

        private static class NativeMethods
        {
            private const string Library = "manifoldc";

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_alloc_manifold();

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_alloc_meshgl();

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_boolean(IntPtr mem, IntPtr a, IntPtr b, NativeOpType op);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_empty(IntPtr mem);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_get_meshgl_w_normals(IntPtr mem, IntPtr m, int normalIdx);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_merge(IntPtr mem, IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_w_options(
                IntPtr mem,
                [In] float[] vert_props,
                UIntPtr n_verts,
                UIntPtr n_props,
                [In] int[] tri_verts,
                UIntPtr n_tris,
                ref MeshGLOptions options);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_of_meshgl(IntPtr mem, IntPtr mesh);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_trim_by_plane(IntPtr mem, IntPtr m, double normal_x, double normal_y, double normal_z, double offset);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern void manifold_destruct_manifold(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern void manifold_destruct_meshgl(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_num_prop(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_num_vert(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_run_index_length(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_run_original_id_length(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_tri_length(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_run_index([Out] int[] dest, IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_run_original_id([Out] int[] dest, IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_tri_verts([Out] int[] dest, IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr manifold_meshgl_vert_properties([Out] float[] dest, IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_meshgl_vert_properties_length(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern UIntPtr manifold_num_tri(IntPtr m);

            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            public static extern NativeError manifold_status(IntPtr m);
        }
    }
}
