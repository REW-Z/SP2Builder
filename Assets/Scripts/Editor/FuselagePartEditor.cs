using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


[CustomEditor(typeof(FuselagePart))]
public class FuselagePartEditor : UnityEditor.Editor
{
	private enum CornerMode
	{
		Rounded,
		Stretched
	}

	private static readonly string[] CornerNames = { "Top Right", "Bottom Right", "Bottom Left", "Top Left" };

	private static readonly string[] EdgeNames = { "Right", "Bottom", "Left", "Top" };

	private static readonly string[] CutNames = { "Top", "Right", "Bottom", "Left" };

	private static readonly string[] CutFieldNames = { "CutTop", "CutRight", "CutBottom", "CutLeft" };

	private static readonly string[] ValueComponentNames = { "X", "Y", "Z", "W" };

	private bool _showRearSection = true;

	private bool _showFrontSection = true;

	private static readonly Dictionary<string, bool> SectionFoldouts = new Dictionary<string, bool>();

	// 绘制机身自定义 Inspector，并在数值变化时触发预览重建。 / Draw the custom fuselage inspector and trigger preview rebuilds when values change.
	public override void OnInspectorGUI()
	{
		FuselagePart fuselage = (FuselagePart)target;
		PartInspectorUtility.DrawPartIdentity(fuselage);
		PartInspectorUtility.DrawMaterialEditor(fuselage);

		serializedObject.Update();

		EditorGUI.BeginChangeCheck();
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_serializationMode"));
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_visualStyle"));
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_glass"));
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_offset"), new GUIContent("Length / Rise / Run"));

		EditorGUILayout.Space(8f);
		_showRearSection = EditorGUILayout.BeginFoldoutHeaderGroup(_showRearSection, "Rear Section");
		if (_showRearSection)
		{
			DrawSection(serializedObject.FindProperty("_rearSection"));
		}
		EditorGUILayout.EndFoldoutHeaderGroup();

		_showFrontSection = EditorGUILayout.BeginFoldoutHeaderGroup(_showFrontSection, "Front Section");
		if (_showFrontSection)
		{
			DrawSection(serializedObject.FindProperty("_frontSection"));
		}
		EditorGUILayout.EndFoldoutHeaderGroup();

		EditorGUILayout.Space(8f);
		if (serializedObject.FindProperty("_serializationMode").enumValueIndex == (int)FuselageSerializationMode.LegacyFuselage)
		{
			EditorGUILayout.HelpBox("Legacy Fuselage 会在预览时映射到统一的 loft 数据结构。导出仍保持 Legacy XML，但高级参数会做近似回写。", MessageType.Info);
		}
		bool shapeGuiChanged = EditorGUI.EndChangeCheck();
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawPartXml", "Cached Part XML");
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawStateXml", "Cached Fuselage State XML");

		bool serializedChanged = serializedObject.ApplyModifiedProperties();
		if (shapeGuiChanged || serializedChanged)
		{
			fuselage.MarkStateXmlDirty();
			PartInspectorUtility.QueuePreviewRefresh(fuselage, lightweight: true);
		}

		EditorGUILayout.Space(8f);
		if (GUILayout.Button("Rebuild Preview", GUILayout.Height(24f)))
		{
			Craft craft = fuselage.GetComponentInParent<Craft>();
			craft.RebuildPreviewForPart(fuselage, lightweight: false);
			EditorUtility.SetDirty(target);
			SceneView.RepaintAll();
		}

		DrawSnapButtons(fuselage);
		PartInspectorUtility.DrawPartActions(fuselage);
		PartConnectionEditorUtility.DrawConnectionEditor(fuselage);
	}

	private static void DrawSnapButtons(FuselagePart fuselage)
	{
		EditorGUILayout.Space(8f);
		EditorGUILayout.LabelField("Fuselage Snap", EditorStyles.boldLabel);
		EditorGUILayout.BeginHorizontal();
		if (GUILayout.Button("Snap Rear To Connected", GUILayout.Height(24f)))
		{
			SnapEnd(fuselage, front: false);
		}
		if (GUILayout.Button("Snap Front To Connected", GUILayout.Height(24f)))
		{
			SnapEnd(fuselage, front: true);
		}
		EditorGUILayout.EndHorizontal();
	}

