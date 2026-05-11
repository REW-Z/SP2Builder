using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif


public enum FuselageSerializationMode
{
	JFuselage,
	LegacyFuselage
}

public enum FuselageVisualStyle
{
	Body,
	Hollow
}

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class FuselagePart : Part
{
	public const double EditorPreviewRefreshDelaySeconds = 0.06d;

	private const int MaxSpatialNeighbourSearchFuselages = 96;

	private const int MaxSpatialSmoothingFuselages = 96;

	private static readonly float[] LegacyCornerRadiusFromStyle = { 0f, 0.25f, 0.5f, 1f };

	private static readonly bool[] LegacyCornerStretchFromStyle = { false, false, false, true };

	[SerializeField]
	private FuselageSerializationMode _serializationMode = FuselageSerializationMode.JFuselage;

	[SerializeField, TextArea(4, 12)]
	private string _rawStateXml;

	[SerializeField]
	private FuselageVisualStyle _visualStyle = FuselageVisualStyle.Body;

	[SerializeField]
	private bool _glass;

	[SerializeField]
	private Vector3 _offset = new Vector3(0f, 0f, 1f);

	[SerializeField]
	private FuselageSectionSettings _rearSection = DefaultSection();

	[SerializeField]
	private FuselageSectionSettings _frontSection = DefaultSection();

	private MeshFilter _meshFilter;

	private MeshRenderer _meshRenderer;

	#if UNITY_EDITOR
	protected override double PreviewRefreshDelaySeconds => EditorPreviewRefreshDelaySeconds;
	#endif

	// 读取机身专用 XML 状态，同时兼容 JFuselage 与 Legacy 两种格式。 / Load fuselage-specific XML state, supporting both JFuselage and legacy formats.
	protected override void LoadPartState(XElement partElement)
	{
		if (partElement.Element("JFuselage.State") is XElement jState)
		{
			_serializationMode = FuselageSerializationMode.JFuselage;
			LoadJFuselageState(jState);
			return;
		}

		if (partElement.Element("Fuselage.State") is XElement legacyState)
		{
			_serializationMode = FuselageSerializationMode.LegacyFuselage;
			LoadLegacyState(partElement, legacyState);
		}
	}

	// 按当前选择的序列化格式写回机身状态。 / Write the current fuselage state back using the selected serialization format.
	protected override void WritePartState(XElement partElement)
	{
		CanonicalizeSections();
		if (_serializationMode == FuselageSerializationMode.JFuselage)
		{
			WriteJFuselageState(partElement);
		}
		else
		{
			WriteLegacyState(partElement);
		}
	}

	// 重建机身预览网格，包括端盖、切割结果和共享材质。 / Rebuild the fuselage preview mesh, including end caps, cuts, and shared material.
	public override void RefreshPreview()
	{
		if (RequestCraftPreviewRebuild())
		{
			return;
		}

		base.RefreshPreview();
		CanonicalizeSections();
		EnsureComponents();
		bool hasTargetedCarvers = HasTargetedCarvers();
		bool hasSectionCutting = HasSectionCutting(_rearSection) || HasSectionCutting(_frontSection);
		bool capRear = hasTargetedCarvers || !TryFindMatchingNeighbour(front: false, out _, out _);
		bool capFront = hasTargetedCarvers || !TryFindMatchingNeighbour(front: true, out _, out _);
		bool applyTargetedCarvers = hasTargetedCarvers;
		bool applySectionCutting = hasSectionCutting;
		if (!applyTargetedCarvers && TryUpdateExistingLoftMesh(capRear, capFront, applySectionCutting))
		{
			_meshRenderer.sharedMaterial = PreviewMaterialFactory.GetFuselageMaterial(this, _glass);
			return;
		}

		Mesh previousMesh = _meshFilter.sharedMesh;
		Mesh mesh = null;
		try
		{
			// 先生成 loft，再做后续切削，避免法线在切削前后不一致。 / Build the loft before applying downstream carvers so normals stay consistent.
			mesh = FuselageGeometry.BuildLoft(_rearSection, _frontSection, _offset, _visualStyle == FuselageVisualStyle.Hollow, capRear, capFront, applySectionCutting);
			if (applyTargetedCarvers)
			{
				mesh = ApplyTargetedCarvers(mesh);
			}
		}
		catch (Exception exception)
		{
			Debug.LogException(exception, this);
			DestroyOwnedObject(mesh);
			mesh = null;
		}

		if (mesh == null || mesh.vertexCount == 0)
		{
			DestroyOwnedObject(mesh);
			if (previousMesh == null || previousMesh.vertexCount == 0)
			{
				mesh = FuselageGeometry.BuildLoft(_rearSection, _frontSection, _offset, _visualStyle == FuselageVisualStyle.Hollow, capRear, capFront, applySectionCutting: false);
			}
			else
			{
				_meshRenderer.sharedMaterial = PreviewMaterialFactory.GetFuselageMaterial(this, _glass);
				return;
			}
		}

		_meshFilter.sharedMesh = mesh;
		if (previousMesh != null && !ReferenceEquals(previousMesh, mesh))
		{
			DestroyOwnedObject(previousMesh);
		}
		_meshRenderer.sharedMaterial = PreviewMaterialFactory.GetFuselageMaterial(this, _glass);
	}

	private bool TryUpdateExistingLoftMesh(bool capRear, bool capFront, bool applySectionCutting)
	{
		if (applySectionCutting && (HasSectionCutting(_rearSection) || HasSectionCutting(_frontSection)))
		{
			return false;
		}

		Mesh mesh = _meshFilter.sharedMesh;
		if (mesh == null || mesh.vertexCount == 0)
		{
			return false;
		}

		PreviewMeshData meshData = FuselageGeometry.BuildLoftData(_rearSection, _frontSection, _offset, _visualStyle == FuselageVisualStyle.Hollow, capRear, capFront);
		if (meshData.Vertices.Count != mesh.vertexCount || meshData.Normals.Count != mesh.vertexCount || meshData.SubMeshTriangles.Count != mesh.subMeshCount)
		{
			return false;
		}

		for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
		{
			int[] existingTriangles = mesh.GetTriangles(subMesh);
			List<int> nextTriangles = meshData.SubMeshTriangles[subMesh];
			if (existingTriangles.Length != nextTriangles.Count)
			{
				return false;
			}

			for (int i = 0; i < existingTriangles.Length; i++)
			{
				if (existingTriangles[i] != nextTriangles[i])
				{
					return false;
				}
			}
		}

		mesh.SetVertices(meshData.Vertices);
		mesh.SetNormals(meshData.Normals);
		mesh.RecalculateBounds();
		return true;
	}

	private static bool HasSectionCutting(FuselageSectionSettings section)
	{
		return section.GetCutEnabled(0)
			|| section.GetCutEnabled(1)
			|| section.GetCutEnabled(2)
			|| section.GetCutEnabled(3);
	}

	private bool HasTargetedCarvers()
	{
		Craft craft = GetOwningCraft();
		foreach (Part part in craft.GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (part == this || part is not IFuselageCarver)
			{
				continue;
			}

			if (part.HasExplicitTargets && part.ExplicitlyTargetsPart(PartId))
			{
				return true;
			}
		}

		return false;
	}

	// 在编辑器首次排队重建前，先补齐未序列化的截面派生状态。 / Normalize nonserialized section state before the editor schedules an initial rebuild.
	protected override void OnEnable()
	{
		CanonicalizeSections();
		base.OnEnable();
	}

	// 在编辑器校验排队重建前，先归一化截面状态。 / Normalize section state before editor validation queues a rebuild.
	protected override void OnValidate()
	{
		CanonicalizeSections();
		base.OnValidate();
	}

	public bool SnapEndToConnected(bool front)
	{
		if (!TryFindConnectedNeighbour(front, out FuselagePart neighbour, out bool neighbourFront))
		{
			return false;
		}

		Vector3 targetNormal = neighbour.GetSliceWorldNormal(neighbourFront);
		Vector3 currentNormal = GetSliceWorldNormal(front);
		if (targetNormal.sqrMagnitude > 0.0001f && currentNormal.sqrMagnitude > 0.0001f)
		{
			Quaternion deltaRotation = Quaternion.FromToRotation(currentNormal, -targetNormal);
			transform.rotation = deltaRotation * transform.rotation;
		}

		Vector3 targetPosition = neighbour.GetSliceWorldPosition(neighbourFront);
		Vector3 currentPosition = GetSliceWorldPosition(front);
		transform.position += targetPosition - currentPosition;
		return true;
	}

	public override Vector3 GetAttachPointLocalPosition(int attachPointId)
	{
		return attachPointId switch
		{
			0 => GetSliceLocalPosition(front: false),
			1 => GetSliceLocalPosition(front: true),
			2 => GetMidSectionSidePoint(0),
			3 => GetMidSectionSidePoint(1),
			4 => GetMidSectionSidePoint(2),
			5 => GetMidSectionSidePoint(3),
			_ => Vector3.zero
		};
	}

	// 对整机中可兼容的机身接缝端面执行法线平滑。 / Smooth seam normals between compatible neighboring fuselage ends across the craft.
	public static void ApplyNeighbourSmoothing(Craft craft)
	{
		ApplyNeighbourSmoothing(craft, null);
	}

	public static void ApplyNeighbourSmoothing(Craft craft, IReadOnlyCollection<int> affectedPartIds)
	{
		FuselagePart[] fuselages = craft.GetComponentsInChildren<FuselagePart>(includeInactive: true);
		Dictionary<FuselagePart, Vector3[]> baseNormals = new Dictionary<FuselagePart, Vector3[]>(fuselages.Length);
		Dictionary<FuselagePart, Vector3[]> workingNormals = new Dictionary<FuselagePart, Vector3[]>(fuselages.Length);
		// 先缓存每个机身的原始法线，便于单向平滑时读取未污染的源数据。 / Capture each fuselage's original normals so one-sided smoothing can sample the untouched source.
		for (int i = 0; i < fuselages.Length; i++)
		{
			FuselagePart fuselage = fuselages[i];
			if (fuselage._meshFilter == null || fuselage._meshFilter.sharedMesh == null)
			{
				continue;
			}

			Vector3[] normals = fuselage._meshFilter.sharedMesh.normals;
			if (normals == null || normals.Length == 0)
			{
				continue;
			}

			baseNormals[fuselage] = (Vector3[])normals.Clone();
			workingNormals[fuselage] = (Vector3[])normals.Clone();
		}

		if (craft.HasConnectionData)
		{
			SmoothConnectedEnds(fuselages, baseNormals, workingNormals, affectedPartIds);
		}
		else if (fuselages.Length <= MaxSpatialSmoothingFuselages)
		{
			SmoothSpatiallyMatchedEnds(fuselages, baseNormals, workingNormals, affectedPartIds);
		}

		// 所有接缝配对结束后再写回法线，避免同一批数据重复写入 Mesh。 / Push normals back once after all seam pairs so the same mesh is not rewritten repeatedly.
		foreach ((FuselagePart fuselage, Vector3[] normals) in workingNormals)
		{
			if (affectedPartIds != null && !affectedPartIds.Contains(fuselage.PartId))
			{
				continue;
			}

			fuselage._meshFilter.sharedMesh.normals = normals;
		}
	}

	private static void SmoothConnectedEnds(
		IReadOnlyList<FuselagePart> fuselages,
		Dictionary<FuselagePart, Vector3[]> baseNormals,
		Dictionary<FuselagePart, Vector3[]> workingNormals,
		IReadOnlyCollection<int> affectedPartIds)
	{
		HashSet<string> visitedPairs = new HashSet<string>();
		for (int i = 0; i < fuselages.Count; i++)
		{
			FuselagePart fuselage = fuselages[i];
			foreach (PartConnectionEndpoint endpoint in fuselage.ConnectionEndpoints)
			{
				if ((endpoint.LocalAttachPointId != 0 && endpoint.LocalAttachPointId != 1)
					|| (endpoint.ConnectedAttachPointId != 0 && endpoint.ConnectedAttachPointId != 1))
				{
					continue;
				}

				if (!fuselage.TryGetConnectedPart(endpoint, out Part connectedPart) || connectedPart is not FuselagePart connectedFuselage || connectedFuselage == fuselage)
				{
					continue;
				}

				bool fuselageFront = endpoint.LocalAttachPointId == 1;
				bool connectedFront = endpoint.ConnectedAttachPointId == 1;
				if (affectedPartIds != null && !affectedPartIds.Contains(fuselage.PartId) && !affectedPartIds.Contains(connectedFuselage.PartId))
				{
					continue;
				}

				string key = BuildEndPairKey(fuselage, fuselageFront, connectedFuselage, connectedFront);
				if (!visitedPairs.Add(key))
				{
					continue;
				}

				if (!AreConnectedEndsSmoothable(fuselage, fuselageFront, connectedFuselage, connectedFront))
				{
					continue;
				}

				SmoothEndPair(fuselage, fuselageFront, connectedFuselage, connectedFront, baseNormals, workingNormals);
			}
		}
	}

	private static void SmoothSpatiallyMatchedEnds(
		IReadOnlyList<FuselagePart> fuselages,
		Dictionary<FuselagePart, Vector3[]> baseNormals,
		Dictionary<FuselagePart, Vector3[]> workingNormals,
		IReadOnlyCollection<int> affectedPartIds)
	{
		for (int i = 0; i < fuselages.Count; i++)
		{
			for (int j = i + 1; j < fuselages.Count; j++)
			{
				if (affectedPartIds != null && !affectedPartIds.Contains(fuselages[i].PartId) && !affectedPartIds.Contains(fuselages[j].PartId))
				{
					continue;
				}

				for (int a = 0; a < 2; a++)
				{
					bool aFront = a == 1;
					for (int b = 0; b < 2; b++)
					{
						bool bFront = b == 1;
						if (!AreEndsCompatible(fuselages[i], aFront, fuselages[j], bFront))
						{
							continue;
						}

						SmoothEndPair(fuselages[i], aFront, fuselages[j], bFront, baseNormals, workingNormals);
					}
				}
			}
		}
	}

	private static void SmoothEndPair(
		FuselagePart a,
		bool aFront,
		FuselagePart b,
		bool bFront,
		Dictionary<FuselagePart, Vector3[]> baseNormals,
		Dictionary<FuselagePart, Vector3[]> workingNormals)
	{
		bool aSmooth = a.GetEndSection(aFront).Smooth;
		bool bSmooth = b.GetEndSection(bFront).Smooth;
		if (!aSmooth && !bSmooth)
		{
			return;
		}

		if (aSmooth && workingNormals.TryGetValue(a, out Vector3[] aNormals) && baseNormals.TryGetValue(b, out Vector3[] bBaseNormals))
		{
			SmoothMatchedEndNormals(a, aFront, aNormals, b, bFront, bBaseNormals, setMean: bSmooth);
		}

		if (bSmooth && workingNormals.TryGetValue(b, out Vector3[] bNormals) && baseNormals.TryGetValue(a, out Vector3[] aBaseNormals))
		{
			SmoothMatchedEndNormals(b, bFront, bNormals, a, aFront, aBaseNormals, setMean: aSmooth);
		}
	}

	private static bool AreConnectedEndsSmoothable(FuselagePart a, bool aFront, FuselagePart b, bool bFront)
	{
		if (a == null || b == null)
		{
			return false;
		}

		if (a._visualStyle != b._visualStyle || a._glass != b._glass)
		{
			return false;
		}

		Vector3 aPosition = a.GetSliceCraftPosition(aFront);
		Vector3 bPosition = b.GetSliceCraftPosition(bFront);
		if ((aPosition - bPosition).sqrMagnitude > 0.0025f)
		{
			return false;
		}

		Vector3 aNormal = a.GetSliceCraftNormal(aFront);
		Vector3 bNormal = b.GetSliceCraftNormal(bFront);
		return Vector3.Dot(aNormal, -bNormal) >= 0.98f;
	}

	private static string BuildEndPairKey(FuselagePart a, bool aFront, FuselagePart b, bool bFront)
	{
		string aKey = $"{a.PartId}:{(aFront ? 1 : 0)}";
		string bKey = $"{b.PartId}:{(bFront ? 1 : 0)}";
		return string.CompareOrdinal(aKey, bKey) <= 0 ? $"{aKey}|{bKey}" : $"{bKey}|{aKey}";
	}

	// 读取 JFuselage 截面数据，并保留原始 XML 以支持往返导出。 / Read JFuselage section data and preserve the raw XML for round-trip export.
	private void LoadJFuselageState(XElement stateElement)
	{
		_rawStateXml = stateElement.ToString(SaveOptions.DisableFormatting);
		_offset = XmlUtil.ParseVector3((string)stateElement.Attribute("offset"), _offset);
		_visualStyle = ParseVisualStyle((string)stateElement.Attribute("style"));
		_glass = XmlUtil.ParseBool((string)stateElement.Attribute("glass"));
		_rearSection = ParseJSection(stateElement.Element("SectionA"), _rearSection);
		_frontSection = ParseJSection(stateElement.Element("SectionB"), _frontSection);
		CanonicalizeSections();
	}

	// 用当前编辑器值写出现代 JFuselage 状态块。 / Write the modern JFuselage state block using the current editor values.
	private void WriteJFuselageState(XElement partElement)
	{
		RemoveStateElements(partElement, "Fuselage.State");
		bool hasRawState = !string.IsNullOrWhiteSpace(_rawStateXml);
		XElement stateElement = hasRawState ? XElement.Parse(_rawStateXml) : new XElement("JFuselage.State");
		stateElement.Name = "JFuselage.State";
		stateElement.SetAttributeValue("version", 3);
		stateElement.SetAttributeValue("offset", XmlUtil.FormatVector3(_offset));
		stateElement.SetAttributeValue("style", _visualStyle == FuselageVisualStyle.Hollow ? "Hollow" : "Body");
		stateElement.SetAttributeValue("glass", XmlUtil.FormatBool(_glass));
		if (!hasRawState)
		{
			stateElement.SetAttributeValue("mass", "0,0,0,0");
			stateElement.SetAttributeValue("deadMassKg", "0");
			stateElement.SetAttributeValue("buoyancy", "0");
			stateElement.SetAttributeValue("fuelProportion", "0");
		}
		WriteJSectionElement(stateElement, "SectionA", _rearSection);
		WriteJSectionElement(stateElement, "SectionB", _frontSection);
		RemoveStateElements(partElement, "JFuselage.State");
		partElement.Add(stateElement);
	}

	// 读取 Legacy 机身状态，并映射到统一的 loft 表示。 / Read legacy fuselage state and map it into the shared loft representation.
	private void LoadLegacyState(XElement partElement, XElement stateElement)
	{
		_rawStateXml = stateElement.ToString(SaveOptions.DisableFormatting);
		Vector2 frontScale = XmlUtil.ParseVector2((string)stateElement.Attribute("frontScale"), Vector2.one);
		Vector2 rearScale = XmlUtil.ParseVector2((string)stateElement.Attribute("rearScale"), Vector2.one);
		Vector3 legacyOffset = XmlUtil.ParseVector3((string)stateElement.Attribute("offset"), Vector3.one);
		_offset = new Vector3(-legacyOffset.x * 0.5f, -legacyOffset.y * 0.5f, legacyOffset.z * 0.5f);
		_frontSection = DefaultSection();
		_rearSection = DefaultSection();
		_frontSection.Width = frontScale.x * 0.5f;
		_frontSection.Height = frontScale.y * 0.5f;
		_rearSection.Width = rearScale.x * 0.5f;
		_rearSection.Height = rearScale.y * 0.5f;
		_frontSection.Smooth = XmlUtil.ParseBool((string)stateElement.Attribute("smoothFront"), _frontSection.Smooth);
		_rearSection.Smooth = XmlUtil.ParseBool((string)stateElement.Attribute("smoothBack"), _rearSection.Smooth);
		ApplyLegacyFill(ref _frontSection, (string)stateElement.Attribute("fillFront"));
		ApplyLegacyFill(ref _rearSection, (string)stateElement.Attribute("fillBack"));
		_frontSection.Thickness = XmlUtil.ParseFloat((string)stateElement.Attribute("inletThicknessFront"), _frontSection.Thickness);
		_rearSection.Thickness = XmlUtil.ParseFloat((string)stateElement.Attribute("inletThicknessRear"), _rearSection.Thickness);
		ApplyLegacyCornerTypes(ref _frontSection, (string)stateElement.Attribute("cornerTypes"), 0);
		ApplyLegacyCornerTypes(ref _rearSection, (string)stateElement.Attribute("cornerTypes"), 4);
		string partType = (string)partElement.Attribute("partType") ?? string.Empty;
		_visualStyle = partType.Contains("Hollow", StringComparison.OrdinalIgnoreCase) || partType.Contains("Glass-2", StringComparison.OrdinalIgnoreCase) || partType.Contains("Inlet", StringComparison.OrdinalIgnoreCase)
			? FuselageVisualStyle.Hollow
			: FuselageVisualStyle.Body;
		_glass = partType.Contains("Glass", StringComparison.OrdinalIgnoreCase);
		CanonicalizeSections();
	}

	// 恢复 Unity 和 XML 都不会直接序列化的截面派生字段。 / Restore derived section fields that are not serialized by Unity or the XML formats.
	private void CanonicalizeSections()
	{
		CanonicalizeSection(ref _rearSection);
		CanonicalizeSection(ref _frontSection);
	}

	private static void CanonicalizeSection(ref FuselageSectionSettings section)
	{
		section.Sanitize();
		Float4Value maxCornerRadii = section.GetMaxCornerRadii();
		for (int i = 0; i < 4; i++)
		{
			section.CornerRadii[i] = Mathf.Clamp(section.CornerRadii[i], 0f, maxCornerRadii[i]);
		}

		section.Sanitize();
		section.GetCuttingRange(out Float4Value minCutting, out Float4Value maxCutting);
		for (int i = 0; i < 4; i++)
		{
			if (!section.GetCutEnabled(i))
			{
				continue;
			}

			section.SetCutValue(i, Mathf.Clamp(section.GetCutValue(i), minCutting[i], maxCutting[i]));
		}

		section.Sanitize();
	}

	// 从统一 loft 数据近似回写 Legacy 机身状态块。 / Write the legacy fuselage block by approximating from the shared loft data.
	private void WriteLegacyState(XElement partElement)
	{
		RemoveStateElements(partElement, "JFuselage.State");
		XElement stateElement = string.IsNullOrWhiteSpace(_rawStateXml) ? new XElement("Fuselage.State") : XElement.Parse(_rawStateXml);
		stateElement.Name = "Fuselage.State";
		Vector3 legacyOffset = new Vector3(-_offset.x * 2f, -_offset.y * 2f, _offset.z * 2f);
		stateElement.SetAttributeValue("version", (string)stateElement.Attribute("version") ?? "1");
		stateElement.SetAttributeValue("frontScale", XmlUtil.FormatVector2(new Vector2(_frontSection.Width * 2f, _frontSection.Height * 2f)));
		stateElement.SetAttributeValue("rearScale", XmlUtil.FormatVector2(new Vector2(_rearSection.Width * 2f, _rearSection.Height * 2f)));
		stateElement.SetAttributeValue("offset", XmlUtil.FormatVector3(legacyOffset));
		stateElement.SetAttributeValue("smoothFront", XmlUtil.FormatBool(_frontSection.Smooth));
		stateElement.SetAttributeValue("smoothBack", XmlUtil.FormatBool(_rearSection.Smooth));
		stateElement.SetAttributeValue("fillFront", LegacyFillString(_frontSection));
		stateElement.SetAttributeValue("fillBack", LegacyFillString(_rearSection));
		stateElement.SetAttributeValue("inletThicknessFront", XmlUtil.FormatFloat(_frontSection.Thickness));
		stateElement.SetAttributeValue("inletThicknessRear", XmlUtil.FormatFloat(_rearSection.Thickness));
		stateElement.SetAttributeValue("cornerTypes", LegacyCornerString(_frontSection, _rearSection));
		UpdateLegacyPartType(partElement);
		RemoveStateElements(partElement, "Fuselage.State");
		partElement.Add(stateElement);
	}

	// 应用来自其他零件、且目标指向当前机身的切割体。 / Apply any fuselage-targeted cutting volumes contributed by other parts in the craft.
	private Mesh ApplyTargetedCarvers(Mesh source)
	{
		Craft craft = GetOwningCraft();
		if (source == null)
		{
			return source;
		}

		Mesh carved = source;
		string carvedMeshName = string.IsNullOrEmpty(source.name) ? "FuselageCarved" : source.name + "_Carved";
		bool hasBooleanCut = false;
		// 直接对目标机身应用所有 solid cutter mesh，避免继续走自定义平面布尔封口。 / Apply all solid cutter meshes directly to the target fuselage so we stop relying on the custom plane-boolean capping path.
		foreach (IFuselageCarver carver in craft.GetComponentsInChildren<MonoBehaviour>(includeInactive: true).OfType<IFuselageCarver>())
		{
			if (ReferenceEquals(carver, this))
			{
				continue;
			}

			if (carver is not Component carverComponent)
			{
				continue;
			}

			if (!carver.TryGetCutMesh(this, out Mesh cutterMesh) || cutterMesh == null || cutterMesh.vertexCount == 0)
			{
				DestroyOwnedObject(cutterMesh);
				continue;
			}

			Mesh next = MeshTools.MeshBoolean.Subtract(carved, transform, cutterMesh, carverComponent.transform, transform);
			DestroyOwnedObject(cutterMesh);
			if (next == null || ReferenceEquals(next, carved))
			{
				continue;
			}

			next.name = carvedMeshName;
			DestroyOwnedObject(carved);
			carved = next;
			hasBooleanCut = true;
		}

		if (!hasBooleanCut)
		{
			return source;
		}

		return carved;
	}

	// 找到第一个与当前端面足够吻合、可以共享接缝的邻接机身端面。 / Find the first fuselage end that matches this end closely enough to share a seam.
	private bool TryFindMatchingNeighbour(bool front, out FuselagePart neighbour, out bool neighbourFront)
	{
		neighbour = null;
		neighbourFront = false;
		if (TryFindConnectedNeighbour(front, out neighbour, out neighbourFront) && AreConnectedEndsSmoothable(this, front, neighbour, neighbourFront))
		{
			return true;
		}

		Craft craft = GetOwningCraft();
		if (craft.HasConnectionData)
		{
			return false;
		}

		FuselagePart[] candidates = craft.GetComponentsInChildren<FuselagePart>(includeInactive: true);
		if (candidates.Length > MaxSpatialNeighbourSearchFuselages)
		{
			return false;
		}

		foreach (FuselagePart other in candidates)
		{
			if (other == this)
			{
				continue;
			}
			for (int i = 0; i < 2; i++)
			{
				bool otherFront = i == 1;
				if (!AreEndsCompatible(this, front, other, otherFront))
				{
					continue;
				}

				neighbour = other;
				neighbourFront = otherFront;
				return true;
			}
		}

		return false;
	}

	// 在世界空间比较两个机身端面，判断它们是否属于同一条接缝。 / Compare two fuselage ends in world space to decide whether they represent the same seam.
	private static bool AreEndsCompatible(FuselagePart a, bool aFront, FuselagePart b, bool bFront)
	{
		if (a == null || b == null)
		{
			return false;
		}

		if (a._visualStyle != b._visualStyle || a._glass != b._glass)
		{
			return false;
		}

		// 在导入/编辑同一帧里优先用 Craft 本地空间比较，避免依赖下一次编辑器事件后才稳定的世界矩阵。 / Compare ends in craft-local space so import-time seam checks do not depend on world matrices settling on a later editor tick.
		Vector3 aPosition = a.GetSliceCraftPosition(aFront);
		Vector3 bPosition = b.GetSliceCraftPosition(bFront);
		if ((aPosition - bPosition).sqrMagnitude > 0.0004f)
		{
			return false;
		}

		Vector3 aNormal = a.GetSliceCraftNormal(aFront);
		Vector3 bNormal = b.GetSliceCraftNormal(bFront);
		if (Vector3.Dot(aNormal, -bNormal) < 0.995f)
		{
			return false;
		}

		return SectionsApproximatelyMatch(a.GetScaledEndSection(aFront), b.GetScaledEndSection(bFront));
	}

	// 把一个端面的接缝法线复制或平均到另一侧匹配端面。 / Copy or average seam normals from one fuselage end onto another matched end.
	private static void SmoothMatchedEndNormals(FuselagePart target, bool targetFront, Vector3[] targetNormals, FuselagePart source, bool sourceFront, Vector3[] sourceNormals, bool setMean)
	{
		if (target._meshFilter == null || source._meshFilter == null || target._meshFilter.sharedMesh == null || source._meshFilter.sharedMesh == null)
		{
			return;
		}

		Vector3[] targetVertices = target._meshFilter.sharedMesh.vertices;
		Vector3[] sourceVertices = source._meshFilter.sharedMesh.vertices;
		List<int> targetSeam = FindSeamVertices(targetVertices, targetNormals, target, targetFront);
		List<int> sourceSeam = FindSeamVertices(sourceVertices, sourceNormals, source, sourceFront);
		if (targetSeam.Count == 0 || sourceSeam.Count == 0)
		{
			return;
		}

		// 为目标端面的每个接缝顶点寻找世界空间里最近的源顶点。 / Match each target seam vertex to the nearest source seam vertex in world space.
		for (int i = 0; i < targetSeam.Count; i++)
		{
			int targetIndex = targetSeam[i];
			Vector3 targetCraftPosition = target.TransformPointToCraftSpace(targetVertices[targetIndex]);
			Vector3 targetCraftNormal = target.TransformDirectionToCraftSpace(targetNormals[targetIndex]);
			int bestSourceIndex = -1;
			float bestDistance = 0.0004f;
			float bestNormalScore = float.NegativeInfinity;
			for (int j = 0; j < sourceSeam.Count; j++)
			{
				int candidate = sourceSeam[j];
				float distance = (targetCraftPosition - source.TransformPointToCraftSpace(sourceVertices[candidate])).sqrMagnitude;
				Vector3 candidateCraftNormal = source.TransformDirectionToCraftSpace(sourceNormals[candidate]);
				float normalScore = Vector3.Dot(targetCraftNormal, candidateCraftNormal);
				if (distance < bestDistance - 0.0000001f || (Mathf.Abs(distance - bestDistance) <= 0.0000001f && normalScore > bestNormalScore))
				{
					bestDistance = distance;
					bestSourceIndex = candidate;
					bestNormalScore = normalScore;
				}
			}

			if (bestSourceIndex < 0)
			{
				continue;
			}

			Vector3 sourceCraftNormal = source.TransformDirectionToCraftSpace(sourceNormals[bestSourceIndex]);
			Vector3 resolved = setMean ? (targetCraftNormal + sourceCraftNormal).normalized : sourceCraftNormal;
			if (resolved.sqrMagnitude <= 0.0001f)
			{
				continue;
			}

			targetNormals[targetIndex] = target.TransformDirectionFromCraftSpace(resolved);
		}
	}

	// 找出位于机身前后端面接缝平面上的顶点。 / Identify vertices that lie on a fuselage's front or rear seam plane.
	private static List<int> FindSeamVertices(Vector3[] vertices, Vector3[] normals, FuselagePart part, bool front)
	{
		List<int> indices = new List<int>();
		Vector3 planePoint = part.GetSliceLocalPosition(front);
		Vector3 planeNormal = part.GetAxisLocal(front);
		float tolerance = 0.0015f + part._offset.magnitude * 0.002f;
		for (int i = 0; i < vertices.Length; i++)
		{
			float distance = Mathf.Abs(Vector3.Dot(vertices[i] - planePoint, planeNormal));
			if (distance <= tolerance)
			{
				if (IsFlatEndCapNormal(normals, i, planeNormal))
				{
					continue;
				}

				indices.Add(i);
			}
		}
		return indices;
	}

	private static bool IsFlatEndCapNormal(Vector3[] normals, int index, Vector3 planeNormal)
	{
		if (normals == null || index < 0 || index >= normals.Length)
		{
			return false;
		}

		Vector3 normal = normals[index];
		if (normal.sqrMagnitude <= 0.0001f)
		{
			return false;
		}

		return Mathf.Abs(Vector3.Dot(normal.normalized, planeNormal)) >= 0.985f;
	}

	// 返回指定前后端截面的局部空间中心点。 / Return the local-space center of the requested end slice.
	private Vector3 GetSliceLocalPosition(bool front)
	{
		return _offset * (front ? 0.5f : -0.5f);
	}

	// 把机身局部点转换到 Craft 本地空间，避免导入当帧依赖世界矩阵刷新时序。 / Convert a fuselage-local point into craft-local space so editor-time seam tests do not depend on world-matrix update timing.
	private Vector3 TransformPointToCraftSpace(Vector3 localPoint)
	{
		return Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale).MultiplyPoint3x4(localPoint);
	}

	private Vector3 TransformVectorToCraftSpace(Vector3 localVector)
	{
		return Matrix4x4.TRS(Vector3.zero, transform.localRotation, transform.localScale).MultiplyVector(localVector);
	}

	// 把机身局部方向转换到 Craft 本地空间。 / Convert a fuselage-local direction into craft-local space.
	private Vector3 TransformDirectionToCraftSpace(Vector3 localDirection)
	{
		Vector3 transformed = transform.localRotation * localDirection;
		return transformed.sqrMagnitude <= 0.0001f ? Vector3.forward : transformed.normalized;
	}

	// 把 Craft 本地空间方向还原回机身局部法线。 / Convert a craft-local direction back into fuselage-local normal space.
	private Vector3 TransformDirectionFromCraftSpace(Vector3 craftDirection)
	{
		Vector3 transformed = Quaternion.Inverse(transform.localRotation) * craftDirection;
		return transformed.sqrMagnitude <= 0.0001f ? Vector3.forward : transformed.normalized;
	}

	// 返回指定端面在 Craft 本地空间里的位置。 / Return the requested end-slice position in craft-local space.
	private Vector3 GetSliceCraftPosition(bool front)
	{
		return TransformPointToCraftSpace(GetSliceLocalPosition(front));
	}

	// 返回指定端面在 Craft 本地空间里的法线。 / Return the requested end-slice normal in craft-local space.
	private Vector3 GetSliceCraftNormal(bool front)
	{
		return TransformDirectionToCraftSpace(GetAxisLocal(front));
	}

	// 把指定端截面的中心点转换到世界空间。 / Convert the requested end slice center into world space.
	private Vector3 GetSliceWorldPosition(bool front)
	{
		return transform.TransformPoint(GetSliceLocalPosition(front));
	}

	// 返回指定端截面所使用的局部空间平面法线。 / Return the local-space plane normal used by the requested end slice.
	private Vector3 GetAxisLocal(bool front)
	{
		return front ? Vector3.forward : Vector3.back;
	}

	// 把指定端截面的法线转换到世界空间。 / Convert the requested end slice normal into world space.
	private Vector3 GetSliceWorldNormal(bool front)
	{
		return transform.TransformDirection(GetAxisLocal(front)).normalized;
	}

	// 获取指定前后端面对应的截面设置。 / Fetch the section settings that own the requested fuselage end.
	private FuselageSectionSettings GetEndSection(bool front)
	{
		return front ? _frontSection : _rearSection;
	}

	private FuselageSectionSettings GetScaledEndSection(bool front)
	{
		FuselageSectionSettings section = GetEndSection(front);
		Vector3 widthVector = TransformVectorToCraftSpace(Vector3.right * section.Width);
		Vector3 heightVector = TransformVectorToCraftSpace(Vector3.up * section.Height);
		float widthScale = section.Width <= 0.0001f ? Mathf.Abs(transform.localScale.x) : widthVector.magnitude / section.Width;
		float heightScale = section.Height <= 0.0001f ? Mathf.Abs(transform.localScale.y) : heightVector.magnitude / section.Height;
		float radiusScale = Mathf.Max(0.0001f, (Mathf.Abs(widthScale) + Mathf.Abs(heightScale)) * 0.5f);
		section.Width *= Mathf.Abs(widthScale);
		section.Height *= Mathf.Abs(heightScale);
		section.CornerRadii.X *= radiusScale;
		section.CornerRadii.Y *= radiusScale;
		section.CornerRadii.Z *= radiusScale;
		section.CornerRadii.W *= radiusScale;
		return section;
	}

	private bool TryFindConnectedNeighbour(bool front, out FuselagePart neighbour, out bool neighbourFront)
	{
		neighbour = null;
		neighbourFront = false;
		int attachPointId = front ? 1 : 0;
		foreach (PartConnectionEndpoint endpoint in ConnectionEndpoints)
		{
			if (endpoint.LocalAttachPointId != attachPointId)
			{
				continue;
			}

			if (!TryGetConnectedPart(endpoint, out Part connectedPart) || connectedPart is not FuselagePart fuselage)
			{
				continue;
			}

			if (endpoint.ConnectedAttachPointId != 0 && endpoint.ConnectedAttachPointId != 1)
			{
				continue;
			}

			neighbour = fuselage;
			neighbourFront = endpoint.ConnectedAttachPointId == 1;
			return true;
		}

		return false;
	}

	private Vector3 GetMidSectionSidePoint(int sideIndex)
	{
		FuselageSectionSettings section = FuselageSectionSettings.Lerp(_rearSection, _frontSection, 0.5f, Int4Value.Max(_rearSection.CornerSamples, _frontSection.CornerSamples), Int4Value.Max(_rearSection.EdgeSamples, _frontSection.EdgeSamples));
		float halfHeight = section.Height * 0.5f;
		float topHalfWidth = section.Width * 0.5f * Mathf.Max(0f, 1f + section.Trapezium);
		float bottomHalfWidth = section.Width * 0.5f * Mathf.Max(0f, 1f - section.Trapezium);
		float midRight = (topHalfWidth + bottomHalfWidth) * 0.5f;
		return sideIndex switch
		{
			0 => new Vector3(0f, halfHeight, 0f),
			1 => new Vector3(midRight, 0f, 0f),
			2 => new Vector3(0f, -halfHeight, 0f),
			3 => new Vector3(-midRight, 0f, 0f),
			_ => Vector3.zero
		};
	}

	// 解析机身预览路径所需的网格组件。 / Resolve the mesh components used by the fuselage preview path.
	private void EnsureComponents()
	{
		EnsureComponent(ref _meshFilter);
		EnsureComponent(ref _meshRenderer);
	}

	// 把源 XML 的样式文本映射成本地预览样式枚举。 / Map the source XML style text into the local preview style enum.
	private static FuselageVisualStyle ParseVisualStyle(string styleText)
	{
		return string.Equals(styleText, "Hollow", StringComparison.OrdinalIgnoreCase) || string.Equals(styleText, "HollowCone", StringComparison.OrdinalIgnoreCase) || string.Equals(styleText, "Inlet", StringComparison.OrdinalIgnoreCase)
			? FuselageVisualStyle.Hollow
			: FuselageVisualStyle.Body;
	}

	// 把一个 JFuselage 截面节点解析成运行时截面设置。 / Parse one JFuselage section element into the runtime section settings struct.
	private static FuselageSectionSettings ParseJSection(XElement element, FuselageSectionSettings fallback)
	{
		if (element == null)
		{
			fallback.Sanitize();
			return fallback;
		}

		FuselageSectionSettings section = fallback;
		Vector2 size = XmlUtil.ParseVector2((string)element.Attribute("size"), new Vector2(section.Width, section.Height));
		section.Width = size.x;
		section.Height = size.y;
		section.Trapezium = XmlUtil.ParseFloat((string)element.Attribute("trapezium"), section.Trapezium);
		section.Thickness = XmlUtil.ParseFloat((string)element.Attribute("thickness"), section.Thickness);
		section.Smooth = XmlUtil.ParseBool((string)element.Attribute("smoothing"), section.Smooth);
		section.CornerRadii = XmlUtil.ParseFloat4((string)element.Attribute("cornerRadii"), section.CornerRadii);
		section.CornerStretch = XmlUtil.ParseBool4((string)element.Attribute("cornerStretch"), section.CornerStretch);
		section.CornerSamples = XmlUtil.ParseInt4((string)element.Attribute("cornerSamples"), section.CornerSamples);
		section.EdgeCurvature = XmlUtil.ParseFloat4((string)element.Attribute("edgeCurvature"), section.EdgeCurvature);
		section.EdgeSamples = XmlUtil.ParseInt4((string)element.Attribute("edgeSamples"), section.EdgeSamples);
		ApplyCutString(ref section, (string)element.Attribute("cutting"));
		section.Sanitize();
		return section;
	}

	// 把一个运行时截面序列化回现代 JFuselage XML 格式。 / Serialize one runtime section back into the modern JFuselage XML format.
	private static XElement CreateJSectionElement(string name, FuselageSectionSettings section)
	{
		XElement element = new XElement(name);
		WriteJSectionAttributes(element, section);
		return element;
	}

	private static void WriteJSectionElement(XElement stateElement, string name, FuselageSectionSettings section)
	{
		XElement element = stateElement.Element(name);
		if (element == null)
		{
			stateElement.Add(CreateJSectionElement(name, section));
			return;
		}

		WriteJSectionAttributes(element, section);
	}

	private static void WriteJSectionAttributes(XElement element, FuselageSectionSettings section)
	{
		section.Sanitize();
		element.SetAttributeValue("size", XmlUtil.FormatVector2(new Vector2(section.Width, section.Height)));
		element.SetAttributeValue("cornerRadii", XmlUtil.FormatFloat4(section.CornerRadii));
		element.SetAttributeValue("cornerStretch", XmlUtil.FormatBool4(section.CornerStretch));
		element.SetAttributeValue("cornerSamples", XmlUtil.FormatInt4(section.CornerSamples));
		element.SetAttributeValue("trapezium", XmlUtil.FormatFloat(section.Trapezium));
		element.SetAttributeValue("edgeCurvature", XmlUtil.FormatFloat4(section.EdgeCurvature));
		element.SetAttributeValue("edgeSamples", XmlUtil.FormatInt4(section.EdgeSamples));
		element.SetAttributeValue("smoothing", section.Smooth ? "True" : "False");
		element.SetAttributeValue("cutting", JCutString(section));
		if (section.Thickness > 0f)
		{
			element.SetAttributeValue("thickness", XmlUtil.FormatFloat(section.Thickness));
		}
		else
		{
			element.SetAttributeValue("thickness", null);
		}
	}

	// 把现代 cutting 字符串解析成四个方向的切割值。 / Parse the modern cutting string into per-side cut values.
	private static void ApplyCutString(ref FuselageSectionSettings section, string cutting)
	{
		if (string.IsNullOrWhiteSpace(cutting))
		{
			section.CutEnabled = Bool4Value.Repeat(false);
			section.CutTop = 0f;
			section.CutRight = 0f;
			section.CutBottom = 0f;
			section.CutLeft = 0f;
			return;
		}

		string[] parts = cutting.Split(',');
		ParseCut(parts, 0, out bool topEnabled, out float topValue);
		ParseCut(parts, 1, out bool rightEnabled, out float rightValue);
		ParseCut(parts, 2, out bool bottomEnabled, out float bottomValue);
		ParseCut(parts, 3, out bool leftEnabled, out float leftValue);
		section.CutEnabled = new Bool4Value(topEnabled, rightEnabled, bottomEnabled, leftEnabled);
		section.CutTop = topValue;
		section.CutRight = rightValue;
		section.CutBottom = bottomValue;
		section.CutLeft = leftValue;

		// 空字符串表示该边没有切割。 / Empty cut entries mean the side is uncut in the modern XML format.
		static void ParseCut(string[] values, int index, out bool enabled, out float value)
		{
			if (index >= values.Length || string.IsNullOrWhiteSpace(values[index]))
			{
				enabled = false;
				value = 0f;
				return;
			}
			enabled = true;
			value = XmlUtil.ParseFloat(values[index]);
		}
	}

	// 把 Legacy fill 数值映射到统一的四边 cut 表示。 / Map legacy fill values into the shared per-side cut representation.
	private static void ApplyLegacyFill(ref FuselageSectionSettings section, string fill)
	{
		if (string.IsNullOrWhiteSpace(fill))
		{
			section.CutEnabled = Bool4Value.Repeat(false);
			section.CutTop = 0f;
			section.CutBottom = 0f;
			section.CutLeft = 0f;
			section.CutRight = 0f;
			return;
		}

		string[] parts = fill.Split(',');
		section.CutTop = 1f - XmlUtil.ParseFloat(parts.Length > 0 ? parts[0] : null, 1f);
		section.CutBottom = 1f - XmlUtil.ParseFloat(parts.Length > 1 ? parts[1] : null, 1f);
		section.CutLeft = 1f - XmlUtil.ParseFloat(parts.Length > 2 ? parts[2] : null, 1f);
		section.CutRight = 1f - XmlUtil.ParseFloat(parts.Length > 3 ? parts[3] : null, 1f);
		section.CutEnabled = new Bool4Value(
			Mathf.Abs(section.CutTop) > 0.0001f,
			Mathf.Abs(section.CutRight) > 0.0001f,
			Mathf.Abs(section.CutBottom) > 0.0001f,
			Mathf.Abs(section.CutLeft) > 0.0001f);
	}

	// 把 Legacy corner type 索引转换成统一的 radius/stretch 表示。 / Convert legacy corner type indices into the shared radius/stretch representation.
	private static void ApplyLegacyCornerTypes(ref FuselageSectionSettings section, string cornerTypes, int startIndex)
	{
		if (string.IsNullOrWhiteSpace(cornerTypes))
		{
			return;
		}

		string[] values = cornerTypes.Split(',');
		if (values.Length <= startIndex)
		{
			return;
		}

		int styleIndex = Mathf.Clamp(XmlUtil.ParseInt(values[startIndex], 0), 0, LegacyCornerRadiusFromStyle.Length - 1);
		section.CornerRadii = Float4Value.Repeat(LegacyCornerRadiusFromStyle[styleIndex]);
		section.CornerStretch = Bool4Value.Repeat(LegacyCornerStretchFromStyle[styleIndex]);
	}

	// 把当前 cut 值转换回 Legacy fill 字符串格式。 / Convert current cut values back into the legacy fill string format.
	private static string LegacyFillString(FuselageSectionSettings section)
	{
		return $"{XmlUtil.FormatFloat(1f - section.CutTop)},{XmlUtil.FormatFloat(1f - section.CutBottom)},{XmlUtil.FormatFloat(1f - section.CutLeft)},{XmlUtil.FormatFloat(1f - section.CutRight)}";
	}

	// 把当前前后截面的 corner 设置转换回 Legacy 样式索引。 / Convert current front and rear corner settings back into legacy style indices.
	private static string LegacyCornerString(FuselageSectionSettings front, FuselageSectionSettings rear)
	{
		int frontStyle = LegacyCornerIndex(front.CornerRadii.X, front.CornerStretch.X);
		int rearStyle = LegacyCornerIndex(rear.CornerRadii.X, rear.CornerStretch.X);
		return $"{frontStyle},{frontStyle},{frontStyle},{frontStyle},{rearStyle},{rearStyle},{rearStyle},{rearStyle}";
	}

	// 为一组 radius/stretch 选择最近的 Legacy corner 样式索引。 / Pick the closest legacy corner style index for a shared radius/stretch pair.
	private static int LegacyCornerIndex(float radius, bool stretched)
	{
		if (stretched)
		{
			return 3;
		}

		float best = float.PositiveInfinity;
		int bestIndex = 0;
		for (int i = 0; i < 3; i++)
		{
			float delta = Mathf.Abs(LegacyCornerRadiusFromStyle[i] - radius);
			if (delta < best)
			{
				best = delta;
				bestIndex = i;
			}
		}
		return bestIndex;
	}

	// 让 Legacy partType 与当前视觉样式和玻璃标记保持一致。 / Keep the legacy part type string aligned with the current visual style and glass flag.
	private void UpdateLegacyPartType(XElement partElement)
	{
		string partType = _visualStyle switch
		{
			FuselageVisualStyle.Hollow when _glass => "Fuselage-Glass-2",
			FuselageVisualStyle.Hollow => "Fuselage-Hollow-1",
			_ when _glass => "Fuselage-Glass-1",
			_ => "Fuselage-Body-1"
		};
		partElement.SetAttributeValue("partType", partType);
	}

	// 把四边 cut 值序列化回现代 cut 字符串格式。 / Serialize per-side cut values back into the modern cut string format.
	private static string JCutString(FuselageSectionSettings section)
	{
		return $"{CutText(section.GetCutEnabled(0), section.CutTop)},{CutText(section.GetCutEnabled(1), section.CutRight)},{CutText(section.GetCutEnabled(2), section.CutBottom)},{CutText(section.GetCutEnabled(3), section.CutLeft)}";

		// 未切到的边输出空项，以匹配原始 XML 习惯。 / Emit empty entries for untouched sides so the exported XML matches the source format.
		static string CutText(bool enabled, float value)
		{
			return enabled ? XmlUtil.FormatFloat(value) : string.Empty;
		}
	}

	// 判断两个截面是否足够接近，从而共享接缝平滑和端盖省略逻辑。 / Check whether two sections are close enough to share seam smoothing and cap suppression.
	private static bool SectionsApproximatelyMatch(FuselageSectionSettings a, FuselageSectionSettings b)
	{
		const float epsilon = 0.001f;
		return Mathf.Abs(a.Width - b.Width) <= epsilon
			&& Mathf.Abs(a.Height - b.Height) <= epsilon
			&& Mathf.Abs(a.Trapezium - b.Trapezium) <= epsilon
			&& Mathf.Abs(a.Thickness - b.Thickness) <= epsilon
			&& Equals(a.CutEnabled, b.CutEnabled)
			&& Mathf.Abs(a.CutTop - b.CutTop) <= epsilon
			&& Mathf.Abs(a.CutRight - b.CutRight) <= epsilon
			&& Mathf.Abs(a.CutBottom - b.CutBottom) <= epsilon
			&& Mathf.Abs(a.CutLeft - b.CutLeft) <= epsilon
			&& Approximately(a.CornerRadii, b.CornerRadii, epsilon)
			&& Approximately(a.EdgeCurvature, b.EdgeCurvature, epsilon)
			&& Equals(a.CornerStretch, b.CornerStretch)
			&& Equals(a.CornerSamples, b.CornerSamples)
			&& Equals(a.EdgeSamples, b.EdgeSamples);
	}

	// 用小的绝对误差比较 Float4Value。 / Compare float4-like values using a small absolute tolerance.
	private static bool Approximately(Float4Value a, Float4Value b, float epsilon)
	{
		return Mathf.Abs(a.X - b.X) <= epsilon
			&& Mathf.Abs(a.Y - b.Y) <= epsilon
			&& Mathf.Abs(a.Z - b.Z) <= epsilon
			&& Mathf.Abs(a.W - b.W) <= epsilon;
	}

	// 逐分量比较 Bool4Value。 / Compare Bool4Value instances component by component.
	private static bool Equals(Bool4Value a, Bool4Value b)
	{
		return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
	}

	// 逐分量比较 Int4Value。 / Compare Int4Value instances component by component.
	private static bool Equals(Int4Value a, Int4Value b)
	{
		return a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W;
	}

	// 提供新建或重置机身端面时使用的默认截面值。 / Provide the default section values used for new or reset fuselage ends.
	private static FuselageSectionSettings DefaultSection()
	{
		return new FuselageSectionSettings
		{
			Width = 1f,
			Height = 1f,
			CornerRadii = Float4Value.Repeat(0.25f),
			CornerStretch = Bool4Value.Repeat(false),
			CornerSamples = Int4Value.Repeat(7),
			EdgeCurvature = Float4Value.Repeat(0f),
			EdgeSamples = Int4Value.Repeat(7),
			CutEnabled = Bool4Value.Repeat(false),
			Smooth = true,
			Thickness = 0.1f
		};
	}
}
