using System;
using System.Collections.Generic;

using UnityEngine;

public class MeshProcessJob
{
    public Matrix4x4 worldMatrix;

    public FuselageSectionSettings rearSection;
    public FuselageSectionSettings frontSection;
    public Vector3 offset;
    public bool capRear;
    public bool capFront;
    public bool applySectionCutting;
    public bool hollow;
    public int version;

    //所有的裁切体预览网格数据    
    public List<PreviewMeshData> cutterPreviewDataList;
    public List<Matrix4x4> cutterMeshMatrixList;


    //后台计算结果
    public PreviewMeshData resultMeshData;
    public Exception error;

}