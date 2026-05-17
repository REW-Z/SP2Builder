using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using UnityEditor;


public interface IPartXmlExtension
{
	// 从所属 Part 的 XML 中读取扩展状态。 / Read extension-specific state from the owning part XML.
	void InitializeFromPartElement(XElement partElement);

	// 把扩展状态写回所属 Part 的 XML。 / Write extension-specific state back into the owning part XML.
	void WriteToPartElement(XElement partElement);

	// 刷新扩展自己负责的编辑器预览。 / Refresh the editor preview owned by the extension.
	void RefreshPreview();
}

[Serializable]
public class PartConnectionEndpoint
{
	public int ConnectionId;

	public bool IsPartAEndpoint;

	public int LocalAttachPointId;

	public int ConnectedPartId;

	public int ConnectedAttachPointId;

	// 供 Unity 序列化反射使用的空构造函数。 / Parameterless constructor used by Unity serialization.
	public PartConnectionEndpoint()
	{
	}

	// 用一组显式端点值创建连接记录。 / Create a connection record from an explicit set of endpoint values.
	public PartConnectionEndpoint(int connectionId, bool isPartAEndpoint, int localAttachPointId, int connectedPartId, int connectedAttachPointId)
	{
		ConnectionId = connectionId;
		IsPartAEndpoint = isPartAEndpoint;
		LocalAttachPointId = localAttachPointId;
		ConnectedPartId = connectedPartId;
		ConnectedAttachPointId = connectedAttachPointId;
	}

	// 生成对端零件视角下的 reciprocal 连接记录。 / Create the reciprocal connection record from the connected part's perspective.
	public PartConnectionEndpoint CreateReciprocal(int sourcePartId)
	{
		return new PartConnectionEndpoint(ConnectionId, !IsPartAEndpoint, ConnectedAttachPointId, sourcePartId, LocalAttachPointId);
	}
}

[ExecuteAlways]
public abstract class Part : MonoBehaviour
{
	[SerializeField]
	private int _partId;

	[SerializeField]
	private string _partType;

	[SerializeField, TextArea(4, 16)]
	private string _rawPartXml;

	[SerializeField]
	private int _orderIndex;

	[SerializeField]
	private string _targetMode;

	[SerializeField]
	private int[] _targetPartIds = Array.Empty<int>();

	[SerializeField]
	private string _targetPartIdsAttributeName = "partIds";

	[SerializeField]
	private int[] _materialIds = Array.Empty<int>();

	[SerializeField]
	private string _materialsText = string.Empty;

	[SerializeField]
	private List<PartConnectionEndpoint> _connectionEndpoints = new List<PartConnectionEndpoint>();

	[SerializeField]
	private bool _stateXmlDirty;

	private bool _isRefreshingPreview;

	private bool _previewRefreshQueued;

	private double _previewRefreshDueTime;

	public int OrderIndex => _orderIndex;

	public int PartId => _partId;

	public string PartType => _partType;

	public int MaterialSlotCount => _materialIds?.Length ?? 0;

	public bool HasExplicitTargets => _targetPartIds != null && _targetPartIds.Length > 0;

	public IReadOnlyList<int> MaterialIds => _materialIds ?? Array.Empty<int>();

	public string MaterialsText => _materialsText ?? string.Empty;

	public IReadOnlyList<PartConnectionEndpoint> ConnectionEndpoints => _connectionEndpoints;

	protected virtual double PreviewRefreshDelaySeconds => 0d;

	public int PrimaryMaterialId
	{
		get
		{
			if (_materialIds == null)
			{
				return -1;
			}

			for (int i = 0; i < _materialIds.Length; i++)
			{
				if (_materialIds[i] >= 0)
				{
					return _materialIds[i];
				}
			}

			return -1;
		}
	}

