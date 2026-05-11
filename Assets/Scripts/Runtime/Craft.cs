using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif


[Serializable]
public struct CraftThemeMaterial
{
	public string Style;

	public string Name;

	public Color Color;

	public float Smoothness;

	public float Metallic;

	public float EmissionDensity;

	public float Emission;

	public bool Hidden;
}

[ExecuteAlways]
public class Craft : MonoBehaviour
{
	[SerializeField]
	private string _sourceXmlPath;

	[SerializeField]
	private string _lastExportPath;

	[SerializeField, TextArea(6, 20)]
	private string _rawAircraftXml;

	[SerializeField]
	private CraftThemeMaterial[] _themeMaterials = Array.Empty<CraftThemeMaterial>();

	[SerializeField]
	private bool _lockImportedPartsInScene = true;

	private bool _isRebuildingPreviews;

	private bool _isLightweightPreviewRebuild;

	private bool _suppressPreviewQueue;

	private readonly Dictionary<int, Part> _partById = new Dictionary<int, Part>();

	private bool _partIndexDirty = true;

	private bool _hasConnectionData;

	#if UNITY_EDITOR
	private bool _previewRebuildQueued;

	private bool _previewRebuildDispatchQueued;

	private double _previewRebuildDueTime;

	private bool _queuedFullPreviewRebuild;

	private bool _queuedPreviewRequiresFullQuality;

	private readonly HashSet<int> _queuedPreviewPartInstanceIds = new HashSet<int>();

	private const double PreviewRebuildFrameBudgetSeconds = 0.006d;

	private const int PreviewRebuildMaxPartsPerFrame = 4;

	private Part[] _activePreviewRebuildParts = Array.Empty<Part>();

	private int _activePreviewRebuildIndex;

	private bool _activePreviewRebuildLightweight;

	private bool _activePreviewTouchedFuselage;

	private readonly HashSet<int> _activePreviewFuselagePartIds = new HashSet<int>();

	private bool _incrementalPreviewRebuildActive;

	private bool _postRebuildSmoothingQueued;
	#endif

	public string SourceXmlPath => _sourceXmlPath;

	public string LastExportPath => _lastExportPath;

	public bool IsRebuildingPreviews => _isRebuildingPreviews;

	public bool IsLightweightPreviewRebuild => _isLightweightPreviewRebuild;

	public bool IsPreviewQueueSuppressed => _suppressPreviewQueue;

	public IReadOnlyList<CraftThemeMaterial> ThemeMaterials => _themeMaterials ?? Array.Empty<CraftThemeMaterial>();

	public bool HasConnectionData => _hasConnectionData;

	public int PaintMaterialCount
	{
		get
		{
			if (_themeMaterials == null || _themeMaterials.Length == 0)
			{
				return 0;
			}

			int hiddenCount = 0;
			int visibleCount = 0;
			for (int i = 0; i < _themeMaterials.Length; i++)
			{
				if (_themeMaterials[i].Hidden)
				{
					hiddenCount++;
				}
				else
				{
					visibleCount++;
				}
			}

			return hiddenCount > 0 ? visibleCount : _themeMaterials.Length;
		}
	}

	// 在编辑器激活 Craft 时排队整机预览重建。 / Queue a full craft preview rebuild when the Craft becomes active in the editor.
	private void OnEnable()
	{
	#if UNITY_EDITOR
		if (!Application.isPlaying)
		{
			PreviewMaterialFactory.ClearThemedMaterialCache();
			QueuePreviewRebuild();
		}
	#endif
	}

	// 在编辑器里修改 Craft 序列化字段时排队整机预览重建。 / Queue a full craft preview rebuild when serialized Craft fields change in the editor.
	private void OnValidate()
	{
	#if UNITY_EDITOR
		if (!Application.isPlaying)
		{
			if (!_isRebuildingPreviews)
			{
				QueuePreviewRebuild();
			}
		}
	#endif
	}

