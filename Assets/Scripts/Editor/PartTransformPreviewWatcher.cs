using UnityEditor;


[InitializeOnLoad]
internal static class PartTransformPreviewWatcher
{
	static PartTransformPreviewWatcher()
	{
		EditorApplication.update -= WatchSelectedPartTransforms;
	}

	// Transform 选择和移动不再触发网格重建；属性 Inspector 和手动按钮会显式排队。 / Transform selection and movement no longer rebuild meshes; inspector edits and manual buttons queue explicitly.
	private static void WatchSelectedPartTransforms()
	{
	}
}