	// 先装载 Part 通用元数据，再把具体状态交给子类处理。 / Populate common part metadata and then hand the remainder to the concrete subclass.
	public virtual void InitializeFromXml(XElement partElement, int orderIndex)
	{
		_orderIndex = orderIndex;
		_partId = XmlUtil.ParseInt((string)partElement.Attribute("id"));
		_partType = (string)partElement.Attribute("partType") ?? string.Empty;
		_rawPartXml = partElement.ToString(SaveOptions.DisableFormatting);
		transform.localPosition = XmlUtil.ParseVector3((string)partElement.Attribute("position"), Vector3.zero);
		transform.localEulerAngles = XmlUtil.ParseVector3((string)partElement.Attribute("rotation"), Vector3.zero);
		transform.localScale = XmlUtil.ParseVector3((string)partElement.Attribute("scale"), Vector3.one);
		_materialsText = (string)partElement.Attribute("materials") ?? string.Empty;
		_materialIds = ParseIntegerCsv(_materialsText);
		_connectionEndpoints.Clear();
		LoadTargetingState(partElement);
		_stateXmlDirty = false;
		ApplyObjectName();
		LoadPartState(partElement);
	}

	// 导出当前 Part 的变换、状态以及扩展数据。 / Export the current part transform, state, and extension data into XML.
	public virtual XElement ExportPartElement()
	{
		bool hasRawPartXml = !string.IsNullOrWhiteSpace(_rawPartXml);
		XElement partElement = hasRawPartXml ? XElement.Parse(_rawPartXml) : new XElement("Part");
		bool commonChanged = !hasRawPartXml || HasCommonXmlChanges(partElement);
		bool shouldWriteState = !hasRawPartXml || _stateXmlDirty;
		if (!commonChanged && !shouldWriteState)
		{
			return partElement;
		}

		if (commonChanged)
		{
			WriteCommonPartAttributes(partElement, writeAll: !hasRawPartXml);
		}

		if (shouldWriteState)
		{
			WritePartState(partElement);
			WriteTargetingState(partElement);
			if (!hasRawPartXml)
			{
				foreach (IPartXmlExtension extension in GetComponents<MonoBehaviour>().OfType<IPartXmlExtension>())
				{
					extension.WriteToPartElement(partElement);
				}
			}
		}
		return partElement;
	}

	// 先刷新扩展预览，再让具体 Part 刷新自己的网格。 / Refresh extension previews before the concrete part rebuilds its own mesh.
	public virtual void RefreshPreview()
	{
		foreach (IPartXmlExtension extension in GetComponents<MonoBehaviour>().OfType<IPartXmlExtension>())
		{
			extension.RefreshPreview();
		}
	}

	// 让子类从 Part XML 中读取自己的状态块。 / Let subclasses read their state payload from the part XML.
	protected virtual void LoadPartState(XElement partElement)
	{
	}

	// 让子类把自己的状态块写回 Part XML。 / Let subclasses write their state payload into the part XML.
	protected virtual void WritePartState(XElement partElement)
	{
	}

	// 在写入新版本状态前移除旧状态节点。 / Remove one or more state elements before rewriting the current version.
	protected void RemoveStateElements(XElement partElement, params string[] stateNames)
	{
		foreach (string stateName in stateNames.Where(name => !string.IsNullOrWhiteSpace(name)))
		{
			XmlUtil.RemoveChildren(partElement, stateName);
		}
	}

	// 销毁预览专用的临时对象，不影响导入来的场景资源。 / Destroy preview-owned transient objects without touching imported scene assets.
	protected static void DestroyOwnedObject(UnityEngine.Object obj)
	{
		if (obj == null)
		{
			return;
		}

		DestroyImmediate(obj);
	}

	// 在编辑器字段变更时同步对象名和预览。 / Keep editor object names and previews synchronized with serialized field edits.
	protected virtual void OnValidate()
	{
		ApplyObjectName();
		QueuePreviewRefresh();
	}

	// 当 Part 在编辑器里激活时排队一次预览重建。 / Queue a preview rebuild whenever the part becomes active in the editor.
	protected virtual void OnEnable()
	{
		QueuePreviewRefresh();
	}

	// 当 Part 被禁用时取消挂起的编辑器预览回调。 / Cancel any pending editor preview callback when the part is disabled.
	protected virtual void OnDisable()
	{
		Craft.UnregisterEditorUpdate(DelayedRefreshPreview);
		_previewRefreshQueued = false;
	}

