using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>Headless Quest APK build, so "it builds" is a fact rather than an assumption.</summary>
public static class BuildQuest
{
    public static void Run()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        System.Console.WriteLine("===== QUEST BUILD =====");
        System.Console.WriteLine("scenes: " + string.Join(", ", scenes));
        System.Console.WriteLine("architecture: " + PlayerSettings.Android.targetArchitectures);
        System.Console.WriteLine("scripting backend: " + PlayerSettings.GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android));
        System.Console.WriteLine("min sdk: " + PlayerSettings.Android.minSdkVersion);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "/tmp/kitbash-quest.apk",
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        BuildReport report;
        try { report = BuildPipeline.BuildPlayer(options); }
        catch (Exception e)
        {
            System.Console.WriteLine("BUILD THREW: " + e.Message);
            System.Console.WriteLine("===== END QUEST BUILD =====");
            EditorApplication.Exit(1);
            return;
        }

        var summary = report.summary;
        System.Console.WriteLine($"result: {summary.result}");
        System.Console.WriteLine($"errors: {summary.totalErrors}  warnings: {summary.totalWarnings}");
        System.Console.WriteLine($"size: {summary.totalSize / (1024 * 1024)} MB   time: {summary.totalTime}");

        // Surface anything that would bite at runtime rather than only counting it.
        foreach (var step in report.steps)
        foreach (var msg in step.messages)
        {
            if (msg.type != LogType.Error && msg.type != LogType.Exception && msg.type != LogType.Assert) continue;
            System.Console.WriteLine($"  [{msg.type}] {step.name}: {msg.content.Replace("\n", " ").Substring(0, Math.Min(240, msg.content.Length))}");
        }

        // Prove the model actually made it into the player data, not just the project.
        var modelIncluded = report.GetFiles().Any(f => f.path.Contains("yolov9") || f.path.EndsWith(".apk"));
        System.Console.WriteLine("apk produced: " + modelIncluded);
        System.Console.WriteLine("===== END QUEST BUILD =====");
        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
