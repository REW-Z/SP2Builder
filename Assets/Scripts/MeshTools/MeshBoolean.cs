using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTools
{
    public enum MeshBooleanOperation
    {
        // 合并两个封闭 Mesh 的体积。
        Union,
        // 从左侧 Mesh 中挖掉右侧 Mesh 占据的体积。
        Subtract,
        // 只保留两个 Mesh 共同占据的体积。
        Intersect
    }

    /// <summary>
    /// 基于 BSP 的 CSG 网格布尔工具，适用于闭合且三角面绕序一致的 Mesh。
    /// 如果不使用 Transform 重载，两个 Mesh 必须已经处在同一坐标空间。
    /// </summary>
    public static class MeshBoolean
    {
        /// <summary>
        /// 对两个同空间 Mesh 求并集。
        /// </summary>
        public static Mesh Union(Mesh lhs, Mesh rhs)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Union);
        }

        /// <summary>
        /// 对两个同空间 Mesh 求差集，结果是 lhs 减去 rhs。
        /// </summary>
        public static Mesh Subtract(Mesh lhs, Mesh rhs)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Subtract);
        }

        /// <summary>
        /// 对两个同空间 Mesh 求交集。
        /// </summary>
        public static Mesh Intersect(Mesh lhs, Mesh rhs)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Intersect);
        }

        /// <summary>
        /// 在同一坐标空间内执行指定布尔运算。
        /// </summary>
        public static Mesh Evaluate(Mesh lhs, Mesh rhs, MeshBooleanOperation operation)
        {
            return Evaluate(lhs, Matrix4x4.identity, rhs, Matrix4x4.identity, operation);
        }

        /// <summary>
        /// 使用两个 Mesh 的 localToWorldMatrix 求并集，默认把结果放回 lhs 的本地空间。
        /// </summary>
        public static Mesh Union(
            Mesh lhs,
            Matrix4x4 lhsWorldMatrix,
            Mesh rhs,
            Matrix4x4 rhsWorldMatrix,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsWorldMatrix, rhs, rhsWorldMatrix, MeshBooleanOperation.Union, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 的 localToWorldMatrix 求差集，默认把结果放回 lhs 的本地空间。
        /// </summary>
        public static Mesh Subtract(
            Mesh lhs,
            Matrix4x4 lhsWorldMatrix,
            Mesh rhs,
            Matrix4x4 rhsWorldMatrix,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsWorldMatrix, rhs, rhsWorldMatrix, MeshBooleanOperation.Subtract, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 的 localToWorldMatrix 求交集，默认把结果放回 lhs 的本地空间。
        /// </summary>
        public static Mesh Intersect(
            Mesh lhs,
            Matrix4x4 lhsWorldMatrix,
            Mesh rhs,
            Matrix4x4 rhsWorldMatrix,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsWorldMatrix, rhs, rhsWorldMatrix, MeshBooleanOperation.Intersect, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 的 localToWorldMatrix 执行布尔运算，默认结果空间是 lhs 的本地空间。
        /// </summary>
        public static Mesh EvaluateWorld(
            Mesh lhs,
            Matrix4x4 lhsWorldMatrix,
            Mesh rhs,
            Matrix4x4 rhsWorldMatrix,
            MeshBooleanOperation operation,
            Transform resultSpace = null)
        {
            // 没有显式结果 Transform 时，转回 lhs 本地空间，便于把结果直接赋给 lhs 对象。
            Matrix4x4 worldToResult = resultSpace != null ? resultSpace.worldToLocalMatrix : lhsWorldMatrix.inverse;
            Matrix4x4 lhsToResult = worldToResult * lhsWorldMatrix;
            Matrix4x4 rhsToResult = worldToResult * rhsWorldMatrix;

            return Evaluate(lhs, lhsToResult, rhs, rhsToResult, operation);
        }

        /// <summary>
        /// 使用两个 Mesh 各自的 Transform 求并集，默认把结果放回 lhsTransform 的本地空间。
        /// </summary>
        public static Mesh Union(
            Mesh lhs,
            Transform lhsTransform,
            Mesh rhs,
            Transform rhsTransform,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsTransform, rhs, rhsTransform, MeshBooleanOperation.Union, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 各自的 Transform 求差集，默认把结果放回 lhsTransform 的本地空间。
        /// </summary>
        public static Mesh Subtract(
            Mesh lhs,
            Transform lhsTransform,
            Mesh rhs,
            Transform rhsTransform,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsTransform, rhs, rhsTransform, MeshBooleanOperation.Subtract, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 各自的 Transform 求交集，默认把结果放回 lhsTransform 的本地空间。
        /// </summary>
        public static Mesh Intersect(
            Mesh lhs,
            Transform lhsTransform,
            Mesh rhs,
            Transform rhsTransform,
            Transform resultSpace = null)
        {
            return EvaluateWorld(lhs, lhsTransform, rhs, rhsTransform, MeshBooleanOperation.Intersect, resultSpace);
        }

        /// <summary>
        /// 使用两个 Mesh 各自的 Transform 执行布尔运算，默认结果空间是 lhsTransform 的本地空间。
        /// </summary>
        public static Mesh EvaluateWorld(
            Mesh lhs,
            Transform lhsTransform,
            Mesh rhs,
            Transform rhsTransform,
            MeshBooleanOperation operation,
            Transform resultSpace = null)
        {
            if (lhsTransform == null)
            {
                throw new ArgumentNullException(nameof(lhsTransform));
            }

            if (rhsTransform == null)
            {
                throw new ArgumentNullException(nameof(rhsTransform));
            }

            // Transform 版本和 Matrix 版本保持同一语义：先到世界，再到结果空间。
            Transform targetSpace = resultSpace != null ? resultSpace : lhsTransform;
            Matrix4x4 worldToResult = targetSpace.worldToLocalMatrix;
            Matrix4x4 lhsToResult = worldToResult * lhsTransform.localToWorldMatrix;
            Matrix4x4 rhsToResult = worldToResult * rhsTransform.localToWorldMatrix;

            return Evaluate(lhs, lhsToResult, rhs, rhsToResult, operation);
        }

        /// <summary>
        /// 执行布尔运算；lhsToResult 和 rhsToResult 会把源 Mesh 顶点变换到结果空间。
        /// </summary>
        public static Mesh Evaluate(
            Mesh lhs,
            Matrix4x4 lhsToResult,
            Mesh rhs,
            Matrix4x4 rhsToResult,
            MeshBooleanOperation operation)
        {
            using StopWatch _ = new("Evaluate");
            

            // 先用 Mesh 自带 bounds 做最快的分离测试，避免分离物体还去展开所有三角形和构建 BSP。
            Bounds lhsFastBounds;
            Bounds rhsFastBounds;
            if (TryGetTransformedBounds(lhs, lhsToResult, out lhsFastBounds) &&
                TryGetTransformedBounds(rhs, rhsToResult, out rhsFastBounds) &&
                !lhsFastBounds.Intersects(rhsFastBounds))
            {
                return BuildFastSeparatedResult(lhs, lhsToResult, rhs, rhsToResult, operation);
            }

            // 先把两个 Mesh 都读成内部三角形数据，并统一到结果坐标空间。
            MeshData lhsData = MeshToolGeometry.ParseTriangles(lhs, lhsToResult);
            MeshData rhsData = MeshToolGeometry.ParseTriangles(rhs, rhsToResult);

            MeshToolAttributeMask attributes = lhsData.Attributes | rhsData.Attributes | MeshToolAttributeMask.Normals;
            int subMeshCount = Mathf.Max(lhsData.SubMeshCount, rhsData.SubMeshCount);

            // 两个包围盒都不相交时，不需要进入 BSP。分离很远的物体做 Subtract 时应当直接等于 lhs。
            Bounds lhsBounds;
            Bounds rhsBounds;
            if (TryGetBounds(lhsData.Triangles, out lhsBounds) &&
                TryGetBounds(rhsData.Triangles, out rhsBounds) &&
                !lhsBounds.Intersects(rhsBounds))
            {
                return BuildSeparatedResult(lhsData, rhsData, attributes, subMeshCount, operation);
            }

            // CSG 算法处理的是带平面信息的多边形，最终再三角化回 Unity Mesh。
            List<CsgPolygon> lhsPolygons = ToPolygons(lhsData.Triangles);
            List<CsgPolygon> rhsPolygons = ToPolygons(rhsData.Triangles);

            List<CsgPolygon> resultPolygons;
            switch (operation)
            {
                case MeshBooleanOperation.Union:
                    resultPolygons = UnionPolygons(lhsPolygons, rhsPolygons);
                    break;
                case MeshBooleanOperation.Subtract:
                    resultPolygons = SubtractPolygons(lhsPolygons, rhsPolygons);
                    break;
                case MeshBooleanOperation.Intersect:
                    resultPolygons = IntersectPolygons(lhsPolygons, rhsPolygons);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }

            // 把 CSG 产生的多边形写回 MeshBuilder，保留原子网格索引。
            MeshBuilder builder = new MeshBuilder(attributes, subMeshCount);
            for (int i = 0; i < resultPolygons.Count; i++)
            {
                builder.AddPolygon(resultPolygons[i].Vertices, resultPolygons[i].SubMesh);
            }



            return builder.ToMesh("MeshBoolean_" + operation);
        }


        /// <summary>
        /// 包围盒已经分离时，尽量少读取三角形来直接构造结果。
        /// </summary>
        private static Mesh BuildFastSeparatedResult(
            Mesh lhs,
            Matrix4x4 lhsToResult,
            Mesh rhs,
            Matrix4x4 rhsToResult,
            MeshBooleanOperation operation)
        {
            switch (operation)
            {
                case MeshBooleanOperation.Subtract:
                {
                    MeshData lhsData = MeshToolGeometry.ParseTriangles(lhs, lhsToResult);
                    return BuildSeparatedResult(
                        lhsData,
                        default(MeshData),
                        lhsData.Attributes,
                        lhsData.SubMeshCount,
                        operation);
                }

                case MeshBooleanOperation.Intersect:
                {
                    MeshBuilder builder = new MeshBuilder(MeshToolAttributeMask.Normals, Mathf.Max(1, lhs.subMeshCount));
                    return builder.ToMesh("MeshBoolean_" + operation);
                }

                case MeshBooleanOperation.Union:
                {
                    MeshData lhsData = MeshToolGeometry.ParseTriangles(lhs, lhsToResult);
                    MeshData rhsData = MeshToolGeometry.ParseTriangles(rhs, rhsToResult);
                    MeshToolAttributeMask attributes = lhsData.Attributes | rhsData.Attributes | MeshToolAttributeMask.Normals;
                    int subMeshCount = Mathf.Max(lhsData.SubMeshCount, rhsData.SubMeshCount);
                    return BuildSeparatedResult(lhsData, rhsData, attributes, subMeshCount, operation);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        /// <summary>
        /// 用 Mesh.bounds 经过矩阵变换后得到结果空间 AABB。
        /// </summary>
        private static bool TryGetTransformedBounds(Mesh mesh, Matrix4x4 meshToResult, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (mesh == null || mesh.vertexCount == 0)
            {
                return false;
            }

            Bounds sourceBounds = mesh.bounds;
            Vector3 min = sourceBounds.min;
            Vector3 max = sourceBounds.max;

            Vector3 first = meshToResult.MultiplyPoint3x4(new Vector3(min.x, min.y, min.z));
            bounds = new Bounds(first, Vector3.zero);

            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(max.x, min.y, min.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(min.x, max.y, min.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(max.x, max.y, min.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(min.x, min.y, max.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(max.x, min.y, max.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(min.x, max.y, max.z)));
            bounds.Encapsulate(meshToResult.MultiplyPoint3x4(new Vector3(max.x, max.y, max.z)));

            return true;
        }

        /// <summary>
        /// 两个 Mesh 的包围盒已经分离时，直接构造布尔结果，避免进入 BSP 重计算。
        /// </summary>
        private static Mesh BuildSeparatedResult(
            MeshData lhsData,
            MeshData rhsData,
            MeshToolAttributeMask attributes,
            int subMeshCount,
            MeshBooleanOperation operation)
        {
            MeshBuilder builder = new MeshBuilder(attributes, subMeshCount);

            switch (operation)
            {
                case MeshBooleanOperation.Union:
                    AddTriangles(builder, lhsData.Triangles);
                    AddTriangles(builder, rhsData.Triangles);
                    break;

                case MeshBooleanOperation.Subtract:
                    AddTriangles(builder, lhsData.Triangles);
                    break;

                case MeshBooleanOperation.Intersect:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }

            return builder.ToMesh("MeshBoolean_" + operation);
        }


        /// <summary>
        /// 把三角形列表直接追加到 MeshBuilder。
        /// </summary>
        private static void AddTriangles(MeshBuilder builder, List<Triangle> triangles)
        {
            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle triangle = triangles[i];
                builder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
            }
        }

        /// <summary>
        /// 根据三角形顶点计算包围盒；空列表返回 false。
        /// </summary>
        private static bool TryGetBounds(List<Triangle> triangles, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (triangles == null || triangles.Count == 0)
            {
                return false;
            }

            Triangle firstTriangle = triangles[0];
            bounds = new Bounds(firstTriangle.A.Position, Vector3.zero);

            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle triangle = triangles[i];
                bounds.Encapsulate(triangle.A.Position);
                bounds.Encapsulate(triangle.B.Position);
                bounds.Encapsulate(triangle.C.Position);
            }

            return true;
        }

        /// <summary>
        /// 对两个 MeshFilter 求并集，默认把结果放在 lhs 的本地空间。
        /// </summary>
        public static Mesh Union(MeshFilter lhs, MeshFilter rhs, Transform resultSpace = null)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Union, resultSpace);
        }

        /// <summary>
        /// 对两个 MeshFilter 求差集，默认把结果放在 lhs 的本地空间。
        /// </summary>
        public static Mesh Subtract(MeshFilter lhs, MeshFilter rhs, Transform resultSpace = null)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Subtract, resultSpace);
        }

        /// <summary>
        /// 对两个 MeshFilter 求交集，默认把结果放在 lhs 的本地空间。
        /// </summary>
        public static Mesh Intersect(MeshFilter lhs, MeshFilter rhs, Transform resultSpace = null)
        {
            return Evaluate(lhs, rhs, MeshBooleanOperation.Intersect, resultSpace);
        }

        /// <summary>
        /// 用 MeshFilter 的世界矩阵执行布尔运算，并返回 resultSpace 本地空间下的结果。
        /// resultSpace 为 null 时，结果使用 lhs 的本地空间。
        /// </summary>
        public static Mesh Evaluate(
            MeshFilter lhs,
            MeshFilter rhs,
            MeshBooleanOperation operation,
            Transform resultSpace = null)
        {
            if (lhs == null)
            {
                throw new ArgumentNullException(nameof(lhs));
            }

            if (rhs == null)
            {
                throw new ArgumentNullException(nameof(rhs));
            }

            if (lhs.sharedMesh == null)
            {
                throw new ArgumentException("Left MeshFilter has no sharedMesh.", nameof(lhs));
            }

            if (rhs.sharedMesh == null)
            {
                throw new ArgumentException("Right MeshFilter has no sharedMesh.", nameof(rhs));
            }

            // MeshFilter 版本复用 Mesh + Transform 接口，保证所有世界空间重载语义一致。
            return EvaluateWorld(lhs.sharedMesh, lhs.transform, rhs.sharedMesh, rhs.transform, operation, resultSpace);
        }

        /// <summary>
        /// 计算两个多边形集合的并集。
        /// </summary>
        private static List<CsgPolygon> UnionPolygons(List<CsgPolygon> lhs, List<CsgPolygon> rhs)
        {
            CsgNode a = new CsgNode(lhs);
            CsgNode b = new CsgNode(rhs);

            // 互相裁掉落在对方内部的面，再把剩余边界合并成一个实体。
            a.ClipTo(b);
            b.ClipTo(a);
            b.Invert();
            b.ClipTo(a);
            b.Invert();
            a.Build(b.AllPolygons());

            return a.AllPolygons();
        }

        /// <summary>
        /// 计算 lhs 减 rhs 的差集。
        /// </summary>
        private static List<CsgPolygon> SubtractPolygons(List<CsgPolygon> lhs, List<CsgPolygon> rhs)
        {
            CsgNode a = new CsgNode(lhs);
            CsgNode b = new CsgNode(rhs);

            // 差集可以理解为把 A 反转成“外部空间”，裁剪后再把 B 的内壁补进来，最后反转回去。
            a.Invert();
            a.ClipTo(b);
            b.ClipTo(a);
            b.Invert();
            b.ClipTo(a);
            b.Invert();
            a.Build(b.AllPolygons());
            a.Invert();

            return a.AllPolygons();
        }

        /// <summary>
        /// 计算两个多边形集合的交集。
        /// </summary>
        private static List<CsgPolygon> IntersectPolygons(List<CsgPolygon> lhs, List<CsgPolygon> rhs)
        {
            CsgNode a = new CsgNode(lhs);
            CsgNode b = new CsgNode(rhs);

            // 交集通过多次反转和裁剪，只保留同时位于两个实体内部的边界面。
            a.Invert();
            b.ClipTo(a);
            b.Invert();
            a.ClipTo(b);
            b.ClipTo(a);
            a.Build(b.AllPolygons());
            a.Invert();

            return a.AllPolygons();
        }

        /// <summary>
        /// 把三角形数据包装成 CSG 多边形，退化三角形会被过滤掉。
        /// </summary>
        private static List<CsgPolygon> ToPolygons(List<Triangle> triangles)
        {
            List<CsgPolygon> polygons = new List<CsgPolygon>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle triangle = triangles[i];
                List<Vertex> vertices = new List<Vertex>(3)
                {
                    triangle.A,
                    triangle.B,
                    triangle.C
                };

                CsgPolygon polygon;
                if (CsgPolygon.TryCreate(vertices, triangle.SubMesh, out polygon))
                {
                    polygons.Add(polygon);
                }
            }

            return polygons;
        }

        private sealed class CsgNode
        {
            private CsgPlane plane;
            private CsgNode front;
            private CsgNode back;
            private List<CsgPolygon> polygons = new List<CsgPolygon>();

            /// <summary>
            /// 创建一个空 BSP 节点。
            /// </summary>
            public CsgNode()
            {
            }

            /// <summary>
            /// 用一组多边形创建 BSP 树。
            /// </summary>
            public CsgNode(List<CsgPolygon> sourcePolygons)
            {
                Build(ClonePolygons(sourcePolygons));
            }

            /// <summary>
            /// 收集当前 BSP 树中的所有多边形。
            /// </summary>
            public List<CsgPolygon> AllPolygons()
            {
                List<CsgPolygon> result = new List<CsgPolygon>();
                Stack<CsgNode> nodes = new Stack<CsgNode>();
                nodes.Push(this);

                while (nodes.Count > 0)
                {
                    CsgNode node = nodes.Pop();
                    result.AddRange(node.polygons);

                    if (node.back != null)
                    {
                        nodes.Push(node.back);
                    }

                    if (node.front != null)
                    {
                        nodes.Push(node.front);
                    }
                }

                return result;
            }

            /// <summary>
            /// 把多边形插入 BSP 树，跨越分割平面的多边形会被切成前后两份。
            /// </summary>
            public void Build(List<CsgPolygon> sourcePolygons)
            {
                if (sourcePolygons == null || sourcePolygons.Count == 0)
                {
                    return;
                }

                Stack<BuildTask> tasks = new Stack<BuildTask>();
                tasks.Push(new BuildTask(this, sourcePolygons));

                while (tasks.Count > 0)
                {
                    BuildTask task = tasks.Pop();
                    task.Node.BuildNode(task.Polygons, tasks);
                }
            }

            /// <summary>
            /// 构建单个 BSP 节点，子节点通过 tasks 继续迭代处理，避免递归过深。
            /// </summary>
            private void BuildNode(List<CsgPolygon> sourcePolygons, Stack<BuildTask> tasks)
            {
                if (plane == null)
                {
                    // 从少量候选面中选一个更平衡的分割平面，避免 BSP 退化成长链导致编辑器长时间卡住。
                    plane = sourcePolygons[SelectSplitPolygonIndex(sourcePolygons)].Plane.Clone();
                }

                List<CsgPolygon> frontPolygons = new List<CsgPolygon>();
                List<CsgPolygon> backPolygons = new List<CsgPolygon>();

                // 当前节点保存共面的面，其余面按分割平面递归放到前后子树。
                for (int i = 0; i < sourcePolygons.Count; i++)
                {
                    plane.SplitPolygon(sourcePolygons[i], polygons, polygons, frontPolygons, backPolygons);
                }

                // 数值退化时，候选分割平面可能没有吸收任何共面面，也没有减少待分类集合。
                // 这会让同一批 polygon 被无限压入同一侧子节点，导入预览时看起来像卡死在 ContinueQueuedPreviewRebuild。
                if (polygons.Count == 0
                    && ((frontPolygons.Count == sourcePolygons.Count && backPolygons.Count == 0)
                        || (backPolygons.Count == sourcePolygons.Count && frontPolygons.Count == 0)))
                {
                    polygons.AddRange(sourcePolygons);
                    return;
                }

                if (frontPolygons.Count > 0)
                {
                    if (front == null)
                    {
                        front = new CsgNode();
                    }

                    tasks.Push(new BuildTask(front, frontPolygons));
                }

                if (backPolygons.Count > 0)
                {
                    if (back == null)
                    {
                        back = new CsgNode();
                    }

                    tasks.Push(new BuildTask(back, backPolygons));
                }
            }

            /// <summary>
            /// 用另一个 BSP 树裁剪当前树，移除位于对方实体内部的多边形。
            /// </summary>
            public void ClipTo(CsgNode bsp)
            {
                Stack<CsgNode> nodes = new Stack<CsgNode>();
                nodes.Push(this);

                while (nodes.Count > 0)
                {
                    CsgNode node = nodes.Pop();
                    node.polygons = bsp.ClipPolygons(node.polygons);

                    if (node.back != null)
                    {
                        nodes.Push(node.back);
                    }

                    if (node.front != null)
                    {
                        nodes.Push(node.front);
                    }
                }
            }

            /// <summary>
            /// 返回 sourcePolygons 中位于当前 BSP 外部的部分。
            /// </summary>
            public List<CsgPolygon> ClipPolygons(List<CsgPolygon> sourcePolygons)
            {
                if (plane == null)
                {
                    return new List<CsgPolygon>(sourcePolygons);
                }

                List<CsgPolygon> lastResult = null;
                Stack<ClipTask> tasks = new Stack<ClipTask>();
                tasks.Push(new ClipTask(this, sourcePolygons));

                while (tasks.Count > 0)
                {
                    ClipTask task = tasks.Pop();

                    if (task.Stage == 0)
                    {
                        if (task.Node.plane == null)
                        {
                            lastResult = new List<CsgPolygon>(task.Input);
                            continue;
                        }

                        List<CsgPolygon> frontPolygons = new List<CsgPolygon>();
                        List<CsgPolygon> backPolygons = new List<CsgPolygon>();

                        // 先按当前节点分割，再继续交给前后子树判断。
                        for (int i = 0; i < task.Input.Count; i++)
                        {
                            task.Node.plane.SplitPolygon(task.Input[i], frontPolygons, backPolygons, frontPolygons, backPolygons);
                        }

                        task.FrontPolygons = frontPolygons;
                        task.BackPolygons = backPolygons;

                        if (task.Node.front != null)
                        {
                            task.Stage = 1;
                            tasks.Push(task);
                            tasks.Push(new ClipTask(task.Node.front, frontPolygons));
                            continue;
                        }

                        task.Stage = 1;
                    }

                    if (task.Stage == 1)
                    {
                        if (task.Node.front != null)
                        {
                            task.FrontPolygons = lastResult;
                        }

                        if (task.Node.back != null)
                        {
                            task.Stage = 2;
                            tasks.Push(task);
                            tasks.Push(new ClipTask(task.Node.back, task.BackPolygons));
                            continue;
                        }

                        // 没有 back 子树时，back 侧代表实体内部，直接丢弃。
                        task.BackPolygons.Clear();
                        task.FrontPolygons.AddRange(task.BackPolygons);
                        lastResult = task.FrontPolygons;
                        continue;
                    }

                    task.BackPolygons = lastResult;
                    task.FrontPolygons.AddRange(task.BackPolygons);
                    lastResult = task.FrontPolygons;
                }

                return lastResult ?? new List<CsgPolygon>();
            }

            /// <summary>
            /// 反转 BSP 树的内外侧，所有面翻面并交换前后子树。
            /// </summary>
            public void Invert()
            {
                Stack<CsgNode> nodes = new Stack<CsgNode>();
                nodes.Push(this);

                while (nodes.Count > 0)
                {
                    CsgNode node = nodes.Pop();
                    for (int i = 0; i < node.polygons.Count; i++)
                    {
                        node.polygons[i].Flip();
                    }

                    if (node.plane != null)
                    {
                        node.plane.Flip();
                    }

                    CsgNode oldFront = node.front;
                    node.front = node.back;
                    node.back = oldFront;

                    if (node.back != null)
                    {
                        nodes.Push(node.back);
                    }

                    if (node.front != null)
                    {
                        nodes.Push(node.front);
                    }
                }
            }

            /// <summary>
            /// 深拷贝多边形列表，避免构建 BSP 时改动调用者传入的数据。
            /// </summary>
            private static List<CsgPolygon> ClonePolygons(List<CsgPolygon> source)
            {
                List<CsgPolygon> clone = new List<CsgPolygon>(source.Count);
                for (int i = 0; i < source.Count; i++)
                {
                    clone.Add(source[i].Clone());
                }

                return clone;
            }

            /// <summary>
            /// 从多边形列表中挑一个较适合作为 BSP 分割平面的面。
            /// </summary>
            private static int SelectSplitPolygonIndex(List<CsgPolygon> sourcePolygons)
            {
                if (sourcePolygons.Count <= 2)
                {
                    return 0;
                }

                int bestIndex = 0;
                int bestScore = int.MaxValue;
                int candidateCount = Math.Min(12, sourcePolygons.Count);
                int step = Math.Max(1, sourcePolygons.Count / candidateCount);
                int tested = 0;

                for (int i = 0; i < sourcePolygons.Count && tested < candidateCount; i += step)
                {
                    int score = ScoreSplitPlane(sourcePolygons[i].Plane, sourcePolygons, bestScore);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }

                    tested++;
                }

                int lastIndex = sourcePolygons.Count - 1;
                int lastScore = ScoreSplitPlane(sourcePolygons[lastIndex].Plane, sourcePolygons, bestScore);
                if (lastScore < bestScore)
                {
                    bestIndex = lastIndex;
                }

                return bestIndex;
            }

            /// <summary>
            /// 评分一个候选分割平面；切开的面越少、前后越均衡，分数越低。
            /// </summary>
            private static int ScoreSplitPlane(CsgPlane splitPlane, List<CsgPolygon> polygons, int stopWhenAboveScore)
            {
                int frontCount = 0;
                int backCount = 0;
                int splitCount = 0;

                for (int i = 0; i < polygons.Count; i++)
                {
                    int classification = ClassifyPolygon(splitPlane, polygons[i]);
                    if (classification == 1)
                    {
                        frontCount++;
                    }
                    else if (classification == 2)
                    {
                        backCount++;
                    }
                    else if (classification == 3)
                    {
                        splitCount++;
                    }

                    int partialScore = splitCount * 16 + Math.Abs(frontCount - backCount);
                    if (partialScore > stopWhenAboveScore)
                    {
                        return partialScore;
                    }
                }

                return splitCount * 16 + Math.Abs(frontCount - backCount);
            }

            /// <summary>
            /// 判断一个多边形相对候选平面位于前侧、后侧、共面或跨平面。
            /// </summary>
            private static int ClassifyPolygon(CsgPlane splitPlane, CsgPolygon polygon)
            {
                int classification = 0;
                for (int i = 0; i < polygon.Vertices.Count; i++)
                {
                    float distance = Vector3.Dot(splitPlane.Normal, polygon.Vertices[i].Position) - splitPlane.W;
                    if (distance < -MeshToolGeometry.Epsilon)
                    {
                        classification |= 2;
                    }
                    else if (distance > MeshToolGeometry.Epsilon)
                    {
                        classification |= 1;
                    }
                }

                return classification;
            }

            private struct BuildTask
            {
                public CsgNode Node;
                public List<CsgPolygon> Polygons;

                /// <summary>
                /// 创建一个 BSP 构树任务。
                /// </summary>
                public BuildTask(CsgNode node, List<CsgPolygon> polygons)
                {
                    Node = node;
                    Polygons = polygons;
                }
            }

            private sealed class ClipTask
            {
                public CsgNode Node;
                public List<CsgPolygon> Input;
                public List<CsgPolygon> FrontPolygons;
                public List<CsgPolygon> BackPolygons;
                public int Stage;

                /// <summary>
                /// 创建一个 BSP 裁剪任务。
                /// </summary>
                public ClipTask(CsgNode node, List<CsgPolygon> input)
                {
                    Node = node;
                    Input = input;
                }
            }
        }

        private sealed class CsgPlane
        {
            private const int Coplanar = 0;
            private const int Front = 1;
            private const int Back = 2;
            private const int Spanning = 3;

            public Vector3 Normal;
            public float W;

            /// <summary>
            /// 创建一个 CSG 分割平面，W 是法线点乘平面上点得到的常量项。
            /// </summary>
            public CsgPlane(Vector3 normal, float w)
            {
                Normal = normal;
                W = w;
            }

            /// <summary>
            /// 尝试从多边形顶点中找出一个非退化平面。
            /// </summary>
            public static bool TryCreate(IList<Vertex> vertices, out CsgPlane plane)
            {
                // 顶点可能因为裁剪产生重复或共线，所以尝试所有三点组合。
                for (int i = 0; i < vertices.Count - 2; i++)
                {
                    for (int j = i + 1; j < vertices.Count - 1; j++)
                    {
                        for (int k = j + 1; k < vertices.Count; k++)
                        {
                            Vector3 normal;
                            if (MeshToolGeometry.TryGetFaceNormal(vertices[i].Position, vertices[j].Position, vertices[k].Position, out normal))
                            {
                                plane = new CsgPlane(normal, Vector3.Dot(normal, vertices[i].Position));
                                return true;
                            }
                        }
                    }
                }

                plane = null;
                return false;
            }

            /// <summary>
            /// 克隆当前平面。
            /// </summary>
            public CsgPlane Clone()
            {
                return new CsgPlane(Normal, W);
            }

            /// <summary>
            /// 翻转平面朝向。
            /// </summary>
            public void Flip()
            {
                Normal = -Normal;
                W = -W;
            }

            /// <summary>
            /// 按当前平面对多边形分类，跨平面的多边形会被切开。
            /// </summary>
            public void SplitPolygon(
                CsgPolygon polygon,
                List<CsgPolygon> coplanarFront,
                List<CsgPolygon> coplanarBack,
                List<CsgPolygon> front,
                List<CsgPolygon> back)
            {
                int polygonType = 0;
                int[] types = new int[polygon.Vertices.Count];

                // 用位标记汇总所有顶点所处侧：全前、全后、共面或跨平面。
                for (int i = 0; i < polygon.Vertices.Count; i++)
                {
                    float distance = Vector3.Dot(Normal, polygon.Vertices[i].Position) - W;
                    int type = distance < -MeshToolGeometry.Epsilon
                        ? Back
                        : distance > MeshToolGeometry.Epsilon
                            ? Front
                            : Coplanar;
                    polygonType |= type;
                    types[i] = type;
                }

                switch (polygonType)
                {
                    case Coplanar:
                        // 共面时根据面法线方向决定归入正向共面还是反向共面列表。
                        if (Vector3.Dot(Normal, polygon.Plane.Normal) > 0f)
                        {
                            coplanarFront.Add(polygon);
                        }
                        else
                        {
                            coplanarBack.Add(polygon);
                        }

                        break;

                    case Front:
                        front.Add(polygon);
                        break;

                    case Back:
                        back.Add(polygon);
                        break;

                    case Spanning:
                        SplitSpanningPolygon(polygon, types, front, back);
                        break;
                }
            }

            /// <summary>
            /// 把跨越当前平面的多边形切成前后两个新多边形。
            /// </summary>
            private void SplitSpanningPolygon(
                CsgPolygon polygon,
                int[] types,
                List<CsgPolygon> front,
                List<CsgPolygon> back)
            {
                List<Vertex> frontVertices = new List<Vertex>();
                List<Vertex> backVertices = new List<Vertex>();

                // 逐边遍历，多边形边穿过平面时插入一个插值顶点。
                for (int i = 0; i < polygon.Vertices.Count; i++)
                {
                    int j = (i + 1) % polygon.Vertices.Count;
                    int ti = types[i];
                    int tj = types[j];
                    Vertex vi = polygon.Vertices[i];
                    Vertex vj = polygon.Vertices[j];

                    if (ti != Back)
                    {
                        frontVertices.Add(vi);
                    }

                    if (ti != Front)
                    {
                        backVertices.Add(vi);
                    }

                    if ((ti | tj) == Spanning)
                    {
                        Vector3 edge = vj.Position - vi.Position;
                        float denominator = Vector3.Dot(Normal, edge);
                        if (Mathf.Abs(denominator) <= MeshToolGeometry.Epsilon)
                        {
                            continue;
                        }

                        float t = Mathf.Clamp01((W - Vector3.Dot(Normal, vi.Position)) / denominator);
                        Vertex vertex = Vertex.Lerp(vi, vj, t);
                        frontVertices.Add(vertex);
                        backVertices.Add(vertex);
                    }
                }

                // 切出来的前后多边形可能退化，TryCreate 会负责过滤。
                CsgPolygon frontPolygon;
                if (CsgPolygon.TryCreate(frontVertices, polygon.SubMesh, out frontPolygon))
                {
                    front.Add(frontPolygon);
                }

                CsgPolygon backPolygon;
                if (CsgPolygon.TryCreate(backVertices, polygon.SubMesh, out backPolygon))
                {
                    back.Add(backPolygon);
                }
            }
        }

        private sealed class CsgPolygon
        {
            public List<Vertex> Vertices;
            public CsgPlane Plane;
            public int SubMesh;

            /// <summary>
            /// 创建一个 CSG 多边形，调用方需保证顶点和平面有效。
            /// </summary>
            private CsgPolygon(List<Vertex> vertices, CsgPlane plane, int subMesh)
            {
                Vertices = vertices;
                Plane = plane;
                SubMesh = subMesh;
            }

            /// <summary>
            /// 清理顶点并创建 CSG 多边形，退化多边形会返回 false。
            /// </summary>
            public static bool TryCreate(IList<Vertex> sourceVertices, int subMesh, out CsgPolygon polygon)
            {
                List<Vertex> vertices = CleanVertices(sourceVertices);
                CsgPlane plane;
                if (vertices.Count < 3 || !CsgPlane.TryCreate(vertices, out plane))
                {
                    polygon = null;
                    return false;
                }

                polygon = new CsgPolygon(vertices, plane, subMesh);
                return true;
            }

            /// <summary>
            /// 克隆当前多边形和平面数据。
            /// </summary>
            public CsgPolygon Clone()
            {
                return new CsgPolygon(new List<Vertex>(Vertices), Plane.Clone(), SubMesh);
            }

            /// <summary>
            /// 翻转多边形绕序、顶点法线以及所属平面。
            /// </summary>
            public void Flip()
            {
                Vertices.Reverse();
                for (int i = 0; i < Vertices.Count; i++)
                {
                    Vertices[i] = Vertices[i].Flipped();
                }

                Plane.Flip();
            }

            /// <summary>
            /// 清理连续重复点和共线点，减少裁剪后生成退化面的概率。
            /// </summary>
            private static List<Vertex> CleanVertices(IList<Vertex> source)
            {
                List<Vertex> result = new List<Vertex>(source.Count);
                for (int i = 0; i < source.Count; i++)
                {
                    Vertex vertex = source[i];
                    if (result.Count == 0 || !MeshToolGeometry.SamePosition(result[result.Count - 1].Position, vertex.Position))
                    {
                        result.Add(vertex);
                    }
                }

                if (result.Count > 1 && MeshToolGeometry.SamePosition(result[0].Position, result[result.Count - 1].Position))
                {
                    result.RemoveAt(result.Count - 1);
                }

                // 循环删除共线点，因为删掉一个点后相邻边可能继续变成共线。
                bool removed;
                do
                {
                    removed = false;
                    if (result.Count <= 3)
                    {
                        break;
                    }

                    for (int i = 0; i < result.Count; i++)
                    {
                        Vector3 previous = result[(i + result.Count - 1) % result.Count].Position;
                        Vector3 current = result[i].Position;
                        Vector3 next = result[(i + 1) % result.Count].Position;
                        if (Vector3.Cross(current - previous, next - current).sqrMagnitude <= MeshToolGeometry.EpsilonSqr)
                        {
                            result.RemoveAt(i);
                            removed = true;
                            break;
                        }
                    }
                }
                while (removed);

                return result;
            }
        }
    }
}
