#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools;

/// <summary>
/// City-grounding acceptance audit (WORK ITEM 5): from every playable roof edge and every swing
/// mid-span, looking straight down, you must see street/ground — never skybox/void. This makes that
/// measurable instead of eyeballed: each shot clears to a distinctive magenta that appears nowhere in
/// the real palette (greys/warm-oranges), so any magenta pixel left after rendering is a hole — a spot
/// nothing drew geometry into. 0% everywhere = pass.
///
/// Sample points are derived from <see cref="RooftopArena.Roofs"/> / <see cref="RooftopArena.Links"/>,
/// not hardcoded: 4 edge midpoints per roof (nudged just outside the footprint so the camera looks
/// down the building's own face into the canyon, where a hole would actually show), plus one mid-span
/// point per Swing link (the chain's rest/grab point, over the chasm). Matches the RenderTexture/PNG
/// pattern in <see cref="PlayModeShot"/> / <see cref="CharacterPreviewShot"/>.
///
/// Also runs a cheap second signal per sample: a straight-down raycast (broad mask) reporting what it
/// hits and at what depth. The ground slab is 960x960 at y~-25 (see TagArenaMapGeometry.CreateRoads),
/// so a hit is expected to be near-universal — a MISS is the interesting finding (something renders
/// with no collider under it, which matters because the street-death feature lands agents on this
/// same slab collider).
///
/// Run interactively via the menu item, or headlessly via -executeMethod RunFromBatch (exits 1 if any
/// shot has holes, so this can gate a build later). Editor-only: reads the freshly (re)built arena,
/// never mutates geometry itself.
/// </summary>
public static class CityGroundingAudit
{
    // ---------------------------------------------------------------- Tuning (dev tool, not
    // VisualThemeConfig — this never ships).
    const int ResolutionPixels = 512; // square RT; resolution only affects hole-% granularity, not detection
    const float FieldOfViewDeg = 60f; // per the brief's math: from y in [1.5,9] down to ground -25, a 60 deg
                                       // cone covers a ~39m circle — well inside frame, horizon never visible
    const float EdgeInsetMeters = 1.0f; // how far outside the roof footprint each edge sample sits, so the
                                         // camera looks past its own roof edge and down into the canyon
    const float CameraClipEpsilon = 0.05f; // lifts the camera a hair above roof height so the near clip
                                            // plane doesn't coincide with the roof surface
    const float RaycastMaxDistance = 200f; // comfortably past the ~34m worst case (roof y9 to slab y-25)
    static readonly Color32 VoidColor = new(255, 0, 255, 255); // magenta: not in this city's palette
    const int VoidColorChannelTolerance = 4; // small slack for gamma/rounding, still nowhere near any real color
    const string OutDir = "Tools/screenshots/cityaudit"; // git-ignored (Tools/screenshots/)
    const string ReportPath = "Tools/cityaudit_report.log"; // git-ignored (Tools/*.log)

    readonly struct Sample
    {
        public readonly string Label;
        public readonly Vector3 Position;
        public Sample(string label, Vector3 position) { Label = label; Position = position; }
    }

    readonly struct ShotResult
    {
        public readonly string Label;
        public readonly Vector3 Position;
        public readonly float HolePercent;
        public readonly int HoleCount;
        public readonly int TotalPixels;
        public readonly bool RayHit;
        public readonly string RayHitDescription;
        public ShotResult(string label, Vector3 position, float holePercent, int holeCount, int totalPixels, bool rayHit, string rayHitDescription)
        {
            Label = label; Position = position; HolePercent = holePercent; HoleCount = holeCount;
            TotalPixels = totalPixels; RayHit = rayHit; RayHitDescription = rayHitDescription;
        }
    }

    [MenuItem("RooftopTag/Audit/Looking-Down Sweep")]
    public static void RunInteractive()
    {
        BuildFreshArena();
        List<ShotResult> results = RunSweep();
        CaptureHorizonShots();
        LogSummary(results);
    }

    /// <summary>-executeMethod entry point: builds the arena, sweeps, writes PNGs + a text report,
    /// logs a machine-greppable summary line, and exits non-zero if any shot has holes.</summary>
    public static void RunFromBatch()
    {
        try
        {
            BuildFreshArena();
            List<ShotResult> results = RunSweep();
            CaptureHorizonShots();
            LogSummary(results);
            bool anyHoles = results.Any(r => r.HolePercent > 0f);
            EditorApplication.Exit(anyHoles ? 1 : 0);
        }
        catch (Exception e)
        {
            Debug.LogError($"CITYAUDIT_FATAL: {e}");
            EditorApplication.Exit(1);
        }
    }

