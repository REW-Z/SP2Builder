using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTools
{
    public struct MeshSliceResult
    {
        public Mesh Positive;
        public Mesh Negative;
        public bool Intersects;

        /// <summary>
        /// 保存一次平面切割得到的正侧、负侧结果以及是否真的发生相交。
        /// </summary>
        public MeshSliceResult(Mesh positive, Mesh negative, bool intersects)
        {
            Positive = positive;
            Negative = negative;
            Intersects = intersects;
        }
    }

    internal struct PreviewMeshSliceResult
    {
        public PreviewMeshData Positive;
        public PreviewMeshData Negative;
        public bool Intersects;

        public PreviewMeshSliceResult(PreviewMeshData positive, PreviewMeshData negative, bool intersects)
        {
            Positive = positive;
            Negative = negative;
            Intersects = intersects;
        }
    }

    /// <summary>
    /// 用平面切割三角网格。正侧表示 Plane.GetDistanceToPoint(vertex) 大于等于 0 的一侧。
    /// </summary>
    public static class MeshPlaneSlicer
    {
        /// <summary>
        /// 切割一个 Mesh 并只返回指定侧的结果。
        /// </summary>
        public static Mesh Cut(Mesh mesh, Plane plane, bool keepPositive, bool cap = true, int capSubMesh = 0)
        {
            if (!IsValidPlane(plane))
            {
                return mesh;
            }

            MeshSliceResult result = Slice(mesh, plane, cap, capSubMesh);
            return keepPositive ? result.Positive : result.Negative;
        }

        internal static PreviewMeshData Cut(PreviewMeshData mesh, Plane plane, bool keepPositive, bool cap = true, int capSubMesh = 0)
        {
            if (!IsValidPlane(plane))
            {
                return mesh;
            }

            PreviewMeshSliceResult result = Slice(mesh, plane, cap, capSubMesh);
            return keepPositive ? result.Positive : result.Negative;
        }

        /// <summary>
        /// 在 Mesh 当前坐标空间中切割，并返回正负两侧结果。
        /// </summary>
        public static MeshSliceResult Slice(Mesh mesh, Plane plane, bool cap = true, int capSubMesh = 0)
        {
            return Slice(mesh, Matrix4x4.identity, plane, cap, capSubMesh);
        }

        internal static PreviewMeshSliceResult Slice(PreviewMeshData mesh, Plane plane, bool cap = true, int capSubMesh = 0)
        {
            return Slice(mesh, Matrix4x4.identity, plane, cap, capSubMesh);
        }

        /// <summary>
        /// 先把 Mesh 顶点变换到结果空间，再用同一结果空间下的平面切割。
        /// </summary>
        public static MeshSliceResult Slice(
            Mesh mesh,
            Matrix4x4 meshToResult,
            Plane planeInResult,
            bool cap = true,
            int capSubMesh = 0)
        {
            // 切割判断依赖 signed distance，所以先保证平面法线长度为 1。
            if (!TryNormalizePlane(planeInResult, out Plane plane))
            {
                return CreateNoOpSliceResult(mesh, meshToResult);
            }

            MeshData data = MeshToolGeometry.ParseTriangles(mesh, meshToResult);
            int subMeshCount = cap ? Mathf.Max(data.SubMeshCount, capSubMesh + 1) : data.SubMeshCount;

            // 正负两侧各自独立构建；capSegments 用来稍后拼接切口封盖。
            MeshBuilder positiveBuilder = new MeshBuilder(data.Attributes, subMeshCount);
            MeshBuilder negativeBuilder = new MeshBuilder(data.Attributes, subMeshCount);
            List<CapSegment> capSegments = new List<CapSegment>();
            bool intersects = false;

            for (int i = 0; i < data.Triangles.Count; i++)
            {
                Triangle triangle = data.Triangles[i];
                Vertex[] vertices =
                {
                    triangle.A,
                    triangle.B,
                    triangle.C
                };

                float[] distances =
                {
                    plane.GetDistanceToPoint(vertices[0].Position),
                    plane.GetDistanceToPoint(vertices[1].Position),
                    plane.GetDistanceToPoint(vertices[2].Position)
                };
                SnapDs(distances);

                // 全在某一侧的三角形可以直接拷贝；跨平面的三角形才需要裁剪。
                bool positiveSide = distances[0] >= -MeshToolGeometry.Epsilon &&
                    distances[1] >= -MeshToolGeometry.Epsilon &&
                    distances[2] >= -MeshToolGeometry.Epsilon;
                bool negativeSide = distances[0] <= MeshToolGeometry.Epsilon &&
                    distances[1] <= MeshToolGeometry.Epsilon &&
                    distances[2] <= MeshToolGeometry.Epsilon;

                if (positiveSide)
                {
                    positiveBuilder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
                }

                if (negativeSide)
                {
                    negativeBuilder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
                }

                if (!positiveSide && !negativeSide)
                {
                    intersects = true;
                    // 三角形被平面切到时，分别裁出正侧和负侧的多边形。
                    List<Vertex> positivePolygon = ClipPolygon(vertices, distances, true);
                    List<Vertex> negativePolygon = ClipPolygon(vertices, distances, false);
                    positiveBuilder.AddPolygon(positivePolygon, triangle.SubMesh);
                    negativeBuilder.AddPolygon(negativePolygon, triangle.SubMesh);

                    // 每个被切开的三角形贡献一条切口线段，所有线段稍后会串成封盖环。
                    CapSegment capSegment;
                    if (cap && TryGetCapSegment(vertices, distances, out capSegment))
                    {
                        capSegments.Add(capSegment);
                    }
                }
                else if (cap && TryGetOnPlaneSeg(vertices, distances, out CapSegment onPlaneSegment))
                {
                    intersects = true;
                    capSegments.Add(onPlaneSegment);
                }
            }

            // 封盖需要正负两侧使用相反朝向，保证结果仍是闭合体。
            if (cap && capSegments.Count > 0)
            {
                AddCaps(positiveBuilder, capSegments, plane, -plane.normal, capSubMesh);
                AddCaps(negativeBuilder, capSegments, plane, plane.normal, capSubMesh);
            }

            return new MeshSliceResult(
                positiveBuilder.ToMesh("MeshSlice_Positive"),
                negativeBuilder.ToMesh("MeshSlice_Negative"),
                intersects);
        }

        internal static PreviewMeshSliceResult Slice(
            PreviewMeshData mesh,
            Matrix4x4 meshToResult,
            Plane planeInResult,
            bool cap = true,
            int capSubMesh = 0)
        {
            if (!TryNormalizePlane(planeInResult, out Plane plane))
            {
                return CreateNoOpSliceResult(mesh, meshToResult);
            }

            MeshData data = MeshToolGeometry.ParseTriangles(mesh, meshToResult);
            int subMeshCount = cap ? Mathf.Max(data.SubMeshCount, capSubMesh + 1) : data.SubMeshCount;

            MeshBuilder positiveBuilder = new MeshBuilder(data.Attributes, subMeshCount);
            MeshBuilder negativeBuilder = new MeshBuilder(data.Attributes, subMeshCount);
            List<CapSegment> capSegments = new List<CapSegment>();
            bool intersects = false;

            for (int i = 0; i < data.Triangles.Count; i++)
            {
                Triangle triangle = data.Triangles[i];
                Vertex[] vertices =
                {
                    triangle.A,
                    triangle.B,
                    triangle.C
                };

                float[] distances =
                {
                    plane.GetDistanceToPoint(vertices[0].Position),
                    plane.GetDistanceToPoint(vertices[1].Position),
                    plane.GetDistanceToPoint(vertices[2].Position)
                };
                SnapDs(distances);

                bool positiveSide = distances[0] >= -MeshToolGeometry.Epsilon &&
                    distances[1] >= -MeshToolGeometry.Epsilon &&
                    distances[2] >= -MeshToolGeometry.Epsilon;
                bool negativeSide = distances[0] <= MeshToolGeometry.Epsilon &&
                    distances[1] <= MeshToolGeometry.Epsilon &&
                    distances[2] <= MeshToolGeometry.Epsilon;

                if (positiveSide)
                {
                    positiveBuilder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
                }

                if (negativeSide)
                {
                    negativeBuilder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
                }

                if (!positiveSide && !negativeSide)
                {
                    intersects = true;
                    List<Vertex> positivePolygon = ClipPolygon(vertices, distances, true);
                    List<Vertex> negativePolygon = ClipPolygon(vertices, distances, false);
                    positiveBuilder.AddPolygon(positivePolygon, triangle.SubMesh);
                    negativeBuilder.AddPolygon(negativePolygon, triangle.SubMesh);

                    if (cap && TryGetCapSegment(vertices, distances, out CapSegment capSegment))
                    {
                        capSegments.Add(capSegment);
                    }
                }
                else if (cap && TryGetOnPlaneSeg(vertices, distances, out CapSegment onPlaneSegment))
                {
                    intersects = true;
                    capSegments.Add(onPlaneSegment);
                }
            }

            if (cap && capSegments.Count > 0)
            {
                AddCaps(positiveBuilder, capSegments, plane, -plane.normal, capSubMesh);
                AddCaps(negativeBuilder, capSegments, plane, plane.normal, capSubMesh);
            }

            return new PreviewMeshSliceResult(
                positiveBuilder.ToPreviewMeshData("MeshSlice_Positive"),
                negativeBuilder.ToPreviewMeshData("MeshSlice_Negative"),
                intersects);
        }

        /// <summary>
        /// 用世界空间平面切割 MeshFilter，并只返回指定侧结果。
        /// </summary>
        public static Mesh Cut(
            MeshFilter source,
            Plane worldPlane,
            bool keepPositive,
            bool cap = true,
            int capSubMesh = 0,
            Transform resultSpace = null)
        {
            if (!IsValidPlane(worldPlane))
            {
                return source != null ? source.sharedMesh : null;
            }

            MeshSliceResult result = Slice(source, worldPlane, cap, capSubMesh, resultSpace);
            return keepPositive ? result.Positive : result.Negative;
        }

        /// <summary>
        /// 用世界空间平面切割 MeshFilter，并返回 resultSpace 本地空间下的正负结果。
        /// resultSpace 为 null 时，结果使用 source 的本地空间。
        /// </summary>
        public static MeshSliceResult Slice(
            MeshFilter source,
            Plane worldPlane,
            bool cap = true,
            int capSubMesh = 0,
            Transform resultSpace = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.sharedMesh == null)
            {
                throw new ArgumentException("MeshFilter has no sharedMesh.", nameof(source));
            }

            // 把源 Mesh 和世界平面都变换到同一个结果空间里再切。
            Transform targetSpace = resultSpace != null ? resultSpace : source.transform;
            Matrix4x4 worldToResult = targetSpace != null ? targetSpace.worldToLocalMatrix : Matrix4x4.identity;
            Matrix4x4 meshToResult = worldToResult * source.transform.localToWorldMatrix;
            Plane planeInResult = MeshToolGeometry.TransformPlane(worldPlane, worldToResult);

            return Slice(source.sharedMesh, meshToResult, planeInResult, cap, capSubMesh);
        }

        /// <summary>
        /// 归一化平面法线，同时按相同比例调整 distance。
        /// </summary>
        private static Plane NormalizePlane(Plane plane)
        {
            if (TryNormalizePlane(plane, out Plane normalizedPlane))
            {
                return normalizedPlane;
            }

            throw new ArgumentException("Plane normal cannot be zero.", nameof(plane));
        }

        private static bool TryNormalizePlane(Plane plane, out Plane normalizedPlane)
        {
            Vector3 normal = plane.normal;
            float magnitude = normal.magnitude;
            if (magnitude <= MeshToolGeometry.Epsilon)
            {
                normalizedPlane = default;
                return false;
            }

            normalizedPlane = new Plane(normal / magnitude, plane.distance / magnitude);
            return true;
        }

        private static bool IsValidPlane(Plane plane)
        {
            return plane.normal.sqrMagnitude > MeshToolGeometry.EpsilonSqr;
        }

        private static MeshSliceResult CreateNoOpSliceResult(Mesh mesh, Matrix4x4 meshToResult)
        {
            Mesh positive = CreateTransformedCopy(mesh, meshToResult, "MeshSlice_Positive");
            Mesh negative = CreateTransformedCopy(mesh, meshToResult, "MeshSlice_Negative");
            return new MeshSliceResult(positive, negative, false);
        }

        private static PreviewMeshSliceResult CreateNoOpSliceResult(PreviewMeshData mesh, Matrix4x4 meshToResult)
        {
            PreviewMeshData positive = CreateTransformedCopy(mesh, meshToResult, "MeshSlice_Positive");
            PreviewMeshData negative = CreateTransformedCopy(mesh, meshToResult, "MeshSlice_Negative");
            return new PreviewMeshSliceResult(positive, negative, false);
        }

        private static Mesh CreateTransformedCopy(Mesh mesh, Matrix4x4 meshToResult, string meshName)
        {
            MeshData data = MeshToolGeometry.ParseTriangles(mesh, meshToResult);
            MeshBuilder builder = new MeshBuilder(data.Attributes, data.SubMeshCount);
            AddAllTriangles(builder, data);
            return builder.ToMesh(meshName);
        }

        private static PreviewMeshData CreateTransformedCopy(PreviewMeshData mesh, Matrix4x4 meshToResult, string meshName)
        {
            MeshData data = MeshToolGeometry.ParseTriangles(mesh, meshToResult);
            MeshBuilder builder = new MeshBuilder(data.Attributes, data.SubMeshCount);
            AddAllTriangles(builder, data);
            return builder.ToPreviewMeshData(meshName);
        }

        private static void AddAllTriangles(MeshBuilder builder, MeshData data)
        {
            for (int i = 0; i < data.Triangles.Count; i++)
            {
                Triangle triangle = data.Triangles[i];
                builder.AddTriangle(triangle.A, triangle.B, triangle.C, triangle.SubMesh);
            }
        }

        /// <summary>
        /// 使用 Sutherland-Hodgman 思路，把三角形裁到平面指定侧。
        /// </summary>
        private static List<Vertex> ClipPolygon(Vertex[] vertices, float[] distances, bool keepPositive)
        {
            List<Vertex> output = new List<Vertex>(4);
            for (int i = 0; i < vertices.Length; i++)
            {
                int nextIndex = (i + 1) % vertices.Length;
                Vertex current = vertices[i];
                Vertex next = vertices[nextIndex];
                float currentDistance = distances[i];
                float nextDistance = distances[nextIndex];
                bool currentInside = keepPositive
                    ? currentDistance >= -MeshToolGeometry.Epsilon
                    : currentDistance <= MeshToolGeometry.Epsilon;
                bool nextInside = keepPositive
                    ? nextDistance >= -MeshToolGeometry.Epsilon
                    : nextDistance <= MeshToolGeometry.Epsilon;

                // 按“当前点/下一点是否在保留侧”决定保留端点还是插入交点。
                if (currentInside && nextInside)
                {
                    AddCleanVertex(output, next);
                }
                else if (currentInside && !nextInside)
                {
                    AddCleanVertex(output, IntersectEdge(current, next, currentDistance, nextDistance));
                }
                else if (!currentInside && nextInside)
                {
                    AddCleanVertex(output, IntersectEdge(current, next, currentDistance, nextDistance));
                    AddCleanVertex(output, next);
                }
            }

            // 裁剪后首尾可能落在同一个切割交点，移除重复点避免退化面。
            if (output.Count > 1 && MeshToolGeometry.SamePosition(output[0].Position, output[output.Count - 1].Position))
            {
                output.RemoveAt(output.Count - 1);
            }

            return output;
        }

        /// <summary>
        /// 根据两端到平面的有符号距离，插值得到边和平面的交点顶点。
        /// </summary>
        private static Vertex IntersectEdge(
            Vertex a,
            Vertex b,
            float distanceA,
            float distanceB)
        {
            float denominator = distanceA - distanceB;
            if (Mathf.Abs(denominator) <= MeshToolGeometry.Epsilon)
            {
                return a;
            }

            float t = Mathf.Clamp01(distanceA / denominator);
            return Vertex.Lerp(a, b, t);
        }

        /// <summary>
        /// 追加顶点时合并连续重复点。
        /// </summary>
        private static void AddCleanVertex(List<Vertex> vertices, Vertex vertex)
        {
            if (vertices.Count == 0 || !MeshToolGeometry.SamePosition(vertices[vertices.Count - 1].Position, vertex.Position))
            {
                vertices.Add(vertex);
            }
        }

        /// <summary>
        /// 从一个跨平面的三角形中提取切口线段。
        /// </summary>
        private static bool TryGetCapSegment(Vertex[] vertices, float[] distances, out CapSegment segment)
        {
            List<Vertex> intersections = new List<Vertex>(2);
            // 原始顶点刚好在平面上时，它本身就是切口端点。
            for (int i = 0; i < vertices.Length; i++)
            {
                if (distances[i] == 0f)
                {
                    AddUniqueIntersection(intersections, vertices[i]);
                }
            }

            // 边两端异号时，边与平面之间存在一个交点。
            for (int i = 0; i < vertices.Length; i++)
            {
                int next = (i + 1) % vertices.Length;
                float distanceA = distances[i];
                float distanceB = distances[next];
                if ((distanceA > MeshToolGeometry.Epsilon && distanceB < -MeshToolGeometry.Epsilon) ||
                    (distanceA < -MeshToolGeometry.Epsilon && distanceB > MeshToolGeometry.Epsilon))
                {
                    AddUniqueIntersection(intersections, IntersectEdge(vertices[i], vertices[next], distanceA, distanceB));
                }
            }

            if (intersections.Count < 2)
            {
                segment = default(CapSegment);
                return false;
            }

            if (intersections.Count > 2)
            {
                // 容差附近可能收集到多于两个点，取距离最远的一对作为稳定线段。
                SelectFarthestPair(intersections);
            }

            segment = new CapSegment(intersections[0], intersections[1]);
            return !MeshToolGeometry.SamePosition(segment.A.Position, segment.B.Position);
        }

        /// <summary>
        /// 把接近平面的 signed distance 吸附到 0，避免“顶点几乎在平面上”时不同分支判断不一致。
        /// </summary>
        private static void SnapDs(float[] distances)
        {
            if (distances == null)
            {
                return;
            }

            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = SnapD(distances[i]);
            }
        }

        /// <summary>
        /// 把单个距离吸附到稳定符号：足够接近切割平面时直接视为 0。
        /// </summary>
        private static float SnapD(float distance)
        {
            return Mathf.Abs(distance) <= MeshToolGeometry.Epsilon ? 0f : distance;
        }

        /// <summary>
        /// 当三角形有一整条边恰好落在切割平面上时，把这条共面边也作为 cap 边界收集起来。
        /// </summary>
        private static bool TryGetOnPlaneSeg(Vertex[] vertices, float[] distances, out CapSegment segment)
        {
            segment = default(CapSegment);

            bool hasPositive = false;
            bool hasNegative = false;
            for (int i = 0; i < distances.Length; i++)
            {
                hasPositive |= distances[i] > MeshToolGeometry.Epsilon;
                hasNegative |= distances[i] < -MeshToolGeometry.Epsilon;
            }

            if (hasPositive && hasNegative)
            {
                return false;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                int next = (i + 1) % vertices.Length;
                if (Mathf.Abs(distances[i]) > MeshToolGeometry.Epsilon || Mathf.Abs(distances[next]) > MeshToolGeometry.Epsilon)
                {
                    continue;
                }

                if (MeshToolGeometry.SamePosition(vertices[i].Position, vertices[next].Position))
                {
                    continue;
                }

                segment = new CapSegment(vertices[i], vertices[next]);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 向交点列表加入一个不重复的交点。
        /// </summary>
        private static void AddUniqueIntersection(List<Vertex> intersections, Vertex vertex)
        {
            for (int i = 0; i < intersections.Count; i++)
            {
                if (MeshToolGeometry.SamePosition(intersections[i].Position, vertex.Position))
                {
                    return;
                }
            }

            intersections.Add(vertex);
        }

        /// <summary>
        /// 从候选交点中保留距离最远的一对。
        /// </summary>
        private static void SelectFarthestPair(List<Vertex> intersections)
        {
            int bestA = 0;
            int bestB = 1;
            float bestDistance = -1f;

            for (int i = 0; i < intersections.Count - 1; i++)
            {
                for (int j = i + 1; j < intersections.Count; j++)
                {
                    float distance = (intersections[i].Position - intersections[j].Position).sqrMagnitude;
                    if (distance > bestDistance)
                    {
                        bestDistance = distance;
                        bestA = i;
                        bestB = j;
                    }
                }
            }

            Vertex a = intersections[bestA];
            Vertex b = intersections[bestB];
            intersections.Clear();
            intersections.Add(a);
            intersections.Add(b);
        }

        /// <summary>
        /// 把所有切口线段串成环，并为每个环生成封盖三角形。
        /// </summary>
        private static void AddCaps(
            MeshBuilder builder,
            List<CapSegment> segments,
            Plane plane,
            Vector3 capNormal,
            int capSubMesh)
        {
            PlaneBasis basis = PlaneBasis.FromNormal(plane.normal);
            CapLoopBuilder loopBuilder = new CapLoopBuilder();

            // 先把独立线段加入图结构，再由图结构追踪闭合环。
            for (int i = 0; i < segments.Count; i++)
            {
                loopBuilder.AddSegment(segments[i].A, segments[i].B);
            }

            List<List<Vertex>> loops = loopBuilder.BuildLoops(basis);
            AddCapLoops(builder, loops, basis, capNormal, capSubMesh);

            List<List<Vertex>> openChains = loopBuilder.BuildOpenChains();
            AddOpenCaps(builder, openChains, capNormal.normalized, capSubMesh);
        }

        /// <summary>
        /// 为切口环生成封盖；当一个切平面同时切到空心外环和内环时，生成环形端面而不是把内孔填死。
        /// </summary>
        private static void AddCapLoops(
            MeshBuilder builder,
            List<List<Vertex>> loops,
            PlaneBasis basis,
            Vector3 capNormal,
            int capSubMesh)
        {
            if (loops == null || loops.Count == 0)
            {
                return;
            }

            List<ProjectedCapLoop> projectedLoops = new List<ProjectedCapLoop>(loops.Count);
            for (int i = 0; i < loops.Count; i++)
            {
                ProjectedCapLoop projectedLoop = ProjectCapLoop(loops[i], basis, capNormal);
                if (projectedLoop.Count >= 3 && projectedLoop.Area > MeshToolGeometry.Epsilon)
                {
                    projectedLoops.Add(projectedLoop);
                }
            }

            projectedLoops.Sort((a, b) => b.Area.CompareTo(a.Area));
            bool[] used = new bool[projectedLoops.Count];
            for (int i = 0; i < projectedLoops.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                int innerIndex = -1;
                for (int j = i + 1; j < projectedLoops.Count; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    if (PointInPolygon(projectedLoops[i].Points2D, projectedLoops[j].Centroid))
                    {
                        innerIndex = j;
                        break;
                    }
                }

                if (innerIndex >= 0)
                {
                    AddRimCapLoop(builder, projectedLoops[i], projectedLoops[innerIndex], capNormal.normalized, capSubMesh);
                    used[i] = true;
                    used[innerIndex] = true;
                    continue;
                }

                AddFanCapLoop(builder, projectedLoops[i], capNormal.normalized, capSubMesh);
                used[i] = true;
            }
        }

        /// <summary>
        /// 为一个切口闭合环生成封盖面。
        /// </summary>
        private static void AddCapLoop(
            MeshBuilder builder,
            List<Vertex> loop,
            PlaneBasis basis,
            Vector3 capNormal,
            int capSubMesh)
        {
            if (loop.Count < 3)
            {
                return;
            }

            List<Vertex> vertices = new List<Vertex>(loop.Count);
            List<Vector2> projected = new List<Vector2>(loop.Count);
            Vector3 normal = capNormal.normalized;
            Vector4 tangent = new Vector4(basis.AxisU.x, basis.AxisU.y, basis.AxisU.z, 1f);

            // 把三维切口点投影到切割平面的二维坐标中，方便做耳切三角化。
            for (int i = 0; i < loop.Count; i++)
            {
                Vertex vertex = loop[i];
                vertex.Normal = normal;
                vertex.Tangent = tangent;
                vertex.Uv = Project(vertex.Position, basis);
                vertex.Uv2 = vertex.Uv;
                vertices.Add(vertex);
                projected.Add(vertex.Uv);
            }

            float area = SignedArea(projected);
            if (Mathf.Abs(area) <= MeshToolGeometry.Epsilon)
            {
                return;
            }

            // 耳切算法假设输入为逆时针多边形，面积为负时先翻转。
            if (area < 0f)
            {
                vertices.Reverse();
                projected.Reverse();
            }

            List<int> triangles = Triangulate(projected);
            bool reverseWinding = Vector3.Dot(normal, basis.Normal) < 0f;

            // 正负两侧的封盖法线相反，所以需要按目标法线决定三角形绕序。
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                Vertex a = vertices[triangles[i]];
                Vertex b = vertices[triangles[i + 1]];
                Vertex c = vertices[triangles[i + 2]];

                if (reverseWinding)
                {
                    builder.AddTriangle(a, c, b, capSubMesh);
                }
                else
                {
                    builder.AddTriangle(a, b, c, capSubMesh);
                }
            }
        }

        private static ProjectedCapLoop ProjectCapLoop(List<Vertex> loop, PlaneBasis basis, Vector3 capNormal)
        {
            List<Vertex> vertices = new List<Vertex>(loop.Count);
            List<Vector2> projected = new List<Vector2>(loop.Count);
            Vector3 normal = capNormal.normalized;
            Vector4 tangent = new Vector4(basis.AxisU.x, basis.AxisU.y, basis.AxisU.z, 1f);
            for (int i = 0; i < loop.Count; i++)
            {
                Vertex vertex = loop[i];
                vertex.Normal = normal;
                vertex.Tangent = tangent;
                vertex.Uv = Project(vertex.Position, basis);
                vertex.Uv2 = vertex.Uv;
                vertices.Add(vertex);
                projected.Add(vertex.Uv);
            }

            if (SignedArea(projected) < 0f)
            {
                vertices.Reverse();
                projected.Reverse();
            }

            return new ProjectedCapLoop(vertices, projected);
        }

        private static void AddFanCapLoop(MeshBuilder builder, ProjectedCapLoop loop, Vector3 normal, int capSubMesh)
        {
            if (loop.Count < 3)
            {
                return;
            }

            List<int> triangles = Triangulate(loop.Points2D);
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                AddCapTriangle(builder, loop.Vertices[triangles[i]], loop.Vertices[triangles[i + 1]], loop.Vertices[triangles[i + 2]], normal, capSubMesh);
            }
        }

        private static void AddRimCapLoop(MeshBuilder builder, ProjectedCapLoop outer, ProjectedCapLoop inner, Vector3 normal, int capSubMesh)
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
                    AddCapTriangle(builder, outer.Vertices[outerIndex], inner.Vertices[innerIndex], outer.Vertices[nextOuter], normal, capSubMesh);
                    outerIndex = nextOuter;
                    walkedOuter++;
                    continue;
                }

                if (advanceInner && !advanceOuter)
                {
                    AddCapTriangle(builder, outer.Vertices[outerIndex], inner.Vertices[nextInner], inner.Vertices[innerIndex], normal, capSubMesh);
                    innerIndex = nextInner;
                    walkedInner++;
                    continue;
                }

                if (walkedOuter == outer.Count)
                {
                    AddCapTriangle(builder, outer.Vertices[outerIndex], inner.Vertices[nextInner], inner.Vertices[innerIndex], normal, capSubMesh);
                    innerIndex = nextInner;
                    walkedInner++;
                    continue;
                }

                if (walkedInner == inner.Count)
                {
                    AddCapTriangle(builder, outer.Vertices[outerIndex], inner.Vertices[innerIndex], outer.Vertices[nextOuter], normal, capSubMesh);
                    outerIndex = nextOuter;
                    walkedOuter++;
                    continue;
                }

                AddCapTriangle(builder, outer.Vertices[outerIndex], inner.Vertices[innerIndex], outer.Vertices[nextOuter], normal, capSubMesh);
                AddCapTriangle(builder, outer.Vertices[nextOuter], inner.Vertices[innerIndex], inner.Vertices[nextInner], normal, capSubMesh);
                outerIndex = nextOuter;
                innerIndex = nextInner;
                walkedOuter++;
                walkedInner++;
            }
        }

        // 当切口图没有完全闭成环时，把剩余开链两两桥接成封口三角带。 / Bridge any leftover open chains pairwise into cap strips when the cut graph fails to close into loops.
        private static void AddOpenCaps(MeshBuilder builder, List<List<Vertex>> openChains, Vector3 normal, int capSubMesh)
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
                AddOpenCaps(builder, openChains[bestA], openChains[bestB], normal, capSubMesh);
            }
        }

        // 把两条开链桥接成一条三角带，尽量恢复缺失的截面封口。 / Bridge two open chains into a triangle strip to recover the missing cap patch.
        private static void AddOpenCaps(MeshBuilder builder, List<Vertex> chainA, List<Vertex> chainB, Vector3 normal, int capSubMesh)
        {
            if (chainA.Count < 2 || chainB.Count < 2)
            {
                return;
            }

            if (ChainLen(chainB) > ChainLen(chainA))
            {
                (chainA, chainB) = (chainB, chainA);
            }

            float sameDirection = Vector3.Distance(chainA[0].Position, chainB[0].Position) + Vector3.Distance(chainA[^1].Position, chainB[^1].Position);
            float reversedDirection = Vector3.Distance(chainA[0].Position, chainB[^1].Position) + Vector3.Distance(chainA[^1].Position, chainB[0].Position);
            if (reversedDirection < sameDirection)
            {
                chainB = new List<Vertex>(chainB);
                chainB.Reverse();
            }

            List<float> fractionsA = OpenFractions(chainA);
            List<float> fractionsB = OpenFractions(chainB);
            int[] linksA = OpenLinks(fractionsA, fractionsB);
            int[] linksB = OpenLinks(fractionsB, fractionsA);

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
                    AddCapTriangle(builder, chainA[indexA], chainB[indexB], chainA[nextA], normal, capSubMesh);
                    indexA = nextA;
                    continue;
                }

                if (canAdvanceB && !canAdvanceA)
                {
                    AddCapTriangle(builder, chainA[indexA], chainB[nextB], chainB[indexB], normal, capSubMesh);
                    indexB = nextB;
                    continue;
                }

                if (indexA == chainA.Count - 1)
                {
                    AddCapTriangle(builder, chainA[indexA], chainB[nextB], chainB[indexB], normal, capSubMesh);
                    indexB = nextB;
                    continue;
                }

                if (indexB == chainB.Count - 1)
                {
                    AddCapTriangle(builder, chainA[indexA], chainB[indexB], chainA[nextA], normal, capSubMesh);
                    indexA = nextA;
                    continue;
                }

                AddCapTriangle(builder, chainA[indexA], chainB[indexB], chainA[nextA], normal, capSubMesh);
                AddCapTriangle(builder, chainA[nextA], chainB[indexB], chainB[nextB], normal, capSubMesh);
                indexA = nextA;
                indexB = nextB;
            }
        }

        // 计算一条开链上的归一化累计长度，便于按相近参数桥接两侧。 / Compute normalized cumulative lengths along an open chain so two chains can be bridged by similar parameters.
        private static List<float> OpenFractions(List<Vertex> chain)
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

            if (total <= MeshToolGeometry.Epsilon)
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

        // 在两条开链之间做最近参数匹配，不做闭环回绕。 / Match nearest parameters between two open chains without circular wrapping.
        private static int[] OpenLinks(List<float> source, List<float> target)
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

        // 估计一条开链的总长度，优先把更长的一侧作为主链。 / Estimate the total length of an open chain so the longer side can be treated as the primary chain.
        private static float ChainLen(List<Vertex> chain)
        {
            float length = 0f;
            for (int i = 1; i < chain.Count; i++)
            {
                length += Vector3.Distance(chain[i - 1].Position, chain[i].Position);
            }

            return length;
        }

        private static void AddCapTriangle(MeshBuilder builder, Vertex a, Vertex b, Vertex c, Vector3 normal, int capSubMesh)
        {
            Vector3 cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (cross.sqrMagnitude <= MeshToolGeometry.EpsilonSqr)
            {
                return;
            }

            if (Vector3.Dot(cross, normal) < 0f)
            {
                builder.AddTriangle(a, c, b, capSubMesh);
            }
            else
            {
                builder.AddTriangle(a, b, c, capSubMesh);
            }
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
            perimeter += Vector2.Distance(points[points.Count - 1], points[0]);

            if (perimeter <= MeshToolGeometry.Epsilon)
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

            if (Mathf.Abs(twiceArea) <= MeshToolGeometry.Epsilon)
            {
                Vector2 sum = Vector2.zero;
                for (int i = 0; i < points.Count; i++)
                {
                    sum += points[i];
                }
                return sum / Mathf.Max(1, points.Count);
            }

            float factor = 1f / (3f * twiceArea);
            return new Vector2(centroidX * factor, centroidY * factor);
        }

        private static bool PointInPolygon(List<Vector2> polygon, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                bool crosses = (a.y > point.y) != (b.y > point.y);
                if (crosses)
                {
                    float denominator = b.y - a.y;
                    if (Mathf.Abs(denominator) <= MeshToolGeometry.Epsilon)
                    {
                        continue;
                    }

                    float x = (b.x - a.x) * (point.y - a.y) / denominator + a.x;
                    if (point.x < x)
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }

        private sealed class ProjectedCapLoop
        {
            public ProjectedCapLoop(List<Vertex> vertices, List<Vector2> points2D)
            {
                Vertices = vertices;
                Points2D = points2D;
                Area = Mathf.Abs(SignedArea(points2D));
                Centroid = ComputePolygonCentroid(points2D);
                Fractions = ComputeFractions(points2D);
            }

            public int Count { get { return Vertices.Count; } }

            public List<Vertex> Vertices { get; }

            public List<Vector2> Points2D { get; }

            public List<float> Fractions { get; }

            public float Area { get; }

            public Vector2 Centroid { get; }
        }

        /// <summary>
        /// 将三维点投影到平面局部二维坐标。
        /// </summary>
        private static Vector2 Project(Vector3 position, PlaneBasis basis)
        {
            return new Vector2(Vector3.Dot(position, basis.AxisU), Vector3.Dot(position, basis.AxisV));
        }

        /// <summary>
        /// 计算二维多边形的有符号面积，正值表示逆时针。
        /// </summary>
        private static float SignedArea(List<Vector2> polygon)
        {
            float area = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 current = polygon[i];
                Vector2 next = polygon[(i + 1) % polygon.Count];
                area += current.x * next.y - next.x * current.y;
            }

            return area * 0.5f;
        }

        /// <summary>
        /// 用耳切法把二维简单多边形三角化。
        /// </summary>
        private static List<int> Triangulate(List<Vector2> polygon)
        {
            List<int> triangles = new List<int>();
            List<int> indices = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                indices.Add(i);
            }

            int guard = polygon.Count * polygon.Count;
            // 每次找到一个“耳朵”三角形就从多边形中剪掉一个点。
            while (indices.Count > 3 && guard-- > 0)
            {
                bool foundEar = false;
                for (int i = 0; i < indices.Count; i++)
                {
                    int previous = indices[(i + indices.Count - 1) % indices.Count];
                    int current = indices[i];
                    int next = indices[(i + 1) % indices.Count];

                    // 非凸角不可能是耳朵。
                    if (!IsConvex(polygon[previous], polygon[current], polygon[next]))
                    {
                        continue;
                    }

                    // 候选耳朵内部不能包含其它顶点，否则会生成跨边三角形。
                    bool containsPoint = false;
                    for (int j = 0; j < indices.Count; j++)
                    {
                        int index = indices[j];
                        if (index == previous || index == current || index == next)
                        {
                            continue;
                        }

                        if (PointInTriangle(polygon[index], polygon[previous], polygon[current], polygon[next]))
                        {
                            containsPoint = true;
                            break;
                        }
                    }

                    if (containsPoint)
                    {
                        continue;
                    }

                    triangles.Add(previous);
                    triangles.Add(current);
                    triangles.Add(next);
                    indices.RemoveAt(i);
                    foundEar = true;
                    break;
                }

                if (!foundEar)
                {
                    // 容差或自交数据导致找不到耳朵时，退回扇形三角化，保证有结果输出。
                    AddFanFallback(triangles, indices);
                    indices.Clear();
                    break;
                }
            }

            if (indices.Count == 3)
            {
                triangles.Add(indices[0]);
                triangles.Add(indices[1]);
                triangles.Add(indices[2]);
            }

            return triangles;
        }

        /// <summary>
        /// 判断二维三点是否形成凸角。
        /// </summary>
        private static bool IsConvex(Vector2 a, Vector2 b, Vector2 c)
        {
            return Cross(b - a, c - b) > MeshToolGeometry.Epsilon;
        }

        /// <summary>
        /// 判断点 p 是否在三角形 abc 内部或边上。
        /// </summary>
        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float ab = Cross(b - a, p - a);
            float bc = Cross(c - b, p - b);
            float ca = Cross(a - c, p - c);
            return ab >= -MeshToolGeometry.Epsilon &&
                bc >= -MeshToolGeometry.Epsilon &&
                ca >= -MeshToolGeometry.Epsilon;
        }

        /// <summary>
        /// 计算二维向量叉积的 z 分量。
        /// </summary>
        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        /// <summary>
        /// 当耳切失败时，使用第一个点作为扇形中心补一个保底三角化结果。
        /// </summary>
        private static void AddFanFallback(List<int> triangles, List<int> indices)
        {
            if (indices.Count < 3)
            {
                return;
            }

            int anchor = indices[0];
            for (int i = 1; i + 1 < indices.Count; i++)
            {
                triangles.Add(anchor);
                triangles.Add(indices[i]);
                triangles.Add(indices[i + 1]);
            }
        }

        private struct CapSegment
        {
            public Vertex A;
            public Vertex B;

            /// <summary>
            /// 创建一条切口线段。
            /// </summary>
            public CapSegment(Vertex a, Vertex b)
            {
                A = a;
                B = b;
            }
        }

        private struct PlaneBasis
        {
            public Vector3 Normal;
            public Vector3 AxisU;
            public Vector3 AxisV;

            /// <summary>
            /// 根据法线创建平面上的一组正交二维基向量。
            /// </summary>
            public static PlaneBasis FromNormal(Vector3 normal)
            {
                Vector3 safeNormal = normal.normalized;
                Vector3 axisU = Vector3.Cross(safeNormal, Vector3.up);
                if (axisU.sqrMagnitude <= MeshToolGeometry.EpsilonSqr)
                {
                    // 法线接近世界 Y 轴时，改用 X 轴构造，避免叉积为零。
                    axisU = Vector3.Cross(safeNormal, Vector3.right);
                }

                axisU.Normalize();
                Vector3 axisV = Vector3.Cross(safeNormal, axisU).normalized;

                return new PlaneBasis
                {
                    Normal = safeNormal,
                    AxisU = axisU,
                    AxisV = axisV
                };
            }
        }

        private sealed class CapLoopBuilder
        {
            private const float PointToleranceSqr = MeshToolGeometry.EpsilonSqr * 16f;

            private readonly List<GraphPoint> points = new List<GraphPoint>();
            private readonly HashSet<EdgeKey> unusedEdges = new HashSet<EdgeKey>();

            /// <summary>
            /// 把一条切口线段加入无向图中。
            /// </summary>
            public void AddSegment(Vertex a, Vertex b)
            {
                int ai = FindOrAddPoint(a);
                int bi = FindOrAddPoint(b);
                if (ai == bi)
                {
                    return;
                }

                // EdgeKey 会自动排序端点，因此 A-B 和 B-A 被视为同一条边。
                EdgeKey edge = new EdgeKey(ai, bi);
                if (!unusedEdges.Add(edge))
                {
                    return;
                }

                if (!points[ai].Neighbors.Contains(bi))
                {
                    points[ai].Neighbors.Add(bi);
                }

                if (!points[bi].Neighbors.Contains(ai))
                {
                    points[bi].Neighbors.Add(ai);
                }
            }

            /// <summary>
            /// 从未使用边集合中追踪所有闭合切口环。
            /// </summary>
            public List<List<Vertex>> BuildLoops(PlaneBasis basis)
            {
                List<List<Vertex>> loops = new List<List<Vertex>>();
                while (unusedEdges.Count > 0)
                {
                    EdgeKey startEdge = FirstUnusedEdge();
                    unusedEdges.Remove(startEdge);

                    // 从任意未使用边出发，一直沿相邻边走到回到起点。
                    int start = startEdge.A;
                    int previous = startEdge.A;
                    int current = startEdge.B;
                    List<int> loop = new List<int>
                    {
                        start,
                        current
                    };

                    int guard = points.Count * points.Count;
                    while (current != start && guard-- > 0)
                    {
                        int next = FindNext(current, previous, start, loop.Count, basis);
                        if (next < 0)
                        {
                            break;
                        }

                        unusedEdges.Remove(new EdgeKey(current, next));
                        previous = current;
                        current = next;

                        if (current != start)
                        {
                            loop.Add(current);
                        }
                    }

                    if (current == start && loop.Count >= 3)
                    {
                        // 只有闭合且至少三个点的路径才能生成封盖面。
                        loops.Add(ToVertexLoop(loop));
                    }
                }

                return loops;
            }

            // 从剩余未闭合的线段图里恢复开链，供后续桥接补面。 / Recover open chains from the remaining unclosed segment graph for bridge-cap fallback.
            public List<List<Vertex>> BuildOpenChains()
            {
                List<List<int>> adjacency = new List<List<int>>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    adjacency.Add(new List<int>());
                }

                foreach (EdgeKey edge in unusedEdges)
                {
                    adjacency[edge.A].Add(edge.B);
                    adjacency[edge.B].Add(edge.A);
                }

                List<List<Vertex>> chains = new List<List<Vertex>>();
                bool[] visited = new bool[points.Count];
                Queue<int> queue = new Queue<int>();
                for (int nodeIndex = 0; nodeIndex < points.Count; nodeIndex++)
                {
                    if (visited[nodeIndex] || adjacency[nodeIndex].Count == 0)
                    {
                        continue;
                    }

                    List<int> component = new List<int>();
                    queue.Enqueue(nodeIndex);
                    visited[nodeIndex] = true;
                    while (queue.Count > 0)
                    {
                        int current = queue.Dequeue();
                        component.Add(current);
                        for (int i = 0; i < adjacency[current].Count; i++)
                        {
                            int next = adjacency[current][i];
                            if (visited[next])
                            {
                                continue;
                            }

                            visited[next] = true;
                            queue.Enqueue(next);
                        }
                    }

                    List<int> endpoints = new List<int>(2);
                    bool validChain = true;
                    for (int i = 0; i < component.Count; i++)
                    {
                        int degree = adjacency[component[i]].Count;
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

                    List<Vertex> chain = new List<Vertex>(component.Count);
                    int previous = -1;
                    int currentNode = endpoints[0];
                    for (int step = 0; step < component.Count; step++)
                    {
                        chain.Add(points[currentNode].Vertex);
                        if (currentNode == endpoints[1])
                        {
                            break;
                        }

                        int nextNode = -1;
                        for (int i = 0; i < adjacency[currentNode].Count; i++)
                        {
                            int candidate = adjacency[currentNode][i];
                            if (candidate == previous)
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

            /// <summary>
            /// 查找已有近似点，找不到时创建新图点。
            /// </summary>
            private int FindOrAddPoint(Vertex vertex)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if ((points[i].Vertex.Position - vertex.Position).sqrMagnitude <= PointToleranceSqr)
                    {
                        return i;
                    }
                }

                points.Add(new GraphPoint(vertex));
                return points.Count - 1;
            }

            /// <summary>
            /// 在当前点的邻接点中找下一条还没使用过的边。
            /// </summary>
            private int FindNext(int current, int previous, int start, int loopCount, PlaneBasis basis)
            {
                GraphPoint point = points[current];
                Vector2 currentPoint = Project(point.Vertex.Position, basis);
                Vector2 previousPoint = Project(points[previous].Vertex.Position, basis);
                Vector2 incoming = currentPoint - previousPoint;
                int bestNeighbor = -1;
                float bestTurn = float.PositiveInfinity;
                float bestDistance = float.NegativeInfinity;

                for (int i = 0; i < point.Neighbors.Count; i++)
                {
                    int neighbor = point.Neighbors[i];
                    EdgeKey edge = new EdgeKey(current, neighbor);
                    if (!unusedEdges.Contains(edge))
                    {
                        continue;
                    }

                    // 走回起点时，至少要已经形成三条边，避免马上折返成两点环。
                    if (neighbor == start && loopCount <= 2)
                    {
                        continue;
                    }

                    Vector2 nextPoint = Project(points[neighbor].Vertex.Position, basis);
                    Vector2 outgoing = nextPoint - currentPoint;
                    float turn = Turn(incoming, outgoing);
                    float distance = outgoing.sqrMagnitude;
                    if (bestNeighbor < 0
                        || turn < bestTurn - MeshToolGeometry.Epsilon
                        || (Mathf.Abs(turn - bestTurn) <= MeshToolGeometry.Epsilon && distance > bestDistance))
                    {
                        bestNeighbor = neighbor;
                        bestTurn = turn;
                        bestDistance = distance;
                    }
                }

                return bestNeighbor;
            }

            // 用平面投影后的转角为邻接边排序，避免多环在同一点附近时走错边。 / Rank candidate edges by projected turning angle so cap loops do not jump across nearby rings.
            private static float Turn(Vector2 incoming, Vector2 outgoing)
            {
                if (incoming.sqrMagnitude <= MeshToolGeometry.EpsilonSqr || outgoing.sqrMagnitude <= MeshToolGeometry.EpsilonSqr)
                {
                    return 0f;
                }

                incoming.Normalize();
                outgoing.Normalize();
                float angle = Mathf.Atan2(incoming.x * outgoing.y - incoming.y * outgoing.x, Vector2.Dot(incoming, outgoing));
                return angle < 0f ? angle + Mathf.PI * 2f : angle;
            }

            /// <summary>
            /// 取出一条还没被追踪过的边。
            /// </summary>
            private EdgeKey FirstUnusedEdge()
            {
                foreach (EdgeKey edge in unusedEdges)
                {
                    return edge;
                }

                return default(EdgeKey);
            }

            /// <summary>
            /// 把图点索引路径转换成顶点路径。
            /// </summary>
            private List<Vertex> ToVertexLoop(List<int> indices)
            {
                List<Vertex> loop = new List<Vertex>(indices.Count);
                for (int i = 0; i < indices.Count; i++)
                {
                    loop.Add(points[indices[i]].Vertex);
                }

                return loop;
            }

            private sealed class GraphPoint
            {
                public Vertex Vertex;
                public List<int> Neighbors = new List<int>();

                /// <summary>
                /// 创建切口图中的一个节点。
                /// </summary>
                public GraphPoint(Vertex vertex)
                {
                    Vertex = vertex;
                }
            }

            private struct EdgeKey : IEquatable<EdgeKey>
            {
                public int A;
                public int B;

                /// <summary>
                /// 创建无向边键，内部会把端点排序以便去重。
                /// </summary>
                public EdgeKey(int a, int b)
                {
                    if (a < b)
                    {
                        A = a;
                        B = b;
                    }
                    else
                    {
                        A = b;
                        B = a;
                    }
                }

                /// <summary>
                /// 判断两条无向边是否连接同一对图点。
                /// </summary>
                public bool Equals(EdgeKey other)
                {
                    return A == other.A && B == other.B;
                }

                /// <summary>
                /// 判断对象是否为相同的无向边键。
                /// </summary>
                public override bool Equals(object obj)
                {
                    return obj is EdgeKey other && Equals(other);
                }

                /// <summary>
                /// 计算无向边键的哈希值。
                /// </summary>
                public override int GetHashCode()
                {
                    unchecked
                    {
                        return (A * 397) ^ B;
                    }
                }
            }
        }
    }
}
