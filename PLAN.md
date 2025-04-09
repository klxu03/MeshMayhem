# GPU Mesh Slicing Implementation Plan

This is some pseudocode and an implementation plan for adding GPU acceleration to the EzySlice mesh slicing framework. EzySlice mesh slicing has it so you can take any 3D object, define a plane about where to slice the object, and then generate the two meshes above and below the plane so you can simulate a slicing happening on any object. The focus is on core GPU processing for triangle intersection and mesh generation.

## Files Overview

### Existing Files to Modify

1. **SlicerExtensions.cs**
2. **Slicer.cs**

### New Files to Create

1. **MeshSliceCompute.compute** - Compute shader for GPU-accelerated mesh slicing
2. **GPUSlicer.cs** - Main coordinator for GPU slicing operations
3. **ComputeBufferHelper.cs** - Utility for managing compute buffers
4. **GPUSlicedHull.cs** - Container for GPU-processed mesh data

## Detailed Component Specifications

### 1. MeshSliceCompute.compute

This compute shader will handle triangle processing on the GPU.

#### Structures:
```
struct Triangle {
    float3 posA, posB, posC;
    float2 uvA, uvB, uvC;
    float3 normalA, normalB, normalC;
    float4 tangentA, tangentB, tangentC;
}

struct SlicingPlane {
    float3 position;
    float3 normal;
}

struct IntersectionPoint {
    float3 position;
    float2 uv;
    float3 normal;
    float4 tangent;
}
```

#### Kernels:

##### TriangleIntersectionKernel
- Input: Original mesh triangles, slicing plane
- Output: Intersection test results for each triangle
- Process: Test each triangle against the plane and classify as above/below/intersecting

Pseudocode:
```
For each triangle:
    Calculate signed distance of each vertex from plane
    If all vertices are on same side:
        Mark triangle for appropriate hull without modification (above or below)
    Else:
        Calculate intersection points with plane
        Mark triangle as requiring splitting (and intersecting classification)
        Store intersection point data
```

##### GenerateHullTrianglesKernel
- Input: Original triangles, intersection test results
- Output: New triangles for upper and lower hulls
- Process: Split intersecting triangles and generate new triangles for each hull

Pseudocode:
```
For each triangle marked as intersecting:
    Retrieve intersection points
    Generate 1-3 triangles for upper hull based on intersection configuration
    Generate 1-3 triangles for lower hull based on intersection configuration
    Interpolate UV, normal, tangent data for new vertices
    Store new triangles in output buffers
```

### 2. GPUSlicer.cs

This class will coordinate the GPU slicing process.

#### Functions:

##### `public static SlicedHull Slice(GameObject obj, Plane pl, TextureRegion crossRegion, Material crossMaterial)`
- Main entry point, matches API of original Slicer
- Extracts mesh data and prepares for GPU processing

Pseudocode:
```
Extract mesh data from GameObject
Initialize compute buffers with mesh data
Dispatch compute shaders for triangle processing
Read back results from GPU
Generate cross-section on CPU
Create and return SlicedHull object
```

##### `PrepareComputeBuffers`
- Sets up input buffers for compute shader

Pseudocode:
```
Extract vertices, UVs, normals, tangents from mesh
Create and populate triangle buffer
Create and set plane buffer with cutting plane parameters
```

##### `DispatchComputeShaders`
- Dispatches compute shaders and manages output buffers

Pseudocode:
```
Create output buffers
Set compute shader parameters
Dispatch TriangleIntersectionKernel
Dispatch GenerateHullTrianglesKernel
```

##### `GenerateCrossSection`
- Uses intersection points to generate cross-section triangles
- Goes ahead and looks for unique intersection points for cross-section within a delta

Pseudocode:
```
Read intersection points from buffer

For each intersection point:
    Check against existing unique points (with epsilon)
    If unique, add to output buffer
    Store metadata about which edges produced this point

Apply Monotone Chain algorithm (on CPU)
Generate UV coordinates for cross-section
Create and return list of triangles for cross-section
```

### 3. ComputeBufferHelper.cs

This utility class will handle data conversion between Unity and GPU formats.

#### Functions:

##### `CreateTriangleBuffer`
- Creates a buffer of triangles from mesh components

Pseudocode:
```
Create GPU-friendly triangle structures
Populate with mesh data (vertices, UVs, normals, tangents)
Create and return compute buffer with triangle data
```

##### `ConvertToTriangles`
- Converts Unity mesh data to triangle structures

Pseudocode:
```
For each triangle in indices:
    Create Triangle structure
    Set positions, UVs, normals, tangents
    Add to array
Return array of triangles
```

##### `ReadBackTriangles`
- Reads triangle data back from GPU

Pseudocode:
```
Create array to receive data
Request buffer.GetData() to fill array
Return triangle array
```

##### `ReadBackIntersectionPoints`
- Reads intersection points back from GPU

Pseudocode:
```
Create array to receive data
Request buffer.GetData() to fill array
Return intersection points array
```

### 4. GPUSlicedHull.cs

This class will store and manage the results of GPU slicing.

#### Functions:

##### `public GPUSlicedHull(Triangle[] upperTriangles, Triangle[] lowerTriangles, List<Triangle> crossSection)`
- Constructor to initialize with GPU-processed data

Pseudocode:
```
Store upper and lower hull triangles
Store cross-section triangles
```

##### `public Mesh CreateUpperHull()`
- Creates Unity mesh for upper hull

Pseudocode:
```
Create new Mesh
Convert triangle data to mesh format
Add cross-section triangles with appropriate orientation
Set mesh vertices, UVs, normals, tangents, triangles
Return completed mesh
```

##### `public Mesh CreateLowerHull()`
- Creates Unity mesh for lower hull

Pseudocode:
```
Create new Mesh
Convert triangle data to mesh format
Add cross-section triangles with appropriate orientation (inverted from upper hull)
Set mesh vertices, UVs, normals, tangents, triangles
Return completed mesh
```

##### `public GameObject CreateUpperHull(GameObject original, Material crossSectionMaterial)`
- Creates GameObject from upper hull mesh

Pseudocode:
```
Create mesh using CreateUpperHull()
Create new GameObject
Add MeshFilter and MeshRenderer
Set mesh and materials
Copy transform from original
Return GameObject
```

##### `public GameObject CreateLowerHull(GameObject original, Material crossSectionMaterial)`
- Creates GameObject from lower hull mesh

Pseudocode:
```
Create mesh using CreateLowerHull()
Create new GameObject
Add MeshFilter and MeshRenderer
Set mesh and materials
Copy transform from original
Return GameObject
```

### 5. Modifications to SlicerExtensions.cs

Add GPU-accelerated versions of existing extension methods:

##### `public static SlicedHull SliceGPU(this GameObject obj, Plane pl, Material crossSectionMaterial = null)`
- GPU-accelerated version of existing Slice method

Pseudocode:
```
Call through to GPUSlicer.Slice with appropriate parameters
Return resulting SlicedHull
```

### 6. Modifications to Slicer.cs

Provide an option to use GPU acceleration:

##### `public static SlicedHull Slice(Mesh sharedMesh, Plane pl, TextureRegion region, int crossIndex, bool useGPU = false)`
- Modified to optionally use GPU acceleration

Pseudocode:
```
If useGPU:
    Return GPUSlicer.Slice(sharedMesh, pl, region, crossIndex)
Else:
    // Original CPU implementation
```
