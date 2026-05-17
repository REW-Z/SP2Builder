using UnityEngine;


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

    // 为普通零件绘制轻量的球形占位 Gizmo。 / Draw a lightweight spherical placeholder gizmo for generic parts.
    private void OnDrawGizmos()
    {
        Event currentEvent = Event.current;
        if(currentEvent != null && currentEvent.type != EventType.Repaint)
        {
            return;
        }
        Gizmos.color = new Color(0.65f, 0.75f, 0.95f, 0.75f);
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireSphere(Vector3.zero, GizmoRadius);
        Gizmos.matrix = oldMatrix;
    }

    // 保留基类的连接可视化绘制。 / Keep the base connection visualization when the generic part is selected.
    protected override void OnDrawGizmosSelected()
    {
        base.OnDrawGizmosSelected();
    }

    // 移除挂在 OtherPart 上的临时渲染预览组件。 / Remove transient render preview components attached to OtherPart.
    private void RemoveRenderPreview()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if(meshFilter != null)
        {
            if(meshFilter.sharedMesh != null)
            {
                DestroyOwnedObject(meshFilter.sharedMesh);
            }
            DestroyOwnedObject(meshFilter);
        }

        MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
        if(meshRenderer != null)
        {
            DestroyOwnedObject(meshRenderer);
        }
    }
}