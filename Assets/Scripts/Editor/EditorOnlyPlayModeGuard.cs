using UnityEditor;
using UnityEngine;

// 阻止项目进入 Play 模式；本工程只支持编辑器工具链。 / Prevent the project from entering Play Mode because this workspace is editor-only.
[InitializeOnLoad]
public static class EditorOnlyPlayModeGuard
{
    static EditorOnlyPlayModeGuard()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    // 一旦检测到即将进入 Play 模式，就立即中止并报错。 / Abort immediately when Unity is about to enter Play Mode.
    private static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.ExitingEditMode)
        {
            return;
        }

        EditorApplication.isPlaying = false;
        Debug.LogError("SPBuilder is editor-only. Entering Play Mode is not supported.");
    }
}
