using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;


[CustomEditor(typeof(Craft))]
public class CraftEditor : UnityEditor.Editor
{
	// 绘制 Craft 根对象的自定义 Inspector 与导入导出按钮。 / Draw the custom inspector and import/export actions for the craft root.
	public override void OnInspectorGUI()
	{
		serializedObject.Update();
		DrawPropertiesExcluding(serializedObject, "_rawAircraftXml", "_themeMaterials", "_renderWindowBayPreviewMeshes", "_craftInfo");
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawAircraftXml", "Cached Aircraft XML");
		if (serializedObject.ApplyModifiedProperties())
		{
			((Craft)target).HandleInspectorDataChanged();
			EditorUtility.SetDirty(target);
		}

		DrawWindowBayPreviewToggle();
		EditorGUILayout.Space(12f);

		Craft craft = (Craft)target;
		if (GUILayout.Button("Import Craft XML", GUILayout.Height(28f)))
		{
			string path = EditorUtility.OpenFilePanel("Import Craft XML", string.IsNullOrWhiteSpace(craft.SourceXmlPath) ? Application.dataPath : System.IO.Path.GetDirectoryName(craft.SourceXmlPath), "xml");
			if (!string.IsNullOrWhiteSpace(path))
			{
				Undo.RecordObject(craft, "Import Craft XML");
				craft.ImportFromXml(path);
				EditorUtility.SetDirty(craft);
				EditorUtility.SetDirty(craft.gameObject);
				SceneView.RepaintAll();
			}
		}

		if (GUILayout.Button("Export Craft XML", GUILayout.Height(28f)))
		{
			string startDirectory = string.IsNullOrWhiteSpace(craft.LastExportPath)
				? (string.IsNullOrWhiteSpace(craft.SourceXmlPath) ? Application.dataPath : System.IO.Path.GetDirectoryName(craft.SourceXmlPath))
				: System.IO.Path.GetDirectoryName(craft.LastExportPath);
			string startName = string.IsNullOrWhiteSpace(craft.LastExportPath) ? craft.name + ".xml" : System.IO.Path.GetFileName(craft.LastExportPath);
			string path = EditorUtility.SaveFilePanel("Export Craft XML", startDirectory, startName, "xml");
			if (!string.IsNullOrWhiteSpace(path))
			{
				craft.ExportToXml(path);
				EditorUtility.SetDirty(craft);
				EditorUtility.SetDirty(craft.gameObject);
			}
		}

		if (GUILayout.Button("Rebuild All Previews", GUILayout.Height(24f)))
		{
			craft.RebuildAllPreviews(lightweight: false);
			EditorUtility.SetDirty(craft.gameObject);
			SceneView.RepaintAll();
		}

		if (GUILayout.Button("ViewInfo", GUILayout.Height(24f)))
		{
			CraftInfoWindow.Open(craft);
		}
	}

	// 绘制 Window/Bay 预览网格显示开关，不触发完整网格重建。 / Draw the Window/Bay preview render toggle without forcing a full mesh rebuild.
	private void DrawWindowBayPreviewToggle()
	{
		Craft craft = (Craft)target;
		serializedObject.Update();
		SerializedProperty renderProperty = serializedObject.FindProperty("_renderWindowBayPreviewMeshes");
		if (renderProperty == null)
		{
			return;
		}

		EditorGUI.BeginChangeCheck();
		EditorGUILayout.PropertyField(renderProperty, new GUIContent("Render Window/Bay Meshes"));
		if (!EditorGUI.EndChangeCheck())
		{
			return;
		}

		serializedObject.ApplyModifiedProperties();
		craft.ApplyWindowBayPreviewVisibility();
		EditorUtility.SetDirty(craft);
	}

	[MenuItem("GameObject/SP2 Craft Editor/Create Craft Root", false, 10)]
	// 在层级面板里创建一个新的 Craft 根对象。 / Create a new Craft root object from the hierarchy menu.
	private static void CreateCraftRoot(MenuCommand command)
	{
		GameObject root = new GameObject("Craft");
		GameObjectUtility.SetParentAndAlign(root, command.context as GameObject);
		root.AddComponent<Craft>();
		Undo.RegisterCreatedObjectUndo(root, "Create Craft Root");
		Selection.activeObject = root;
	}
}

