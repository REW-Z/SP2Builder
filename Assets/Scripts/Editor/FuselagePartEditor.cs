using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
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

	private const float CornerDisplayStep = 0.001f;

	private const float CornerDisplayStepInverse = 1000f;

	private const float AutoConnectMaxDistance = 30f;

	private const float AutoConnectMaxDistanceSqr = AutoConnectMaxDistance * AutoConnectMaxDistance;

	private const float MinimumSliceRatio = 0.001f;

	private const float MinimumExtensionRatio = 0.0001f;

	private const float BridgeMinimumDistance = 0.0001f;

	private const float BridgeNormalWarningDot = 0.98f;

	private const float GeneratedFrameLength = 0.01f;

	private const string AutoConnectSelectedMenuPath = "Tools/SP2 Craft Editor/Fuselage/Auto Connect Selected #t";

	private const string SnapSelectedRearMenuPath = "Tools/SP2 Craft Editor/Fuselage/Snap Selected Rear #q";

	private const string SnapSelectedFrontMenuPath = "Tools/SP2 Craft Editor/Fuselage/Snap Selected Front #e";

	private const string SliceSelectedByRatioMenuPath = "Tools/SP2 Craft Editor/Fuselage/Slice Selected By Ratio...";

	private const string ExtendSelectedFromRearMenuPath = "Tools/SP2 Craft Editor/Fuselage/Extend Selected From Rear...";

	private const string ExtendSelectedFromFrontMenuPath = "Tools/SP2 Craft Editor/Fuselage/Extend Selected From Front...";

	private const string BridgeSelectedFramesMenuPath = "Tools/SP2 Craft Editor/Fuselage/Bridge Selected Frames #b";

	private const string GenerateSelectedEndFramesMenuPath = "Tools/SP2 Craft Editor/Fuselage/Generate End Frames From Selected";

	private bool _showRearSection = true;

	private bool _showFrontSection = true;

	private static readonly Dictionary<string, bool> SectionFoldouts = new Dictionary<string, bool>();

	private static readonly Dictionary<string, GUIStyle> ColoredStyleCache = new Dictionary<string, GUIStyle>();

	private static readonly Color BaseInfoLabelColor = new Color(0.34f, 0.78f, 0.36f);

	private static readonly Color CornerLabelColor = new Color(1f, 0.78f, 0.18f);

	private static readonly Color EdgeLabelColor = new Color(0.28f, 0.62f, 1f);

	private static readonly Color SliceLabelColor = new Color(1f, 0.34f, 0.28f);

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
		SerializedProperty visualStyleProperty = serializedObject.FindProperty("_visualStyle");
		if (visualStyleProperty.enumValueIndex == (int)FuselageVisualStyle.Cone || visualStyleProperty.enumValueIndex == (int)FuselageVisualStyle.HollowCone)
		{
			EditorGUILayout.Slider(serializedObject.FindProperty("_noseconeRoundness"), 0f, 1f);
		}
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_glass"));
		EditorGUILayout.PropertyField(serializedObject.FindProperty("_offset"), new GUIContent("Run / Rise / Length"));

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

		DrawCopySectionButtons(fuselage);
		PartInspectorUtility.DrawPartActions(fuselage);
		PartConnectionEditorUtility.DrawConnectionEditor(fuselage);
	}

	// 绘制前后端面的截面复制按钮。 / Draw the section-copy buttons for the front and rear fuselage ends.
	private static void DrawCopySectionButtons(FuselagePart fuselage)
	{
		EditorGUILayout.Space(8f);
		EditorGUILayout.LabelField("Fuselage Section Tools", EditorStyles.boldLabel);
		EditorGUILayout.BeginHorizontal();
		if (GUILayout.Button("CopySectionRear", GUILayout.Height(24f)))
		{
			CopyConnectedSection(fuselage, front: false);
		}
		if (GUILayout.Button("CopySectionFront", GUILayout.Height(24f)))
		{
			CopyConnectedSection(fuselage, front: true);
		}
		EditorGUILayout.EndHorizontal();
	}

	// 从相连机身复制前/后端截面，并立即重建预览。 / Copy the connected front or rear section and rebuild the preview immediately.
	private static void CopyConnectedSection(FuselagePart fuselage, bool front)
	{
		if (fuselage == null)
		{
			return;
		}

		Undo.RecordObject(fuselage, front ? "Copy Fuselage Front Section" : "Copy Fuselage Rear Section");
		if (!fuselage.TryCopyConnectedSection(front))
		{
			Debug.LogWarning(front
				? "Failed to copy the connected front fuselage section."
				: "Failed to copy the connected rear fuselage section.", fuselage);
			return;
		}

		Craft craft = fuselage.GetComponentInParent<Craft>();
		if (craft != null)
		{
			EditorUtility.SetDirty(craft);
			craft.RebuildPreviewForPart(fuselage, lightweight: false);
		}

		EditorUtility.SetDirty(fuselage);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	// 将当前机身的前/后端吸附到已连接机身的对应端面。 / Snap the current fuselage front or rear end onto its connected neighbor.
	private static void SnapEnd(FuselagePart fuselage, bool front)
	{
		if (fuselage == null)
		{
			return;
		}

		Undo.RecordObject(fuselage.transform, front ? "Snap Fuselage Front" : "Snap Fuselage Rear");
		if (!fuselage.SnapEndToConnected(front))
		{
			Debug.LogWarning(front
				? "Failed to snap the fuselage front because no connected fuselage end was found."
				: "Failed to snap the fuselage rear because no connected fuselage end was found.", fuselage);
			return;
		}

		Craft craft = fuselage.GetComponentInParent<Craft>();
		if (craft != null)
		{
			EditorUtility.SetDirty(craft);
			craft.RebuildPreviewForPart(fuselage);
		}

		EditorUtility.SetDirty(fuselage);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	[MenuItem(AutoConnectSelectedMenuPath)]
	// 对同时选中的两段机身按最近的前后端自动创建连接。 / Auto-create a connection between the nearest front/rear ends of the two selected fuselages.
	private static void AutoConnectSelectedFuselages()
	{
		if (!TryGetSelectedFuselagePair(out FuselagePart first, out FuselagePart second, out Craft craft))
		{
			return;
		}

		float rearToFrontDistance = (first.GetAttachPointWorldPosition(0) - second.GetAttachPointWorldPosition(1)).sqrMagnitude;
		float frontToRearDistance = (first.GetAttachPointWorldPosition(1) - second.GetAttachPointWorldPosition(0)).sqrMagnitude;
		bool connectRearToFront = rearToFrontDistance <= frontToRearDistance;
		int firstAttachPointId = connectRearToFront ? 0 : 1;
		int secondAttachPointId = connectRearToFront ? 1 : 0;
		float bestDistance = connectRearToFront ? rearToFrontDistance : frontToRearDistance;

		if (bestDistance > AutoConnectMaxDistanceSqr)
		{
			Debug.LogWarning("Selected fuselages are not close enough to auto-connect.", craft);
			return;
		}

		ConnectFuselageEnds(craft, first, firstAttachPointId, second, secondAttachPointId);
	}

	[MenuItem(AutoConnectSelectedMenuPath, true)]
	private static bool ValidateAutoConnectSelectedFuselages()
	{
		return TryGetSelectedFuselagePair(out _, out _, out _);
	}

	[MenuItem(SnapSelectedRearMenuPath)]
	// 对当前选中的机身执行 Rear 端吸附。 / Snap the rear end of the currently selected fuselage.
	private static void SnapSelectedRear()
	{
		if (TryGetSingleSelectedFuselage(out FuselagePart fuselage))
		{
			SnapEnd(fuselage, front: false);
		}
	}

	[MenuItem(SnapSelectedRearMenuPath, true)]
	private static bool ValidateSnapSelectedRear()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	[MenuItem(SnapSelectedFrontMenuPath)]
	// 对当前选中的机身执行 Front 端吸附。 / Snap the front end of the currently selected fuselage.
	private static void SnapSelectedFront()
	{
		if (TryGetSingleSelectedFuselage(out FuselagePart fuselage))
		{
			SnapEnd(fuselage, front: true);
		}
	}

	[MenuItem(SnapSelectedFrontMenuPath, true)]
	private static bool ValidateSnapSelectedFront()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	[MenuItem(SliceSelectedByRatioMenuPath)]
	// 打开比例横切 Wizard，比例表示从 Rear 到 Front 的切点位置。 / Open the ratio slicing wizard; the ratio is measured from rear to front.
	private static void OpenSliceSelectedByRatioWizard()
	{
		SliceRatioWizard.Open();
	}

	[MenuItem(SliceSelectedByRatioMenuPath, true)]
	private static bool ValidateOpenSliceSelectedByRatioWizard()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	[MenuItem(ExtendSelectedFromRearMenuPath)]
	// 打开从 Rear 端延申的 Wizard。 / Open the wizard that extends from the rear end.
	private static void OpenExtendSelectedFromRearWizard()
	{
		ExtendFuselageWizard.Open(fromFront: false);
	}

	[MenuItem(ExtendSelectedFromRearMenuPath, true)]
	private static bool ValidateOpenExtendSelectedFromRearWizard()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	[MenuItem(ExtendSelectedFromFrontMenuPath)]
	// 打开从 Front 端延申的 Wizard。 / Open the wizard that extends from the front end.
	private static void OpenExtendSelectedFromFrontWizard()
	{
		ExtendFuselageWizard.Open(fromFront: true);
	}

	[MenuItem(ExtendSelectedFromFrontMenuPath, true)]
	private static bool ValidateOpenExtendSelectedFromFrontWizard()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	[MenuItem(BridgeSelectedFramesMenuPath)]
	// 在两个选中的框架机身之间创建一段桥接机身。 / Create one bridge fuselage between the selected frame fuselages.
	private static void BridgeSelectedFrames()
	{
		if (!TryGetSelectedFuselagePair(out FuselagePart first, out FuselagePart second, out Craft craft))
		{
			return;
		}

		if (!first.SupportsLinearCylinderTools() || !second.SupportsLinearCylinderTools())
		{
			Debug.LogWarning("Bridge only supports non-cone fuselage cylinders.", craft);
			return;
		}

		if (!TryResolveBridgeEnds(
			first,
			second,
			out FuselagePart rearFrame,
			out bool rearFrameFront,
			out FuselagePart frontFrame,
			out bool frontFrameFront,
			out Vector3 rearPosition,
			out Vector3 frontPosition,
			out Vector3 bridgeForward))
		{
			Debug.LogWarning("Selected fuselage frames are too close to create a bridge.", craft);
			return;
		}

		FuselagePart bridge = CreateFuselageClone(craft, rearFrame, "Bridge Fuselage");
		if (bridge == null)
		{
			return;
		}

		bridge.transform.SetParent(rearFrame.transform.parent, worldPositionStays: true);
		bridge.transform.position = (rearPosition + frontPosition) * 0.5f;
		bridge.transform.rotation = BuildBridgeRotation(rearFrame, bridgeForward);
		bridge.transform.localScale = rearFrame.transform.localScale;
		Vector3 bridgeOffset = bridge.transform.InverseTransformVector(frontPosition - rearPosition);
		bridge.ConfigureBridgeShape(
			bridgeOffset,
			rearFrame.GetEndSectionSettings(rearFrameFront),
			frontFrame.GetEndSectionSettings(frontFrameFront));

		if (Vector3.Dot(bridgeForward.normalized, -frontFrame.GetEndWorldNormal(frontFrameFront).normalized) < BridgeNormalWarningDot)
		{
			Debug.LogWarning("Bridge was created, but the selected frame normals are not closely opposite. Check the new fuselage end alignment.", bridge);
		}

		FinishFuselageTool(craft, bridge);
		Selection.activeGameObject = bridge.gameObject;
	}

	[MenuItem(BridgeSelectedFramesMenuPath, true)]
	private static bool ValidateBridgeSelectedFrames()
	{
		return TryGetSelectedFuselagePair(out _, out _, out _);
	}

	[MenuItem(GenerateSelectedEndFramesMenuPath)]
	// 从当前选中的圆筒两端生成短框架圆筒。 / Generate short frame fuselages at both ends of the selected cylinder.
	private static void GenerateSelectedEndFrames()
	{
		if (!TryGetSingleSelectedFuselage(out FuselagePart source))
		{
			return;
		}

		Craft craft = source.GetComponentInParent<Craft>();
		if (craft == null)
		{
			return;
		}

		if (!source.SupportsLinearCylinderTools())
		{
			Debug.LogWarning("End frame generation only supports non-cone fuselage cylinders.", source);
			return;
		}

		FuselagePart rearFrame = CreateEndFrame(craft, source, front: false);
		FuselagePart frontFrame = CreateEndFrame(craft, source, front: true);
		if (rearFrame == null || frontFrame == null)
		{
			return;
		}

		FinishFuselageTool(craft, rearFrame, frontFrame);
		Selection.objects = new Object[] { rearFrame.gameObject, frontFrame.gameObject };
	}

	[MenuItem(GenerateSelectedEndFramesMenuPath, true)]
	private static bool ValidateGenerateSelectedEndFrames()
	{
		return TryGetSingleSelectedFuselage(out _);
	}

	// 读取当前是否只选中了一个可编辑机身。 / Check whether the current selection contains exactly one editable fuselage.
	private static bool TryGetSingleSelectedFuselage(out FuselagePart fuselage)
	{
		FuselagePart[] selected = Selection.GetFiltered<FuselagePart>(SelectionMode.Editable | SelectionMode.ExcludePrefab | SelectionMode.TopLevel);
		fuselage = selected.Length == 1 ? selected[0] : null;
		return fuselage != null;
	}

	// 读取当前是否选中了同一 Craft 下的两个机身。 / Check whether the current selection contains two fuselages under the same craft.
	private static bool TryGetSelectedFuselagePair(out FuselagePart first, out FuselagePart second, out Craft craft)
	{
		FuselagePart[] selected = Selection.GetFiltered<FuselagePart>(SelectionMode.Editable | SelectionMode.ExcludePrefab | SelectionMode.TopLevel);
		first = selected.Length == 2 ? selected[0] : null;
		second = selected.Length == 2 ? selected[1] : null;
		craft = null;
		if (first == null || second == null)
		{
			return false;
		}

		craft = first.GetComponentInParent<Craft>();
		return craft != null && craft == second.GetComponentInParent<Craft>() && first != second;
	}

	// 按比例从当前选中的机身生成前后两段，原机身保持不变。 / Create rear and front slice spans from the selected fuselage while keeping the source unchanged.
	private static void SliceSelectedFuselageByRatio(float cutRatio)
	{
		if (!TryGetSingleSelectedFuselage(out FuselagePart source))
		{
			return;
		}

		Craft craft = source.GetComponentInParent<Craft>();
		if (craft == null)
		{
			return;
		}

		if (!source.SupportsLinearCylinderTools())
		{
			Debug.LogWarning("Ratio slicing only supports non-cone fuselage cylinders.", source);
			return;
		}

		float ratio = ClampSliceRatio(cutRatio);
		FuselagePart rearSegment = CreateFuselageClone(craft, source, "Slice Fuselage");
		if (rearSegment == null)
		{
			return;
		}

		FuselagePart frontSegment = CreateFuselageClone(craft, source, "Slice Fuselage");
		if (frontSegment == null)
		{
			Undo.DestroyObjectImmediate(rearSegment.gameObject);
			return;
		}

		rearSegment.transform.SetParent(source.transform.parent, worldPositionStays: false);
		frontSegment.transform.SetParent(source.transform.parent, worldPositionStays: false);
		Undo.RecordObjects(new Object[] { craft, rearSegment, rearSegment.transform, frontSegment, frontSegment.transform }, "Slice Fuselage");

		rearSegment.ConfigureAsSpanOf(source, 0f, ratio);
		frontSegment.ConfigureAsSpanOf(source, ratio, 1f);

		FinishFuselageTool(craft, rearSegment, rearSegment, frontSegment);
		Selection.objects = new Object[] { source.gameObject, rearSegment.gameObject, frontSegment.gameObject };
	}

	// 从当前选中的机身一端按比例延申出一段新机身。 / Extend the selected fuselage from one end by creating one new proportional segment.
	private static void ExtendSelectedFuselage(float extensionRatio, bool fromFront)
	{
		if (!TryGetSingleSelectedFuselage(out FuselagePart source))
		{
			return;
		}

		Craft craft = source.GetComponentInParent<Craft>();
		if (craft == null)
		{
			return;
		}

		if (!source.SupportsLinearCylinderTools())
		{
			Debug.LogWarning("Fuselage extension only supports non-cone fuselage cylinders.", source);
			return;
		}

		float ratio = ClampExtensionRatio(extensionRatio);
		FuselagePart extension = CreateFuselageClone(craft, source, fromFront ? "Extend Fuselage From Front" : "Extend Fuselage From Rear");
		if (extension == null)
		{
			return;
		}

		extension.transform.SetParent(source.transform.parent, worldPositionStays: false);
		Undo.RecordObjects(new Object[] { craft, source, source.transform, extension, extension.transform }, fromFront ? "Extend Fuselage From Front" : "Extend Fuselage From Rear");

		extension.ConfigureAsExtensionOf(source, fromFront, ratio);

		FinishFuselageTool(craft, extension, source, extension);
		Selection.objects = fromFront
			? new Object[] { source.gameObject, extension.gameObject }
			: new Object[] { extension.gameObject, source.gameObject };
	}

	// 创建一个基于模板 XML 的新机身并分配唯一 PartId。 / Create a new fuselage from template XML with a unique PartId.
	private static FuselagePart CreateFuselageClone(Craft craft, FuselagePart template, string undoName)
	{
		if (craft == null || template == null)
		{
			return null;
		}

		XElement cloneElement = template.ExportPartElement();
		cloneElement.SetAttributeValue("id", craft.AllocatePartId());
		Part createdPart = craft.CreatePartFromXml(cloneElement, craft.AllocateOrderIndex());
		if (createdPart is not FuselagePart fuselage)
		{
			if (createdPart != null)
			{
				Undo.DestroyObjectImmediate(createdPart.gameObject);
			}

			return null;
		}

		Undo.RegisterCreatedObjectUndo(fuselage.gameObject, undoName);
		return fuselage;
	}

	// 在源圆筒一个端面中心创建短框架，框架中心与源端面中心重合。 / Create a short frame at one source end with its center on the source end center.
	private static FuselagePart CreateEndFrame(Craft craft, FuselagePart source, bool front)
	{
		FuselagePart frame = CreateFuselageClone(craft, source, front ? "Generate Front Fuselage Frame" : "Generate Rear Fuselage Frame");
		if (frame == null)
		{
			return null;
		}

		frame.transform.SetParent(source.transform.parent, worldPositionStays: true);
		frame.transform.position = source.GetEndWorldPosition(front);
		frame.transform.rotation = source.transform.rotation;
		frame.transform.localScale = source.transform.localScale;
		FuselageSectionSettings section = source.GetEndSectionSettings(front);
		frame.ConfigureBridgeShape(Vector3.forward * GeneratedFrameLength, section, section);
		return frame;
	}

	// 从四种端点组合里选出最适合桥接的一对相向端面，桥接坐标锚定框架中心。 / Pick facing frame ends while anchoring bridge coordinates to frame centers.
	private static bool TryResolveBridgeEnds(
		FuselagePart first,
		FuselagePart second,
		out FuselagePart rearFrame,
		out bool rearFrameFront,
		out FuselagePart frontFrame,
		out bool frontFrameFront,
		out Vector3 rearPosition,
		out Vector3 frontPosition,
		out Vector3 bridgeForward)
	{
		rearFrame = null;
		rearFrameFront = false;
		frontFrame = null;
		frontFrameFront = false;
		rearPosition = Vector3.zero;
		frontPosition = Vector3.zero;
		bridgeForward = Vector3.forward;
		Vector3 firstCenter = first.GetCenterWorldPosition();
		Vector3 secondCenter = second.GetCenterWorldPosition();
		Vector3 delta = secondCenter - firstCenter;
		if (delta.sqrMagnitude <= BridgeMinimumDistance * BridgeMinimumDistance)
		{
			return false;
		}

		bool firstToSecond = TryEvaluateBridgeOrder(first, second, out bool firstFront, out bool secondFront, out Vector3 firstBridgeForward, out float firstScore);
		bool secondToFirst = TryEvaluateBridgeOrder(second, first, out bool secondRearFront, out bool firstFrontEnd, out Vector3 secondBridgeForward, out float secondScore);
		if (!firstToSecond && !secondToFirst)
		{
			return false;
		}

		if (!secondToFirst || (firstToSecond && firstScore >= secondScore))
		{
			rearFrame = first;
			rearFrameFront = firstFront;
			frontFrame = second;
			frontFrameFront = secondFront;
			rearPosition = firstCenter;
			frontPosition = secondCenter;
			bridgeForward = firstBridgeForward;
			return true;
		}

		rearFrame = second;
		rearFrameFront = secondRearFront;
		frontFrame = first;
		frontFrameFront = firstFrontEnd;
		rearPosition = secondCenter;
		frontPosition = firstCenter;
		bridgeForward = secondBridgeForward;
		return true;
	}

	// 评估一个“rear -> front”的桥接候选，优先选择端面相向且旋转接近源框架的结果。 / Score one rear-to-front bridge order, preferring facing ends and rotations close to the source frames.
	private static bool TryEvaluateBridgeOrder(
		FuselagePart rearCandidate,
		FuselagePart frontCandidate,
		out bool rearCandidateFront,
		out bool frontCandidateFront,
		out Vector3 bridgeForward,
		out float score)
	{
		rearCandidateFront = false;
		frontCandidateFront = false;
		bridgeForward = Vector3.forward;
		score = float.NegativeInfinity;
		Vector3 rearPosition = rearCandidate.GetCenterWorldPosition();
		Vector3 frontPosition = frontCandidate.GetCenterWorldPosition();
		Vector3 delta = frontPosition - rearPosition;
		if (delta.sqrMagnitude <= BridgeMinimumDistance * BridgeMinimumDistance)
		{
			return false;
		}

		Vector3 direction = delta.normalized;
		rearCandidateFront = SelectEndFacingDirection(rearCandidate, direction);
		frontCandidateFront = SelectEndFacingDirection(frontCandidate, -direction);
		Vector3 rearNormal = rearCandidate.GetEndWorldNormal(rearCandidateFront).normalized;
		Vector3 frontNormal = frontCandidate.GetEndWorldNormal(frontCandidateFront).normalized;
		bridgeForward = rearNormal.sqrMagnitude > 0.0001f ? rearNormal : direction;
		float facingScore = Vector3.Dot(rearNormal, direction)
			+ Vector3.Dot(frontNormal, -direction)
			+ Vector3.Dot(rearNormal, -frontNormal);
		Quaternion bridgeRotation = BuildBridgeRotation(rearCandidate, bridgeForward);
		float rotationPenalty = Quaternion.Angle(bridgeRotation, rearCandidate.transform.rotation)
			+ Quaternion.Angle(bridgeRotation, frontCandidate.transform.rotation);
		score = facingScore * 1000f - rotationPenalty;
		return true;
	}

	// 选择法线最接近目标方向的端面。 / Pick the end whose normal best follows the target direction.
	private static bool SelectEndFacingDirection(FuselagePart fuselage, Vector3 direction)
	{
		float rearScore = Vector3.Dot(fuselage.GetEndWorldNormal(front: false).normalized, direction);
		float frontScore = Vector3.Dot(fuselage.GetEndWorldNormal(front: true).normalized, direction);
		return frontScore >= rearScore;
	}

	// 使用第一块框架的 up 方向构建桥接段旋转。 / Build the bridge rotation using the first frame's up direction.
	private static Quaternion BuildBridgeRotation(FuselagePart first, Vector3 bridgeForward)
	{
		Vector3 forward = bridgeForward.sqrMagnitude <= 0.0001f ? first.transform.forward : bridgeForward.normalized;
		Vector3 up = first.transform.up;
		if (Vector3.Cross(forward, up).sqrMagnitude <= 0.0001f)
		{
			up = first.transform.right;
		}

		return Quaternion.LookRotation(forward, up);
	}

	// 标记相关对象并立即重建预览。 / Mark related objects dirty and rebuild the preview immediately.
	private static void FinishFuselageTool(Craft craft, Part previewRoot, params Part[] dirtyParts)
	{
		if (craft == null)
		{
			return;
		}

		EditorUtility.SetDirty(craft);
		if (previewRoot != null)
		{
			EditorUtility.SetDirty(previewRoot);
		}

		foreach (Part part in dirtyParts)
		{
			if (part != null)
			{
				EditorUtility.SetDirty(part);
			}
		}

		craft.RebuildAllPreviews(lightweight: false);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	private static float ClampSliceRatio(float ratio)
	{
		return Mathf.Clamp(ratio, MinimumSliceRatio, 1f - MinimumSliceRatio);
	}

	private static float ClampExtensionRatio(float ratio)
	{
		return float.IsFinite(ratio) ? Mathf.Max(MinimumExtensionRatio, ratio) : 1f;
	}

	private sealed class SliceRatioWizard : ScriptableWizard
	{
		public float CutRatio = 0.5f;

		public static void Open()
		{
			DisplayWizard<SliceRatioWizard>("Slice Fuselage By Ratio", "Slice");
		}

		private void OnWizardUpdate()
		{
			CutRatio = ClampSliceRatio(CutRatio);
			helpString = "CutRatio is measured from Rear to Front. 0.3 creates new 30% rear and 70% front segments while keeping the source fuselage.";
			errorString = TryGetSingleSelectedFuselage(out _) ? string.Empty : "Select exactly one FuselagePart.";
			isValid = string.IsNullOrEmpty(errorString);
		}

		private void OnWizardCreate()
		{
			SliceSelectedFuselageByRatio(CutRatio);
		}
	}

	private sealed class ExtendFuselageWizard : ScriptableWizard
	{
		private static bool OpenFromFront;

		public float Ratio = 1f;

		public static void Open(bool fromFront)
		{
			OpenFromFront = fromFront;
			DisplayWizard<ExtendFuselageWizard>(
				fromFront ? "Extend Fuselage From Front" : "Extend Fuselage From Rear",
				"Extend");
		}

		private void OnWizardUpdate()
		{
			Ratio = ClampExtensionRatio(Ratio);
			helpString = OpenFromFront
				? "Ratio extends a new segment past Front. 1 creates another segment with the same Run/Rise/Length and section deltas."
				: "Ratio extends a new segment before Rear. 1 creates another segment with the same Run/Rise/Length and section deltas.";
			errorString = TryGetSingleSelectedFuselage(out _) ? string.Empty : "Select exactly one FuselagePart.";
			isValid = string.IsNullOrEmpty(errorString);
		}

		private void OnWizardCreate()
		{
			ExtendSelectedFuselage(Ratio, OpenFromFront);
		}
	}

	// 追加一条 reciprocal 连接；同一端点位允许多连，但完全相同的端点对不重复创建。 / Add one reciprocal connection without clearing other links on the same attach points.
	private static void ConnectFuselageEnds(
		Craft craft,
		FuselagePart first,
		int firstAttachPointId,
		FuselagePart second,
		int secondAttachPointId,
		string undoName = "Auto Connect Fuselages",
		bool rebuildPreview = true)
	{
		if (craft == null || first == null || second == null)
		{
			return;
		}

		Undo.RecordObjects(new Object[] { craft, first, second }, undoName);

		bool existsOnFirst = first.HasConnectionEndpoint(firstAttachPointId, second.PartId, secondAttachPointId);
		bool existsOnSecond = second.HasConnectionEndpoint(secondAttachPointId, first.PartId, firstAttachPointId);
		if (existsOnFirst || existsOnSecond)
		{
			if (existsOnFirst)
			{
				craft.SynchronizeConnectionsFrom(first, removeStaleReciprocals: false);
			}
			else
			{
				craft.SynchronizeConnectionsFrom(second, removeStaleReciprocals: false);
			}

			EditorUtility.SetDirty(craft);
			EditorUtility.SetDirty(first);
			EditorUtility.SetDirty(second);
			if (rebuildPreview)
			{
				craft.RebuildPreviewForPart(first, lightweight: false);
				EditorApplication.QueuePlayerLoopUpdate();
				SceneView.RepaintAll();
			}
			return;
		}

		int connectionId = craft.AllocateConnectionId();
		first.AddConnectionEndpoint(connectionId, isPartAEndpoint: true, localAttachPointId: firstAttachPointId, connectedPartId: second.PartId, connectedAttachPointId: secondAttachPointId);
		craft.SynchronizeConnectionsFrom(first, removeStaleReciprocals: false);

		EditorUtility.SetDirty(craft);
		EditorUtility.SetDirty(first);
		EditorUtility.SetDirty(second);
		if (rebuildPreview)
		{
			craft.RebuildPreviewForPart(first, lightweight: false);
			EditorApplication.QueuePlayerLoopUpdate();
			SceneView.RepaintAll();
		}
	}

	// 以原游戏的 corner 编辑语义绘制一个截面。 / Draw one serialized fuselage section using the original game's corner editing semantics.
	private static void DrawSection(SerializedProperty section)
	{
		EditorGUI.indentLevel++;
       DrawSectionGroup(section, "BaseInfos", BaseInfoLabelColor, draw: () =>
		{
			DrawColoredPropertyField(section.FindPropertyRelative("Width"), BaseInfoLabelColor);
			DrawColoredPropertyField(section.FindPropertyRelative("Height"), BaseInfoLabelColor);
			DrawColoredPropertyField(section.FindPropertyRelative("Trapezium"), BaseInfoLabelColor);
			DrawColoredPropertyField(section.FindPropertyRelative("Thickness"), BaseInfoLabelColor);
			DrawColoredPropertyField(section.FindPropertyRelative("Smooth"), BaseInfoLabelColor);
			DrawUniformIntField(section.FindPropertyRelative("CornerSamples"), "cornerSamples", 2, BaseInfoLabelColor);
			DrawUniformIntField(section.FindPropertyRelative("EdgeSamples"), "edgeSamples", 1, BaseInfoLabelColor);
		});
		DrawSectionGroup(section, "Corners", CornerLabelColor, draw: () =>
		{
			DrawCornerStyleGroup(section, CornerLabelColor);
		});
		DrawSectionGroup(section, "Edges", EdgeLabelColor, draw: () =>
		{
			DrawFloat4Group(section.FindPropertyRelative("EdgeCurvature"), "Edge Curvature", EdgeNames, EdgeLabelColor);
		});
		DrawSectionGroup(section, "Slices", SliceLabelColor, draw: () =>
		{
			DrawCuttingGroup(section, SliceLabelColor);
		});
		EditorGUI.indentLevel--;
	}

	// 把截面 Inspector 分成可折叠的小组，减少一次性绘制控件数量。 / Split section inspector UI into foldout groups to reduce the amount of controls drawn at once.
	private static void DrawSectionGroup(SerializedProperty section, string groupName, Color labelColor, System.Action draw)
	{
		string key = section.propertyPath + "." + groupName;
		bool expanded = GetSectionFoldout(key, defaultValue: groupName == "BaseInfos");
        expanded = EditorGUILayout.Foldout(expanded, groupName, true, GetColoredStyle(EditorStyles.foldout, labelColor));
		SetSectionFoldout(key, expanded);
		if (expanded)
		{
         EditorGUI.indentLevel++;
			draw();
           EditorGUI.indentLevel--;
		}
	}

 // 按当前编辑器语义绘制每边切割：滑块大于 0 即自动启用，回到 0 则关闭。 / Draw per-side slice controls so values above zero enable cutting and zero disables it.
	private static void DrawCuttingGroup(SerializedProperty section, Color labelColor)
	{
		SerializedProperty cutEnabled = section.FindPropertyRelative("CutEnabled");
		FuselageSectionSettings previewSection = CreatePreviewSection(section);
		previewSection.GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting);

		DrawColoredHeader("Slice Cutting", labelColor);
		EditorGUI.indentLevel++;
		for (int i = 0; i < CutNames.Length; i++)
		{
			DrawCutField(
				GetValueComponent(cutEnabled, i),
				section.FindPropertyRelative(CutFieldNames[i]),
				CutNames[i],
				minCutting[i],
				maxCutting[i],
				labelColor);
		}
		EditorGUI.indentLevel--;
	}

	// 把单边切割画成条件扩展范围的滑块；0 仍表示不切，只有真实轮廓超出名义外框时才开放 <0 或 >1 的输入。 / Draw one cut side with a conditionally extended range; zero still means uncut, while <0 or >1 become available only when the live outline requires it.
	private static void DrawCutField(SerializedProperty enabledProperty, SerializedProperty valueProperty, string label, float minCutting, float maxCutting, Color labelColor)
	{
		float sliderMin = Mathf.Min(0f, minCutting);
		float sliderMax = Mathf.Max(1f, maxCutting);
		float currentValue = enabledProperty.boolValue ? Mathf.Clamp(valueProperty.floatValue, sliderMin, sliderMax) : 0f;
		Rect position = EditorGUILayout.GetControlRect();
		Rect fieldRect = EditorGUI.PrefixLabel(
			position,
			GUIUtility.GetControlID(FocusType.Passive, position),
			new GUIContent(label),
			GetColoredStyle(EditorStyles.label, labelColor));
		const float floatFieldWidth = 64f;
		const float spacing = 4f;
		Rect sliderRect = new Rect(fieldRect.x, fieldRect.y, Mathf.Max(0f, fieldRect.width - floatFieldWidth - spacing), fieldRect.height);
		Rect floatRect = new Rect(sliderRect.xMax + spacing, fieldRect.y, floatFieldWidth, fieldRect.height);
		float editedValue = EditorGUI.Slider(sliderRect, currentValue, sliderMin, sliderMax);
		editedValue = EditorGUI.FloatField(floatRect, editedValue);

		float clampedValue = Mathf.Clamp(editedValue, sliderMin, sliderMax);
		bool enabled = Mathf.Abs(clampedValue) > 0.0001f;
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
	private static void DrawCornerStyleGroup(SerializedProperty section, Color labelColor)
	{
		SerializedProperty cornerRadii = section.FindPropertyRelative("CornerRadii");
		SerializedProperty cornerStretch = section.FindPropertyRelative("CornerStretch");
		FuselageSectionSettings previewSection = CreatePreviewSection(section);
		Float4Value maxRoundedRadii = previewSection.GetMaxCornerRadii(stretched: false);
		Float4Value maxStretchedRadii = previewSection.GetMaxCornerRadii(stretched: true);

		DrawColoredHeader("Corner Styles", labelColor);
		EditorGUI.indentLevel++;
		for (int i = 0; i < CornerNames.Length; i++)
		{
			DrawCornerStyleField(cornerRadii, cornerStretch, i, CornerNames[i], maxRoundedRadii[i], maxStretchedRadii[i], labelColor);
		}
		EditorGUI.indentLevel--;
	}

	// 绘制单个 corner 的编辑行，并根据模式切换米和百分比输入。 / Draw a single corner row that switches between meter and percent editing based on mode.
	private static void DrawCornerStyleField(SerializedProperty cornerRadii, SerializedProperty cornerStretch, int index, string label, float maxRoundedRadius, float maxStretchedRadius, Color labelColor)
	{
		SerializedProperty radiusProperty = GetValueComponent(cornerRadii, index);
		SerializedProperty stretchProperty = GetValueComponent(cornerStretch, index);
		CornerMode mode = stretchProperty.boolValue ? CornerMode.Stretched : CornerMode.Rounded;
		float activeMax = Mathf.Max(0f, mode == CornerMode.Stretched ? maxStretchedRadius : maxRoundedRadius);
		float clampedRadius = Mathf.Clamp(radiusProperty.floatValue, 0f, activeMax);

		Rect position = EditorGUILayout.GetControlRect();
		Rect fieldRect = EditorGUI.PrefixLabel(
			position,
			GUIUtility.GetControlID(FocusType.Passive, position),
			new GUIContent(label),
			GetColoredStyle(EditorStyles.label, labelColor));
		const float spacing = 4f;
		float modeWidth = Mathf.Min(96f, fieldRect.width * 0.45f);
		const float unitWidth = 18f;
		Rect modeRect = new Rect(fieldRect.x, fieldRect.y, modeWidth, fieldRect.height);
		Rect unitRect = new Rect(fieldRect.xMax - unitWidth, fieldRect.y, unitWidth, fieldRect.height);
		Rect valueRect = new Rect(modeRect.xMax + spacing, fieldRect.y, Mathf.Max(40f, unitRect.x - modeRect.xMax - spacing), fieldRect.height);

		// 在 Rounded 和 Stretched 之间切换时保持相同的归一化位置。 / Preserve the same normalized position when switching between rounded and stretched modes.
		CornerMode newMode = (CornerMode)EditorGUI.EnumPopup(modeRect, mode);
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

		float displayValue = QuantizeCornerDisplayValue(mode == CornerMode.Stretched ? clampedRadius * 100f : clampedRadius, maxDisplayValue: mode == CornerMode.Stretched ? activeMax * 100f : activeMax);
		float maxDisplayValue = mode == CornerMode.Stretched ? activeMax * 100f : activeMax;
		float newDisplayValue = EditorGUI.FloatField(valueRect, displayValue);
		GUI.Label(unitRect, mode == CornerMode.Stretched ? "%" : "m", GetColoredStyle(EditorStyles.label, labelColor));

		// 把百分比输入换算回 stretched corner 内部保存的归一化半径。 / Convert percent input back into the stored normalized radius used by stretched corners.
		if (!Mathf.Approximately(newDisplayValue, displayValue))
		{
			float clampedDisplayValue = QuantizeCornerDisplayValue(newDisplayValue, maxDisplayValue);
			float nextRadius = mode == CornerMode.Stretched ? clampedDisplayValue * 0.01f : clampedDisplayValue;
			radiusProperty.floatValue = Mathf.Clamp(nextRadius, 0f, activeMax);
		}
	}

	// 将 corner 输入限制到 0.001 显示粒度，并允许接近上限时吸附到该粒度。 / Quantize corner editor values to 0.001 display units while snapping near-limit values onto that grid.
	private static float QuantizeCornerDisplayValue(float value, float maxDisplayValue)
	{
		float clampedValue = Mathf.Clamp(value, 0f, maxDisplayValue);
		float snappedValue = Mathf.Round(clampedValue * CornerDisplayStepInverse) / CornerDisplayStepInverse;
		float snappedMax = Mathf.Round(Mathf.Max(0f, maxDisplayValue) * CornerDisplayStepInverse) / CornerDisplayStepInverse;
		return Mathf.Clamp(snappedValue, 0f, snappedMax);
	}

	// 用单个整数字段统一设置四个 corner 或四条 edge 的采样数；若当前四个值不一致，则用 mixed-value 显示。 / Use one integer field to set all four corner or edge sample counts, showing a mixed value when the stored components differ.
	private static void DrawUniformIntField(SerializedProperty property, string label, int minimumValue, Color labelColor)
	{
		SerializedProperty x = property.FindPropertyRelative("X");
		SerializedProperty y = property.FindPropertyRelative("Y");
		SerializedProperty z = property.FindPropertyRelative("Z");
		SerializedProperty w = property.FindPropertyRelative("W");
		int currentValue = x.intValue;
		bool mixed = currentValue != y.intValue || currentValue != z.intValue || currentValue != w.intValue;
		bool previousShowMixedValue = EditorGUI.showMixedValue;
		EditorGUI.showMixedValue = mixed;
		EditorGUI.BeginChangeCheck();
		int editedValue = DrawColoredIntField(label, currentValue, labelColor);
		if (EditorGUI.EndChangeCheck())
		{
			int clampedValue = Mathf.Max(minimumValue, editedValue);
			x.intValue = clampedValue;
			y.intValue = clampedValue;
			z.intValue = clampedValue;
			w.intValue = clampedValue;
		}

		EditorGUI.showMixedValue = previousShowMixedValue;
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
	private static void DrawFloat4Group(SerializedProperty property, string label, string[] itemNames, Color labelColor)
	{
		DrawValueGroup(property, label, itemNames, labelColor, (itemProperty, itemLabel) =>
		{
			DrawColoredPropertyField(itemProperty, labelColor, itemLabel);
		});
	}

	// 用逐分量行的方式绘制 Int4Value。 / Draw a labeled Int4Value group using one line per component.
	private static void DrawInt4Group(SerializedProperty property, string label, string[] itemNames)
	{
		DrawValueGroup(property, label, itemNames, EditorStyles.label.normal.textColor, (itemProperty, itemLabel) =>
		{
			EditorGUILayout.PropertyField(itemProperty, new GUIContent(itemLabel));
		});
	}

	// 复用四元辅助结构的通用绘制模式。 / Share the repeated four-component drawing pattern across the helper value structs.
	private static void DrawValueGroup(SerializedProperty property, string label, string[] itemNames, Color labelColor, System.Action<SerializedProperty, string> drawValue)
	{
		DrawColoredHeader(label, labelColor);
		EditorGUI.indentLevel++;
		drawValue(property.FindPropertyRelative("X"), itemNames[0]);
		drawValue(property.FindPropertyRelative("Y"), itemNames[1]);
		drawValue(property.FindPropertyRelative("Z"), itemNames[2]);
		drawValue(property.FindPropertyRelative("W"), itemNames[3]);
		EditorGUI.indentLevel--;
	}

	// 绘制带颜色的字段标签，只染左侧说明文本。 / Draw a property field with only its left label tinted.
	private static void DrawColoredPropertyField(SerializedProperty property, Color labelColor, string label = null)
	{
		Rect position = EditorGUILayout.GetControlRect(true, EditorGUI.GetPropertyHeight(property, GUIContent.none, includeChildren: false));
		Rect fieldRect = EditorGUI.PrefixLabel(
			position,
			GUIUtility.GetControlID(FocusType.Passive, position),
			new GUIContent(label ?? property.displayName),
			GetColoredStyle(EditorStyles.label, labelColor));
		EditorGUI.PropertyField(fieldRect, property, GUIContent.none);
	}

	// 绘制带颜色左侧标签的整数输入。 / Draw an integer field with a tinted left label.
	private static int DrawColoredIntField(string label, int value, Color labelColor)
	{
		Rect position = EditorGUILayout.GetControlRect();
		Rect fieldRect = EditorGUI.PrefixLabel(
			position,
			GUIUtility.GetControlID(FocusType.Passive, position),
			new GUIContent(label),
			GetColoredStyle(EditorStyles.label, labelColor));
		return EditorGUI.IntField(fieldRect, value);
	}

	// 绘制带颜色的组内标题。 / Draw a tinted title inside one section group.
	private static void DrawColoredHeader(string label, Color labelColor)
	{
		EditorGUILayout.LabelField(label, GetColoredStyle(EditorStyles.boldLabel, labelColor));
	}

	// 从 Unity 内置样式派生一个只改文字颜色的样式。 / Create a text-color-only variant from a built-in Unity editor style.
	private static GUIStyle GetColoredStyle(GUIStyle source, Color labelColor)
	{
		string key = $"{source.name}:{labelColor.r:0.###},{labelColor.g:0.###},{labelColor.b:0.###},{labelColor.a:0.###}";
		if (ColoredStyleCache.TryGetValue(key, out GUIStyle cachedStyle))
		{
			return cachedStyle;
		}

		GUIStyle style = new GUIStyle(source);
		style.normal.textColor = labelColor;
		style.hover.textColor = labelColor;
		style.focused.textColor = labelColor;
		style.active.textColor = labelColor;
		style.onNormal.textColor = labelColor;
		style.onHover.textColor = labelColor;
		style.onFocused.textColor = labelColor;
		style.onActive.textColor = labelColor;
		ColoredStyleCache[key] = style;
		return style;
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

		PartInspectorUtility.DrawCarverRefreshButton(selectedPart);
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
	private const string CloneSelectedMenuPath = "Tools/SP2 Craft Editor/Part/Clone Selected #r";

	private const string SymmetricCopySelectedMenuPath = "Tools/SP2 Craft Editor/Part/Symmetric Copy Selected #s";

	private const string PinSelectedMenuPath = "Tools/SP2 Craft Editor/Part/Pin Selected";

	private const string UnpinSelectedMenuPath = "Tools/SP2 Craft Editor/Part/Unpin Selected";

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

		DrawPinToggle(part);
	}

	private static void DrawPinToggle(Part part)
	{
		Craft craft = part.GetComponentInParent<Craft>();
		if (craft == null)
		{
			return;
		}

		bool pinned = craft.IsPartPinned(part);
		EditorGUI.BeginChangeCheck();
		bool nextPinned = EditorGUILayout.Toggle("Pinned", pinned);
		if (!EditorGUI.EndChangeCheck() || nextPinned == pinned)
		{
			return;
		}

		RegisterPartUndo(part, craft, nextPinned ? "Pin Part" : "Unpin Part");
		if (nextPinned)
		{
			craft.PinPart(part);
		}
		else
		{
			craft.UnpinPart(part);
		}

		EditorUtility.SetDirty(craft);
		EditorUtility.SetDirty(part);
		EditorApplication.RepaintHierarchyWindow();
		SceneView.RepaintAll();
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
		if (SupportsSymmetricCopy(part) && GUILayout.Button("Symmetric Copy", GUILayout.Height(24f)))
		{
			SymmetricCopyPart(craft, part);
		}
		EditorGUILayout.EndHorizontal();
	}

	[MenuItem(CloneSelectedMenuPath)]
	private static void CloneSelectedPart()
	{
		if (!TryGetSelectedParts(out Part[] parts, requireSymmetricSupport: false))
		{
			return;
		}

		CloneParts(parts, mirrored: false);
	}

	[MenuItem(CloneSelectedMenuPath, true)]
	private static bool ValidateCloneSelectedPart()
	{
		return TryGetSelectedParts(out _, requireSymmetricSupport: false);
	}

	[MenuItem(SymmetricCopySelectedMenuPath)]
	private static void SymmetricCopySelectedPart()
	{
		if (!TryGetSelectedParts(out Part[] parts, requireSymmetricSupport: true))
		{
			return;
		}

		CloneParts(parts, mirrored: true);
	}

	[MenuItem(SymmetricCopySelectedMenuPath, true)]
	private static bool ValidateSymmetricCopySelectedPart()
	{
		return TryGetSelectedParts(out _, requireSymmetricSupport: true);
	}

	[MenuItem(PinSelectedMenuPath)]
	private static void PinSelectedParts()
	{
		if (!TryGetSelectedPartsIncludingChildren(out Part[] parts))
		{
			return;
		}

		SetPartsPinned(parts, pinned: true);
	}

	[MenuItem(PinSelectedMenuPath, true)]
	private static bool ValidatePinSelectedParts()
	{
		return TryGetSelectedPartsIncludingChildren(out _);
	}

	[MenuItem(UnpinSelectedMenuPath)]
	private static void UnpinSelectedParts()
	{
		if (!TryGetSelectedPartsIncludingChildren(out Part[] parts))
		{
			return;
		}

		SetPartsPinned(parts, pinned: false);
	}

	[MenuItem(UnpinSelectedMenuPath, true)]
	private static bool ValidateUnpinSelectedParts()
	{
		return TryGetSelectedPartsIncludingChildren(out _);
	}

	public static void DrawCarverRefreshButton(Part part)
	{
		if (!(part is WindowPart) && !(part is BayPart))
		{
			return;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		if (craft == null)
		{
			return;
		}

		EditorGUILayout.Space(8f);
		if (GUILayout.Button("Refresh Affected Fuselage Cuts", GUILayout.Height(24f)))
		{
			craft.RebuildPreviewForPart(part, lightweight: false);
			EditorUtility.SetDirty(craft);
			SceneView.RepaintAll();
		}
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

	private static void SetPartsPinned(IReadOnlyList<Part> parts, bool pinned)
	{
		if (parts == null || parts.Count == 0)
		{
			return;
		}

		string actionName = pinned ? "Pin Parts" : "Unpin Parts";
		int undoGroup = Undo.GetCurrentGroup();
		Undo.SetCurrentGroupName(actionName);
		HashSet<Craft> changedCrafts = new HashSet<Craft>();
		for (int i = 0; i < parts.Count; i++)
		{
			Part part = parts[i];
			Craft craft = part != null ? part.GetComponentInParent<Craft>() : null;
			if (craft == null)
			{
				continue;
			}

			RegisterPartUndo(part, craft, actionName);
			if (pinned)
			{
				craft.PinPart(part);
			}
			else
			{
				craft.UnpinPart(part);
			}

			EditorUtility.SetDirty(part);
			EditorUtility.SetDirty(craft);
			changedCrafts.Add(craft);
		}

		foreach (Craft craft in changedCrafts)
		{
			EditorUtility.SetDirty(craft.gameObject);
		}

		Undo.CollapseUndoOperations(undoGroup);
		EditorApplication.RepaintHierarchyWindow();
		SceneView.RepaintAll();
	}

	private static void ClonePart(Craft craft, Part source)
	{
		CloneParts(new[] { source }, mirrored: false);
	}

	private static void SymmetricCopyPart(Craft craft, Part source)
	{
		CloneParts(new[] { source }, mirrored: true);
	}

	private static void CloneParts(IReadOnlyList<Part> sources, bool mirrored)
	{
		if (sources == null || sources.Count == 0)
		{
			return;
		}

		string actionName = mirrored ? "Symmetric Copy Parts" : "Clone Parts";
		int undoGroup = Undo.GetCurrentGroup();
		Undo.SetCurrentGroupName(actionName);

		List<Object> selectedClones = new List<Object>();
		HashSet<Craft> changedCrafts = new HashSet<Craft>();
		for (int i = 0; i < sources.Count; i++)
		{
			Part source = sources[i];
			Craft craft = source != null ? source.GetComponentInParent<Craft>() : null;
			if (craft == null || (mirrored && !SupportsSymmetricCopy(source)))
			{
				continue;
			}

			Part clone = mirrored
				? craft.ClonePartMirrored(source, rebuildPreview: false)
				: craft.ClonePart(source, rebuildPreview: false);
			if (clone == null)
			{
				continue;
			}

			Undo.RegisterCreatedObjectUndo(clone.gameObject, actionName);
			EditorUtility.SetDirty(craft);
			changedCrafts.Add(craft);
			selectedClones.Add(clone.gameObject);
		}

		foreach (Craft craft in changedCrafts)
		{
			craft.RebuildAllPreviews();
		}

		if (selectedClones.Count > 0)
		{
			Selection.objects = selectedClones.ToArray();
		}

		Undo.CollapseUndoOperations(undoGroup);
		SceneView.RepaintAll();
	}

	private static bool TryGetSelectedParts(out Part[] parts, bool requireSymmetricSupport)
	{
		parts = Selection.GetFiltered<Part>(SelectionMode.Editable | SelectionMode.ExcludePrefab | SelectionMode.TopLevel)
			.Where(part => part != null
				&& part.GetComponentInParent<Craft>() != null
				&& (!requireSymmetricSupport || SupportsSymmetricCopy(part)))
			.Distinct()
			.ToArray();
		return parts.Length > 0;
	}

	private static bool TryGetSelectedPartsIncludingChildren(out Part[] parts)
	{
		parts = Selection.GetTransforms(SelectionMode.Editable | SelectionMode.ExcludePrefab | SelectionMode.TopLevel)
			.Where(transform => transform != null)
			.SelectMany(transform => transform.GetComponentsInChildren<Part>(includeInactive: true))
			.Where(part => part != null && part.GetComponentInParent<Craft>() != null)
			.Distinct()
			.ToArray();
		return parts.Length > 0;
	}

	private static bool TryGetSelectedPart(out Part part, out Craft craft)
	{
		if (!TryGetSelectedParts(out Part[] parts, requireSymmetricSupport: false) || parts.Length != 1)
		{
			part = null;
			craft = null;
			return false;
		}

		part = parts[0];
		craft = part != null ? part.GetComponentInParent<Craft>() : null;
		return part != null && craft != null;
	}

	private static bool SupportsSymmetricCopy(Part part)
	{
		return part is FuselagePart || part is IFuselageCarver;
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
			int localAttachPointId = EditorGUILayout.DelayedIntField("Local Attach Point", endpoint.LocalAttachPointId);
			int connectedPartId = EditorGUILayout.DelayedIntField("Connected Part Id", endpoint.ConnectedPartId);
			int connectedAttachPointId = EditorGUILayout.DelayedIntField("Connected Attach Point", endpoint.ConnectedAttachPointId);
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
