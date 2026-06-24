using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;


[Serializable]
public sealed class CraftInfo
{
	private const int CraftRootHierarchyNodeId = 0;

	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	[SerializeField, HideInInspector]
	private HierarchyNodeRecord[] _hierarchyNodes = Array.Empty<HierarchyNodeRecord>();

	[SerializeField, HideInInspector]
	private PartHierarchyAssignment[] _hierarchyAssignments = Array.Empty<PartHierarchyAssignment>();

	[SerializeField, HideInInspector]
	private PinnedPartTransformRecord[] _pinnedPartTransforms = Array.Empty<PinnedPartTransformRecord>();

	[SerializeField, HideInInspector]
	private int[] _orderedPartIds = Array.Empty<int>();

	[NonSerialized]
	private Dictionary<int, HierarchyNodeRecord> _hierarchyNodeById;

	[NonSerialized]
	private Dictionary<int, int> _hierarchyNodeIdByPartId;

	[NonSerialized]
	private Dictionary<int, PinnedPartTransformRecord> _pinnedTransformByPartId;

	[NonSerialized]
	private Dictionary<int, Transform> _restoredHierarchyNodeById;

	public IReadOnlyList<HierarchyNodeRecord> HierarchyNodes => _hierarchyNodes ?? Array.Empty<HierarchyNodeRecord>();

	public IReadOnlyList<PartHierarchyAssignment> HierarchyAssignments => _hierarchyAssignments ?? Array.Empty<PartHierarchyAssignment>();

	public IReadOnlyList<PinnedPartTransformRecord> PinnedPartTransforms => _pinnedPartTransforms ?? Array.Empty<PinnedPartTransformRecord>();

	public IReadOnlyList<int> OrderedPartIds => _orderedPartIds ?? Array.Empty<int>();

	[Serializable]
	public sealed class HierarchyNodeRecord
	{
		public int NodeId;

		public int ParentNodeId;

		public int SiblingIndex;

		public string Name;

		public HierarchyNodeRecord()
		{
		}

		public HierarchyNodeRecord(int nodeId, int parentNodeId, int siblingIndex, string name)
		{
			NodeId = nodeId;
			ParentNodeId = parentNodeId;
			SiblingIndex = siblingIndex;
			Name = name ?? string.Empty;
		}
	}

	[Serializable]
	public sealed class PartHierarchyAssignment
	{
		public int PartId;

		public int ParentNodeId;

		public PartHierarchyAssignment()
		{
		}

		public PartHierarchyAssignment(int partId, int parentNodeId)
		{
			PartId = partId;
			ParentNodeId = parentNodeId;
		}
	}

	[Serializable]
	public sealed class PinnedPartTransformRecord
	{
		public int PartId;

		public string PositionText;

		public string RotationText;

		public PinnedPartTransformRecord()
		{
		}

		public PinnedPartTransformRecord(int partId, Vector3 position, Vector3 rotation)
		{
			PartId = partId;
			PositionText = FormatVector3Precise(position);
			RotationText = FormatVector3Precise(rotation);
		}
	}

	// 记录当前 Unity 分类树，供下一次 XML 导入后还原。 / Capture the current Unity grouping tree for restoration after the next XML import.
	public void CaptureHierarchySnapshot(Transform craftRoot, IReadOnlyList<Part> parts)
	{
		CapturePartOrderSnapshot(parts);
		if (craftRoot == null)
		{
			return;
		}

		if (parts == null || parts.Count == 0)
		{
			RebuildHierarchyLookup();
			return;
		}

		List<HierarchyNodeRecord> nodes = new List<HierarchyNodeRecord>();
		List<PartHierarchyAssignment> assignments = new List<PartHierarchyAssignment>();
		foreach (Transform child in craftRoot.Cast<Transform>().OrderBy(item => item.GetSiblingIndex()))
		{
			CaptureHierarchyNode(child, CraftRootHierarchyNodeId, nodes, assignments);
		}

		_hierarchyNodes = nodes.ToArray();
		_hierarchyAssignments = assignments.ToArray();
		RebuildHierarchyLookup();
	}

	// 保存当前 XML 导出使用的 PartId 顺序，用于下次导入时识别原游戏复用旧 ID 的新零件。 / Store the current XML export PartId order so the next import can detect reused ids from the original game.
	public void CapturePartOrderSnapshot(IReadOnlyList<Part> parts)
	{
		if (parts == null)
		{
			return;
		}

		_orderedPartIds = parts
			.Where(part => part != null && part.PartId > 0)
			.Select(part => part.PartId)
			.ToArray();
	}