    /// <summary>PHASE 4 addendum: 4 cardinal, normal-perspective (not top-down) shots from roughly roof
    /// height at the play area's centre, out toward the GLB skyline — for a HUMAN to judge whether the
    /// far city reads as a fogged skyline or as stretched mush, which the top-down sweep above can never
    /// show (its cone never reaches past ~39m and never sees a horizon by design). Deliberately NOT fed
    /// into <see cref="ShotResult"/>/the hole count: a normal sky is the CORRECT and expected background
    /// here, unlike the top-down sweep's magenta-means-hole convention, so these sit outside the PASS/
    /// FAIL gate entirely — same OutDir, just a different filename prefix, purely for eyeballing.</summary>
    static void CaptureHorizonShots()
    {
        Vector3 vantage = new(6f, 6f, 13f); // roughly the play area's centre, roof height
        (string name, Vector3 dir)[] dirs =
        {
            ("N", Vector3.forward), ("S", Vector3.back), ("E", Vector3.right), ("W", Vector3.left),
        };

        foreach ((string name, Vector3 dir) in dirs)
        {
            var camGo = new GameObject($"AuditHorizonCam_{name}");
            Camera cam = camGo.AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(vantage, Quaternion.LookRotation(dir, Vector3.up));
            cam.fieldOfView = FieldOfViewDeg;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 500f;
            // Default Skybox clear (NOT the top-down sweep's magenta void colour) — sky/fog is the
            // expected, correct background in a horizon shot.

            var rt = new RenderTexture(1024, 576, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1024, 576, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1024, 576), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;

            File.WriteAllBytes($"{OutDir}/Horizon_{name}.png", tex.EncodeToPNG());

            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(camGo);
        }
    }

    /// <summary>Rebuilds the real arena (same path as RooftopTag/Build Rooftop Arena) so the audit
    /// never checks stale geometry. This saves Assets/Scenes/RooftopArena.unity, same as that menu item.</summary>
    static void BuildFreshArena() => PlaygroundBuilder.BuildRooftopArena();

    static List<ShotResult> RunSweep()
    {
        Directory.CreateDirectory(OutDir);
        List<Sample> samples = BuildSamples();
        var results = new List<ShotResult>(samples.Count);
        foreach (Sample s in samples) results.Add(CaptureSample(s));
        WriteReport(samples.Count, results);
        return results;
    }

    // ---------------------------------------------------------------- Sample points

    static readonly (string name, Vector3 dir)[] EdgeDirs =
    {
        ("N", new Vector3(0f, 0f, 1f)),
        ("S", new Vector3(0f, 0f, -1f)),
        ("E", new Vector3(1f, 0f, 0f)),
        ("W", new Vector3(-1f, 0f, 0f)),
    };

    static List<Sample> BuildSamples()
    {
        var samples = new List<Sample>();
        RooftopArena.Roof[] roofs = RooftopArena.Roofs;

        foreach (RooftopArena.Roof r in roofs)
        {
            foreach ((string name, Vector3 dir) in EdgeDirs)
            {
                float halfExtent = Mathf.Abs(dir.x) > 0.5f ? r.SizeX * 0.5f : r.SizeZ * 0.5f;
                Vector3 edgeXZ = new Vector3(r.Center.x, 0f, r.Center.z) + dir * (halfExtent + EdgeInsetMeters);
                Vector3 camPos = new(edgeXZ.x, r.Center.y + CameraClipEpsilon, edgeXZ.z);
                samples.Add(new Sample($"{r.Name}_{name}", camPos));
            }
        }

        foreach (RooftopArena.Link link in RooftopArena.Links)
        {
            if (link.Kind != RooftopArena.LinkKind.Swing) continue;
            RooftopArena.Roof from = roofs[link.From];
            RooftopArena.Roof to = roofs[link.To];
            Vector3 pivot = RooftopArena.SwingPivot(from, to, link.Param);
            Vector3 grabPoint = pivot + Vector3.down * link.Param; // rest/grab point, mid-span over the chasm
            samples.Add(new Sample($"Swing_{from.Name}_to_{to.Name}", grabPoint));
        }

        return samples;
    }

    // ---------------------------------------------------------------- Capture