	private static void SnapEnd(FuselagePart fuselage, bool front)
	{
		Undo.RecordObject(fuselage.transform, front ? "Snap Fuselage Front" : "Snap Fuselage Rear");
		if (!fuselage.SnapEndToConnected(front))
		{
			return;
		}

		Craft craft = fuselage.GetComponentInParent<Craft>();
		craft.RebuildPreviewForPart(fuselage);
		EditorUtility.SetDirty(fuselage);
		SceneView.RepaintAll();
	}

	// 以原游戏的 corner 编辑语义绘制一个截面。 / Draw one serialized fuselage section using the original game's corner editing semantics.
	private static void DrawSection(SerializedProperty section)
	{
		EditorGUI.indentLevel++;
       DrawSectionGroup(section, "BaseInfos", draw: () =>
		{
			EditorGUILayout.PropertyField(section.FindPropertyRelative("Width"));
			EditorGUILayout.PropertyField(section.FindPropertyRelative("Height"));
			EditorGUILayout.PropertyField(section.FindPropertyRelative("Trapezium"));
			EditorGUILayout.PropertyField(section.FindPropertyRelative("Thickness"));
			EditorGUILayout.PropertyField(section.FindPropertyRelative("Smooth"));
		});
		DrawSectionGroup(section, "Corners", draw: () =>
		{
			DrawCornerStyleGroup(section);
		});
		DrawSectionGroup(section, "Edges", draw: () =>
		{
			DrawFloat4Group(section.FindPropertyRelative("EdgeCurvature"), "Edge Curvature", EdgeNames);
		});
		DrawSectionGroup(section, "Slices", draw: () =>
		{
			DrawCuttingGroup(section);
		});
		EditorGUI.indentLevel--;
	}

	// 把截面 Inspector 分成可折叠的小组，减少一次性绘制控件数量。 / Split section inspector UI into foldout groups to reduce the amount of controls drawn at once.
	private static void DrawSectionGroup(SerializedProperty section, string groupName, System.Action draw)
	{
		string key = section.propertyPath + "." + groupName;
		bool expanded = GetSectionFoldout(key, defaultValue: groupName == "BaseInfos");
        expanded = EditorGUILayout.Foldout(expanded, groupName, true);
		SetSectionFoldout(key, expanded);
		if (expanded)
		{
         EditorGUI.indentLevel++;
			draw();
           EditorGUI.indentLevel--;
		}
	}

 // 按当前编辑器语义绘制每边切割：滑块大于 0 即自动启用，回到 0 则关闭。 / Draw per-side slice controls so values above zero enable cutting and zero disables it.
	private static void DrawCuttingGroup(SerializedProperty section)
	{
		SerializedProperty cutEnabled = section.FindPropertyRelative("CutEnabled");
		FuselageSectionSettings previewSection = CreatePreviewSection(section);
		previewSection.GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting);