	// 延迟解析预览所需组件，缺失时自动补齐。 / Lazily resolve required preview components, creating them if necessary.
	protected bool EnsureComponent<T>(ref T component) where T : Component
	{
		if (component == null)
		{
			component = GetComponent<T>();
		}

		if (component == null)
		{
			component = gameObject.AddComponent<T>();
		}

		return component != null;
	}

	// 向上查找拥有当前 Part 预览的 Craft。 / Walk up the hierarchy to find the craft that owns this part preview.
	protected Craft GetOwningCraft()
	{
		return GetComponentInParent<Craft>();
	}

	// 当单零件刷新会影响邻接关系时，升级成整机重建。 / Escalate single-part refreshes into whole-craft rebuilds when seam interactions matter.
	protected bool RequestCraftPreviewRebuild()
	{
		Craft craft = GetOwningCraft();
		if (!craft.IsRebuildingPreviews)
		{
			craft.RebuildAllPreviews();
			return true;
		}

		return false;
	}

	// 判断当前 Part 是否显式指定了某个目标零件。 / Check whether this part explicitly targets another part by id.
	public bool ExplicitlyTargetsPart(int partId)
	{
		return _targetPartIds != null && Array.IndexOf(_targetPartIds, partId) >= 0;
	}

	// 清空当前零件记录的所有连接端点。 / Clear all connection endpoints recorded on this part.
	public void ClearConnectionEndpoints()
	{
		_connectionEndpoints.Clear();
	}

	// 追加一个连接端点记录。 / Append one connection endpoint record.
	public void AddConnectionEndpoint(int connectionId, bool isPartAEndpoint, int localAttachPointId, int connectedPartId, int connectedAttachPointId)
	{
		_connectionEndpoints.Add(new PartConnectionEndpoint(connectionId, isPartAEndpoint, localAttachPointId, connectedPartId, connectedAttachPointId));
	}

	// 更新 materials 文本并重新解析材质 id 列表。 / Update the materials text and reparse the material id list.
	public void SetMaterialsText(string materialsText)
	{
		_materialsText = materialsText ?? string.Empty;
		_materialIds = ParseIntegerCsv(_materialsText);
	}

	// 标记状态 XML 在下次导出时需要重写。 / Mark the state XML as needing a rewrite on the next export.
	public void MarkStateXmlDirty()
	{
		_stateXmlDirty = true;
	}

	// 更新 Part 的逻辑类型字符串，供导出 XML 和编辑器名称复用。 / Update the part type string used by XML export and editor naming.
	protected void SetPartType(string partType)
	{
		_partType = partType ?? string.Empty;
	}

	// 按索引删除一个连接端点。 / Remove one connection endpoint by index.
	public void RemoveConnectionEndpointAt(int index)
	{
		if (index < 0 || index >= _connectionEndpoints.Count)
		{
			return;
		}

		_connectionEndpoints.RemoveAt(index);
	}

	// 删除占用指定本地 attach point 的所有连接端点。 / Remove all connection endpoints that occupy the specified local attach point.
	public void RemoveConnectionEndpointsForLocalAttachPoint(int localAttachPointId)
	{
		for (int i = _connectionEndpoints.Count - 1; i >= 0; i--)
		{
			if (_connectionEndpoints[i].LocalAttachPointId == localAttachPointId)
			{
				_connectionEndpoints.RemoveAt(i);
			}
		}
	}

	// 按端点里的 connectedPartId 解析出对端零件。 / Resolve the connected part referenced by an endpoint.
	public bool TryGetConnectedPart(PartConnectionEndpoint endpoint, out Part connectedPart)
	{
		connectedPart = null;
		if (endpoint == null)
		{
			return false;
		}

		Craft craft = GetOwningCraft();
		connectedPart = craft.FindPartById(endpoint.ConnectedPartId);
		return connectedPart != null;
	}

