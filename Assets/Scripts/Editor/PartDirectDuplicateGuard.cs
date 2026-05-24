using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


[InitializeOnLoad]
internal static class PartDirectDuplicateGuard
{
	private static readonly HashSet<int> KnownPartInstanceIds = new HashSet<int>();

	static PartDirectDuplicateGuard()
	{
		RebuildKnownParts();
		EditorApplication.delayCall += RebuildKnownParts;
		ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
	}

	// 在 Unity 发布对象创建事件时立即检查直接复制出的重复 Part。 / Check direct Part duplicates as soon as Unity publishes object creation events.
	private static void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
	{
		if (!ContainsGameObjectCreation(stream))
		{
			return;
		}

		CheckForDirectPartDuplicates();
	}

	// 只对新 GameObject 层级创建做重复 Part 检查，避免普通属性改动触发扫描。 / Only scan on new GameObject hierarchy creation, not ordinary property edits.
	private static bool ContainsGameObjectCreation(ObjectChangeEventStream stream)
	{
		for (int i = 0; i < stream.length; i++)
		{
			if (stream.GetEventType(i) == ObjectChangeKind.CreateGameObjectHierarchy)
			{
				return true;
			}
		}

		return false;
	}

	// 删除直接复制出来的重复 PartId 对象，只允许 Clone Part 生成新 id。 / Remove directly copied duplicate PartId objects so only Clone Part can mint new ids.
	private static void CheckForDirectPartDuplicates()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
		{
			RebuildKnownParts();
			return;
		}

		if (KnownPartInstanceIds.Count == 0)
		{
			RebuildKnownParts();
			return;
		}

		List<Part> duplicates = FindNewDuplicateParts();
		if (duplicates.Count == 0)
		{
			RebuildKnownParts();
			return;
		}

		HashSet<int> duplicatePartInstanceIds = new HashSet<int>();
		foreach (Part duplicate in duplicates)
		{
			if (duplicate != null)
			{
				duplicatePartInstanceIds.Add(duplicate.GetInstanceID());
			}
		}

		HashSet<GameObject> objectsToRemove = new HashSet<GameObject>();
		foreach (Part duplicate in duplicates)
		{
			GameObject removalRoot = FindDuplicateRemovalRoot(duplicate, duplicatePartInstanceIds);
			if (removalRoot != null)
			{
				objectsToRemove.Add(removalRoot);
			}
		}

		foreach (GameObject objectToRemove in objectsToRemove)
		{
			if (objectToRemove == null)
			{
				continue;
			}

			Debug.LogWarning($"Direct copy of '{objectToRemove.name}' was removed. Use the Inspector Clone Part button to create a new unique Part id.", objectToRemove);
			Undo.DestroyObjectImmediate(objectToRemove);
		}

		RebuildKnownParts();
	}

	// 刷新当前已知 Part 实例集合。 / Refresh the set of currently known Part instances.
	private static void RebuildKnownParts()
	{
		KnownPartInstanceIds.Clear();
		foreach (Part part in FindSceneParts())
		{
			KnownPartInstanceIds.Add(part.GetInstanceID());
		}
	}

	// 找出同一 Craft 下同 PartId、且本轮新出现的重复 Part。 / Find duplicate PartIds under the same Craft that appeared in the latest hierarchy change.
	private static List<Part> FindNewDuplicateParts()
	{
		Dictionary<string, List<Part>> partsByCraftAndId = new Dictionary<string, List<Part>>();
		foreach (Part part in FindSceneParts())
		{
			if (part.PartId <= 0)
			{
				continue;
			}

			Craft craft = part.GetComponentInParent<Craft>();
			if (craft == null)
			{
				continue;
			}

			string key = craft.GetInstanceID() + ":" + part.PartId;
			if (!partsByCraftAndId.TryGetValue(key, out List<Part> parts))
			{
				parts = new List<Part>();
				partsByCraftAndId.Add(key, parts);
			}

			parts.Add(part);
		}

		List<Part> duplicates = new List<Part>();
		foreach (List<Part> parts in partsByCraftAndId.Values)
		{
			if (parts.Count <= 1)
			{
				continue;
			}

			foreach (Part part in parts)
			{
				if (!KnownPartInstanceIds.Contains(part.GetInstanceID()))
				{
					duplicates.Add(part);
				}
			}
		}

		return duplicates;
	}

	// 如果用户复制了整个分类节点，则删除该复制出的分类根；否则只删除重复 Part。 / Remove the copied group root when every descendant Part is a new duplicate; otherwise remove only the duplicate Part.
	private static GameObject FindDuplicateRemovalRoot(Part duplicate, HashSet<int> duplicatePartInstanceIds)
	{
		if (duplicate == null || duplicate.gameObject == null)
		{
			return null;
		}

		GameObject removalRoot = duplicate.gameObject;
		Craft craft = duplicate.GetComponentInParent<Craft>();
		Transform craftTransform = craft != null ? craft.transform : null;
		Transform candidate = duplicate.transform.parent;
		while (candidate != null && candidate != craftTransform)
		{
			if (candidate.GetComponent<Part>() != null || !ContainsOnlyDuplicateParts(candidate, duplicatePartInstanceIds))
			{
				break;
			}

			removalRoot = candidate.gameObject;
			candidate = candidate.parent;
		}

		return removalRoot;
	}

	// 判断一个分类节点下是否只包含本轮复制出来的重复 Part。 / Check whether a group contains only duplicate Parts from this copy operation.
	private static bool ContainsOnlyDuplicateParts(Transform root, HashSet<int> duplicatePartInstanceIds)
	{
		int partCount = 0;
		foreach (Part part in root.GetComponentsInChildren<Part>(includeInactive: true))
		{
			partCount++;
			if (!duplicatePartInstanceIds.Contains(part.GetInstanceID()))
			{
				return false;
			}
		}

		return partCount > 0;
	}

	// 返回所有场景里的 Part，排除 Project 资源和 Prefab Asset。 / Return scene Parts only, excluding Project assets and Prefab assets.
	private static IEnumerable<Part> FindSceneParts()
	{
		foreach (Part part in Resources.FindObjectsOfTypeAll<Part>())
		{
			if (part == null || part.gameObject == null || !part.gameObject.scene.IsValid())
			{
				continue;
			}

			yield return part;
		}
	}
}
