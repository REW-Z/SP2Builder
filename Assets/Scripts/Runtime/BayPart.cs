using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;


[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class BayPart : Part, IFuselageCarver
{
    private const float WindowEquivalentCutDepth = 0.3f;

    [SerializeField, TextArea(2, 8)]
    private string _rawStateXml;

    [SerializeField]
    private float _width = 0.3f;

    [SerializeField]
    private float _height = 0.6f;

    [SerializeField]
    private float _depth = 0.4f;

    [SerializeField]
    private float _cornerRadius = 0.2f;

    [SerializeField]
    private string _doorStyle = "None";

    [SerializeField]
    private bool _startOpen;

    private MeshFilter _meshFilter;

    private MeshRenderer _meshRenderer;

    protected override void LoadPartState(XElement partElement)
    {
        if(partElement.Element("ProceduralBay.State") is not XElement stateElement)
        {
            return;
        }
        _rawStateXml = stateElement.ToString(SaveOptions.DisableFormatting);
        _width = XmlUtil.ParseFloat((string)stateElement.Attribute("width"), _width);
        _height = XmlUtil.ParseFloat((string)stateElement.Attribute("height"), _height);
        _depth = XmlUtil.ParseFloat((string)stateElement.Attribute("depth"), _depth);
        _cornerRadius = XmlUtil.ParseFloat((string)stateElement.Attribute("cornerRadius"), _cornerRadius);
        _doorStyle = (string)stateElement.Attribute("doorStyle") ?? _doorStyle;
        _startOpen = XmlUtil.ParseBool((string)stateElement.Attribute("startOpen"));
    }

    protected override void WritePartState(XElement partElement)
    {
        XElement stateElement = string.IsNullOrWhiteSpace(_rawStateXml) ? new XElement("ProceduralBay.State") : XElement.Parse(_rawStateXml);
        stateElement.Name = "ProceduralBay.State";
        stateElement.SetAttributeValue("width", XmlUtil.FormatFloat(_width));
        stateElement.SetAttributeValue("height", XmlUtil.FormatFloat(_height));
        stateElement.SetAttributeValue("depth", XmlUtil.FormatFloat(_depth));
        stateElement.SetAttributeValue("cornerRadius", XmlUtil.FormatFloat(_cornerRadius));
        stateElement.SetAttributeValue("doorStyle", _doorStyle);
        stateElement.SetAttributeValue("startOpen", XmlUtil.FormatBool(_startOpen));
        RemoveStateElements(partElement, "ProceduralBay.State");
        partElement.Add(stateElement);
    }

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
        _meshFilter.sharedMesh = FuselageCarverUtility.BuildWireframeMesh(BuildOutline(), GetCutDepth(), "ProceduralBayWire");
        _meshRenderer.sharedMaterial = PreviewMaterialFactory.GetBayMaterial(this);
    }

    public bool TryGetCutPlanes(FuselagePart target, out Plane[] planes)
    {
        planes = null;
        if(!FuselageCarverUtility.CanCarveTarget(this, target))
        {
            return false;
        }

        planes = FuselageCarverUtility.BuildConvexPlanes(target.transform, transform, BuildOutline(), GetCutDepth());
        return planes.Length > 0;
    }

    public bool TryGetCutMesh(FuselagePart target, out Mesh mesh)
    {
        mesh = null;
        if(!FuselageCarverUtility.CanCarveTarget(this, target))
        {
            return false;
        }

        mesh = FuselageCarverUtility.BuildSolidCutMesh(BuildOutline(), GetCutDepth(), "ProceduralBayCut");
        return mesh.vertexCount > 0;
    }

    private List<Vector2> BuildOutline()
    {
        return FuselageCarverUtility.BuildBayOutline(_width, _height, _cornerRadius);
    }

    private float GetCutDepth()
    {
        return Mathf.Min(Mathf.Max(0.01f, _depth), WindowEquivalentCutDepth);
    }
}
