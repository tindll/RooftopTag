#nullable enable

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityMeshSimplifier;

namespace Game.EditorTools;

/// <summary>
/// PHASE 1 of the GLB integration plan: building4.glb imports at 1,016,677 verts (the other Tripo
/// models are ~7-8k), which makes it unusable instanced dozens of times across a skyline. This menu
/// item decimates it down to the same ballpark (~10,000 verts) using UnityMeshSimplifier and commits
/// the result as a standalone asset, so simplification happens once (deterministic, survives reimport)
/// rather than being re-derived at import time.
///
/// Does not touch building4.glb itself — Assets/Art/building4_10k.asset is the output.
/// </summary>
public static class GlbDecimator
{
    const string SourcePath = "Assets/Art/building4.glb";
    const string OutputPath = "Assets/Art/building4_10k.asset";
    const int TargetVertexCount = 10000; // other Tripo models are 7-8k; nothing else in-scene exceeds 10k

    [MenuItem("RooftopTag/Art/Decimate building4")]
    public static void Decimate()
    {
        Mesh[] sourceMeshes = AssetDatabase.LoadAllAssetsAtPath(SourcePath)
            .OfType<Mesh>()
            .OrderByDescending(m => m.vertexCount)
            .ToArray();

        if (sourceMeshes.Length == 0)
        {
            Debug.LogError($"GLBDECIMATE_FAIL: no Mesh sub-assets found at {SourcePath}. " +
                "Is the glTFast import complete (focus Unity and let it resolve)?");
            return;
        }

        int totalBefore = sourceMeshes.Sum(m => m.vertexCount);

        // Preserve UV topology: the model's texture is its entire look, so foldover edges (where
        // simplification would flip/mirror a UV triangle) must not be collapsed away — that's the
        // one preserve flag that's free here (tested: identical vert count with it on vs. fully
        // off). PreserveUVSeamEdges/PreserveBorderEdges are deliberately left off: this mesh's
        // auto-unwrapped UVs have a dense seam network, and preserving every seam edge hard-blocks
        // collapses well short of the target (tested: 117k vs the 27k floor below). Starting from
        // SimplificationOptions.Default (not `new SimplificationOptions { ... }`) matters: this is
        // a struct, so an object initializer zero-inits every unset field — MaxIterationCount=0,
        // Agressiveness=0, EnableSmartLink=false — which breaks the algorithm.
        SimplificationOptions options = SimplificationOptions.Default;
        options.PreserveUVFoldoverEdges = true;

        // Usually a single mesh, but handle multi-primitive imports too: split the 10k budget
        // proportionally across sub-meshes by their share of the original vertex count.
        var simplifiedMeshes = new Mesh[sourceMeshes.Length];
        for (int i = 0; i < sourceMeshes.Length; i++)
        {
            Mesh src = sourceMeshes[i];
            int targetForThisMesh = Mathf.Max(3, Mathf.RoundToInt(TargetVertexCount * (src.vertexCount / (float)totalBefore)));

            // MeshSimplifier's internal error-threshold schedule resets on each Initialize() call
            // and doesn't reach very aggressive ratios (>~30x reduction) in a single pass — verified
            // this mesh plateaus at ~27.8k regardless of MaxIterationCount/Agressiveness tuning.
            // Re-running the pass on its own output (a fresh Initialize() each time) breaks through
            // that plateau. Capped at 8 passes with a no-progress guard so a mesh that genuinely
            // can't go lower (e.g. already near its edge-collapse floor) doesn't loop forever.
            Mesh current = src;
            for (int pass = 0; pass < 8 && current.vertexCount > targetForThisMesh; pass++)
            {
                float quality = Mathf.Clamp01(targetForThisMesh / (float)current.vertexCount);
                var simplifier = new MeshSimplifier { SimplificationOptions = options };
                simplifier.Initialize(current);
                simplifier.SimplifyMesh(quality);
                Mesh next = simplifier.ToMesh();
                if (next.vertexCount >= current.vertexCount) break; // no further progress
                current = next;
            }

            current.name = sourceMeshes.Length == 1 ? "building4_10k" : $"building4_10k_{src.name}";
            simplifiedMeshes[i] = current;
        }

        if (File.Exists(OutputPath)) AssetDatabase.DeleteAsset(OutputPath);
        AssetDatabase.CreateAsset(simplifiedMeshes[0], OutputPath);
        for (int i = 1; i < simplifiedMeshes.Length; i++)
            AssetDatabase.AddObjectToAsset(simplifiedMeshes[i], OutputPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        int totalAfter = simplifiedMeshes.Sum(m => m.vertexCount);
        Debug.Log($"GLBDECIMATE_SUMMARY meshes={sourceMeshes.Length} vertsBefore={totalBefore} " +
            $"vertsAfter={totalAfter} target={TargetVertexCount} saved={OutputPath}");
    }
}
