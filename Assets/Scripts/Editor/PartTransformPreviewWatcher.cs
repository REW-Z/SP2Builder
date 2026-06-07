using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


[InitializeOnLoad]
internal static class PartTransformPreviewWatcher
{
	private static GUIStyle _pinMarkerStyle;

	static PartTransformPreviewWatcher()
	{
		EditorApplication.hierarchyWindowItemOnGUI -= DrawPinnedHierarchyMarker;
		EditorApplication.hierarchyWindowItemOnGUI += DrawPinnedHierarchyMarker;
		Undo.postprocessModifications -= OnPostprocessModifications;
		Undo.postprocessModifications += OnPostprocessModifications;
	}

	// 捕获 Inspector、Scene Handle、Undo 等 Transform 写入，只处理发生变化的 Part。 / Catch transform writes from inspector, scene handles, and undo while only touching modified Parts.
	private static UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
	{
		if (modifications == null || modifications.Length == 0)
		{
			return modifications;
		}

		HashSet<Part> touchedParts = new HashSet<Part>();
		for (int i = 0; i < modifications.Length; i++)
		{
			Transform modifiedTransform = modifications[i].currentValue.target as Transform;
			Part part = modifiedTransform != null ? modifiedTransform.GetComponent<Part>() : null;
			if (part != null)
			{
				touchedParts.Add(part);
			}
		}

		foreach (Part part in touchedParts)
		{
			Craft craft = part.GetComponentInParent<Craft>();
			if (craft != null && craft.EnforcePinnedPartTransform(part, warnIfChanged: true, "transform modification"))
			{
				EditorUtility.SetDirty(part);
				EditorUtility.SetDirty(craft);
			}
		}

		return modifications;
	}

	// 在 Hierarchy 行右侧绘制 pinned 标记，不修改 GameObject 名称。 / Draw a pinned marker on the hierarchy row without changing the GameObject name.
	private static void DrawPinnedHierarchyMarker(int instanceId, Rect selectionRect)
	{
		GameObject gameObject = EditorUtility.EntityIdToObject(instanceId) as GameObject;
		Part part = gameObject != null ? gameObject.GetComponent<Part>() : null;
		Craft craft = part != null ? part.GetComponentInParent<Craft>() : null;
		if (craft == null || !craft.IsPartPinned(part))
		{
			return;
		}

		const float markerWidth = 48f;
		if (selectionRect.width <= markerWidth + 8f)
		{
			return;
		}

		Rect markerRect = new Rect(selectionRect.xMax - markerWidth - 6f, selectionRect.y + 2f, markerWidth, selectionRect.height - 4f);
		EditorGUI.DrawRect(markerRect, new Color(0.18f, 0.36f, 0.95f, 0.86f));
		GUI.Label(markerRect, "Pinned", GetPinMarkerStyle());
	}

	private static GUIStyle GetPinMarkerStyle()
	{
		if (_pinMarkerStyle != null)
		{
			return _pinMarkerStyle;
		}

		_pinMarkerStyle = new GUIStyle(EditorStyles.miniBoldLabel)
		{
			alignment = TextAnchor.MiddleCenter,
			clipping = TextClipping.Clip
		};
		_pinMarkerStyle.normal.textColor = Color.white;
		return _pinMarkerStyle;
	}
}