	// 把某个端点的 reciprocal 视图插入或更新到当前零件。 / Insert or update the reciprocal view of an endpoint on this part.
	public void UpsertReciprocalConnection(PartConnectionEndpoint sourceEndpoint, int sourcePartId)
	{
		if (sourceEndpoint == null)
		{
			return;
		}

		PartConnectionEndpoint reciprocal = sourceEndpoint.CreateReciprocal(sourcePartId);
		for (int i = 0; i < _connectionEndpoints.Count; i++)
		{
			PartConnectionEndpoint existing = _connectionEndpoints[i];
			if (!ConnectionEndpointsMatch(existing, reciprocal))
			{
				continue;
			}

			existing.IsPartAEndpoint = reciprocal.IsPartAEndpoint;
			existing.LocalAttachPointId = reciprocal.LocalAttachPointId;
			existing.ConnectedPartId = reciprocal.ConnectedPartId;
			existing.ConnectedAttachPointId = reciprocal.ConnectedAttachPointId;
			return;
		}

		_connectionEndpoints.Add(reciprocal);
	}

	// 删除指向指定 source part 的过期 reciprocal 端点。 / Remove stale reciprocal endpoints that point back to the specified source part.
	public void RemoveStaleReciprocalConnections(int connectedPartId, IReadOnlyCollection<PartConnectionEndpoint> expectedReciprocals)
	{
		for (int i = _connectionEndpoints.Count - 1; i >= 0; i--)
		{
			PartConnectionEndpoint endpoint = _connectionEndpoints[i];
			if (endpoint.ConnectedPartId != connectedPartId)
			{
				continue;
			}

			bool matched = false;
			if (expectedReciprocals != null)
			{
				foreach (PartConnectionEndpoint expected in expectedReciprocals)
				{
					if (!ConnectionEndpointsMatch(endpoint, expected))
					{
						continue;
					}

					matched = true;
					break;
				}
			}

			if (!matched)
			{
				_connectionEndpoints.RemoveAt(i);
			}
		}
	}

	// 用完整端点身份比较两条连接记录，避免把同一 ConnectionId 下的多 attachpoint 对错误折叠成一条。 / Compare the full endpoint identity so multiple attach-point pairs under one ConnectionId are not collapsed into a single record.
	private static bool ConnectionEndpointsMatch(PartConnectionEndpoint a, PartConnectionEndpoint b)
	{
		if (a == null || b == null)
		{
			return false;
		}

		return a.ConnectionId == b.ConnectionId
			&& a.IsPartAEndpoint == b.IsPartAEndpoint
			&& a.LocalAttachPointId == b.LocalAttachPointId
			&& a.ConnectedPartId == b.ConnectedPartId
			&& a.ConnectedAttachPointId == b.ConnectedAttachPointId;
	}

	// 为缺少 id 的端点分配连续且唯一的连接 id。 / Assign sequential unique connection ids to endpoints that do not have one yet.
	public void EnsureConnectionIds(int firstAvailableId)
	{
		int nextId = Mathf.Max(1, firstAvailableId);
		foreach (PartConnectionEndpoint endpoint in _connectionEndpoints)
		{
			if (endpoint.ConnectionId > 0)
			{
				nextId = Mathf.Max(nextId, endpoint.ConnectionId + 1);
				continue;
			}

			endpoint.ConnectionId = nextId++;
			endpoint.IsPartAEndpoint = true;
		}
	}

	// 让子类提供指定 attach point 的局部空间坐标。 / Let subclasses provide the local-space position for a given attach point.
	public virtual Vector3 GetAttachPointLocalPosition(int attachPointId)
	{
		return Vector3.zero;
	}

	// 把 attach point 的局部坐标转换为世界坐标。 / Convert an attach point's local position into world space.
	public Vector3 GetAttachPointWorldPosition(int attachPointId)
	{
		return transform.TransformPoint(GetAttachPointLocalPosition(attachPointId));
	}

	// 在零件被选中时绘制连接 Gizmos。 / Draw connection gizmos when the part is selected.
	protected virtual void OnDrawGizmosSelected()
	{
		Event currentEvent = Event.current;
		if (currentEvent != null && currentEvent.type != EventType.Repaint)
		{
			return;
		}

		if (Selection.activeGameObject != gameObject)
		{
			return;
		}

		DrawConnectionHandles();
	}

