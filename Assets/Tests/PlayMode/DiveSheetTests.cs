#nullable enable

using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace RooftopTag.Tests.PlayMode;

/// <summary>
/// PlayMode replacement for edit-mode dive contact sheets: edit mode drives Animator.Update by
/// hand, but GPU skinning never refreshes off the editor loop, so every rendered cell showed one
/// frozen pose. Here the real player loop runs (Time.captureFramerate pins each `yield return
/// null` to 1/60s), so skinning updates for real. This asmdef can't reference
/// Assembly-CSharp-Editor, so the grid-composition helpers below duplicate CharacterPreviewShot's.
/// Run: Unity.exe -batchmode -runTests -testPlatform PlayMode -projectPath . -logFile Tools/divesheettests.log
/// </summary>
public class DiveSheetTests
{
    const int CellSize = 400;
    const string SheetFolder = "Tools/screenshots/dive_sheets";
    static readonly int HashDiveRoll = Animator.StringToHash("DiveRoll");
    static readonly int HashDivingCatch = Animator.StringToHash("DivingCatch");

    GameObject? _light;
    GameObject? _ground;
    Camera? _camSide;
    Camera? _camQuarter;
    RenderTexture? _rt;
    Texture2D? _scratch;
    RuntimeAnimatorController? _controller;

    [SetUp]
    public void SetUpStage()
    {
        Time.captureFramerate = 60;

        _light = new GameObject("SheetLight");
        var light = _light.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 1.2f;
        _light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _ground.name = "SheetGround";
        _ground.transform.position = Vector3.zero;
        _ground.transform.localScale = new Vector3(2f, 1f, 2f);
        Shader? shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var groundMat = new Material(shader!) { color = new Color(0.55f, 0.55f, 0.55f) };
        _ground.GetComponent<Renderer>().sharedMaterial = groundMat;

        _camSide = MakeCam("SheetCamSide");
        _camSide.transform.position = new Vector3(3.2f, 1.0f, 0f);
        _camSide.transform.LookAt(new Vector3(0f, 0.45f, 0f));

        _camQuarter = MakeCam("SheetCamQuarter");
        _camQuarter.transform.position = new Vector3(2.3f, 1.0f, 2.3f);
        _camQuarter.transform.LookAt(new Vector3(0f, 0.45f, 0f));

        _rt = new RenderTexture(CellSize, CellSize, 24, RenderTextureFormat.ARGB32);
        _scratch = new Texture2D(CellSize, CellSize, TextureFormat.RGB24, false);

        _controller = Resources.Load<RuntimeAnimatorController>("CharacterAnimator");
        Assert.IsNotNull(_controller, "CharacterAnimator controller not found via Resources.Load(\"CharacterAnimator\") " +
            "(expected at Assets/Art/Characters/Resources/CharacterAnimator.controller).");
    }

    [TearDown]
    public void TearDownStage()
    {
        Time.captureFramerate = 0;
        if (_light != null) Object.DestroyImmediate(_light);
        if (_ground != null) Object.DestroyImmediate(_ground);
        if (_camSide != null) Object.DestroyImmediate(_camSide.gameObject);
        if (_camQuarter != null) Object.DestroyImmediate(_camQuarter.gameObject);
        if (_rt != null) { _rt.Release(); Object.DestroyImmediate(_rt); }
        if (_scratch != null) Object.DestroyImmediate(_scratch);
        RenderTexture.active = null;
    }