	// 导入飞机 XML，并在当前 Craft 下生成对应的子零件对象。 / Import an aircraft XML file and instantiate child part GameObjects under this craft.
	public void ImportFromXml(string xmlPath)
	{
	#if UNITY_EDITOR
		if (!Application.isPlaying)
		{
			ResetPreviewRebuildState();
		}
	#endif

		XDocument document = XmlUtil.LoadDocument(xmlPath);
		XElement aircraftElement = document.Root;
		if (aircraftElement == null || aircraftElement.Name.LocalName != "Aircraft")
		{
			throw new InvalidOperationException("The selected XML does not contain an Aircraft root node.");
		}

		_sourceXmlPath = xmlPath;
		_rawAircraftXml = aircraftElement.ToString();
		name = (string)aircraftElement.Attribute("name") ?? "Craft";
		_themeMaterials = ParseThemeMaterials(aircraftElement.Element("Theme"));
		PreviewMaterialFactory.ClearThemedMaterialCache();

		_suppressPreviewQueue = true;
		try
		{
			ClearChildren();

			XElement partsElement = aircraftElement.Element("Assembly")?.Element("Parts");
			if (partsElement == null)
			{
				return;
			}

			int orderIndex = 0;
			// 逐个生成零件预览对象，并把 XML 状态灌入组件。 / Instantiate each part preview object and hydrate it from XML state.
			foreach (XElement partElement in partsElement.Elements("Part"))
			{
				CreatePartFromXml(partElement, orderIndex++, _lockImportedPartsInScene);
			}

			ImportConnections(aircraftElement.Element("Assembly")?.Element("Connections"));
		}
		finally
		{
			_suppressPreviewQueue = false;
		}

	#if UNITY_EDITOR
		if (!Application.isPlaying)
		{
			QueuePreviewRebuild(0d, lightweight: false);
			return;
		}
	#endif
		RebuildAllPreviews();
	}

	// 把当前子零件层级导出回飞机 XML。 / Export the current child part hierarchy back into aircraft XML.
	public void ExportToXml(string xmlPath)
	{
		XDocument document = string.IsNullOrWhiteSpace(_rawAircraftXml)
			? new XDocument(new XElement("Aircraft", new XElement("Assembly", new XElement("Parts"))))
			: XDocument.Parse(_rawAircraftXml);

		XElement aircraftElement = document.Root ?? throw new InvalidOperationException("Aircraft root node is missing.");
		XElement assemblyElement = XmlUtil.GetOrCreateChild(aircraftElement, "Assembly");
		XElement partsElement = XmlUtil.GetOrCreateChild(assemblyElement, "Parts");
		partsElement.RemoveAll();

		foreach (Part part in GetExportParts())
		{
			partsElement.Add(part.ExportPartElement());
		}

		WriteConnections(assemblyElement);
		_lastExportPath = xmlPath;
		XmlUtil.SaveDocument(document, xmlPath);
	}

	// 重建所有零件预览，然后统一处理机身接缝法线平滑。 / Rebuild every part preview and then smooth fuselage seams across neighbors.
	public void RebuildAllPreviews(bool lightweight = true)
	{
	#if UNITY_EDITOR
		if (!Application.isPlaying)
		{
			QueuePreviewRebuild(0d, lightweight);
			return;
		}
	#endif
		if (_isRebuildingPreviews)
		{
			return;
		}

		try
		{
			_isRebuildingPreviews = true;
			_isLightweightPreviewRebuild = false;
			foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
			{
				part.RefreshPreview();
			}
			FuselagePart.ApplyNeighbourSmoothing(this);
		}
		finally
		{
			_isLightweightPreviewRebuild = false;
			_isRebuildingPreviews = false;
		}
	}

	public Part FindPartById(int partId)
	{
		if (partId <= 0)
		{
			return null;
		}

		if (_partIndexDirty)
		{
			RebuildPartIndex();
		}

		if (_partById.TryGetValue(partId, out Part part) && part != null)
		{
			return part;
		}

		_partIndexDirty = true;
		RebuildPartIndex();
		return _partById.TryGetValue(partId, out part) ? part : null;
	}

	public Part CreatePartFromXml(XElement partElement, int orderIndex, bool lockScenePicking = false)
	{
		if (partElement == null)
		{
			throw new ArgumentNullException(nameof(partElement));
		}

		int partId = XmlUtil.ParseInt((string)partElement.Attribute("id"), int.MinValue);
		ValidatePartIdAvailable(partId, ignoredPart: null);
		Type componentType = ResolvePartComponentType((string)partElement.Attribute("partType"), partElement);
		GameObject partObject = new GameObject();
		partObject.transform.SetParent(transform, false);
		Part part = (Part)partObject.AddComponent(componentType);
		part.InitializeFromXml(partElement, orderIndex);
		_partById[part.PartId] = part;
		_partIndexDirty = true;
		ApplyScenePickingLock(partObject, lockScenePicking);
		if (partElement.Element("Label.State") != null)
		{
			LabelState label = partObject.AddComponent<LabelState>();
			label.InitializeFromPartElement(partElement);
		}

		return part;
	}

