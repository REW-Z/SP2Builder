using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using UnityEditor;


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
        if(stateElement == null)
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
        if(string.IsNullOrWhiteSpace(_rawStateXml) && string.IsNullOrWhiteSpace(_designText))
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
        if(_labelObject != null && _textMesh != null)
        {
            return;
        }

        Transform existing = transform.Find("LabelPreview");
        if(existing != null)
        {
            _labelObject = existing.gameObject;
            _textMesh = _labelObject.GetComponent<TextMesh>();
        }
        if(_labelObject == null)
        {
            _labelObject = new GameObject("LabelPreview");
            _labelObject.transform.SetParent(transform, false);
        }

        if(_textMesh == null)
        {
            _textMesh = _labelObject.GetComponent<TextMesh>();
            if(_textMesh == null)
            {
                _textMesh = _labelObject.AddComponent<TextMesh>();
            }
        }

        MeshRenderer renderer = _labelObject.GetComponent<MeshRenderer>();
        if(renderer != null)
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
        if(_previewRefreshQueued)
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

        if(this == null || gameObject == null)
        {
            return;
        }

        RefreshPreview();
    }
}
