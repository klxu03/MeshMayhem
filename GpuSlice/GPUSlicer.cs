// Assets/EzySlice/GPUSlicer.cs
// Single‑file GPU slicing helper.
// © 2025 – drop‑in for Unity 6 / URP / Quest 3

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;             // NativeArray
using UnityEngine;
using UnityEngine.Rendering;          // VertexAttribute, ComputeBufferType
using System.Buffers;

namespace EzySlice {

    /// <summary>
    /// Handles the compute‑shader side of slicing.
    /// Marked internal because it returns the internal type Slicer.SlicedSubmesh.
    /// </summary>
    internal static class GPUSlicer {

        public static ComputeShader SliceShader;          // assign in a ScriptableObject or inspector

        // cache kernel IDs
        static int kClassify = -1;
        static int kSplit    = -1;

        // ---------- public entry ----------

        /// <summary>
        /// Slice ONE sub‑mesh of <paramref name="mesh"/> with <paramref name="plane"/>.
        /// </summary>
        internal static Slicer.SlicedSubmesh SliceSubmesh(
            Mesh mesh,
            Plane plane,              // EzySlice.Plane !
            out List<Vector3> crossPts,
            int subMeshIndex = 0)
        {
            if (SliceShader == null)
                throw new InvalidOperationException("GPUSlicer: Assign SliceShader first.");

            if (!SystemInfo.supportsComputeShaders)
                throw new InvalidOperationException("GPUSlicer: compute shaders not supported.");

            // ------------ gather vertex data (zero‑alloc via MeshData) ----------
            using var mdArr = Mesh.AcquireReadOnlyMeshData(mesh);
            var md = mdArr[0];

            int vCount = md.vertexCount;
            bool hasUV  = mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
            bool hasNor = mesh.HasVertexAttribute(VertexAttribute.Normal);
            bool hasTan = mesh.HasVertexAttribute(VertexAttribute.Tangent);

            // var vertsRO = md.GetVertexData<Vector3>(0);
            // var vertsRO = mesh.vertices;

            var tempVerts = new NativeArray<Vector3>(vCount, Allocator.TempJob);
            md.GetVertices(tempVerts);

            var vertsRO = ArrayPool<Vector3>.Shared.Rent(vCount);
            for (int i = 0; i < vCount; i++) {
                vertsRO[i] = tempVerts[i];
            }

            Vector3[] normalsRO = null;
            Vector4[] tangentsRO = null;
            Vector2[] uvsRO = null;

            if (hasNor) {
                int s = md.GetVertexAttributeStream(VertexAttribute.Normal);     // -1 if absent
                if (s >= 0 && s < md.vertexBufferCount) {
                    var tempNormals = new NativeArray<Vector3>(vCount, Allocator.TempJob);
                    md.GetNormals(tempNormals);
                    normalsRO = ArrayPool<Vector3>.Shared.Rent(vCount);
                    for (int i = 0; i < vCount; i++) {
                        normalsRO[i] = tempNormals[i];
                    }
                    tempNormals.Dispose();
                }
            }
            if (hasTan) {
                int s = md.GetVertexAttributeStream(VertexAttribute.Tangent);
                if (s >= 0 && s < md.vertexBufferCount) {
                    var tempTangents = new NativeArray<Vector4>(vCount, Allocator.TempJob);
                    md.GetTangents(tempTangents);
                    tangentsRO = ArrayPool<Vector4>.Shared.Rent(vCount);
                    for (int i = 0; i < vCount; i++) {
                        tangentsRO[i] = tempTangents[i];
                    }
                    tempTangents.Dispose();
                }
            }
            if (hasUV) {
                int s = md.GetVertexAttributeStream(VertexAttribute.TexCoord0);
                if (s >= 0 && s < md.vertexBufferCount) {
                    var tempUVs = new NativeArray<Vector2>(vCount, Allocator.TempJob);
                    md.GetUVs(0, tempUVs);
                    uvsRO = ArrayPool<Vector2>.Shared.Rent(vCount);
                    for (int i = 0; i < vCount; i++) {
                        uvsRO[i] = tempUVs[i];
                    }
                    tempUVs.Dispose();
                }
            }

            PackedVertex[] packed = new PackedVertex[vCount];
            for (int i = 0; i < vCount; ++i)
                packed[i] = new PackedVertex {
                    pos  = vertsRO[i],
                    norm = hasNor && normalsRO != null ? normalsRO[i] : Vector3.zero,
                    uv   = hasUV  && uvsRO != null ? uvsRO[i] : Vector2.zero,
                    tan  = hasTan && tangentsRO != null ? tangentsRO[i] : Vector4.zero
                };

            int[] indices = mesh.GetTriangles(subMeshIndex);
            int triCount  = indices.Length / 3;

            // ---------- GPU buffers ----------
            var vb = new ComputeBuffer(vCount, Marshal.SizeOf(typeof(PackedVertex)));
            var ib = new ComputeBuffer(indices.Length, sizeof(int));
            var classifyBuf = new ComputeBuffer(triCount, sizeof(int));
            var interBuf    = new ComputeBuffer(triCount, sizeof(int), ComputeBufferType.Append);
            var upBuf       = new ComputeBuffer(triCount * 2, Marshal.SizeOf(typeof(TriOut)), ComputeBufferType.Append);
            var lowBuf      = new ComputeBuffer(triCount * 2, Marshal.SizeOf(typeof(TriOut)), ComputeBufferType.Append);
            var ptBuf       = new ComputeBuffer(triCount * 2, sizeof(float) * 3, ComputeBufferType.Append);

            vb.SetData(packed);
            ib.SetData(indices);
            interBuf.SetCounterValue(0);
            upBuf.SetCounterValue(0);
            lowBuf.SetCounterValue(0);
            ptBuf.SetCounterValue(0);

            // ---------- dispatch #1: classify ----------
            if (kClassify < 0) {
                kClassify = SliceShader.FindKernel("ClassifyTris");
                kSplit    = SliceShader.FindKernel("SplitIntersecting");
            }

            SliceShader.SetInt("_NumTriangles", triCount);
            SliceShader.SetVector("_PlaneNormal", plane.normal);
            SliceShader.SetFloat("_PlaneDist",   plane.dist); // NOTE: EzySlice.Plane.dist
            SliceShader.SetBuffer(kClassify, "_Vertices",           vb);
            SliceShader.SetBuffer(kClassify, "_Indices",            ib);
            SliceShader.SetBuffer(kClassify, "_Classification",     classifyBuf);
            SliceShader.SetBuffer(kClassify, "_IntersectAppend",    interBuf);
            SliceShader.SetBuffer(kClassify, "_UpperHullTriangles",   upBuf);
            SliceShader.SetBuffer(kClassify, "_LowerHullTriangles",   lowBuf);

            int groups = (triCount + 63) / 64;
            SliceShader.Dispatch(kClassify, groups, 1, 1);

            // count intersecting tris
            int interCount = CopyAppendCount(interBuf);
            if (interCount > 0) {
                // ---------- dispatch #2: split only intersecting ----------
                SliceShader.SetInt("_NumIntersectTriangles", interCount);
                SliceShader.SetBool("_HasUV",  hasUV);
                SliceShader.SetBool("_HasNormal", hasNor);
                SliceShader.SetBool("_HasTangent", hasTan);
                SliceShader.SetBuffer(kSplit, "_Vertices", vb);
                SliceShader.SetBuffer(kSplit, "_Indices", ib);
                SliceShader.SetBuffer(kSplit, "_IntersectRead", interBuf);
                SliceShader.SetBuffer(kSplit, "_UpperHullTriangles",   upBuf);
                SliceShader.SetBuffer(kSplit, "_LowerHullTriangles",   lowBuf);
                SliceShader.SetBuffer(kSplit, "_IntersectionPoints",   ptBuf);

                int g2 = (interCount + 63) / 64;
                SliceShader.Dispatch(kSplit, g2, 1, 1);
            }

            // ---------- readback ----------
            int upCount  = CopyAppendCount(upBuf);
            int lowCount = CopyAppendCount(lowBuf);
            int ptCount  = CopyAppendCount(ptBuf);

            TriOut[]   upTris  = new TriOut[upCount];
            TriOut[]   lowTris = new TriOut[lowCount];
            Vector3[]  pts     = new Vector3[ptCount];

            if (upCount  > 0) upBuf.GetData(upTris);
            if (lowCount > 0) lowBuf.GetData(lowTris);
            if (ptCount  > 0) ptBuf.GetData(pts);

            // ---------- convert to EzySlice types ----------
            var subResult = new Slicer.SlicedSubmesh();
            crossPts = new List<Vector3>(pts);

            foreach (var t in upTris)  subResult.upperHull.Add(ToTri(t, hasUV, hasNor, hasTan));
            foreach (var t in lowTris) subResult.lowerHull.Add(ToTri(t, hasUV, hasNor, hasTan));

            // ---------- cleanup ----------
            vb.Release(); ib.Release(); classifyBuf.Release();
            interBuf.Release(); upBuf.Release(); lowBuf.Release(); ptBuf.Release();
            tempVerts.Dispose();
            ArrayPool<Vector3>.Shared.Return(vertsRO, clearArray: false);
            if (normalsRO != null) ArrayPool<Vector3>.Shared.Return(normalsRO, clearArray: false);
            if (tangentsRO != null) ArrayPool<Vector4>.Shared.Return(tangentsRO, clearArray: false);
            if (uvsRO != null) ArrayPool<Vector2>.Shared.Return(uvsRO, clearArray: false);

            return subResult;
        }