	public void HandleInspectorDataChanged()
	{
		PreviewMaterialFactory.ClearThemedMaterialCache();
		RebuildAllPreviews();
	#if UNITY_EDITOR
		SceneView.RepaintAll();
	#endif
	}

	public Part ClonePart(Part source)
	{
		if (source == null)
		{
			throw new ArgumentNullException(nameof(source));
		}

		XElement cloneElement = source.ExportPartElement();
		cloneElement.SetAttributeValue("id", AllocatePartId());
		Vector3 clonePosition = XmlUtil.ParseVector3((string)cloneElement.Attribute("position"), source.transform.localPosition);
		cloneElement.SetAttributeValue("position", XmlUtil.FormatVector3(clonePosition + new Vector3(0.5f, 0f, 0f)));
		Part clone = CreatePartFromXml(cloneElement, AllocateOrderIndex());
		RebuildAllPreviews();
		return clone;
	}

	public bool TryGetThemeMaterial(int materialId, out CraftThemeMaterial material)
	{
		material = default;
		if (_themeMaterials == null || materialId < 0 || _themeMaterials.Length == 0)
		{
			return false;
		}

		bool hasHiddenMaterials = false;
		for (int i = 0; i < _themeMaterials.Length; i++)
		{
			hasHiddenMaterials |= _themeMaterials[i].Hidden;
		}

		if (!hasHiddenMaterials)
		{
			if (materialId >= _themeMaterials.Length)
			{
				return false;
			}

			material = _themeMaterials[materialId];
			return true;
		}

		int visibleIndex = 0;
		for (int i = 0; i < _themeMaterials.Length; i++)
		{
			if (_themeMaterials[i].Hidden)
			{
				continue;
			}

			if (visibleIndex == materialId)
			{
				material = _themeMaterials[i];
				return true;
			}

			visibleIndex++;
		}

		return false;
	}

