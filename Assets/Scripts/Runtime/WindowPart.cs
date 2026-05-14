using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WindowPart : Part, IFuselageCarver
{
    [SerializeField, TextArea(2, 8)]
    private string _rawStateXml;

    [SerializeField]
    private Vector2 _upperSpan = new Vector2(-0.2f, 0.2f);

    [SerializeField]
    private Vector2 _lowerSpan = new Vector2(-0.2f, 0.2f);

    [SerializeField]
    private float _height = 0.6f;

    [SerializeField]
    private float _depth = 0.3f;

    [SerializeField]
    private float _cornerRadius = 0.2f;

    [SerializeField]
    private bool _hideGlass;

    private MeshFilter _meshFilter;

    private MeshRenderer _meshRenderer;

    // 从 Part XML 中读取 Window 的参数状态。 / Read the procedural window parameters from the part XML.
    protected override void LoadPartState(XElement partElement)
    {
        if(partElement.Element("ProceduralWindow.State") is not XElement stateElement)
        {
            return;
        }
        _rawStateXml = stateElement.ToString(SaveOptions.DisableFormatting);
        _upperSpan = XmlUtil.ParseVector2((string)stateElement.Attribute("upperSpan"), _upperSpan);
        _lowerSpan = XmlUtil.ParseVector2((string)stateElement.Attribute("lowerSpan"), _lowerSpan);
        _height = XmlUtil.ParseFloat((string)stateElement.Attribute("height"), _height);
        _depth = XmlUtil.ParseFloat((string)stateElement.Attribute("depth"), _depth);
        _cornerRadius = XmlUtil.ParseFloat((string)stateElement.Attribute("cornerRadius"), _cornerRadius);
        _hideGlass = XmlUtil.ParseBool((string)stateElement.Attribute("hideGlass"));
    }

	// 把当前 Window 参数写回 ProceduralWindow.State XML。 / Write the current window parameters back into the ProceduralWindow.State XML.
    protected override void WritePartState(XElement partElement)
    {
        XElement stateElement = string.IsNullOrWhiteSpace(_rawStateXml) ? new XElement("ProceduralWindow.State") : XElement.Parse(_rawStateXml);
        stateElement.Name = "ProceduralWindow.State";
        stateElement.SetAttributeValue("cornerRadius", XmlUtil.FormatFloat(_cornerRadius));
        stateElement.SetAttributeValue("upperSpan", XmlUtil.FormatVector2(_upperSpan));
        stateElement.SetAttributeValue("lowerSpan", XmlUtil.FormatVector2(_lowerSpan));
        stateElement.SetAttributeValue("height", XmlUtil.FormatFloat(_height));
        stateElement.SetAttributeValue("depth", XmlUtil.FormatFloat(_depth));
        stateElement.SetAttributeValue("hideGlass", _hideGlass ? "True" : "False");
        RemoveStateElements(partElement, "ProceduralWindow.State");
        partElement.Add(stateElement);
    }

	// 刷新 Window 在编辑器里的线框预览。 / Refresh the window wireframe preview in the editor.
    public override void RefreshPreview()
    {
        if(RequestCraftPreviewRebuild())
        {
            return;
        }

        base.RefreshPreview();
        EnsureComponent(ref _meshFilter);
        EnsureComponent(ref _meshRenderer);
        DestroyOwnedObject(_meshFilter.sharedMesh);
        _meshFilter.sharedMesh = FuselageCarverUtility.BuildWireframeMesh(BuildOutline(), _depth, "ProceduralWindowWire");
        _meshRenderer.sharedMaterial = PreviewMaterialFactory.GetWindowMaterial(this);
    }

	// 构建用于机身布尔切割的 Window 闭体 PreviewMeshData。 / Build the closed Window PreviewMeshData used for fuselage cutting booleans.
    public bool TryBuildCutPreviewData(FuselagePart target, out PreviewMeshData previewMeshData)
    {
        previewMeshData = null;
        if(!FuselageCarverUtility.CanCarveTarget(this, target))
        {
            return false;
        }

        previewMeshData = FuselageCarverUtility.BuildSolidCutPreviewData(BuildOutline(), _depth, "ProceduralWindowCut");
        return previewMeshData != null && previewMeshData.Vertices.Count > 0;
    }

	// 生成 Window 在局部截面平面中的二维外轮廓。 / Build the 2D local outline used by the window preview and cutter.
    private List<Vector2> BuildOutline()
    {
        return FuselageCarverUtility.BuildWindowOutline(_upperSpan, _lowerSpan, _height, _cornerRadius);
    }
}
