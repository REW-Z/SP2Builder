using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using UnityEditor;


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

    private static bool _editorUpdateRegistered;

	private static readonly Dictionary<int, Craft> _editorUpdateCrafts = new Dictionary<int, Craft>();

	private static readonly List<Action> _editorUpdateCallbacks = new List<Action>();

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

	private bool _delayedRebuildPending;

	private bool _continueQueuedPreviewRebuildPending;

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
      RegisterForEditorUpdate();
		PreviewMaterialFactory.ClearThemedMaterialCache();
		QueuePreviewRebuild();
	}

	// 当 Craft 被禁用时，从共享编辑器 update 循环中移除自己。 / Remove this craft from the shared editor update loop when it is disabled.
	private void OnDisable()
	{
		UnregisterFromEditorUpdate();
	}

	// 在编辑器里修改 Craft 序列化字段时排队整机预览重建。 / Queue a full craft preview rebuild when serialized Craft fields change in the editor.
	private void OnValidate()
	{
		if (!_isRebuildingPreviews)
		{
			QueuePreviewRebuild();
		}
	}

	// 导入飞机 XML，并在当前 Craft 下生成对应的子零件对象。 / Import an aircraft XML file and instantiate child part GameObjects under this craft.
	public void ImportFromXml(string xmlPath)
	{
		ResetPreviewRebuildState();

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

		QueuePreviewRebuild(0d, lightweight: false);
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
		QueuePreviewRebuild(0d, lightweight);
	}

	// 按 PartId 查找零件，必要时延迟重建查找索引。 / Find a part by PartId, rebuilding the lookup index lazily when needed.
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

	// 从一个 Part XML 节点创建对应的预览对象并挂载正确组件。 / Create the preview object and attach the correct component for a part XML node.
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

	// 在 Inspector 改动 Craft 级数据后清理材质缓存并整体重建预览。 / Clear material caches and rebuild all previews after craft-level inspector edits.
	public void HandleInspectorDataChanged()
	{
		PreviewMaterialFactory.ClearThemedMaterialCache();
		RebuildAllPreviews();
		SceneView.RepaintAll();
	}

	// 通过 XML 往返复制一个零件，并为副本分配新 id 和轻微偏移。 / Clone a part by round-tripping through XML, then assign a new id and slight offset.
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

	// 按可见材质槽索引解析一个 Theme 材质。 / Resolve one theme material by the visible material-slot index.
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

	// 计算当前 Craft 中下一个可用的连接 id。 / Compute the next available connection id inside the current craft.
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

	// 计算当前 Craft 中下一个可用的零件 id。 / Compute the next available part id inside the current craft.
	public int AllocatePartId()
	{
		int maxId = 0;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			maxId = Mathf.Max(maxId, part.PartId);
		}
		return maxId + 1;
	}

	// 计算导出顺序使用的下一个 orderIndex。 / Compute the next orderIndex used for export ordering.
	public int AllocateOrderIndex()
	{
		int maxOrder = -1;
		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			maxOrder = Mathf.Max(maxOrder, part.OrderIndex);
		}
		return maxOrder + 1;
	}

	// 把一个零件的连接端点同步成整个 Craft 中的双向连接图。 / Synchronize one part's endpoints into the craft-wide reciprocal connection graph.
	public void SynchronizeConnectionsFrom(Part source)
	{
		if (source == null)
		{
			return;
		}

		source.EnsureConnectionIds(AllocateConnectionId());

		foreach (Part part in GetComponentsInChildren<Part>(includeInactive: true))
		{
			if (part == source)
			{
				continue;
			}

			List<PartConnectionEndpoint> expectedReciprocals = source.ConnectionEndpoints
				.Where(endpoint => endpoint.ConnectionId > 0 && endpoint.ConnectedPartId == part.PartId)
				.Select(endpoint => endpoint.CreateReciprocal(source.PartId))
				.ToList();
			part.RemoveStaleReciprocalConnections(source.PartId, expectedReciprocals);
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

	// 把多次编辑器刷新请求合并成一次延迟的整机重建。 / Coalesce multiple editor refresh requests into a single delayed craft rebuild.
	public void QueuePreviewRebuild(double delaySeconds = 0.12d)
	{
		QueuePreviewRebuild(delaySeconds, lightweight: true);
	}

	// 以指定延迟和质量模式排队整机预览重建。 / Queue a full-craft preview rebuild with the requested delay and quality mode.
	public void QueuePreviewRebuild(double delaySeconds, bool lightweight)
	{
		QueuePreviewRebuildInternal(delaySeconds, fullRebuild: true, lightweight);
	}

	// 为一个零件及其受影响邻居排队预览重建。 / Queue a preview rebuild for one part and any impacted neighbors.
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

	// 立即启动与某个零件相关的预览重建，必要时中断当前增量队列。 / Start an immediate preview rebuild for one part, canceling the current incremental queue if needed.
	public void RebuildPreviewForPart(Part changedPart, bool lightweight = true)
	{
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

      ResetQueuedPreviewRequest();
		_queuedPreviewRequiresFullQuality = !lightweight;
		QueueImpactedPreviewParts(changedPart);
		BeginQueuedPreviewRebuild();
	}

	// 记录下一次延迟重建的范围、质量和触发时间。 / Record the scope, quality, and due time for the next delayed preview rebuild.
	private void QueuePreviewRebuildInternal(double delaySeconds, bool fullRebuild, bool lightweight)
	{
		if (_suppressPreviewQueue)
		{
			return;
		}

		if (_incrementalPreviewRebuildActive && !_isRebuildingPreviews)
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
		SetDelayedRebuildPending(true);
	}

	// 把连接零件或显式目标机身一并加入待重建集合。 / Add connected parts or explicitly targeted fuselages to the queued rebuild set.
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

	// 把一个零件实例 id 记入当前排队请求。 / Record one part instance id in the current queued request.
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

      SetDelayedRebuildPending(false);
		if (_previewRebuildDispatchQueued)
		{
			return;
		}

		_previewRebuildDispatchQueued = true;
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		EditorApplication.delayCall += RunQueuedPreviewRebuild;
	}

	// 在 delayCall 回调里真正切入分帧预览重建。 / Enter the incremental preview rebuild from the editor delay-call callback.
	private void RunQueuedPreviewRebuild()
	{
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		_previewRebuildDispatchQueued = false;

		if (this == null || gameObject == null)
		{
			FinishQueuedPreviewRebuild(repaint: false);
			return;
		}

        if (EditorApplication.timeSinceStartup < _previewRebuildDueTime)
		{
			SetDelayedRebuildPending(true);
			return;
		}

		BeginQueuedPreviewRebuild();
	}

	// 消费当前排队请求，并冻结成一轮活动的分帧重建状态。 / Consume the queued request and freeze it into one active incremental rebuild pass.
	private void BeginQueuedPreviewRebuild()
	{
		Part[] queuedParts = GetQueuedPreviewParts();
		bool activeLightweight = !_queuedPreviewRequiresFullQuality;
		ResetQueuedPreviewRequest();
		_activePreviewRebuildParts = queuedParts;
		ResetActivePreviewProgress();
		_activePreviewRebuildLightweight = activeLightweight;
		_incrementalPreviewRebuildActive = true;
     SetContinueQueuedPreviewRebuildPending(true);
	}

	// 在共享 editor update 里按预算推进当前分帧预览重建。 / Advance the active incremental preview rebuild within the shared editor update budget.
	private void ContinueQueuedPreviewRebuild()
	{
		try
		{
			if (!_incrementalPreviewRebuildActive)
			{
              SetContinueQueuedPreviewRebuildPending(false);
				return;
			}

			if (this == null || gameObject == null)
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

	// 把接缝平滑延迟到重建结束后的下一次编辑器回调。 / Delay seam smoothing until the next editor callback after the rebuild completes.
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

	// 执行重建后的机身接缝平滑，并刷新场景视图。 / Apply post-rebuild fuselage seam smoothing and repaint the scene.
	private void ApplyPostRebuildSmoothing()
	{
		EditorApplication.delayCall -= ApplyPostRebuildSmoothing;
		_postRebuildSmoothingQueued = false;
		if (this == null || gameObject == null)
		{
			return;
		}

		FuselagePart.ApplyNeighbourSmoothing(this);
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	// 结束当前活动重建，并按需刷新场景。 / Finish the current active rebuild and repaint if requested.
	private void FinishQueuedPreviewRebuild(bool repaint)
	{
		ResetActivePreviewState();
		if (repaint)
		{
          RepaintScene();
		}
	}

	// 一次性清空排队状态、活动状态和挂起的延迟回调。 / Clear queued state, active state, and all pending delayed callbacks in one place.
	private void ResetPreviewRebuildState()
	{
      ResetQueuedPreviewRequest();
		ResetActivePreviewState();
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		EditorApplication.delayCall -= ApplyPostRebuildSmoothing;
		_postRebuildSmoothingQueued = false;
	}

	// 根据当前排队模式返回全量或局部受影响的零件集合。 / Return either all parts or just the impacted subset for the queued rebuild.
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

	// 取消当前正在进行的分帧重建。 / Cancel the currently active incremental rebuild pass.
	private void CancelActivePreviewRebuild()
	{
      ResetActivePreviewState();
	}

	// 清空本次排队请求的状态，让下一轮重建重新开始排队。 / Clear the queued preview request state so the next rebuild can start from a clean request.
	private void ResetQueuedPreviewRequest()
	{
		SetDelayedRebuildPending(false);
		EditorApplication.delayCall -= RunQueuedPreviewRebuild;
		_previewRebuildQueued = false;
		_previewRebuildDispatchQueued = false;
		_queuedFullPreviewRebuild = false;
		_queuedPreviewRequiresFullQuality = false;
		_queuedPreviewPartInstanceIds.Clear();
	}

	// 清空当前分帧重建的执行进度。 / Clear the active incremental rebuild progress for the current craft.
	private void ResetActivePreviewState()
	{
		SetContinueQueuedPreviewRebuildPending(false);
		ResetActivePreviewProgress();
		_activePreviewRebuildParts = Array.Empty<Part>();
		_activePreviewRebuildLightweight = false;
		_incrementalPreviewRebuildActive = false;
	}

	// 重置当前分帧重建游标和受影响机身集合。 / Reset the incremental rebuild cursor and touched fuselage tracking.
	private void ResetActivePreviewProgress()
	{
		_activePreviewRebuildIndex = 0;
		_activePreviewTouchedFuselage = false;
		_activePreviewFuselagePartIds.Clear();
	}

	// 统一刷新编辑器场景视图和玩家循环。 / Refresh the editor scene view and player loop in one place.
	private static void RepaintScene()
	{
		EditorApplication.QueuePlayerLoopUpdate();
		SceneView.RepaintAll();
	}

	// 统一的编辑器 update 入口；项目内只向 EditorApplication.update 注册这一处。 / Central editor update entry point so the whole project registers to EditorApplication.update only once.
	public static void EditorUpdate()
	{
		for (int i = _editorUpdateCallbacks.Count - 1; i >= 0; i--)
		{
			Action callback = _editorUpdateCallbacks[i];
			callback?.Invoke();
		}

		foreach (Craft craft in _editorUpdateCrafts.Values.ToArray())
		{
			if (craft == null || craft.gameObject == null)
			{
				continue;
			}

			craft.EditorTick();
		}
	}

	// 注册一个通用编辑器 update 回调，供非 Craft 系统复用统一入口。 / Register a generic editor update callback so non-Craft systems can share the same single update hook.
	public static void RegisterEditorUpdate(Action callback)
	{
		if (callback == null)
		{
			return;
		}

		EnsureEditorUpdateRegistration();
		if (!_editorUpdateCallbacks.Contains(callback))
		{
			_editorUpdateCallbacks.Add(callback);
		}
	}

	// 注销通用编辑器 update 回调。 / Unregister a generic editor update callback from the shared update loop.
	public static void UnregisterEditorUpdate(Action callback)
	{
		if (callback == null)
		{
			return;
		}

		_editorUpdateCallbacks.Remove(callback);
	}

	// 由统一编辑器循环驱动本 Craft 的预览调度。 / Drive this craft's preview scheduling from the shared editor update loop.
	private void EditorTick()
	{
		if (_delayedRebuildPending)
		{
			DelayedRebuildPreviews();
		}

		if (_continueQueuedPreviewRebuildPending)
		{
			ContinueQueuedPreviewRebuild();
		}
	}

	// 把当前 Craft 注册到共享编辑器 update 集合。 / Register this craft with the shared editor update set.
	private void RegisterForEditorUpdate()
	{
		EnsureEditorUpdateRegistration();
		_editorUpdateCrafts[GetInstanceID()] = this;
	}

	// 从共享编辑器 update 集合移除当前 Craft。 / Remove this craft from the shared editor update set.
	private void UnregisterFromEditorUpdate()
	{
		_editorUpdateCrafts.Remove(GetInstanceID());
	}

	// 确保全项目只向 EditorApplication.update 注册一次。 / Ensure the project binds to EditorApplication.update exactly once.
	private static void EnsureEditorUpdateRegistration()
	{
		if (_editorUpdateRegistered)
		{
			return;
		}

		EditorApplication.update += EditorUpdate;
		_editorUpdateRegistered = true;
	}

   // 设置延迟重建轮询是否应在共享 update 中继续执行。 / Set whether delayed rebuild polling should continue inside the shared editor update loop.
	private void SetDelayedRebuildPending(bool pending)
	{
      _delayedRebuildPending = pending;
	}

   // 设置分帧重建是否应在共享 update 中继续执行。 / Set whether incremental preview rebuild should continue inside the shared editor update loop.
	private void SetContinueQueuedPreviewRebuildPending(bool pending)
	{
     _continueQueuedPreviewRebuildPending = pending;
	}

	// 导入新飞机前先清空旧的预览子对象。 / Remove all existing preview children before importing a new aircraft.
	private void ClearChildren()
	{
		_partById.Clear();
		_partIndexDirty = true;
		foreach (Transform child in transform.Cast<Transform>().ToArray())
		{
			DestroyImmediate(child.gameObject);
		}
	}

	// 从 Assembly/Connections XML 中重建零件间的双向连接关系。 / Rebuild reciprocal part connections from the Assembly/Connections XML.
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

	// 根据当前所有端点重新计算是否存在连接数据。 / Recompute whether the craft currently contains any connection data.
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

	// 重建 PartId 到 Part 实例的查找索引。 / Rebuild the lookup index from PartId to Part instance.
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

	// 返回导出时应写出的直属零件，并按顺序排序。 / Return the direct child parts that should be exported, sorted by order.
	private IEnumerable<Part> GetExportParts()
	{
		return GetComponentsInChildren<Part>(includeInactive: true)
			.Where(part => part != null && part.transform.IsChildOf(transform))
			.OrderBy(part => part.OrderIndex);
	}

	// 确保即将导入或创建的 PartId 在当前 Craft 中唯一有效。 / Ensure an imported or created PartId is valid and unique inside the current craft.
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

	// 把当前连接图写回导出的 Assembly/Connections 节点。 / Write the current connection graph back into the exported Assembly/Connections node.
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

	// 从双向端点集合归并出导出用的唯一连接记录。 / Collapse reciprocal endpoint data into unique export connection records.
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

	// 判断一个连接 id 是否已经存在主端点。 / Check whether a connection id already has a primary endpoint.
	private static bool HasPrimaryEndpoint(IEnumerable<Part> parts, int connectionId)
	{
		if (connectionId <= 0)
		{
			return false;
		}

		return parts.SelectMany(part => part.ConnectionEndpoints).Any(endpoint => endpoint.ConnectionId == connectionId && endpoint.IsPartAEndpoint);
	}

	// 解析连接 XML 中的 attach-point 列表。 / Parse an attach-point list from the connection XML.
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

	// 把 attach-point 列表格式化为连接 XML 属性字符串。 / Format an attach-point list into the connection XML attribute format.
	private static string FormatAttachPointList(IReadOnlyList<int> attachPoints)
	{
		return attachPoints == null || attachPoints.Count == 0 ? "0" : string.Join(",", attachPoints);
	}

	// 按导入选项启用或禁用零件在场景里的可拾取状态。 / Enable or disable scene picking for an imported part based on the import option.
	private static void ApplyScenePickingLock(GameObject partObject, bool locked)
	{
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
	}

	// 从 Theme XML 节点解析材质主题数组。 / Parse the material theme array from the Theme XML node.
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

	// 把 Theme XML 里的十六进制颜色文本解析成 Unity Color。 / Parse a Theme XML hex color string into a Unity Color.
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
		// 创建一条导出连接记录并初始化双方 attach-point 列表。 / Create one export connection record and initialize both attach-point lists.
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
