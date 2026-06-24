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
			if (craft != null && craft.EnforcePinnedPartTransform(part, logIfChanged: true, "transform modification"))
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

[InitializeOnLoad]
internal static class OtherPartScenePicker
{
	private const float MinimumPickRadiusHandleScale = 0.045f;

	static OtherPartScenePicker()
	{
		SceneView.duringSceneGui -= OnSceneGUI;
		SceneView.duringSceneGui += OnSceneGUI;
	}

	// 给只绘制 Gizmo 的 OtherPart 注册 SceneView 点击区域。 / Register SceneView pick areas for OtherPart objects that only draw Gizmos.
	private static void OnSceneGUI(SceneView sceneView)
	{
		Event currentEvent = Event.current;
		if (currentEvent == null || EditorApplication.isPlayingOrWillChangePlaymode)
		{
			return;
		}

		foreach (OtherPart part in Resources.FindObjectsOfTypeAll<OtherPart>())
		{
			if (!IsPickableScenePart(part))
			{
				continue;
			}

			RegisterPartPickControl(part, currentEvent);
		}
	}

	// 为单个 OtherPart 建立一个无渲染的 Handle control。 / Create one renderless Handle control for a single OtherPart.
	private static void RegisterPartPickControl(OtherPart part, Event currentEvent)
	{
		int controlId = GUIUtility.GetControlID(part.GetInstanceID(), FocusType.Passive);
		Vector3 position = part.transform.position;
		float pickRadius = GetWorldPickRadius(part, position);

		if (currentEvent.type == EventType.Layout)
		{
			HandleUtility.AddControl(controlId, HandleUtility.DistanceToCircle(position, pickRadius));
			return;
		}

		if (currentEvent.type == EventType.MouseDown
			&& currentEvent.button == 0
			&& !currentEvent.alt
			&& HandleUtility.nearestControl == controlId)
		{
			SelectPart(part, currentEvent);
			GUIUtility.hotControl = controlId;
			currentEvent.Use();
			return;
		}

		if (currentEvent.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
		{
			GUIUtility.hotControl = 0;
			currentEvent.Use();
		}
	}

	// 判断对象是否属于当前场景并可参与拾取。 / Check whether the object belongs to the scene and can be picked.
	private static bool IsPickableScenePart(OtherPart part)
	{
		return part != null
			&& part.gameObject != null
			&& part.gameObject.scene.IsValid()
			&& part.gameObject.activeInHierarchy
			&& part.enabled;
	}

	// 选中 OtherPart，同时支持 Shift/Ctrl 追加或移除选择。 / Select the OtherPart, supporting Shift/Ctrl add or remove selection.
	private static void SelectPart(OtherPart part, Event currentEvent)
	{
		GameObject target = part.gameObject;
		if (currentEvent.shift || EditorGUI.actionKey)
		{
			ToggleSelection(target);
		}
		else
		{
			Selection.activeGameObject = target;
		}
	}

	private static void ToggleSelection(GameObject target)
	{
		List<Object> selectedObjects = new List<Object>(Selection.objects);
		int existingIndex = selectedObjects.IndexOf(target);
		if (existingIndex >= 0)
		{
			selectedObjects.RemoveAt(existingIndex);
		}
		else
		{
			selectedObjects.Add(target);
		}

		Selection.objects = selectedObjects.ToArray();
		Selection.activeGameObject = target;
	}

	private static float GetWorldPickRadius(OtherPart part, Vector3 position)
	{
		Vector3 scale = part.transform.lossyScale;
		float largestScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
		float visualRadius = OtherPart.GizmoRadius * largestScale;
		float minimumRadius = HandleUtility.GetHandleSize(position) * MinimumPickRadiusHandleScale;
		return Mathf.Max(visualRadius, minimumRadius);
	}
}