		EditorGUILayout.LabelField("Slice Cutting", EditorStyles.boldLabel);
		EditorGUI.indentLevel++;
		for (int i = 0; i < CutNames.Length; i++)
		{
			DrawCutField(
				GetValueComponent(cutEnabled, i),
				section.FindPropertyRelative(CutFieldNames[i]),
				CutNames[i],
				minCutting[i],
				maxCutting[i]);
		}
		EditorGUI.indentLevel--;
	}

   // 把单边切割画成纯滑块，值回到 0 时自动清除启用状态。 / Draw one cut side as a pure slider that automatically clears the enabled flag at zero.
	private static void DrawCutField(SerializedProperty enabledProperty, SerializedProperty valueProperty, string label, float minCutting, float maxCutting)
	{
     float currentValue = enabledProperty.boolValue ? Mathf.Clamp01(valueProperty.floatValue) : 0f;
		EditorGUILayout.BeginHorizontal();
     float editedValue = EditorGUILayout.Slider(label, currentValue, 0f, 1f);
		editedValue = EditorGUILayout.FloatField(editedValue, GUILayout.Width(64f));
		EditorGUILayout.EndHorizontal();

       float clampedValue = Mathf.Clamp01(editedValue);
		bool enabled = clampedValue > 0.0001f;
		enabledProperty.boolValue = enabled;
        valueProperty.floatValue = enabled ? Mathf.Clamp(clampedValue, minCutting, maxCutting) : 0f;
	}

	// 读取一个截面小组的折叠状态。 / Read the persisted foldout state for one section group.
	private static bool GetSectionFoldout(string key, bool defaultValue)
	{
		return SectionFoldouts.TryGetValue(key, out bool expanded) ? expanded : defaultValue;
	}

	// 保存一个截面小组的折叠状态。 / Store the persisted foldout state for one section group.
	private static void SetSectionFoldout(string key, bool expanded)
	{
		SectionFoldouts[key] = expanded;
	}

	// 把每个 corner 画成“模式 + 单一数值”，而不是拆开的半径和 stretch 字段。 / Draw each corner as a shared mode-plus-value pair instead of separate radius and stretch fields.
	private static void DrawCornerStyleGroup(SerializedProperty section)
	{
		SerializedProperty cornerRadii = section.FindPropertyRelative("CornerRadii");
		SerializedProperty cornerStretch = section.FindPropertyRelative("CornerStretch");
		FuselageSectionSettings previewSection = CreatePreviewSection(section);
		Float4Value maxRoundedRadii = previewSection.GetMaxCornerRadii(stretched: false);
		Float4Value maxStretchedRadii = previewSection.GetMaxCornerRadii(stretched: true);

		EditorGUILayout.LabelField("Corner Styles", EditorStyles.boldLabel);
		EditorGUI.indentLevel++;
		for (int i = 0; i < CornerNames.Length; i++)
		{
			DrawCornerStyleField(cornerRadii, cornerStretch, i, CornerNames[i], maxRoundedRadii[i], maxStretchedRadii[i]);
		}
		EditorGUI.indentLevel--;
	}

	// 绘制单个 corner 的编辑行，并根据模式切换米和百分比输入。 / Draw a single corner row that switches between meter and percent editing based on mode.
	private static void DrawCornerStyleField(SerializedProperty cornerRadii, SerializedProperty cornerStretch, int index, string label, float maxRoundedRadius, float maxStretchedRadius)
	{
		SerializedProperty radiusProperty = GetValueComponent(cornerRadii, index);
		SerializedProperty stretchProperty = GetValueComponent(cornerStretch, index);
		CornerMode mode = stretchProperty.boolValue ? CornerMode.Stretched : CornerMode.Rounded;
		float activeMax = Mathf.Max(0f, mode == CornerMode.Stretched ? maxStretchedRadius : maxRoundedRadius);
		float clampedRadius = Mathf.Clamp(radiusProperty.floatValue, 0f, activeMax);

		EditorGUILayout.BeginHorizontal();
		EditorGUILayout.PrefixLabel(label);

		// 在 Rounded 和 Stretched 之间切换时保持相同的归一化位置。 / Preserve the same normalized position when switching between rounded and stretched modes.
		CornerMode newMode = (CornerMode)EditorGUILayout.EnumPopup(mode, GUILayout.MaxWidth(96f));
		if (newMode != mode)
		{
			float oldMax = Mathf.Max(0.0001f, activeMax);
			float newMax = Mathf.Max(0f, newMode == CornerMode.Stretched ? maxStretchedRadius : maxRoundedRadius);
			float normalized = clampedRadius / oldMax;
			radiusProperty.floatValue = Mathf.Clamp(normalized * newMax, 0f, newMax);
			stretchProperty.boolValue = newMode == CornerMode.Stretched;
			mode = newMode;
			activeMax = newMax;
			clampedRadius = radiusProperty.floatValue;
		}

		float displayValue = mode == CornerMode.Stretched ? clampedRadius * 100f : clampedRadius;
		float maxDisplayValue = mode == CornerMode.Stretched ? activeMax * 100f : activeMax;
		float newDisplayValue = EditorGUILayout.FloatField(displayValue);
		GUILayout.Label(mode == CornerMode.Stretched ? "%" : "m", GUILayout.Width(18f));
		EditorGUILayout.EndHorizontal();

		// 把百分比输入换算回 stretched corner 内部保存的归一化半径。 / Convert percent input back into the stored normalized radius used by stretched corners.
		if (!Mathf.Approximately(newDisplayValue, displayValue))
		{
			float clampedDisplayValue = Mathf.Clamp(newDisplayValue, 0f, maxDisplayValue);
			radiusProperty.floatValue = mode == CornerMode.Stretched ? clampedDisplayValue * 0.01f : clampedDisplayValue;
		}
	}

	// 构建一个轻量截面结构，让编辑器 UI 复用运行时的 corner 上限计算。 / Build a lightweight section struct so editor UI can reuse runtime corner limit calculations.
	private static FuselageSectionSettings CreatePreviewSection(SerializedProperty section)
	{
		FuselageSectionSettings previewSection = new FuselageSectionSettings
		{
			Width = section.FindPropertyRelative("Width").floatValue,
			Height = section.FindPropertyRelative("Height").floatValue,
			Trapezium = section.FindPropertyRelative("Trapezium").floatValue,
			Thickness = section.FindPropertyRelative("Thickness").floatValue,
			CornerRadii = ReadFloat4(section.FindPropertyRelative("CornerRadii")),
			CornerStretch = ReadBool4(section.FindPropertyRelative("CornerStretch")),
			EdgeCurvature = ReadFloat4(section.FindPropertyRelative("EdgeCurvature")),
			CutEnabled = ReadBool4(section.FindPropertyRelative("CutEnabled")),
			CutTop = section.FindPropertyRelative("CutTop").floatValue,
			CutRight = section.FindPropertyRelative("CutRight").floatValue,
			CutBottom = section.FindPropertyRelative("CutBottom").floatValue,
			CutLeft = section.FindPropertyRelative("CutLeft").floatValue
		};
		previewSection.CornerStretchAmount = previewSection.CornerStretch.ToFloatMask();
		previewSection.Sanitize();
		return previewSection;
	}

	// 从序列化的 Float4Value 读出运行时副本，供预览和范围计算使用。 / Read a serialized Float4Value into a runtime copy for preview and range calculations.
	private static Float4Value ReadFloat4(SerializedProperty property)
	{
		return new Float4Value(
			property.FindPropertyRelative("X").floatValue,
			property.FindPropertyRelative("Y").floatValue,
			property.FindPropertyRelative("Z").floatValue,
			property.FindPropertyRelative("W").floatValue);
	}

	// 从序列化的 Int4Value 读出运行时副本，供预览和范围计算使用。 / Read a serialized Int4Value into a runtime copy for preview and range calculations.
	private static Int4Value ReadInt4(SerializedProperty property)
	{
		return new Int4Value(
			property.FindPropertyRelative("X").intValue,
			property.FindPropertyRelative("Y").intValue,
			property.FindPropertyRelative("Z").intValue,
			property.FindPropertyRelative("W").intValue);
	}

	// 从序列化的 Bool4Value 读出运行时副本，供预览和范围计算使用。 / Read a serialized Bool4Value into a runtime copy for preview and range calculations.
	private static Bool4Value ReadBool4(SerializedProperty property)
	{
		return new Bool4Value(
			property.FindPropertyRelative("X").boolValue,
			property.FindPropertyRelative("Y").boolValue,
			property.FindPropertyRelative("Z").boolValue,
			property.FindPropertyRelative("W").boolValue);
	}

	// 从四元辅助结构里取出指定的 X/Y/Z/W 子属性。 / Resolve a specific X/Y/Z/W child property from the serialized 4-component helper structs.
	private static SerializedProperty GetValueComponent(SerializedProperty property, int index)
	{
		return property.FindPropertyRelative(ValueComponentNames[index]);
	}

	// 用逐分量行的方式绘制 Float4Value。 / Draw a labeled Float4Value group using one line per component.
	private static void DrawFloat4Group(SerializedProperty property, string label, string[] itemNames)
	{
		DrawValueGroup(property, label, itemNames, (itemProperty, itemLabel) =>
		{
			EditorGUILayout.PropertyField(itemProperty, new GUIContent(itemLabel));
		});
	}

	// 用逐分量行的方式绘制 Int4Value。 / Draw a labeled Int4Value group using one line per component.
	private static void DrawInt4Group(SerializedProperty property, string label, string[] itemNames)
	{
		DrawValueGroup(property, label, itemNames, (itemProperty, itemLabel) =>
		{
			EditorGUILayout.PropertyField(itemProperty, new GUIContent(itemLabel));
		});
	}

	// 复用四元辅助结构的通用绘制模式。 / Share the repeated four-component drawing pattern across the helper value structs.
	private static void DrawValueGroup(SerializedProperty property, string label, string[] itemNames, System.Action<SerializedProperty, string> drawValue)
	{
		EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
		EditorGUI.indentLevel++;
		drawValue(property.FindPropertyRelative("X"), itemNames[0]);
		drawValue(property.FindPropertyRelative("Y"), itemNames[1]);
		drawValue(property.FindPropertyRelative("Z"), itemNames[2]);
		drawValue(property.FindPropertyRelative("W"), itemNames[3]);
		EditorGUI.indentLevel--;
	}
}