internal class CraftInfoWindow : EditorWindow
{
	private Craft _craft;

	private Vector2 _scroll;

	private bool _showAircraftInfo = true;

	private bool _showTransformTree = true;

	private bool _showCachedHierarchy = true;

	private bool _showPinnedParts = true;

	// 打开 Craft 信息窗口。 / Open the Craft information window.
	public static void Open(Craft craft)
	{
		CraftInfoWindow window = GetWindow<CraftInfoWindow>("Craft Info");
		window._craft = craft;
		window.minSize = new Vector2(520f, 420f);
		window.Show();
	}

	// 绘制 Craft 信息和层级只读视图。 / Draw a read-only view of Craft data and hierarchy.
	private void OnGUI()
	{
		_craft = EditorGUILayout.ObjectField("Craft", _craft, typeof(Craft), true) as Craft;
		if (_craft == null)
		{
			EditorGUILayout.HelpBox("Select a Craft to view its information.", MessageType.Info);
			return;
		}

		EditorGUILayout.Space(6f);
		EditorGUILayout.BeginHorizontal();
		if (GUILayout.Button("Select Craft", GUILayout.Width(96f)))
		{
			Selection.activeObject = _craft.gameObject;
		}
		if (GUILayout.Button("Refresh", GUILayout.Width(80f)))
		{
			Repaint();
		}
		EditorGUILayout.EndHorizontal();

		_scroll = EditorGUILayout.BeginScrollView(_scroll);
		DrawAircraftInfo();
		DrawTransformTree();
		DrawCachedHierarchy();
		DrawPinnedParts();
		EditorGUILayout.EndScrollView();
	}

	// 绘制飞机概要统计。 / Draw summary statistics for the craft.
	private void DrawAircraftInfo()
	{
		_showAircraftInfo = EditorGUILayout.BeginFoldoutHeaderGroup(_showAircraftInfo, "Aircraft Info");
		if (_showAircraftInfo)
		{
			Part[] parts = _craft.GetComponentsInChildren<Part>(includeInactive: true);
			EditorGUILayout.LabelField("Name", _craft.name);
			EditorGUILayout.LabelField("Source XML", string.IsNullOrWhiteSpace(_craft.SourceXmlPath) ? "(none)" : _craft.SourceXmlPath);
			EditorGUILayout.LabelField("Last Export", string.IsNullOrWhiteSpace(_craft.LastExportPath) ? "(none)" : _craft.LastExportPath);
			EditorGUILayout.LabelField("Total Parts", parts.Length.ToString());
			EditorGUILayout.LabelField("Fuselage Parts", CountParts<FuselagePart>(parts).ToString());
			EditorGUILayout.LabelField("Window Parts", CountParts<WindowPart>(parts).ToString());
			EditorGUILayout.LabelField("Bay Parts", CountParts<BayPart>(parts).ToString());
			EditorGUILayout.LabelField("Other Parts", CountOtherParts(parts).ToString());
			EditorGUILayout.LabelField("Grouping Nodes", CountGroupingNodes(_craft.transform).ToString());
			EditorGUILayout.LabelField("Paint Materials", _craft.PaintMaterialCount.ToString());
			EditorGUILayout.LabelField("Has Connections", _craft.HasConnectionData ? "Yes" : "No");
		}
		EditorGUILayout.EndFoldoutHeaderGroup();
	}

	// 绘制当前 Unity 分类树，只显示 Group 和下属 Part 数量。 / Draw the current Unity grouping tree with Group nodes and Part counts only.
	private void DrawTransformTree()
	{
		EditorGUILayout.Space(8f);
		_showTransformTree = EditorGUILayout.BeginFoldoutHeaderGroup(_showTransformTree, "Grouping Tree");
		if (_showTransformTree)
		{
			DrawGroupNode(_craft.transform, 0);
		}
		EditorGUILayout.EndFoldoutHeaderGroup();
	}

