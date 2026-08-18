#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Creates a headless Linux server build without changing the Web build profile.</summary>
public static class ColorClashDedicatedServerBuild
{
    const string BattleScene = "Assets/Scenes/battle.unity";
    const string OutputPath = "Builds/ColorClashServer/ColorClashServer.x86_64";

    [MenuItem("Color Clash/Build/Linux Dedicated Server")]
    public static void BuildLinuxDedicatedServer()
    {
        if (!File.Exists(BattleScene))
        {
            Debug.LogError($"[Color Clash] Server build failed: scene not found at {BattleScene}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        var options = new BuildPlayerOptions
        {
            scenes = new[] { BattleScene },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[Color Clash] Linux dedicated server build: {report.summary.result} → {OutputPath}");
    }
}
#endif