[CustomEditor(typeof(Part), true)]
public class PartEditor : UnityEditor.Editor
{
	public override void OnInspectorGUI()
	{
		Part selectedPart = target as Part;
		if (targets.Length == 1 && selectedPart != null)
		{
			PartInspectorUtility.DrawPartIdentity(selectedPart);
		}

		serializedObject.Update();
		EditorGUI.BeginChangeCheck();
		DrawPropertiesExcluding(serializedObject, "_partId", "_partType", "_materialIds", "_materialsText", "_connectionEndpoints", "_targetPartIdsAttributeName", "_stateXmlDirty", "_rawPartXml", "_rawStateXml");
		bool propertyGuiChanged = EditorGUI.EndChangeCheck();
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawPartXml", "Cached Part XML");
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawStateXml", "Cached State XML");
		bool changed = serializedObject.ApplyModifiedProperties();
		if (targets.Length != 1 || selectedPart == null)
		{
			return;
		}

		if (propertyGuiChanged || changed)
		{
			selectedPart.MarkStateXmlDirty();
			PartInspectorUtility.QueuePreviewRefresh(selectedPart);
		}

		PartInspectorUtility.DrawMaterialEditor(selectedPart);
		PartInspectorUtility.DrawPartActions(selectedPart);
		PartConnectionEditorUtility.DrawConnectionEditor(selectedPart);
	}
}

