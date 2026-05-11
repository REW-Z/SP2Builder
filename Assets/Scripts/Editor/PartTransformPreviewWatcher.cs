using UnityEditor;
using UnityEngine;


[InitializeOnLoad]
internal static class PartTransformPreviewWatcher
{
	private const double TransformPreviewDelaySeconds = 0.04d;

	static PartTransformPreviewWatcher()
	{
		EditorApplication.update -= WatchSelectedPartTransforms;
		EditorApplication.update += WatchSelectedPartTransforms;
	}

	private static void WatchSelectedPartTransforms()
	{
		if (Application.isPlaying || Selection.transforms == null || Selection.transforms.Length == 0)
		{
			return;
		}

		foreach (Transform transform in Selection.transforms)
		{
			if (transform == null || !transform.hasChanged)
			{
				continue;
			}

			Part part = transform.GetComponent<Part>();
			if (part == null)
			{
				transform.hasChanged = false;
				continue;
			}

			Craft craft = part.GetComponentInParent<Craft>();
			bool lightweight = part is FuselagePart || part is IFuselageCarver;
			craft.QueuePreviewRebuildForPart(part, TransformPreviewDelaySeconds, lightweight);
			EditorUtility.SetDirty(craft);

			EditorUtility.SetDirty(part);
			transform.hasChanged = false;
		}
	}
}
