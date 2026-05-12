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

    //所有的裁切网格数据    
    public List<MeshData> cutterMeshDataList;
    public List<Matrix4x4> cutterMeshMatrixList;


}