internal class RawXmlTextEditorWindow : EditorWindow
{
	private Object _targetObject;

	private string _propertyPath;

	private string _label;

	private string _text;

	private Vector2 _scroll;

	// 打开一个独立窗口查看并编辑缓存的 XML 文本。 / Open a standalone window to inspect and edit cached XML text.
	public static void Open(Object targetObject, string propertyPath, string label)
	{
		if (targetObject == null || string.IsNullOrWhiteSpace(propertyPath))
		{
			return;
		}

		RawXmlTextEditorWindow window = GetWindow<RawXmlTextEditorWindow>("Cached XML");
		window._targetObject = targetObject;
		window._propertyPath = propertyPath;
		window._label = label;
		window.LoadText();
		window.minSize = new Vector2(560f, 360f);
		window.Show();
	}

	// 绘制 XML 文本编辑窗口的主体界面。 / Draw the main UI of the cached XML text editor window.
	private void OnGUI()
	{
		if (_targetObject == null || string.IsNullOrWhiteSpace(_propertyPath))
		{
			EditorGUILayout.HelpBox("The source object is no longer available.", MessageType.Info);
			return;
		}

		EditorGUILayout.LabelField(_label ?? "Cached XML", EditorStyles.boldLabel);
		EditorGUILayout.Space(4f);
		_scroll = EditorGUILayout.BeginScrollView(_scroll);
		_text = EditorGUILayout.TextArea(_text ?? string.Empty, GUILayout.ExpandHeight(true));
		EditorGUILayout.EndScrollView();

		EditorGUILayout.Space(6f);
		EditorGUILayout.BeginHorizontal();
		if (GUILayout.Button("Reload", GUILayout.Width(86f)))
		{
			LoadText();
		}
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("Apply", GUILayout.Width(86f)))
		{
			ApplyText();
		}
		EditorGUILayout.EndHorizontal();
	}

	// 从目标对象重新读取当前缓存 XML 文本。 / Reload the current cached XML text from the target object.
	private void LoadText()
	{
		SerializedObject serializedObject = new SerializedObject(_targetObject);
		SerializedProperty property = serializedObject.FindProperty(_propertyPath);
		_text = property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue : string.Empty;
	}

	// 把编辑后的 XML 文本写回目标序列化属性。 / Write the edited XML text back into the target serialized property.
	private void ApplyText()
	{
		SerializedObject serializedObject = new SerializedObject(_targetObject);
		SerializedProperty property = serializedObject.FindProperty(_propertyPath);
		if (property == null || property.propertyType != SerializedPropertyType.String)
		{
			return;
		}

		Undo.RecordObject(_targetObject, "Edit Cached XML");
		property.stringValue = _text ?? string.Empty;
		serializedObject.ApplyModifiedProperties();
		EditorUtility.SetDirty(_targetObject);
	}
}

