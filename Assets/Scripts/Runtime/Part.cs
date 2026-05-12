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

	public PartConnectionEndpoint()
	{
	}

	public PartConnectionEndpoint(int connectionId, bool isPartAEndpoint, int localAttachPointId, int connectedPartId, int connectedAttachPointId)
	{
		ConnectionId = connectionId;
		IsPartAEndpoint = isPartAEndpoint;
		LocalAttachPointId = localAttachPointId;
		ConnectedPartId = connectedPartId;
		ConnectedAttachPointId = connectedAttachPointId;
	}

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

	public void ClearConnectionEndpoints()
	{
		_connectionEndpoints.Clear();
	}

	public void AddConnectionEndpoint(int connectionId, bool isPartAEndpoint, int localAttachPointId, int connectedPartId, int connectedAttachPointId)
	{
		_connectionEndpoints.Add(new PartConnectionEndpoint(connectionId, isPartAEndpoint, localAttachPointId, connectedPartId, connectedAttachPointId));
	}

	public void SetMaterialsText(string materialsText)
	{
		_materialsText = materialsText ?? string.Empty;
		_materialIds = ParseIntegerCsv(_materialsText);
	}

	public void MarkStateXmlDirty()
	{
		_stateXmlDirty = true;
	}

	public void RemoveConnectionEndpointAt(int index)
	{
		if (index < 0 || index >= _connectionEndpoints.Count)
		{
			return;
		}

		_connectionEndpoints.RemoveAt(index);
	}

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
			if (existing.ConnectionId != reciprocal.ConnectionId)
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

	public void RemoveStaleReciprocalConnections(int connectedPartId, HashSet<int> activeConnectionIds)
	{
		for (int i = _connectionEndpoints.Count - 1; i >= 0; i--)
		{
			PartConnectionEndpoint endpoint = _connectionEndpoints[i];
			if (endpoint.ConnectedPartId != connectedPartId)
			{
				continue;
			}

			if (endpoint.ConnectionId <= 0 || activeConnectionIds == null || !activeConnectionIds.Contains(endpoint.ConnectionId))
			{
				_connectionEndpoints.RemoveAt(i);
			}
		}
	}

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

	public virtual Vector3 GetAttachPointLocalPosition(int attachPointId)
	{
		return Vector3.zero;
	}

	public Vector3 GetAttachPointWorldPosition(int attachPointId)
	{
		return transform.TransformPoint(GetAttachPointLocalPosition(attachPointId));
	}

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
		if (craft.IsRebuildingPreviews || craft.IsPreviewQueueSuppressed)
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

[ExecuteAlways]
public class OtherPart : Part
{
	private const float GizmoRadius = 0.24f;

	// 普通零件不生成渲染网格，只保留 Gizmos 线框占位。 / Generic parts do not create render meshes; they only draw a lightweight Gizmos placeholder.
	public override void RefreshPreview()
	{
		base.RefreshPreview();
		RemoveRenderPreview();
	}

	private void OnDrawGizmos()
	{
		Event currentEvent = Event.current;
		if (currentEvent != null && currentEvent.type != EventType.Repaint)
		{
			return;
		}
		Gizmos.color = new Color(0.65f, 0.75f, 0.95f, 0.75f);
		Matrix4x4 oldMatrix = Gizmos.matrix;
		Gizmos.matrix = transform.localToWorldMatrix;
		Gizmos.DrawWireSphere(Vector3.zero, GizmoRadius);
		Gizmos.matrix = oldMatrix;
	}

	protected override void OnDrawGizmosSelected()
	{
		base.OnDrawGizmosSelected();
	}

	private void RemoveRenderPreview()
	{
		MeshFilter meshFilter = GetComponent<MeshFilter>();
		if (meshFilter != null)
		{
			if (meshFilter.sharedMesh != null)
			{
				DestroyOwnedObject(meshFilter.sharedMesh);
			}
			DestroyOwnedObject(meshFilter);
		}

		MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
		if (meshRenderer != null)
		{
			DestroyOwnedObject(meshRenderer);
		}
	}
}

[ExecuteAlways]
public class LabelState : MonoBehaviour, IPartXmlExtension
{
	[SerializeField, TextArea(2, 6)]
	private string _rawStateXml;

	[SerializeField]
	private string _designText = "Text";

	[SerializeField]
	private float _fontSize = 1f;

	[SerializeField]
	private float _width = 1f;

	[SerializeField]
	private float _height = 0.5f;

	[SerializeField]
	private Vector3 _offset = new Vector3(0f, 0.006f, 0f);

	[SerializeField]
	private Vector3 _rotation = new Vector3(90f, 0f, 0f);

	private GameObject _labelObject;

	private TextMesh _textMesh;

	private bool _previewRefreshQueued;

