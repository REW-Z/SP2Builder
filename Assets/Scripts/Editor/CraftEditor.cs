
using UnityEditor;
using UnityEngine;


[CustomEditor(typeof(Craft))]
public class CraftEditor : UnityEditor.Editor
{
	// 绘制 Craft 根对象的自定义 Inspector 与导入导出按钮。 / Draw the custom inspector and import/export actions for the craft root.
	public override void OnInspectorGUI()
	{
		serializedObject.Update();
		DrawPropertiesExcluding(serializedObject, "_rawAircraftXml", "_themeMaterials");
		PartInspectorUtility.DrawRawXmlFoldout(serializedObject, "_rawAircraftXml", "Cached Aircraft XML");
		if (serializedObject.ApplyModifiedProperties())
		{
			((Craft)target).HandleInspectorDataChanged();
			EditorUtility.SetDirty(target);
		}

		EditorGUILayout.Space(12f);

		Craft craft = (Craft)target;
		if (GUILayout.Button("Import Craft XML", GUILayout.Height(28f)))
		{
			string path = EditorUtility.OpenFilePanel("Import Craft XML", string.IsNullOrWhiteSpace(craft.SourceXmlPath) ? Application.dataPath : System.IO.Path.GetDirectoryName(craft.SourceXmlPath), "xml");
			if (!string.IsNullOrWhiteSpace(path))
			{
				Undo.RecordObject(craft, "Import Craft XML");
				craft.ImportFromXml(path);
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
				EditorUtility.SetDirty(craft.gameObject);
			}
		}

		if (GUILayout.Button("Rebuild All Previews", GUILayout.Height(24f)))
		{
			craft.RebuildAllPreviews(lightweight: false);
			EditorUtility.SetDirty(craft.gameObject);
			SceneView.RepaintAll();
		}
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