	// 在 SceneView 里绘制当前零件与连接目标之间的连线。 / Draw SceneView lines between this part and its resolved connection targets.
	private void DrawConnectionHandles()
	{
		if (_connectionEndpoints == null || _connectionEndpoints.Count == 0)
		{
			return;
		}

		Color previousColor = Handles.color;
		Handles.color = new Color(0.25f, 0.85f, 1f, 0.9f);
		foreach (PartConnectionEndpoint endpoint in _connectionEndpoints)
		{
			if (!TryGetConnectedPart(endpoint, out Part connectedPart))
			{
				continue;
			}

			Vector3 start = GetAttachPointWorldPosition(endpoint.LocalAttachPointId);
			Vector3 end = connectedPart.GetAttachPointWorldPosition(endpoint.ConnectedAttachPointId);
			Handles.DrawAAPolyLine(3f, start, end);
			Handles.SphereHandleCap(0, start, Quaternion.identity, HandleUtility.GetHandleSize(start) * 0.045f, EventType.Repaint);
			if (SceneView.currentDrawingSceneView != null && SceneView.currentDrawingSceneView.camera != null)
			{
				Handles.Label(Vector3.Lerp(start, end, 0.5f), $"{PartId}:{endpoint.LocalAttachPointId} -> {connectedPart.PartId}:{endpoint.ConnectedAttachPointId}");
			}
		}
		Handles.color = previousColor;
	}

	// 读取所有 Part 通用的目标选择元数据。 / Read targeting metadata shared by all part types.
	private void LoadTargetingState(XElement partElement)
	{
		XElement targetingElement = partElement.Element("PartTargeting.State");
		if (targetingElement == null)
		{
			_targetMode = string.Empty;
			_targetPartIdsAttributeName = "partIds";
			_targetPartIds = Array.Empty<int>();
			return;
		}

		_targetMode = (string)targetingElement.Attribute("targetMode") ?? string.Empty;
		_targetPartIdsAttributeName = ResolveTargetIdsAttributeName(_targetMode, targetingElement);
		_targetPartIds = ParseTargetIds((string)targetingElement.Attribute(_targetPartIdsAttributeName));
	}

	// 当存在目标信息时，把它写回导出的 XML。 / Write targeting metadata back into the exported XML when it is present.
	private void WriteTargetingState(XElement partElement)
	{
		if (string.IsNullOrWhiteSpace(_targetMode) && (_targetPartIds == null || _targetPartIds.Length == 0))
		{
			XmlUtil.RemoveChildren(partElement, "PartTargeting.State");
			return;
		}

		XElement targetingElement = partElement.Element("PartTargeting.State") ?? new XElement("PartTargeting.State");
		string targetMode = string.IsNullOrWhiteSpace(_targetMode) ? "MultipleParts" : _targetMode;
		string idsAttributeName = ResolveTargetIdsAttributeName(targetMode, _targetPartIdsAttributeName);
		targetingElement.SetAttributeValue("targetMode", targetMode);
		targetingElement.SetAttributeValue(idsAttributeName, FormatTargetIds(_targetPartIds));
		targetingElement.SetAttributeValue(idsAttributeName == "customPartIds" ? "partIds" : "customPartIds", null);
		if (targetingElement.Parent == null)
		{
			partElement.Add(targetingElement);
		}
	}