	// 根据当前缓存先恢复所有分类节点，之后创建 Part 时即可直接挂到目标父节点。 / Restore cached grouping nodes before Parts are recreated under their target parents.
	public void CreateRestoredHierarchyNodes(Transform craftRoot)
	{
		EnsureRuntimeCollections();
		_restoredHierarchyNodeById.Clear();
		EnsureHierarchyLookup();
		foreach (HierarchyNodeRecord node in _hierarchyNodes ?? Array.Empty<HierarchyNodeRecord>())
		{
			if (node == null)
			{
				continue;
			}

			ResolveHierarchyNode(craftRoot, node.NodeId);
		}
	}

	// 按 PartId 查找导入时应使用的父节点；没有缓存记录则仍直接挂到 Craft。 / Resolve the import parent for a PartId; uncached parts stay under the Craft root.
	public Transform ResolveImportedPartParent(Transform craftRoot, int partId)
	{
		if (craftRoot == null)
		{
			return null;
		}

		EnsureHierarchyLookup();
		if (partId > 0 && _hierarchyNodeIdByPartId.TryGetValue(partId, out int parentNodeId))
		{
			return ResolveHierarchyNode(craftRoot, parentNodeId);
		}

		return craftRoot;
	}

	// 清理导入过程中懒创建的分类节点缓存。 / Clear the lazily restored grouping-node cache used during import.
	public void ClearRestoredHierarchyNodes()
	{
		EnsureRuntimeCollections();
		_restoredHierarchyNodeById.Clear();
	}

	// 把序列化的层级表重建成导入时 O(1) 查询的字典。 / Rebuild serialized hierarchy tables into O(1) lookup dictionaries.
	public void RebuildHierarchyLookup()
	{
		EnsureRuntimeCollections();
		_hierarchyNodeById.Clear();
		_hierarchyNodeIdByPartId.Clear();
		foreach (HierarchyNodeRecord node in _hierarchyNodes ?? Array.Empty<HierarchyNodeRecord>())
		{
			if (node == null || node.NodeId <= CraftRootHierarchyNodeId || _hierarchyNodeById.ContainsKey(node.NodeId))
			{
				continue;
			}

			_hierarchyNodeById.Add(node.NodeId, node);
		}

		foreach (PartHierarchyAssignment assignment in _hierarchyAssignments ?? Array.Empty<PartHierarchyAssignment>())
		{
			if (assignment == null || assignment.PartId <= 0 || !_hierarchyNodeById.ContainsKey(assignment.ParentNodeId))
			{
				continue;
			}

			_hierarchyNodeIdByPartId[assignment.PartId] = assignment.ParentNodeId;
		}
	}

	// 让一个零件进入 Pin 状态，并立即刷新基准字符串。 / Pin one part and immediately refresh its authoritative transform strings.
	public void PinPart(Part part)
	{
		if (part == null || part.PartId <= 0)
		{
			return;
		}

		EnsurePinnedLookup();
		PinnedPartTransformRecord record = new PinnedPartTransformRecord(part.PartId, part.transform.localPosition, part.transform.localEulerAngles);
		_pinnedTransformByPartId[part.PartId] = record;
		_pinnedPartTransforms = _pinnedTransformByPartId.Values
			.OrderBy(item => item.PartId)
			.ToArray();
	}

	// 移除一个零件的 Pin 记录。 / Remove one part's pin record.
	public bool UnpinPart(int partId)
	{
		if (partId <= 0)
		{
			return false;
		}

		EnsurePinnedLookup();
		if (!_pinnedTransformByPartId.Remove(partId))
		{
			return false;
		}

		_pinnedPartTransforms = _pinnedTransformByPartId.Values
			.OrderBy(item => item.PartId)
			.ToArray();
		return true;
	}

	public bool IsPinned(int partId)
	{
		if (partId <= 0)
		{
			return false;
		}

		EnsurePinnedLookup();
		return _pinnedTransformByPartId.ContainsKey(partId);
	}

	// 按 Pin 权威文本校准零件 Transform，发生回退时返回 true。 / Correct one part transform from the authoritative pin text and return true when a correction occurred.
	public bool ApplyPinnedTransform(Part part, bool logIfChanged, string context)
	{
		if (part == null || !TryGetPinnedTransform(part.PartId, out Vector3 position, out Vector3 rotation))
		{
			return false;
		}

		Vector3 previousPosition = part.transform.localPosition;
		Vector3 previousRotation = part.transform.localEulerAngles;
		part.transform.localPosition = position;
		part.transform.localEulerAngles = rotation;
		if (Exactly(previousPosition, position) && Exactly(previousRotation, rotation))
		{
			return false;
		}

		if (logIfChanged)
		{
			Debug.Log(
				$"Pinned part {part.PartId} transform was corrected from CraftInfo during {context}. " +
				$"position {FormatVector3Precise(previousPosition)} -> {FormatVector3Precise(position)}, " +
				$"rotation {FormatVector3Precise(previousRotation)} -> {FormatVector3Precise(rotation)}",
				part);
		}

		return true;
	}

