#nullable enable

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools;

public static class BuildGame
{
    [MenuItem("RooftopTag/Build/Windows x64 (Development)")]
    public static void BuildWindowsDev()
    {
        BuildWindows("Builds/RooftopTag_dev.exe", BuildOptions.Development | BuildOptions.AllowDebugging);
    }

    [MenuItem("RooftopTag/Build/Windows x64 (Release)")]
    public static void BuildWindowsRelease()
    {
        BuildWindows("Builds/RooftopTag.exe", BuildOptions.None);
    }

    private static void BuildWindows(string outputPath, BuildOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var report = BuildPipeline.BuildPlayer(
            System.Array.ConvertAll(EditorBuildSettings.scenes, s => s.path),
            outputPath,
            BuildTarget.StandaloneWindows64,
            options
        );

        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log($"BUILD_SUCCESS: {outputPath} ({(int)(report.summary.totalSize / 1024 / 1024)}MB)");
            EditorUtility.RevealInFinder(outputPath);
        }
        else
        {
            Debug.LogError($"BUILD_FAILED: {report.summary.totalErrors} errors");
        }
    }
}