	// 根据 targetMode 决定该读取哪个目标 id 属性名。 / Pick the target-id attribute name to read based on the current target mode.
	private static string ResolveTargetIdsAttributeName(string targetMode, XElement targetingElement)
	{
		if (string.Equals(targetMode, "Custom", StringComparison.OrdinalIgnoreCase))
		{
			return "customPartIds";
		}

		if (string.Equals(targetMode, "SinglePart", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(targetMode, "MultipleParts", StringComparison.OrdinalIgnoreCase))
		{
			return "partIds";
		}

		return targetingElement.Attribute("customPartIds") != null ? "customPartIds" : "partIds";
	}

	// 在没有 XML 节点上下文时，根据 targetMode 回退到默认目标 id 属性名。 / Resolve the fallback target-id attribute name when no XML node context is available.
	private static string ResolveTargetIdsAttributeName(string targetMode, string fallbackAttributeName)
	{
		if (string.Equals(targetMode, "Custom", StringComparison.OrdinalIgnoreCase))
		{
			return "customPartIds";
		}

		if (string.Equals(targetMode, "SinglePart", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(targetMode, "MultipleParts", StringComparison.OrdinalIgnoreCase))
		{
			return "partIds";
		}

		return string.Equals(fallbackAttributeName, "customPartIds", StringComparison.Ordinal) ? "customPartIds" : "partIds";
	}

	// 比较当前公共 Part 字段与缓存 XML 是否已经发生变化。 / Check whether the current common part fields differ from the cached XML.
	private bool HasCommonXmlChanges(XElement partElement)
	{
		if (partElement == null)
		{
			return true;
		}

		if (XmlUtil.ParseInt((string)partElement.Attribute("id"), int.MinValue) != _partId)
		{
			return true;
		}

		if (!string.Equals((string)partElement.Attribute("partType") ?? string.Empty, _partType ?? string.Empty, StringComparison.Ordinal))
		{
			return true;
		}

		if (!Approximately(XmlUtil.ParseVector3((string)partElement.Attribute("position"), Vector3.zero), transform.localPosition))
		{
			return true;
		}

		if (!Approximately(XmlUtil.ParseVector3((string)partElement.Attribute("rotation"), Vector3.zero), transform.localEulerAngles))
		{
			return true;
		}

		Vector3 rawScale = XmlUtil.ParseVector3((string)partElement.Attribute("scale"), Vector3.one);
		if (!Approximately(rawScale, transform.localScale))
		{
			return true;
		}

		return !string.Equals((string)partElement.Attribute("materials") ?? string.Empty, _materialsText ?? string.Empty, StringComparison.Ordinal);
	}

	// 按需把公共 Part 属性写回导出 XML。 / Write the common Part attributes back into the export XML when needed.
	private void WriteCommonPartAttributes(XElement partElement, bool writeAll)
	{
		if (writeAll || XmlUtil.ParseInt((string)partElement.Attribute("id"), int.MinValue) != _partId)
		{
			partElement.SetAttributeValue("id", _partId);
		}

		if (writeAll || !string.Equals((string)partElement.Attribute("partType") ?? string.Empty, _partType ?? string.Empty, StringComparison.Ordinal))
		{
			partElement.SetAttributeValue("partType", _partType);
		}

		if (writeAll || !Approximately(XmlUtil.ParseVector3((string)partElement.Attribute("position"), Vector3.zero), transform.localPosition))
		{
			partElement.SetAttributeValue("position", XmlUtil.FormatVector3(transform.localPosition));
		}

		if (writeAll || !Approximately(XmlUtil.ParseVector3((string)partElement.Attribute("rotation"), Vector3.zero), transform.localEulerAngles))
		{
			partElement.SetAttributeValue("rotation", XmlUtil.FormatVector3(transform.localEulerAngles));
		}

		if (writeAll || !Approximately(XmlUtil.ParseVector3((string)partElement.Attribute("scale"), Vector3.one), transform.localScale))
		{
			partElement.SetAttributeValue("scale", IsDefaultScale(transform.localScale) ? null : XmlUtil.FormatVector3(transform.localScale));
		}

		if (writeAll || !string.Equals((string)partElement.Attribute("materials") ?? string.Empty, _materialsText ?? string.Empty, StringComparison.Ordinal))
		{
			partElement.SetAttributeValue("materials", string.IsNullOrWhiteSpace(_materialsText) ? null : _materialsText);
		}
	}

	// 用统一 epsilon 比较两个 Vector3 是否近似相等。 / Compare two Vector3 values using the shared epsilon.
	private static bool Approximately(Vector3 a, Vector3 b)
	{
		const float epsilon = 0.0001f;
		return Mathf.Abs(a.x - b.x) <= epsilon
			&& Mathf.Abs(a.y - b.y) <= epsilon
			&& Mathf.Abs(a.z - b.z) <= epsilon;
	}

	// 把逗号分隔的目标 id 字符串解析成去重后的整数数组。 / Parse a comma-separated part id list into a distinct integer array.
	private static int[] ParseTargetIds(string csv)
	{
		if (string.IsNullOrWhiteSpace(csv))
		{
			return Array.Empty<int>();
		}

		return csv
			.Split(',')
			.Where(item => !string.IsNullOrWhiteSpace(item))
			.Select(item => XmlUtil.ParseInt(item.Trim(), int.MinValue))
			.Where(item => item != int.MinValue)
			.Distinct()
			.ToArray();
	}

	// 把目标 id 数组格式化回 XML 属性字符串。 / Format explicit target ids back into the XML attribute representation.
	private static string FormatTargetIds(int[] ids)
	{
		if (ids == null || ids.Length == 0)
		{
			return string.Empty;
		}

		return string.Join(",", ids);
	}

	// 解析一个逗号分隔的整数列表，但不做去重。 / Parse a comma-separated integer list without deduplicating it.
	private static int[] ParseIntegerCsv(string csv)
	{
		if (string.IsNullOrWhiteSpace(csv))
		{
			return Array.Empty<int>();
		}

		return csv
			.Split(',')
			.Where(item => !string.IsNullOrWhiteSpace(item))
			.Select(item => XmlUtil.ParseInt(item.Trim(), int.MinValue))
			.Where(item => item != int.MinValue)
			.ToArray();
	}

	// 判断一个缩放值是否仍可视为默认的 1,1,1。 / Check whether a scale value should still be treated as the default 1,1,1.
	private static bool IsDefaultScale(Vector3 scale)
	{
		const float epsilon = 0.0001f;
		return Mathf.Abs(scale.x - 1f) <= epsilon
			&& Mathf.Abs(scale.y - 1f) <= epsilon
			&& Mathf.Abs(scale.z - 1f) <= epsilon;
	}

	// 防止依赖系统触发嵌套重建时发生递归刷新。 / Prevent recursive preview refreshes when dependent systems trigger nested rebuilds.
	private void RefreshPreviewInternal()
	{
		if (_isRefreshingPreview)
		{
			return;
		}

		try
		{
			_isRefreshingPreview = true;
			RefreshPreview();
		}
		finally
		{
			_isRefreshingPreview = false;
		}
	}

	// 把多次编辑器预览刷新请求合并成一次延迟执行。 / Coalesce editor preview refresh requests into a single delayed execution.
	protected void QueuePreviewRefresh()
	{
		Craft craft = GetOwningCraft();
		if (craft == null || craft.IsPreviewQueueSuppressed)
		{
			return;
		}

		_previewRefreshDueTime = EditorApplication.timeSinceStartup + Math.Max(0d, PreviewRefreshDelaySeconds);
		if (_previewRefreshQueued)
		{
			return;
		}

		_previewRefreshQueued = true;
        Craft.RegisterEditorUpdate(DelayedRefreshPreview);
	}

	// 如果存在所属 Craft，则把延迟刷新路由到整机重建。 / Route delayed editor refreshes through the owning craft when one exists.
	private void DelayedRefreshPreview()
	{
		if (EditorApplication.timeSinceStartup < _previewRefreshDueTime)
		{
			return;
		}

      Craft.UnregisterEditorUpdate(DelayedRefreshPreview);
		_previewRefreshQueued = false;

		if (this == null || gameObject == null)
		{
			return;
		}

		Craft craft = GetOwningCraft();
		craft.QueuePreviewRebuildForPart(this, 0d, lightweight: this is FuselagePart);
	}

	// 保持 GameObject 名称与导入的零件身份一致。 / Keep the GameObject name aligned with the imported part identity.
	private void ApplyObjectName()
	{
		name = string.IsNullOrWhiteSpace(_partType) ? $"Part {_partId}" : $"Part {_partId} {_partType}";
	}
}

