using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;

using MeshTools;

public class MeshProcessJob
{
    //原始网格数据
    public MeshData meshData;//也许需要改用PreviewMeshData  
    public Matrix4x4 worldMatrix;

    public FuselageSectionSettings rearSection;
    public FuselageSectionSettings frontSection;
    public Vector3 offset;
    public bool capRear;
    public bool capFront;
    public bool applySectionCutting;
    public bool hollow;
    public bool hasPreviousMesh;
    public int version;

    //所有的裁切网格数据    
    public List<MeshData> cutterMeshDataList;
    public List<Matrix4x4> cutterMeshMatrixList;


    //后台计算结果
    public PreviewMeshData resultMeshData;
    public PreviewMeshData fallbackMeshData;
    public Exception error;

}