	// 导出时用 Pin 权威文本覆盖 XML 坐标，不读取 GameObject Transform。 / Override export XML coordinates from the authoritative pin text without reading the GameObject Transform.
	public bool ApplyPinnedTransformToXml(Part part, XElement partElement)
	{
		return part != null && ApplyPinnedTransformToXml(part.PartId, partElement);
	}

	// 用指定 PartId 的 Pin 权威文本覆盖 XML 坐标。 / Override XML coordinates from the authoritative pin text for the given PartId.
	public bool ApplyPinnedTransformToXml(int partId, XElement partElement)
	{
		return ApplyPinnedTransformToXml(partId, partElement, logIfChanged: false, context: null, logContext: null);
	}

	// 用指定 PartId 的 Pin 权威文本覆盖 XML 坐标，并按需记录普通日志。 / Override XML coordinates from pin text and optionally log the correction.
	public bool ApplyPinnedTransformToXml(int partId, XElement partElement, bool logIfChanged, string context, UnityEngine.Object logContext)
	{
		if (partId <= 0 || partElement == null)
		{
			return false;
		}

		EnsurePinnedLookup();
		if (!_pinnedTransformByPartId.TryGetValue(partId, out PinnedPartTransformRecord record) || record == null)
		{
			return false;
		}

		string previousPosition = (string)partElement.Attribute("position") ?? string.Empty;
		string previousRotation = (string)partElement.Attribute("rotation") ?? string.Empty;
		partElement.SetAttributeValue("position", record.PositionText);
		partElement.SetAttributeValue("rotation", record.RotationText);
		if (logIfChanged
			&& (!string.Equals(previousPosition, record.PositionText, StringComparison.Ordinal)
				|| !string.Equals(previousRotation, record.RotationText, StringComparison.Ordinal)))
		{
			Debug.Log(
				$"Pinned part {partId} XML transform was corrected from CraftInfo during {context}. " +
				$"position {previousPosition} -> {record.PositionText}, " +
				$"rotation {previousRotation} -> {record.RotationText}",
				logContext);
		}

		return true;
	}

	// 导入 XML 已经不包含的 pinned Part 视为外部删除，清理对应 Pin 和层级映射。 / Treat pinned parts missing from the imported XML as external deletions and stop tracking them.
	public int[] RemoveMissingPinnedParts(IReadOnlyCollection<int> importedPartIds)
	{
		HashSet<int> imported = importedPartIds != null
			? new HashSet<int>(importedPartIds)
			: new HashSet<int>();
		int[] removedPartIds = GetTrackedPartIds()
			.Where(partId => !imported.Contains(partId))
			.OrderBy(partId => partId)
			.ToArray();
		return RemovePartRecords(removedPartIds);
	}

	// 根据上次导出的 PartId 顺序和本次导入 XML 顺序，清理已删除或被原游戏复用的旧 Part 缓存。 / Remove stale cache records by comparing the previous export PartId order with the imported XML order.
	public int[] RemoveStalePartRecordsByImportedOrder(IReadOnlyList<int> importedOrderedPartIds)
	{
		int[] cachedOrderedPartIds = _orderedPartIds ?? Array.Empty<int>();
		if (cachedOrderedPartIds.Length == 0)
		{
			return RemoveMissingPinnedParts(importedOrderedPartIds != null ? new HashSet<int>(importedOrderedPartIds) : null);
		}

		List<int> stalePartIds = new List<int>();
		int importedIndex = 0;
		for (int cachedIndex = 0; cachedIndex < cachedOrderedPartIds.Length; cachedIndex++)
		{
			int cachedPartId = cachedOrderedPartIds[cachedIndex];
			if (cachedPartId <= 0)
			{
				continue;
			}

			if (importedOrderedPartIds == null || importedIndex >= importedOrderedPartIds.Count)
			{
				stalePartIds.Add(cachedPartId);
				continue;
			}

			int importedPartId = importedOrderedPartIds[importedIndex];
			if (cachedPartId != importedPartId)
			{
				stalePartIds.Add(cachedPartId);
				continue;
			}
			importedIndex++;
		}

		return RemovePartRecords(stalePartIds);
	}