	// 绘制隐藏序列化层级缓存，方便检查下一次 XML 导入会怎样还原。 / Draw the hidden serialized hierarchy cache used by the next XML import.
	private void DrawCachedHierarchy()
	{
		EditorGUILayout.Space(8f);
		_showCachedHierarchy = EditorGUILayout.BeginFoldoutHeaderGroup(_showCachedHierarchy, "Cached Tree Map");
		if (_showCachedHierarchy)
		{
			CraftInfo info = _craft.Info;
			EditorGUILayout.LabelField("Cached Group Nodes", info.HierarchyNodes.Count.ToString());
			EditorGUILayout.LabelField("Cached Part Assignments", info.HierarchyAssignments.Count.ToString());
			DrawCachedNodes(info.HierarchyNodes, info.HierarchyAssignments);
		}
		EditorGUILayout.EndFoldoutHeaderGroup();
	}

	// 绘制 CraftInfo 中被 Pin 的零件基准。 / Draw pinned part baselines stored in CraftInfo.
	private void DrawPinnedParts()
	{
		EditorGUILayout.Space(8f);
		_showPinnedParts = EditorGUILayout.BeginFoldoutHeaderGroup(_showPinnedParts, "Pinned Parts");
		if (_showPinnedParts)
		{
			IReadOnlyList<CraftInfo.PinnedPartTransformRecord> pinnedParts = _craft.Info.PinnedPartTransforms;
			EditorGUILayout.LabelField("Pinned Count", pinnedParts.Count.ToString());
			foreach (CraftInfo.PinnedPartTransformRecord record in pinnedParts.Where(item => item != null).OrderBy(item => item.PartId))
			{
				Part part = _craft.FindPartById(record.PartId);
				EditorGUILayout.BeginHorizontal();
				EditorGUILayout.LabelField(
					$"#{record.PartId} exists={(part != null ? "Yes" : "No")} pos={record.PositionText} rot={record.RotationText}");
				using (new EditorGUI.DisabledScope(part == null))
				{
					if (GUILayout.Button("Select", GUILayout.Width(64f)))
					{
						Selection.activeObject = part.gameObject;
					}
				}
				EditorGUILayout.EndHorizontal();
			}
		}
		EditorGUILayout.EndFoldoutHeaderGroup();
	}

	// 递归绘制一个分类节点，只显示下属 Part 数量，不列出每个 Part。 / Recursively draw one grouping node, showing Part counts without listing individual Parts.
	private static void DrawGroupNode(Transform node, int depth)
	{
		if (node == null || node.GetComponent<Part>() != null)
		{
			return;
		}

		EditorGUILayout.BeginHorizontal();
		GUILayout.Space(depth * 16f);
		EditorGUILayout.LabelField(GetGroupNodeLabel(node, depth == 0));
		if (GUILayout.Button("Select", GUILayout.Width(64f)))
		{
			Selection.activeObject = node.gameObject;
		}
		EditorGUILayout.EndHorizontal();

		foreach (Transform child in node)
		{
			if (child.GetComponent<Part>() == null)
			{
				DrawGroupNode(child, depth + 1);
			}
		}
	}

	// 绘制缓存的分类节点表，只显示每个节点下属 Part 数量。 / Draw the cached grouping-node table with Part counts only.
	private static void DrawCachedNodes(
		IReadOnlyList<CraftInfo.HierarchyNodeRecord> nodes,
		IReadOnlyList<CraftInfo.PartHierarchyAssignment> assignments)
	{
		if (nodes == null || nodes.Count == 0)
		{
			return;
		}

		Dictionary<int, int> parentByNodeId = BuildCachedParentMap(nodes);
		Dictionary<int, int> directPartCountByNodeId = BuildCachedDirectPartCounts(assignments);
		EditorGUILayout.Space(4f);
		EditorGUILayout.LabelField("Group Nodes", EditorStyles.boldLabel);
		for (int i = 0; i < nodes.Count; i++)
		{
			CraftInfo.HierarchyNodeRecord node = nodes[i];
			if (node == null)
			{
				continue;
			}

			int nodeId = node.NodeId;
			int parentNodeId = node.ParentNodeId;
			int siblingIndex = node.SiblingIndex;
			string name = node.Name ?? string.Empty;
			int directParts = directPartCountByNodeId.TryGetValue(nodeId, out int directCount) ? directCount : 0;
			int totalParts = CountCachedDescendantParts(nodeId, parentByNodeId, directPartCountByNodeId);
			EditorGUILayout.LabelField($"#{nodeId} parent={parentNodeId} sibling={siblingIndex} parts={directParts}/{totalParts} name={name}");
		}
	}