        // ---------- helpers ----------------------------------------------------

        static int CopyAppendCount(ComputeBuffer buf) {
            var tmp = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
            ComputeBuffer.CopyCount(buf, tmp, 0);
            int[] c = {0}; tmp.GetData(c); tmp.Release(); return c[0];
        }

        static EzySlice.Triangle ToTri(in TriOut src,
                               bool hasUV, bool hasN, bool hasT)
        {
            // --- positions ----------------------------------------------------------
            Vector3 p0 = new Vector3(src.v0.pos.x, src.v0.pos.y, src.v0.pos.z);
            Vector3 p1 = new Vector3(src.v1.pos.x, src.v1.pos.y, src.v1.pos.z);
            Vector3 p2 = new Vector3(src.v2.pos.x, src.v2.pos.y, src.v2.pos.z);

            var tri = new EzySlice.Triangle(p0, p1, p2);

            // --- UVs ----------------------------------------------------------------
            if (hasUV) {
                Vector2 uv0 = new Vector2(src.v0.uv.x, src.v0.uv.y);
                Vector2 uv1 = new Vector2(src.v1.uv.x, src.v1.uv.y);
                Vector2 uv2 = new Vector2(src.v2.uv.x, src.v2.uv.y);
                tri.SetUV(uv0, uv1, uv2);
            }

            // --- normals ------------------------------------------------------------
            if (hasN) {
                Vector3 n0 = new Vector3(src.v0.norm.x, src.v0.norm.y, src.v0.norm.z);
                Vector3 n1 = new Vector3(src.v1.norm.x, src.v1.norm.y, src.v1.norm.z);
                Vector3 n2 = new Vector3(src.v2.norm.x, src.v2.norm.y, src.v2.norm.z);
                tri.SetNormal(n0, n1, n2);
            }

            // --- tangents -----------------------------------------------------------
            if (hasT) {
                tri.SetTangent(src.v0.tan, src.v1.tan, src.v2.tan);
            }

            return tri;
        }



        // ---------- GPU–CPU structs (must match .compute) ----------------------

        [StructLayout(LayoutKind.Sequential)]
        struct PackedVertex {
            public Vector3 pos;
            public Vector3 norm;
            public Vector2 uv;
            public Vector4 tan;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VertOut {
            public Vector4 pos, norm, uv, tan;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TriOut {
            public VertOut v0, v1, v2;
        }
    }
}