	// 移除指定 PartId 对应的 Pin、层级分配和顺序快照记录。 / Remove pin, hierarchy assignment, and order snapshot records for the requested PartIds.
	public int[] RemovePartRecords(IReadOnlyCollection<int> partIds)
	{
		if (partIds == null || partIds.Count == 0)
		{
			return Array.Empty<int>();
		}

		HashSet<int> removedSet = new HashSet<int>(partIds.Where(partId => partId > 0));
		if (removedSet.Count == 0)
		{
			return Array.Empty<int>();
		}

		EnsurePinnedLookup();
		foreach (int partId in removedSet)
		{
			_pinnedTransformByPartId.Remove(partId);
		}

		_pinnedPartTransforms = _pinnedTransformByPartId.Values
			.OrderBy(item => item.PartId)
			.ToArray();
		_hierarchyAssignments = (_hierarchyAssignments ?? Array.Empty<PartHierarchyAssignment>())
			.Where(item => item != null && !removedSet.Contains(item.PartId))
			.ToArray();
		RemovePartIdsFromOrderSnapshot(removedSet);
		PruneUnusedHierarchyNodes();
		RebuildHierarchyLookup();
		return removedSet
			.OrderBy(partId => partId)
			.ToArray();
	}

	private int[] GetTrackedPartIds()
	{
		EnsurePinnedLookup();
		HashSet<int> result = new HashSet<int>();
		foreach (int partId in _pinnedTransformByPartId.Keys)
		{
			if (partId > 0)
			{
				result.Add(partId);
			}
		}

		foreach (PartHierarchyAssignment assignment in _hierarchyAssignments ?? Array.Empty<PartHierarchyAssignment>())
		{
			if (assignment != null && assignment.PartId > 0)
			{
				result.Add(assignment.PartId);
			}
		}

		foreach (int partId in _orderedPartIds ?? Array.Empty<int>())
		{
			if (partId > 0)
			{
				result.Add(partId);
			}
		}

		return result.ToArray();
	}

	private void RemovePartIdsFromOrderSnapshot(IReadOnlyCollection<int> removedPartIds)
	{
		int[] sourceIds = _orderedPartIds ?? Array.Empty<int>();
		List<int> keptIds = new List<int>(sourceIds.Length);
		for (int i = 0; i < sourceIds.Length; i++)
		{
			int partId = sourceIds[i];
			if (removedPartIds.Contains(partId))
			{
				continue;
			}

			keptIds.Add(partId);
		}

		_orderedPartIds = keptIds.ToArray();
	}

	private void PruneUnusedHierarchyNodes()
	{
		_hierarchyNodes ??= Array.Empty<HierarchyNodeRecord>();
		_hierarchyAssignments ??= Array.Empty<PartHierarchyAssignment>();
		if (_hierarchyNodes.Length == 0)
		{
			return;
		}

		HashSet<int> usedNodeIds = new HashSet<int>();
		foreach (PartHierarchyAssignment assignment in _hierarchyAssignments)
		{
			if (assignment == null || assignment.ParentNodeId <= CraftRootHierarchyNodeId)
			{
				continue;
			}

			AddHierarchyNodeAndParents(assignment.ParentNodeId, usedNodeIds);
		}

		_hierarchyNodes = _hierarchyNodes
			.Where(node => node != null && usedNodeIds.Contains(node.NodeId))
			.ToArray();
	}

	private void AddHierarchyNodeAndParents(int nodeId, ISet<int> usedNodeIds)
	{
		if (nodeId <= CraftRootHierarchyNodeId || !usedNodeIds.Add(nodeId))
		{
			return;
		}

		EnsureHierarchyLookup();
		if (_hierarchyNodeById.TryGetValue(nodeId, out HierarchyNodeRecord node) && node != null)
		{
			AddHierarchyNodeAndParents(node.ParentNodeId, usedNodeIds);
		}
	}

	private void CaptureHierarchyNode(
		Transform current,
		int parentNodeId,
		List<HierarchyNodeRecord> nodes,
		List<PartHierarchyAssignment> assignments)
	{
		if (current == null)
		{
			return;
		}

		Part part = current.GetComponent<Part>();
		if (part != null)
		{
			if (parentNodeId != CraftRootHierarchyNodeId && part.PartId > 0)
			{
				assignments.Add(new PartHierarchyAssignment(part.PartId, parentNodeId));
			}
			return;
		}

		int nodeId = nodes.Count + 1;
		nodes.Add(new HierarchyNodeRecord(nodeId, parentNodeId, current.GetSiblingIndex(), current.name));
		foreach (Transform child in current.Cast<Transform>().OrderBy(item => item.GetSiblingIndex()))
		{
			CaptureHierarchyNode(child, nodeId, nodes, assignments);
		}
	}

