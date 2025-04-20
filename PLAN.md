# GPU Acceleration for **EzySlice** – Implementation Summary

---

## 1 · Overview
This is some pseudocode and an implementation plan for adding GPU acceleration to the EzySlice mesh slicing framework. EzySlice mesh slicing has it so you can take any 3D object, define a plane about where to slice the object, and then generate the two meshes above and below the plane so you can simulate a slicing happening on any object. The focus is on core GPU processing for triangle intersection and mesh generation.

We kept EzySlice’s public API intact (`GameObject.Slice(...)`) and off‑loaded the triangle classification/splitting work to a two‑pass compute‑shader pipeline.  
Everything above or below the cut is decided in parallel on the GPU; the CPU now only:
* packs vertex/index data once,
* dispatches two kernels,
* rebuilds the final meshes with EzySlice’s existing code.

## 2 · Files & High‑level Changes
| File | Status | Key Responsibilities |
|------|--------|----------------------|
| **`Slicer.cs`** | **MOD** | Detects compute support and calls `GPUSlicer` per‑submesh (falls back to CPU on very old HW). |
| **`GPUSlicer.cs`** | **NEW** | Packs mesh, uploads ComputeBuffers, dispatches kernels, rebuilds `SlicedSubmesh`. |
| **`SliceCompute.compute`** | **NEW** | Two kernels: `ClassifyTris` (cheap side‑test) → `SplitIntersecting` (exact splitting & winding preservation). |

## 3 · GPU Flow
```text
CPU (C#)
  ├─ pack verts / indices → StructuredBuffers
  ├─ Dispatch ❶  ClassifyTris    (append IDs of intersecting faces)
  └─ Dispatch ❷  SplitIntersecting (consume IDs, emit new tris + cap pts)
GPU results → CPU → EzySlice mesh‑builder → Upper + Lower `Mesh` → user code
```

### Why **two** kernels?
* Keeps branch divergence out of the tight inner loop.
* Avoids full‑mesh prefix‑sum; append/consume buffers are natively supported on Quest 3 (Adreno /XR2).

## 4 · Core Function Tweaks (pseudo‑diff)
```csharp
// Slicer.cs  (inside Slice( Mesh … ))
if (SystemInfo.supportsComputeShaders && GPUSlicer.SliceShader)
     slices[s] = GPUSlicer.SliceSubmesh(mesh, plane, …);
else slices[s] = SliceSubmeshCPU(...);
```

```csharp
// GPUSlicer.SliceSubmesh
AcquireReadOnlyMeshData();
Pack → _Vertices, _Indices;
Dispatch(ClassifyTris);
Dispatch(SplitIntersecting);
ReadBack(Upper/Lower/Cap);
return BuildSlicedSubmesh(...);
```

```hlsl
// SliceCompute.compute (snippet)
StructuredBuffer<uint3>  _Indices;
StructuredBuffer<PackedVertex> _Vertices;
AppendStructuredBuffer<TriOut>  _UpperHullTriangles, _LowerHullTriangles;
ConsumeStructuredBuffer<uint>   _IntersectRead;

[numthreads(64,1,1)]
void ClassifyTris(...) { /* dot sign test → Append */ }

void SplitIntersecting(...) { PreserveWinding(); buffer.Append(outTri); }
```

## 5 · Winding Preservation Strategy
1. Compute original face normal via `cross(v1-v0, v2-v0)` on the GPU.
2. After splitting, compare new tri normal with original; if flipped, swap `v1`/`v2` once.
3. Result: side rings always face outward, cap quads form a watertight loop.