    static ShotResult CaptureSample(Sample s)
    {
        var camGo = new GameObject($"AuditCam_{s.Label}");
        Camera cam = camGo.AddComponent<Camera>();
        cam.transform.position = s.Position;
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // straight down
        cam.fieldOfView = FieldOfViewDeg;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = VoidColor;

        var rt = new RenderTexture(ResolutionPixels, ResolutionPixels, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(ResolutionPixels, ResolutionPixels, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, ResolutionPixels, ResolutionPixels), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;

        Color32[] pixels = tex.GetPixels32();
        int holeCount = 0;
        foreach (Color32 p in pixels)
            if (IsVoidColor(p)) holeCount++;
        float holePercent = 100f * holeCount / pixels.Length;

        File.WriteAllBytes($"{OutDir}/{s.Label}.png", tex.EncodeToPNG());

        // Second signal: straight-down raycast, broad mask (~0, ignore triggers so ladder/swing
        // trigger volumes don't mask the real solid surface underneath).
        bool rayHit = Physics.Raycast(s.Position, Vector3.down, out RaycastHit hit, RaycastMaxDistance, ~0, QueryTriggerInteraction.Ignore);
        string rayDesc = rayHit
            ? $"{hit.collider.name} @ y={hit.point.y:F1} (depth {hit.distance:F1}m)"
            : "MISS";

        UnityEngine.Object.DestroyImmediate(tex);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
        UnityEngine.Object.DestroyImmediate(camGo);

        return new ShotResult(s.Label, s.Position, holePercent, holeCount, pixels.Length, rayHit, rayDesc);
    }

    static bool IsVoidColor(Color32 p) =>
        Math.Abs(p.r - VoidColor.r) <= VoidColorChannelTolerance &&
        Math.Abs(p.g - VoidColor.g) <= VoidColorChannelTolerance &&
        Math.Abs(p.b - VoidColor.b) <= VoidColorChannelTolerance;

    // ---------------------------------------------------------------- Report

    static void WriteReport(int sampleCount, List<ShotResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("City-Grounding Looking-Down Audit");
        sb.AppendLine($"Samples: {sampleCount} (roof edges + swing mid-spans, derived from RooftopArena.Roofs/Links)");
        sb.AppendLine($"Resolution: {ResolutionPixels}x{ResolutionPixels}, FOV: {FieldOfViewDeg} deg, void color: magenta (255,0,255)");
        sb.AppendLine();

        int holeSamples = results.Count(r => r.HolePercent > 0f);
        int rayMisses = results.Count(r => !r.RayHit);
        sb.AppendLine($"Shots with holes: {holeSamples}/{results.Count}");
        sb.AppendLine($"Raycast misses: {rayMisses}/{results.Count} (expect ~0 — the ground slab is 960x960, a MISS is the interesting finding)");
        sb.AppendLine();

        sb.AppendLine("-- Worst offenders (by hole %) --");
        foreach (ShotResult r in results.OrderByDescending(r => r.HolePercent).Take(15))
        {
            if (r.HolePercent <= 0f) break;
            sb.AppendLine($"  {r.Label,-28} hole={r.HolePercent,6:F2}% ({r.HoleCount}/{r.TotalPixels}px) pos=({r.Position.x:F1},{r.Position.y:F1},{r.Position.z:F1}) ray={r.RayHitDescription}");
        }
        if (holeSamples == 0) sb.AppendLine("  (none — 0% holes everywhere)");
        sb.AppendLine();

        sb.AppendLine("-- Raycast misses --");
        foreach (ShotResult r in results.Where(r => !r.RayHit))
            sb.AppendLine($"  {r.Label,-28} pos=({r.Position.x:F1},{r.Position.y:F1},{r.Position.z:F1})");
        if (rayMisses == 0) sb.AppendLine("  (none)");
        sb.AppendLine();

        sb.AppendLine("-- Full per-shot table --");
        foreach (ShotResult r in results)
            sb.AppendLine($"  {r.Label,-28} hole={r.HolePercent,6:F2}% pos=({r.Position.x:F1},{r.Position.y:F1},{r.Position.z:F1}) ray={r.RayHitDescription}");

        File.WriteAllText(ReportPath, sb.ToString());
    }

    static void LogSummary(List<ShotResult> results)
    {
        int holeSamples = results.Count(r => r.HolePercent > 0f);
        int rayMisses = results.Count(r => !r.RayHit);
        float worst = results.Count > 0 ? results.Max(r => r.HolePercent) : 0f;
        string status = holeSamples > 0 ? "FAIL" : "PASS";
        Debug.Log($"CITYAUDIT_SUMMARY status={status} samples={results.Count} holeSamples={holeSamples} worstHolePercent={worst:F2} raycastMisses={rayMisses} report={ReportPath} pngs={OutDir}");
    }
}