	public int AllocateConnectionId()
	{
		int maxId = 0;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			foreach (PartConnectionEndpoint endpoint in part.ConnectionEndpoints)
			{
				maxId = Mathf.Max(maxId, endpoint.ConnectionId);
			}
		}
		return maxId + 1;
	}

	public int AllocatePartId()
	{
		int maxId = 0;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			maxId = Mathf.Max(maxId, part.PartId);
		}
		return maxId + 1;
	}

	public int AllocateOrderIndex()
	{
		int maxOrder = -1;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			maxOrder = Mathf.Max(maxOrder, part.OrderIndex);
		}
		return maxOrder + 1;
	}

	public void SynchronizeConnectionsFrom(Part source)
	{
		if (source == null)
		{
			return;
		}

		source.EnsureConnectionIds(AllocateConnectionId());
		HashSet<int> activeConnectionIds = source.ConnectionEndpoints
			.Where(endpoint => endpoint.ConnectionId > 0)
			.Select(endpoint => endpoint.ConnectionId)
			.ToHashSet();

		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (part == source)
			{
				continue;
			}

			part.RemoveStaleReciprocalConnections(source.PartId, activeConnectionIds);
		}

		foreach (PartConnectionEndpoint endpoint in source.ConnectionEndpoints)
		{
			Part target = FindPartById(endpoint.ConnectedPartId);
			if (target == null || target == source)
			{
				continue;
			}

			target.UpsertReciprocalConnection(endpoint, source.PartId);
		}

		RefreshConnectionDataFlag();
	}

	#if UNITY_EDITOR
	// 把多次编辑器刷新请求合并成一次延迟的整机重建。 / Coalesce multiple editor refresh requests into a single delayed craft rebuild.
	public void QueuePreviewRebuild(double delaySeconds = 0.12d)
	{
		QueuePreviewRebuild(delaySeconds, lightweight: true);
	}

	public void QueuePreviewRebuild(double delaySeconds, bool lightweight)
	{
		QueuePreviewRebuildInternal(delaySeconds, fullRebuild: true, lightweight);
	}

	public void QueuePreviewRebuildForPart(Part changedPart, double delaySeconds = 0.12d, bool lightweight = true)
	{
		if (changedPart == null)
		{
			QueuePreviewRebuild(delaySeconds);
			return;
		}

		QueuePreviewRebuildInternal(delaySeconds, fullRebuild: false, lightweight);
		QueueImpactedPreviewParts(changedPart);
	}

	public void RebuildPreviewForPart(Part changedPart, bool lightweight = true)
	{
		if (Application.isPlaying)
		{
			return;
		}

		if (changedPart == null)
		{
			RebuildAllPreviews();
			return;
		}

		if (_isRebuildingPreviews)
		{
			QueuePreviewRebuildForPart(changedPart, 0d, lightweight);
			return;
		}

		if (_incrementalPreviewRebuildActive)
		{
			CancelActivePreviewRebuild();
		}

		EditorApplication.update -= DelayedRebuildPreviews;
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		_previewRebuildQueued = false;
		_previewRebuildDispatchQueued = false;
		_queuedFullPreviewRebuild = false;
		_queuedPreviewRequiresFullQuality = !lightweight;
		_queuedPreviewPartInstanceIds.Clear();
		QueueImpactedPreviewParts(changedPart);
		BeginQueuedPreviewRebuild();
	}

	private void QueuePreviewRebuildInternal(double delaySeconds, bool fullRebuild, bool lightweight)
	{
		if (Application.isPlaying)
		{
			return;
		}

		if (_suppressPreviewQueue || _isRebuildingPreviews)
		{
			return;
		}

		if (_incrementalPreviewRebuildActive)
		{
			CancelActivePreviewRebuild();
		}

		_previewRebuildDueTime = EditorApplication.timeSinceStartup + Math.Max(0d, delaySeconds);
		if (fullRebuild)
		{
			_queuedFullPreviewRebuild = true;
			_queuedPreviewRequiresFullQuality = !lightweight;
			_queuedPreviewPartInstanceIds.Clear();
		}
		else if (!lightweight)
		{
			_queuedPreviewRequiresFullQuality = true;
		}
		if (_previewRebuildQueued)
		{
			return;
		}

		_previewRebuildQueued = true;
		EditorApplication.update += DelayedRebuildPreviews;
	}

	private void QueueImpactedPreviewParts(Part changedPart)
	{
		if (_queuedFullPreviewRebuild || changedPart == null)
		{
			return;
		}

		AddQueuedPreviewPart(changedPart);
		if (changedPart is FuselagePart)
		{
			foreach (PartConnectionEndpoint endpoint in changedPart.ConnectionEndpoints)
			{
				Part connectedPart = FindPartById(endpoint.ConnectedPartId);
				if (connectedPart is FuselagePart connectedFuselage)
				{
					AddQueuedPreviewPart(connectedFuselage);
				}
			}
			return;
		}

		if (changedPart is IFuselageCarver)
		{
			foreach (FuselagePart fuselage in GetComponentsInChildren<FuselagePart>(includeInactive: true))
			{
				if (changedPart.ExplicitlyTargetsPart(fuselage.PartId))
				{
					AddQueuedPreviewPart(fuselage);
				}
			}
		}
	}

	private void AddQueuedPreviewPart(Part part)
	{
		if (part != null)
		{
			_queuedPreviewPartInstanceIds.Add(part.GetInstanceID());
		}
	}

	// 在编辑器空闲时执行这次延迟重建。 / Run the queued rebuild once the editor is idle again.
	private void DelayedRebuildPreviews()
	{
		if (EditorApplication.timeSinceStartup < _previewRebuildDueTime)
		{
			return;
		}

		EditorApplication.update -= DelayedRebuildPreviews;
		if (_previewRebuildDispatchQueued)
		{
			return;
		}

		_previewRebuildDispatchQueued = true;
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		EditorApplication.delayCall += RunQueuedPreviewRebuild;
	}

	private void RunQueuedPreviewRebuild()
	{
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		_previewRebuildDispatchQueued = false;

		if (this == null || gameObject == null || Application.isPlaying)
		{
			FinishQueuedPreviewRebuild(repaint: false);
			return;
		}

		if (EditorApplication.timeSinceStartup < _previewRebuildDueTime)
		{
			EditorApplication.update -= DelayedRebuildPreviews;
			EditorApplication.update += DelayedRebuildPreviews;
			return;
		}

		BeginQueuedPreviewRebuild();
	}

	private void BeginQueuedPreviewRebuild()
	{
		_activePreviewRebuildParts = GetQueuedPreviewParts();

		_activePreviewRebuildIndex = 0;
		_activePreviewTouchedFuselage = false;
		_activePreviewFuselagePartIds.Clear();
		_activePreviewRebuildLightweight = !_queuedPreviewRequiresFullQuality;
		_incrementalPreviewRebuildActive = true;
		EditorApplication.update -= ContinueQueuedPreviewRebuild;
		EditorApplication.update += ContinueQueuedPreviewRebuild;
	}

	private void ContinueQueuedPreviewRebuild()
	{
		try
		{
			if (!_incrementalPreviewRebuildActive)
			{
				EditorApplication.update -= ContinueQueuedPreviewRebuild;
				return;
			}

			if(this == null || gameObject == null || Application.isPlaying)
			{
				using(new SampleProfiler("FuselagePart.ApplyNeighbourSmoothing"))
				{
                    FinishQueuedPreviewRebuild(repaint: false);
                }
                    
				return;
			}

			double startedAt = EditorApplication.timeSinceStartup;
			int processedCount = 0;
			_isRebuildingPreviews = true;
			_isLightweightPreviewRebuild = _activePreviewRebuildLightweight;
			try
			{
				while (_activePreviewRebuildIndex < _activePreviewRebuildParts.Length)
				{
					Part part = _activePreviewRebuildParts[_activePreviewRebuildIndex++];
					if (part == null)
					{
						continue;
					}

					if (part is FuselagePart fuselage)
					{
						_activePreviewTouchedFuselage = true;
						_activePreviewFuselagePartIds.Add(fuselage.PartId);

						using(new SampleProfiler("fuselage.RefreshPreview"))
						{
                            fuselage.RefreshPreview();
                        }
					}
					else
					{
						part.RefreshPreview();
					}

					processedCount++;
					if (processedCount >= PreviewRebuildMaxPartsPerFrame || EditorApplication.timeSinceStartup - startedAt >= PreviewRebuildFrameBudgetSeconds)
					{
						break;
					}
				}
			}
			finally
			{
				_isLightweightPreviewRebuild = false;
				_isRebuildingPreviews = false;
			}

			if (_activePreviewRebuildIndex < _activePreviewRebuildParts.Length)
			{
				return;
			}

			if (_activePreviewTouchedFuselage)
			{

                using(new SampleProfiler("FuselagePart.ApplyNeighbourSmoothing"))
				{
                    FuselagePart.ApplyNeighbourSmoothing(this, _activePreviewFuselagePartIds);
                }
			}

			bool shouldDelaySmoothing = _activePreviewTouchedFuselage;


            using(new SampleProfiler("FuselagePart.ApplyNeighbourSmoothing"))
            {
                FinishQueuedPreviewRebuild(repaint: true);
            }

			if (shouldDelaySmoothing)
			{
                using(new SampleProfiler("QueuePostRebuildSmoothing"))
				{
                    QueuePostRebuildSmoothing();
                }
			}
		}
		catch (Exception exception)
		{
			_isLightweightPreviewRebuild = false;
			_isRebuildingPreviews = false;
			FinishQueuedPreviewRebuild(repaint: false);
			Debug.LogException(exception, this);
		}
	}

	private void QueuePostRebuildSmoothing()
	{
		if (_postRebuildSmoothingQueued)
		{
			return;
		}

		_postRebuildSmoothingQueued = true;
		EditorApplication.delayCall -= ApplyPostRebuildSmoothing;
		EditorApplication.delayCall += ApplyPostRebuildSmoothing;
	}

	private void ApplyPostRebuildSmoothing()
	{
		EditorApplication.delayCall -= ApplyPostRebuildSmoothing;
		_postRebuildSmoothingQueued = false;
		if (this == null || gameObject == null || Application.isPlaying)
		{
			return;
		}

		FuselagePart.ApplyNeighbourSmoothing(this);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	private void FinishQueuedPreviewRebuild(bool repaint)
	{
		EditorApplication.update -= DelayedRebuildPreviews;
		EditorApplication.update -= ContinueQueuedPreviewRebuild;
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		_previewRebuildQueued = false;
		_previewRebuildDispatchQueued = false;
		_queuedFullPreviewRebuild = false;
		_queuedPreviewRequiresFullQuality = false;
		_queuedPreviewPartInstanceIds.Clear();
		_activePreviewRebuildParts = Array.Empty<Part>();
		_activePreviewRebuildIndex = 0;
		_activePreviewRebuildLightweight = false;
		_activePreviewTouchedFuselage = false;
		_activePreviewFuselagePartIds.Clear();
		_incrementalPreviewRebuildActive = false;
		if (repaint)
		{
			EditorApplication.QueuePlayerLoopUpdate();
			SceneView.RepaintAll();
		}
	}

	private void ResetPreviewRebuildState()
	{
		EditorApplication.update -= DelayedRebuildPreviews;
		EditorApplication.update -= ContinueQueuedPreviewRebuild;
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		EditorApplication.delayCall -= ApplyPostRebuildSmoothing;
		_previewRebuildQueued = false;
		_previewRebuildDispatchQueued = false;
		_queuedFullPreviewRebuild = false;
		_queuedPreviewRequiresFullQuality = false;
		_queuedPreviewPartInstanceIds.Clear();
		_activePreviewRebuildParts = Array.Empty<Part>();
		_activePreviewRebuildIndex = 0;
		_activePreviewRebuildLightweight = false;
		_activePreviewTouchedFuselage = false;
		_activePreviewFuselagePartIds.Clear();
		_incrementalPreviewRebuildActive = false;
		_postRebuildSmoothingQueued = false;
	}

	private Part[] GetQueuedPreviewParts()
	{
		Part[] parts = GetComponentsInChildren<Part>(includeInactive: true);
		if (_queuedFullPreviewRebuild || _queuedPreviewPartInstanceIds.Count == 0)
		{
			return parts;
		}

		return parts
			.Where(part => part != null && _queuedPreviewPartInstanceIds.Contains(part.GetInstanceID()))
			.ToArray();
	}

	private void CancelActivePreviewRebuild()
	{
		EditorApplication.update -= ContinueQueuedPreviewRebuild;
		_activePreviewRebuildParts = Array.Empty<Part>();
		_activePreviewRebuildIndex = 0;
		_activePreviewTouchedFuselage = false;
		_activePreviewFuselagePartIds.Clear();
		_incrementalPreviewRebuildActive = false;
	}
	#endif

	// 导入新飞机前先清空旧的预览子对象。 / Remove all existing preview children before importing a new aircraft.
	private void ClearChildren()
	{
		_partById.Clear();
		_partIndexDirty = true;
		foreach (Transform child in transform.Cast<Transform>().ToArray())
		{
			if (Application.isPlaying)
			{
				Destroy(child.gameObject);
			}
			else
			{
				DestroyImmediate(child.gameObject);
			}
		}
	}

	private void ImportConnections(XElement connectionsElement)
	{
		_hasConnectionData = false;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			part.ClearConnectionEndpoints();
		}

		if (connectionsElement == null)
		{
			return;
		}

		int connectionId = 1;
		foreach (XElement connectionElement in connectionsElement.Elements("Connection"))
		{
			int partAId = XmlUtil.ParseInt((string)connectionElement.Attribute("partA"), int.MinValue);
			int partBId = XmlUtil.ParseInt((string)connectionElement.Attribute("partB"), int.MinValue);
			if (partAId == int.MinValue || partBId == int.MinValue)
			{
				continue;
			}

			Part partA = FindPartById(partAId);
			Part partB = FindPartById(partBId);
			if (partA == null || partB == null)
			{
				connectionId++;
				continue;
			}

			int[] attachPointsA = ParseAttachPointList((string)connectionElement.Attribute("attachPointsA"));
			int[] attachPointsB = ParseAttachPointList((string)connectionElement.Attribute("attachPointsB"));
			int pairCount = Mathf.Max(attachPointsA.Length, attachPointsB.Length);
			if (pairCount == 0)
			{
				pairCount = 1;
			}

			for (int i = 0; i < pairCount; i++)
			{
				int attachPointA = attachPointsA.Length == 0 ? 0 : attachPointsA[Mathf.Min(i, attachPointsA.Length - 1)];
				int attachPointB = attachPointsB.Length == 0 ? 0 : attachPointsB[Mathf.Min(i, attachPointsB.Length - 1)];
				partA.AddConnectionEndpoint(connectionId, isPartAEndpoint: true, attachPointA, partBId, attachPointB);
				partB.AddConnectionEndpoint(connectionId, isPartAEndpoint: false, attachPointB, partAId, attachPointA);
				_hasConnectionData = true;
			}

			connectionId++;
		}
	}

	private void RefreshConnectionDataFlag()
	{
		_hasConnectionData = false;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (part.ConnectionEndpoints.Count <= 0)
			{
				continue;
			}

			_hasConnectionData = true;
			return;
		}
	}

	private void RebuildPartIndex()
	{
		_partById.Clear();
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (part == null || part.PartId <= 0)
			{
				continue;
			}

			if (_partById.TryGetValue(part.PartId, out Part existing) && existing != part)
			{
				throw new InvalidOperationException($"Duplicate Part id {part.PartId} found on '{existing.name}' and '{part.name}'. Part ids must be unique.");
			}

			_partById[part.PartId] = part;
		}
		_partIndexDirty = false;
	}

	private IEnumerable<Part> GetExportParts()
	{
		return GetComponentsInChildren<Part>(includeInactive: true)
			.Where(part => part != null && part.transform.IsChildOf(transform))
			.OrderBy(part => part.OrderIndex);
	}

	private void ValidatePartIdAvailable(int partId, Part ignoredPart)
	{
		if (partId <= 0)
		{
			throw new InvalidOperationException($"Part XML has an invalid id '{partId}'. Part ids must be positive and unique.");
		}

		foreach (Part existing in GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (existing == null || existing == ignoredPart || existing.PartId <= 0)
			{
				continue;
			}

			if (existing.PartId == partId)
			{
				throw new InvalidOperationException($"Duplicate Part id {partId} found while importing a Part. Existing object: '{existing.name}'. Part ids must be unique.");
			}
		}
	}

	private void WriteConnections(XElement assemblyElement)
	{
		XmlUtil.RemoveChildren(assemblyElement, "Connections");
		List<ExportConnection> exportConnections = CollectExportConnections();
		if (exportConnections.Count == 0)
		{
			return;
		}

		XElement connectionsElement = new XElement("Connections");
		foreach (ExportConnection connection in exportConnections.OrderBy(item => item.ConnectionId))
		{
			XElement element = new XElement("Connection");
			element.SetAttributeValue("partA", connection.PartAId);
			element.SetAttributeValue("partB", connection.PartBId);
			element.SetAttributeValue("attachPointsA", FormatAttachPointList(connection.AttachPointsA));
			element.SetAttributeValue("attachPointsB", FormatAttachPointList(connection.AttachPointsB));
			connectionsElement.Add(element);
		}
		assemblyElement.Add(connectionsElement);
	}

	private List<ExportConnection> CollectExportConnections()
	{
		Dictionary<int, ExportConnection> byId = new Dictionary<int, ExportConnection>();
		List<Part> parts = GetComponentsInChildren<Part>(includeInactive: true)
			.Where(item => item.transform.parent == transform)
			.OrderBy(item => item.OrderIndex)
			.ToList();

		foreach (Part part in parts)
		{
			foreach (PartConnectionEndpoint endpoint in part.ConnectionEndpoints)
			{
				if (endpoint.ConnectedPartId <= 0)
				{
					continue;
				}

				bool exportFromLocalEndpoint = endpoint.IsPartAEndpoint || !HasPrimaryEndpoint(parts, endpoint.ConnectionId);
				if (!exportFromLocalEndpoint)
				{
					continue;
				}

				int key = endpoint.ConnectionId > 0 ? endpoint.ConnectionId : AllocateConnectionId();
				if (!byId.TryGetValue(key, out ExportConnection connection))
				{
					connection = endpoint.IsPartAEndpoint || endpoint.ConnectionId <= 0
						? new ExportConnection(key, part.PartId, endpoint.ConnectedPartId)
						: new ExportConnection(key, endpoint.ConnectedPartId, part.PartId);
					byId.Add(key, connection);
				}

				if (connection.PartAId == part.PartId)
				{
					connection.AttachPointsA.Add(endpoint.LocalAttachPointId);
					connection.AttachPointsB.Add(endpoint.ConnectedAttachPointId);
				}
				else
				{
					connection.AttachPointsA.Add(endpoint.ConnectedAttachPointId);
					connection.AttachPointsB.Add(endpoint.LocalAttachPointId);
				}
			}
		}

		return byId.Values
			.Where(connection => connection.PartAId > 0 && connection.PartBId > 0 && connection.AttachPointsA.Count > 0 && connection.AttachPointsB.Count > 0)
			.ToList();
	}

	private static bool HasPrimaryEndpoint(IEnumerable<Part> parts, int connectionId)
	{
		if (connectionId <= 0)
		{
			return false;
		}

		return parts.SelectMany(part => part.ConnectionEndpoints).Any(endpoint => endpoint.ConnectionId == connectionId && endpoint.IsPartAEndpoint);
	}

	private static int[] ParseAttachPointList(string csv)
	{
		if (string.IsNullOrWhiteSpace(csv))
		{
			return Array.Empty<int>();
		}

		return csv.Split(',')
			.Where(item => !string.IsNullOrWhiteSpace(item))
			.Select(item => XmlUtil.ParseInt(item.Trim(), int.MinValue))
			.Where(item => item != int.MinValue)
			.ToArray();
	}

	private static string FormatAttachPointList(IReadOnlyList<int> attachPoints)
	{
		return attachPoints == null || attachPoints.Count == 0 ? "0" : string.Join(",", attachPoints);
	}

	private static void ApplyScenePickingLock(GameObject partObject, bool locked)
	{
	#if UNITY_EDITOR
		if (partObject == null)
		{
			return;
		}

		if (locked)
		{
			SceneVisibilityManager.instance.DisablePicking(partObject, true);
		}
		else
		{
			SceneVisibilityManager.instance.EnablePicking(partObject, true);
		}
	#endif
	}

	private static CraftThemeMaterial[] ParseThemeMaterials(XElement themeElement)
	{
		if (themeElement == null)
		{
			return Array.Empty<CraftThemeMaterial>();
		}

		return themeElement.Elements("Material")
			.Select(element => new CraftThemeMaterial
			{
				Style = (string)element.Attribute("style") ?? string.Empty,
				Name = (string)element.Attribute("name") ?? string.Empty,
				Color = ParseThemeColor((string)element.Attribute("color"), Color.white),
				Smoothness = XmlUtil.ParseFloat((string)element.Attribute("s"), 0.08f),
				Metallic = XmlUtil.ParseFloat((string)element.Attribute("m"), 0f),
				EmissionDensity = XmlUtil.ParseFloat((string)element.Attribute("ed"), 0f),
				Emission = XmlUtil.ParseFloat((string)element.Attribute("en"), 0f),
				Hidden = XmlUtil.ParseBool((string)element.Attribute("hidden"))
			})
			.ToArray();
	}

	private static Color ParseThemeColor(string hex, Color fallback)
	{
		if (string.IsNullOrWhiteSpace(hex))
		{
			return fallback;
		}

		string value = hex.Trim().TrimStart('#');
		if (value.Length != 6 && value.Length != 8)
		{
			return fallback;
		}

		if (!uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint parsed))
		{
			return fallback;
		}

		float r;
		float g;
		float b;
		float a;
		if (value.Length == 8)
		{
			r = ((parsed >> 24) & 0xFF) / 255f;
			g = ((parsed >> 16) & 0xFF) / 255f;
			b = ((parsed >> 8) & 0xFF) / 255f;
			a = (parsed & 0xFF) / 255f;
		}
		else
		{
			r = ((parsed >> 16) & 0xFF) / 255f;
			g = ((parsed >> 8) & 0xFF) / 255f;
			b = (parsed & 0xFF) / 255f;
			a = 1f;
		}

		return new Color(r, g, b, a);
	}

	private sealed class ExportConnection
	{
		public ExportConnection(int connectionId, int partAId, int partBId)
		{
			ConnectionId = connectionId;
			PartAId = partAId;
			PartBId = partBId;
			AttachPointsA = new List<int>();
			AttachPointsB = new List<int>();
		}

		public int ConnectionId { get; }

		public int PartAId { get; }

		public int PartBId { get; }

		public List<int> AttachPointsA { get; }

		public List<int> AttachPointsB { get; }
	}

	// 根据 XML 节点判断应该挂哪种预览组件。 / Pick the preview component type that should own a given part XML node.
	private static Type ResolvePartComponentType(string partType, XElement partElement)
	{
		if (!string.IsNullOrWhiteSpace(partType) && (partType.StartsWith("JFuselage", StringComparison.OrdinalIgnoreCase) || partType.StartsWith("Fuselage", StringComparison.OrdinalIgnoreCase)))
		{
			return typeof(FuselagePart);
		}
		if (!string.IsNullOrWhiteSpace(partType) && partType.StartsWith("ModifierWindow", StringComparison.OrdinalIgnoreCase))
		{
			return typeof(WindowPart);
		}
		if (!string.IsNullOrWhiteSpace(partType) && partType.StartsWith("ModifierBay", StringComparison.OrdinalIgnoreCase))
		{
			return typeof(BayPart);
		}
		if (partElement.Element("JFuselage.State") != null || partElement.Element("Fuselage.State") != null)
		{
			return typeof(FuselagePart);
		}
		return typeof(OtherPart);
	}
}