	// 从所属 Part XML 中读取标签文本与摆放数据。 / Read label text and placement data from the owning part XML.
	public void InitializeFromPartElement(XElement partElement)
	{
		XElement stateElement = partElement.Element("Label.State");
		if (stateElement == null)
		{
			return;
		}

		_rawStateXml = stateElement.ToString(SaveOptions.DisableFormatting);
		_designText = (string)stateElement.Attribute("designText") ?? _designText;
		_fontSize = XmlUtil.ParseFloat((string)stateElement.Attribute("fontSize"), _fontSize);
		_width = XmlUtil.ParseFloat((string)stateElement.Attribute("width"), _width);
		_height = XmlUtil.ParseFloat((string)stateElement.Attribute("height"), _height);
		_offset = XmlUtil.ParseVector3((string)stateElement.Attribute("offset"), _offset);
		_rotation = XmlUtil.ParseVector3((string)stateElement.Attribute("rotation"), _rotation);
		RefreshPreview();
	}

	// 把标签文本与摆放数据写回所属 Part XML。 / Write label text and placement data back into the owning part XML.
	public void WriteToPartElement(XElement partElement)
	{
		if (string.IsNullOrWhiteSpace(_rawStateXml) && string.IsNullOrWhiteSpace(_designText))
		{
			return;
		}

		XmlUtil.RemoveChildren(partElement, "Label.State");
		XElement stateElement = string.IsNullOrWhiteSpace(_rawStateXml) ? new XElement("Label.State") : XElement.Parse(_rawStateXml);
		stateElement.SetAttributeValue("designText", _designText ?? string.Empty);
		stateElement.SetAttributeValue("fontSize", XmlUtil.FormatFloat(_fontSize));
		stateElement.SetAttributeValue("width", XmlUtil.FormatFloat(_width));
		stateElement.SetAttributeValue("height", XmlUtil.FormatFloat(_height));
		stateElement.SetAttributeValue("offset", XmlUtil.FormatVector3(_offset));
		stateElement.SetAttributeValue("rotation", XmlUtil.FormatVector3(_rotation));
		partElement.Add(stateElement);
	}

	// 刷新场景里的文字标签预览。 / Refresh the in-scene text preview that represents the label extension.
	public void RefreshPreview()
	{
		EnsurePreviewObject();
		_labelObject.transform.localPosition = _offset;
		_labelObject.transform.localRotation = Quaternion.Euler(_rotation);
		_textMesh.text = _designText;
		_textMesh.characterSize = Mathf.Max(0.01f, _fontSize * 0.08f);
		_textMesh.anchor = TextAnchor.MiddleCenter;
		_textMesh.alignment = TextAlignment.Center;
		_textMesh.color = PreviewMaterialFactory.GetLabelMaterial().color;
		_labelObject.transform.localScale = new Vector3(Mathf.Max(0.05f, _width), Mathf.Max(0.05f, _height), 1f);
	}

	// 创建或找回用于渲染标签预览的 TextMesh 对象。 / Create or recover the TextMesh object used to render the label preview.
	private void EnsurePreviewObject()
	{
		if (_labelObject != null && _textMesh != null)
		{
			return;
		}

		Transform existing = transform.Find("LabelPreview");
		if (existing != null)
		{
			_labelObject = existing.gameObject;
			_textMesh = _labelObject.GetComponent<TextMesh>();
		}
		if (_labelObject == null)
		{
			_labelObject = new GameObject("LabelPreview");
			_labelObject.transform.SetParent(transform, false);
		}

		if (_textMesh == null)
		{
			_textMesh = _labelObject.GetComponent<TextMesh>();
			if (_textMesh == null)
			{
				_textMesh = _labelObject.AddComponent<TextMesh>();
			}
		}

		MeshRenderer renderer = _labelObject.GetComponent<MeshRenderer>();
		if (renderer != null)
		{
			renderer.sharedMaterial = PreviewMaterialFactory.GetLabelMaterial();
		}
	}

	// 当编辑器中的序列化字段变化时排队刷新标签预览。 / Queue label preview updates when serialized properties change in the editor.
	private void OnValidate()
	{
		QueuePreviewRefresh();
	}

	// 在编辑器中合并多次标签预览刷新请求。 / Coalesce label preview updates in the editor.
	private void QueuePreviewRefresh()
	{
		if (_previewRefreshQueued)
		{
			return;
		}

		_previewRefreshQueued = true;
		EditorApplication.delayCall += DelayedRefreshPreview;
	}

	// 在编辑器空闲时执行延迟的标签预览刷新。 / Execute the queued label preview refresh once the editor is idle.
	private void DelayedRefreshPreview()
	{
		EditorApplication.delayCall -= DelayedRefreshPreview;
		_previewRefreshQueued = false;

		if (this == null || gameObject == null)
		{
			return;
		}

		RefreshPreview();
	}
}