	private static string GetGroupNodeLabel(Transform node, bool isRoot)
	{
		string prefix = isRoot ? "[Craft]" : "[Group]";
		int childGroups = CountDirectGroupChildren(node);
		int directParts = CountDirectPartChildren(node);
		int totalParts = CountDescendantParts(node);
		return $"{prefix} {node.name} groups={childGroups} parts={directParts}/{totalParts}";
	}

	private static int CountDirectGroupChildren(Transform node)
	{
		int count = 0;
		foreach (Transform child in node)
		{
			if (child.GetComponent<Part>() == null)
			{
				count++;
			}
		}
		return count;
	}

	private static int CountDirectPartChildren(Transform node)
	{
		int count = 0;
		foreach (Transform child in node)
		{
			if (child.GetComponent<Part>() != null)
			{
				count++;
			}
		}
		return count;
	}

	private static int CountDescendantParts(Transform node)
	{
		int count = 0;
		foreach (Transform child in node)
		{
			if (child.GetComponent<Part>() != null)
			{
				count++;
				continue;
			}

			count += CountDescendantParts(child);
		}
		return count;
	}

	private static Dictionary<int, int> BuildCachedParentMap(IReadOnlyList<CraftInfo.HierarchyNodeRecord> nodes)
	{
		Dictionary<int, int> result = new Dictionary<int, int>();
		if (nodes == null)
		{
			return result;
		}

		for (int i = 0; i < nodes.Count; i++)
		{
			CraftInfo.HierarchyNodeRecord node = nodes[i];
			int nodeId = node?.NodeId ?? 0;
			if (nodeId > 0)
			{
				result[nodeId] = node.ParentNodeId;
			}
		}
		return result;
	}

	private static Dictionary<int, int> BuildCachedDirectPartCounts(IReadOnlyList<CraftInfo.PartHierarchyAssignment> assignments)
	{
		Dictionary<int, int> result = new Dictionary<int, int>();
		if (assignments == null)
		{
			return result;
		}

		for (int i = 0; i < assignments.Count; i++)
		{
			int parentNodeId = assignments[i]?.ParentNodeId ?? 0;
			if (parentNodeId <= 0)
			{
				continue;
			}

			result[parentNodeId] = result.TryGetValue(parentNodeId, out int count) ? count + 1 : 1;
		}
		return result;
	}

	private static int CountCachedDescendantParts(int nodeId, Dictionary<int, int> parentByNodeId, Dictionary<int, int> directPartCountByNodeId)
	{
		int total = directPartCountByNodeId.TryGetValue(nodeId, out int directCount) ? directCount : 0;
		foreach (KeyValuePair<int, int> item in parentByNodeId)
		{
			if (item.Value == nodeId)
			{
				total += CountCachedDescendantParts(item.Key, parentByNodeId, directPartCountByNodeId);
			}
		}
		return total;
	}

	private static int CountParts<TPart>(Part[] parts) where TPart : Part
	{
		int count = 0;
		foreach (Part part in parts)
		{
			if (part is TPart)
			{
				count++;
			}
		}
		return count;
	}

	private static int CountOtherParts(Part[] parts)
	{
		int count = 0;
		foreach (Part part in parts)
		{
			if (!(part is FuselagePart) && !(part is WindowPart) && !(part is BayPart))
			{
				count++;
			}
		}
		return count;
	}

	private static int CountGroupingNodes(Transform root)
	{
		int count = 0;
		foreach (Transform child in root)
		{
			count += CountGroupingNodesRecursive(child);
		}
		return count;
	}

	private static int CountGroupingNodesRecursive(Transform node)
	{
		if (node == null || node.GetComponent<Part>() != null)
		{
			return 0;
		}

		int count = 1;
		foreach (Transform child in node)
		{
			count += CountGroupingNodesRecursive(child);
		}
		return count;
	}

}