    static Camera MakeCam(string name)
    {
        var go = new GameObject(name);
        Camera cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.20f, 0.22f, 0.28f);
        cam.fieldOfView = 35f;
        return cam;
    }

    [UnityTest]
    public IEnumerator DiveSheets_pest_control() => DiveSheets("pest_control");

    [UnityTest]
    public IEnumerator DiveSheets_raccoon() => DiveSheets("raccoon");

    IEnumerator DiveSheets(string model)
    {
        Directory.CreateDirectory(SheetFolder);

        yield return CaptureSanity(model);
        yield return CaptureDiveOrCatch(model, catching: false, sheetName: "diveroll");
        yield return CaptureDiveOrCatch(model, catching: true, sheetName: "divingcatch");
        yield return CaptureEntry(model);
        yield return CaptureExit(model);
    }

    // Reference "recovered/standing" height for this model: idle, no dive params set, a few frames
    // in so the controller has settled into whatever its default state is.
    IEnumerator CaptureSanity(string model)
    {
        var (go, anim) = SpawnRig(model);
        try
        {
            anim.SetInteger("MotorState", 0);
            for (int i = 0; i < 5; i++) yield return null;

            Transform? hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Transform? head = anim.GetBoneTransform(HumanBodyBones.Head);
            float hipsY = hips != null ? hips.position.y : float.NaN;
            float headY = head != null ? head.position.y : float.NaN;
            Debug.Log($"SANITY: model={model} hipsY={hipsY:0.000} headY={headY:0.000}");
        }
        finally { Object.DestroyImmediate(go); }
    }

    // diveroll.png / divingcatch.png: 12 frames evenly across the 0.8s committed-dive window (every
    // 4th 60Hz update), captured from both cameras on the same frame (no time advance between views).
    IEnumerator CaptureDiveOrCatch(string model, bool catching, string sheetName)
    {
        var (go, anim) = SpawnRig(model);
        var sideCells = new Color[12][];
        var quarterCells = new Color[12][];
        try
        {
            anim.SetInteger("MotorState", 0); // Grounded
            anim.SetBool("Diving", true);
            anim.SetBool("Catching", catching);

            for (int cell = 0; cell < 12; cell++)
            {
                for (int s = 0; s < 4; s++) yield return null;

                sideCells[cell] = CaptureCell(_camSide!);
                quarterCells[cell] = CaptureCell(_camQuarter!);

                LogState(model, sheetName, cell, anim, go);
            }

            WriteGridSheet($"{model}_{sheetName}.png", sideCells, quarterCells);
            Debug.Log($"ANIMCHECK: sheet={model}_{sheetName} cellsDiffer={MeanAbsDiff(sideCells[0], sideCells[6]):0.00}");
        }
        finally { Object.DestroyImmediate(go); }
    }

    // entry.png: settle into a full-speed sprint, then flip Diving true — 3 consecutive frames
    // showing the blend INTO the dive.
    IEnumerator CaptureEntry(string model)
    {
        var (go, anim) = SpawnRig(model);
        var cells = new Color[3][];
        try
        {
            anim.SetInteger("MotorState", 0); // Grounded
            anim.SetFloat("Speed", 7f);
            anim.SetFloat("ForwardSpeed", 7f); // sprint threshold (MovementConfig)
            anim.SetFloat("StrafeSpeed", 0f);
            anim.SetBool("Diving", false);
            anim.SetBool("Catching", false);

            for (int i = 0; i < 30; i++) yield return null;

            anim.SetBool("Diving", true); // sprint params stay set — this is the blend-in

            for (int i = 0; i < cells.Length; i++)
            {
                yield return null;
                cells[i] = CaptureCell(_camSide!);
                LogState(model, "entry", i, anim, go);
            }

            WriteRowSheet($"{model}_entry.png", cells);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // exit.png: the full 0.8s dive, then flip Diving false — 3 consecutive frames showing the blend
    // back to locomotion.
    IEnumerator CaptureExit(string model)
    {
        var (go, anim) = SpawnRig(model);
        var cells = new Color[3][];
        try
        {
            anim.SetInteger("MotorState", 0); // Grounded
            anim.SetFloat("Speed", 7f);
            anim.SetFloat("ForwardSpeed", 7f);
            anim.SetFloat("StrafeSpeed", 0f);
            anim.SetBool("Diving", true);
            anim.SetBool("Catching", false);

            for (int i = 0; i < 48; i++) yield return null;

            anim.SetBool("Diving", false); // sprint params (ForwardSpeed etc.) stay set

            for (int i = 0; i < cells.Length; i++)
            {
                yield return null;
                cells[i] = CaptureCell(_camSide!);
                LogState(model, "exit", i, anim, go);
            }

            WriteRowSheet($"{model}_exit.png", cells);
        }
        finally { Object.DestroyImmediate(go); }
    }

    void LogState(string model, string sheetName, int frameIndex, Animator anim, GameObject go)
    {
        var info = anim.GetCurrentAnimatorStateInfo(0);
        Transform? hips = anim.GetBoneTransform(HumanBodyBones.Hips);
        Transform? head = anim.GetBoneTransform(HumanBodyBones.Head);
        float hipsY = hips != null ? hips.position.y : float.NaN;
        float headY = head != null ? head.position.y : float.NaN;
        Bounds b = GetSkinnedBounds(go);
        Debug.Log($"STATE: sheet={model}_{sheetName} frame={frameIndex} shortNameHash={info.shortNameHash} " +
            $"isDiveRoll={info.shortNameHash == HashDiveRoll} isDivingCatch={info.shortNameHash == HashDivingCatch} " +
            $"normalizedTime={info.normalizedTime:0.000} hipsY={hipsY:0.000} headY={headY:0.000} boundsMinY={b.min.y:0.000}");
    }

    // Fresh model + Animator bound to the real controller, driven by the normal PlayMode player loop
    // (no manual Animator.Update — Time.captureFramerate makes each `yield return null` exactly 1/60s).
    (GameObject go, Animator anim) SpawnRig(string model)
    {
        var prefab = Resources.Load<GameObject>(model);
        Assert.IsNotNull(prefab, $"SHEET_MISSING_MODEL {model}");
        GameObject go = Object.Instantiate(prefab!, Vector3.zero, Quaternion.identity);

        var anim = go.GetComponent<Animator>();
        if (anim == null) anim = go.AddComponent<Animator>();
        anim.runtimeAnimatorController = _controller;
        anim.applyRootMotion = false; // character must stay at origin for the fixed cameras
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        return (go, anim);
    }

    static Bounds GetSkinnedBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    Color[] CaptureCell(Camera cam)
    {
        cam.targetTexture = _rt;
        cam.Render();
        RenderTexture.active = _rt;
        _scratch!.ReadPixels(new Rect(0, 0, CellSize, CellSize), 0, 0);
        _scratch.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;
        return _scratch.GetPixels();
    }

    static float MeanAbsDiff(Color[] a, Color[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Mathf.Abs(a[i].r - b[i].r) * 255f;
            sum += Mathf.Abs(a[i].g - b[i].g) * 255f;
            sum += Mathf.Abs(a[i].b - b[i].b) * 255f;
        }
        return (float)(sum / (a.Length * 3));
    }

    // 6 cols x 4 rows: rows 0-1 = 12 side-view frames in time order, rows 2-3 = the same frames from
    // the 3/4 view. Composited into ONE PNG (not 24 loose files).
    static void WriteGridSheet(string fileName, Color[][] sideCells, Color[][] quarterCells)
    {
        const int cols = 6, rows = 4, perRow = 6;
        var canvas = new Texture2D(cols * CellSize, rows * CellSize, TextureFormat.RGB24, false);
        try
        {
            PlaceRow(canvas, rows, 0, sideCells, 0, perRow);
            PlaceRow(canvas, rows, 1, sideCells, perRow, perRow);
            PlaceRow(canvas, rows, 2, quarterCells, 0, perRow);
            PlaceRow(canvas, rows, 3, quarterCells, perRow, perRow);
            DrawGridBorder(canvas, cols, rows);
            canvas.Apply();
            WritePng(fileName, canvas);
        }
        finally { Object.DestroyImmediate(canvas); }
    }

    // 3 cols x 1 row, side view only (entry/exit blend sheets).
    static void WriteRowSheet(string fileName, Color[][] cells)
    {
        var canvas = new Texture2D(cells.Length * CellSize, CellSize, TextureFormat.RGB24, false);
        try
        {
            PlaceRow(canvas, 1, 0, cells, 0, cells.Length);
            DrawGridBorder(canvas, cells.Length, 1);
            canvas.Apply();
            WritePng(fileName, canvas);
        }
        finally { Object.DestroyImmediate(canvas); }
    }

    static void WritePng(string fileName, Texture2D canvas)
    {
        string path = $"{SheetFolder}/{fileName}";
        File.WriteAllBytes(path, canvas.EncodeToPNG());
        Debug.Log($"SHEET: {Path.GetFullPath(path)}");
    }

    // Texture2D row 0 is the BOTTOM of the encoded PNG, so visual row 0 (top of the contact sheet,
    // earliest frames) must land at the highest texture y-offset — not texture row 0.
    static void PlaceRow(Texture2D canvas, int totalRows, int visualRow, Color[][] cells, int offset, int count)
    {
        int y = (totalRows - 1 - visualRow) * CellSize;
        for (int col = 0; col < count; col++)
            canvas.SetPixels(col * CellSize, y, CellSize, CellSize, cells[offset + col]);
    }

    static void DrawGridBorder(Texture2D canvas, int cols, int rows)
    {
        var black = Color.black;
        for (int c = 0; c <= cols; c++)
        {
            int x = Mathf.Clamp(c * CellSize - 1, 0, canvas.width - 2);
            for (int px = 0; px < 2; px++)
                for (int y = 0; y < canvas.height; y++)
                    canvas.SetPixel(x + px, y, black);
        }
        for (int r = 0; r <= rows; r++)
        {
            int y = Mathf.Clamp(r * CellSize - 1, 0, canvas.height - 2);
            for (int py = 0; py < 2; py++)
                for (int x = 0; x < canvas.width; x++)
                    canvas.SetPixel(x, y + py, black);
        }
    }
}