internal static class PartInspectorUtility
{
	public static void DrawPartIdentity(Part part)
	{
		if (part == null)
		{
			return;
		}

		EditorGUILayout.Space(4f);
		EditorGUILayout.LabelField("Part Identity", EditorStyles.boldLabel);
		using (new EditorGUI.DisabledScope(true))
		{
			EditorGUILayout.IntField("Part Id", part.PartId);
			EditorGUILayout.TextField("Part Type", part.PartType);
		}
	}

	public static void DrawMaterialEditor(Part part)
	{
		if (part == null)
		{
			return;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		EditorGUILayout.Space(8f);
		EditorGUILayout.LabelField("Materials XML Attribute", EditorStyles.boldLabel);
		string currentMaterials = part.MaterialsText;
		string nextMaterials = EditorGUILayout.TextField("materials", currentMaterials);
		if (string.Equals(nextMaterials, currentMaterials, System.StringComparison.Ordinal))
		{
			return;
		}

		RegisterPartUndo(part, craft, "Change Part Materials");
		part.SetMaterialsText(nextMaterials);
		RefreshPartPreview(part, craft);
	}

	public static void DrawPartActions(Part part)
	{
		if (part == null)
		{
			return;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		EditorGUILayout.Space(8f);
		EditorGUILayout.LabelField("Part Actions", EditorStyles.boldLabel);
		EditorGUILayout.BeginHorizontal();
		if (GUILayout.Button("Clone Part", GUILayout.Height(24f)))
		{
			ClonePart(craft, part);
		}
		EditorGUILayout.EndHorizontal();
	}

	public static void DrawRawXmlFoldout(SerializedObject owner, string propertyName, string label)
	{
		if (owner == null || string.IsNullOrWhiteSpace(propertyName))
		{
			return;
		}

		SerializedProperty property = owner.FindProperty(propertyName);
		if (property == null || property.propertyType != SerializedPropertyType.String)
		{
			return;
		}

		EditorGUILayout.Space(6f);
		EditorGUILayout.BeginHorizontal();
		EditorGUILayout.LabelField(label, GUILayout.MinWidth(120f));
		GUILayout.FlexibleSpace();
		EditorGUILayout.LabelField($"{property.stringValue?.Length ?? 0} chars", EditorStyles.miniLabel, GUILayout.Width(72f));
		if (GUILayout.Button("Open", GUILayout.Width(64f)))
		{
			RawXmlTextEditorWindow.Open(owner.targetObject, property.propertyPath, label);
		}
		EditorGUILayout.EndHorizontal();
	}

	public static void QueuePreviewRefresh(Part part, bool lightweight = true)
	{
		if (part == null)
		{
			return;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		double delaySeconds = part is FuselagePart ? FuselagePart.EditorPreviewRefreshDelaySeconds : 0.08d;
		craft.QueuePreviewRebuildForPart(part, delaySeconds, lightweight);
		EditorUtility.SetDirty(craft);

		EditorUtility.SetDirty(part);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	private static void ClonePart(Craft craft, Part source)
	{
		Part clone = craft.ClonePart(source);
		if (clone == null)
		{
			return;
		}

		Undo.RegisterCreatedObjectUndo(clone.gameObject, "Clone Part");
		EditorUtility.SetDirty(craft);
		Selection.activeGameObject = clone.gameObject;
		SceneView.RepaintAll();
	}

	private static void RefreshPartPreview(Part part, Craft craft)
	{
		craft.RebuildPreviewForPart(part);
		EditorUtility.SetDirty(craft);

		EditorUtility.SetDirty(part);
		SceneView.RepaintAll();
	}

	private static void RegisterPartUndo(Part part, Craft craft, string actionName)
	{
		Undo.RecordObject(craft, actionName);
		Undo.RecordObject(part, actionName);
	}
}

internal static class PartConnectionEditorUtility
{
	public static void DrawConnectionEditor(Part part)
	{
		if (part == null)
		{
			return;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		EditorGUILayout.Space(10f);
		EditorGUILayout.LabelField("Attach Point Connections", EditorStyles.boldLabel);

		IReadOnlyList<PartConnectionEndpoint> endpoints = part.ConnectionEndpoints;
		bool changed = false;
		for (int i = 0; i < endpoints.Count; i++)
		{
			PartConnectionEndpoint endpoint = endpoints[i];
			EditorGUILayout.BeginVertical(EditorStyles.helpBox);
			EditorGUILayout.BeginHorizontal();
			EditorGUILayout.LabelField($"Connection {endpoint.ConnectionId}", EditorStyles.boldLabel);
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("Remove", GUILayout.Width(72f)))
			{
				RegisterUndo(part, craft, "Remove Part Connection");
				part.RemoveConnectionEndpointAt(i);
				changed = true;
				EditorGUILayout.EndHorizontal();
				EditorGUILayout.EndVertical();
				break;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUI.BeginChangeCheck();
			bool isPartAEndpoint = EditorGUILayout.Toggle("Owns partA", endpoint.IsPartAEndpoint);
			int localAttachPointId = EditorGUILayout.IntField("Local Attach Point", endpoint.LocalAttachPointId);
			int connectedPartId = EditorGUILayout.IntField("Connected Part Id", endpoint.ConnectedPartId);
			int connectedAttachPointId = EditorGUILayout.IntField("Connected Attach Point", endpoint.ConnectedAttachPointId);
			if (EditorGUI.EndChangeCheck())
			{
				RegisterUndo(part, craft, "Edit Part Connection");
				endpoint.IsPartAEndpoint = isPartAEndpoint;
				endpoint.LocalAttachPointId = Mathf.Max(0, localAttachPointId);
				endpoint.ConnectedPartId = Mathf.Max(0, connectedPartId);
				endpoint.ConnectedAttachPointId = Mathf.Max(0, connectedAttachPointId);
				changed = true;
			}

			DrawConnectionResolution(part, craft, endpoint);
			EditorGUILayout.EndVertical();
		}

		if (GUILayout.Button("Add Connection", GUILayout.Height(22f)))
		{
			RegisterUndo(part, craft, "Add Part Connection");
			int connectionId = craft.AllocateConnectionId();
			part.AddConnectionEndpoint(connectionId, isPartAEndpoint: true, localAttachPointId: 0, connectedPartId: 0, connectedAttachPointId: 0);
			changed = true;
		}

		if (!changed)
		{
			return;
		}

		craft.SynchronizeConnectionsFrom(part);
		EditorUtility.SetDirty(craft);
		PartInspectorUtility.QueuePreviewRefresh(part);
	}

	private static void DrawConnectionResolution(Part part, Craft craft, PartConnectionEndpoint endpoint)
	{
		if (endpoint == null || endpoint.ConnectedPartId <= 0)
		{
			return;
		}

		Part connectedPart = craft.FindPartById(endpoint.ConnectedPartId);
		if (connectedPart == null)
		{
			EditorGUILayout.HelpBox("Connected part id is not present in this Craft.", MessageType.Warning);
			return;
		}

		EditorGUILayout.LabelField("Resolved", $"{part.PartId}:{endpoint.LocalAttachPointId} -> {connectedPart.PartId}:{endpoint.ConnectedAttachPointId}");
	}

	private static void RegisterUndo(Part part, Craft craft, string actionName)
	{
		Undo.RecordObject(craft, actionName);
		Undo.RecordObject(part, actionName);
	}
}
