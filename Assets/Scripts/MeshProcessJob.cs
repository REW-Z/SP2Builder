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
    public bool cone;
    public float noseconeRoundness;
    public int version;

    //所有的裁切体网格数据
    public List<Mesh> cutterMeshList;
    public List<Matrix4x4> cutterMeshMatrixList;


    //计算结果
    public Mesh resultMesh;
    public Exception error;

}