	private Transform ResolveHierarchyNode(Transform craftRoot, int nodeId)
	{
		if (craftRoot == null || nodeId <= CraftRootHierarchyNodeId)
		{
			return craftRoot;
		}

		EnsureRuntimeCollections();
		if (_restoredHierarchyNodeById.TryGetValue(nodeId, out Transform restored) && restored != null)
		{
			return restored;
		}

		EnsureHierarchyLookup();
		if (!_hierarchyNodeById.TryGetValue(nodeId, out HierarchyNodeRecord record) || record == null)
		{
			return craftRoot;
		}

		Transform parent = record.ParentNodeId > CraftRootHierarchyNodeId && record.ParentNodeId != record.NodeId
			? ResolveHierarchyNode(craftRoot, record.ParentNodeId)
			: craftRoot;
		GameObject groupObject = new GameObject(string.IsNullOrWhiteSpace(record.Name) ? "Group" : record.Name);
		Transform groupTransform = groupObject.transform;
		groupTransform.SetParent(parent, false);
		groupTransform.localPosition = Vector3.zero;
		groupTransform.localRotation = Quaternion.identity;
		groupTransform.localScale = Vector3.one;
		if (record.SiblingIndex >= 0)
		{
			groupTransform.SetSiblingIndex(Mathf.Min(record.SiblingIndex, Mathf.Max(0, parent.childCount - 1)));
		}

		_restoredHierarchyNodeById[nodeId] = groupTransform;
		return groupTransform;
	}

	private bool TryGetPinnedTransform(int partId, out Vector3 position, out Vector3 rotation)
	{
		position = Vector3.zero;
		rotation = Vector3.zero;
		if (partId <= 0)
		{
			return false;
		}

		EnsurePinnedLookup();
		if (!_pinnedTransformByPartId.TryGetValue(partId, out PinnedPartTransformRecord record) || record == null)
		{
			return false;
		}

		position = XmlUtil.ParseVector3(record.PositionText, Vector3.zero);
		rotation = XmlUtil.ParseVector3(record.RotationText, Vector3.zero);
		return true;
	}

	private void EnsureHierarchyLookup()
	{
		EnsureRuntimeCollections();
		if (_hierarchyNodeById.Count > 0 || ((_hierarchyNodes?.Length ?? 0) == 0 && (_hierarchyAssignments?.Length ?? 0) == 0))
		{
			return;
		}

		RebuildHierarchyLookup();
	}

	private void EnsurePinnedLookup()
	{
		EnsureRuntimeCollections();
		if (_pinnedTransformByPartId.Count > 0 || (_pinnedPartTransforms?.Length ?? 0) == 0)
		{
			return;
		}

		_pinnedTransformByPartId.Clear();
		foreach (PinnedPartTransformRecord record in _pinnedPartTransforms ?? Array.Empty<PinnedPartTransformRecord>())
		{
			if (record == null || record.PartId <= 0 || _pinnedTransformByPartId.ContainsKey(record.PartId))
			{
				continue;
			}

			_pinnedTransformByPartId.Add(record.PartId, record);
		}
	}

	private void EnsureRuntimeCollections()
	{
		_hierarchyNodeById ??= new Dictionary<int, HierarchyNodeRecord>();
		_hierarchyNodeIdByPartId ??= new Dictionary<int, int>();
		_pinnedTransformByPartId ??= new Dictionary<int, PinnedPartTransformRecord>();
		_restoredHierarchyNodeById ??= new Dictionary<int, Transform>();
		_hierarchyNodes ??= Array.Empty<HierarchyNodeRecord>();
		_hierarchyAssignments ??= Array.Empty<PartHierarchyAssignment>();
		_pinnedPartTransforms ??= Array.Empty<PinnedPartTransformRecord>();
		_orderedPartIds ??= Array.Empty<int>();
	}

	private static bool Exactly(Vector3 a, Vector3 b)
	{
		return a.x == b.x && a.y == b.y && a.z == b.z;
	}

	private static string FormatVector3Precise(Vector3 value)
	{
		return $"{FormatFloatPrecise(value.x)},{FormatFloatPrecise(value.y)},{FormatFloatPrecise(value.z)}";
	}

	private static string FormatFloatPrecise(float value)
	{
		return value.ToString("G9", Invariant);
	}
}
