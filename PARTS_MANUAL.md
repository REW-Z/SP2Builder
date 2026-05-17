# 当前支持的零件说明书

这份文档只描述当前工程里已经实现到可导入、可编辑、可预览、可导出的零件类型。
它反映的是当前代码状态，不等同于原游戏的完整零件覆盖范围。

## 1. 当前支持的零件类型

| 运行时组件 | 导入识别规则 | 基本形状 | 主要用途 |
| --- | --- | --- | --- |
| `FuselagePart` | `partType` 以 `JFuselage` 或 `Fuselage` 开头，或 XML 中存在 `JFuselage.State` / `Fuselage.State` | 可变截面 loft 机身 | 主体机身、锥体机身、空心机身 |
| `WindowPart` | `partType` 以 `ModifierWindow` 开头 | 圆角梯形/圆角窗框的拉伸体 | 作为机身切割器，开窗 |
| `BayPart` | `partType` 以 `ModifierBay` 开头 | 圆角矩形拉伸体 | 作为机身切割器，开舱口/舱位 |
| `OtherPart` | 以上规则都不匹配 | 无实体预览，仅 Gizmo 占位 | 保留未知零件的 XML、位置、连接与基础元数据 |

## 2. 所有零件的通用参数

所有零件都继承自 `Part`，因此都共享下面这些通用数据。

| 参数 | 含义 | 说明 |
| --- | --- | --- |
| `Part Id` | 零件唯一 id | 主要用于连接、目标选择、XML 往返；Inspector 中只读 |
| `Part Type` | 原始零件类型字符串 | 用于导入判定与导出回写；Inspector 中只读 |
| `position / rotation / scale` | 变换 | 所有零件都支持位置、旋转、缩放 |
| `materials` | 材质槽文本 | 直接对应 XML 的 `materials` 属性 |
| `Connection Endpoints` | 连接端点列表 | 可手动编辑连接关系，机身平滑也依赖它 |
| `Target Mode` | 目标模式 | 主要给 `WindowPart` / `BayPart` 使用 |
| `Target Part Ids` | 目标零件 id 列表 | 切割器只有显式命中目标机身时才会生效 |
| `Cached Part XML / Cached State XML` | 缓存 XML | 方便调试与往返导出，不是日常造型参数 |

说明：

- `WindowPart` 和 `BayPart` 是否真正切到某个机身，取决于 `Target Part Ids` 是否包含目标 `FuselagePart` 的 `Part Id`。
- `OtherPart` 没有自定义状态块，因此基本只依赖这些通用字段。

## 3. FuselagePart

### 3.1 形状类型

`FuselagePart` 是当前最完整的零件类型，几何本质是“后截面到前截面”的 loft。

| `Visual Style` | 形状 | 说明 |
| --- | --- | --- |
| `Body` | 实心机身 | 类似可变截面的圆筒/机身段 |
| `Hollow` | 空心机身 | 外壳 + 内腔，类似管道 |
| `Cone` | 实心锥体机身 | 用于鼻锥、锥形过渡段 |
| `HollowCone` | 空心锥体机身 | 空心鼻锥/空心锥形壳体 |

补充：

- `Glass` 不是一种独立几何类型，它主要影响材质/导出类型字符串，不改变外轮廓的基本类别。
- `Serialization Mode` 只影响 XML 读写格式，不改变预览几何的生成路径。

### 3.2 主参数

| 参数 | 含义 | 说明 |
| --- | --- | --- |
| `Serialization Mode` | 序列化格式 | `JFuselage` 或 `LegacyFuselage` |
| `Visual Style` | 机身视觉类型 | `Body` / `Hollow` / `Cone` / `HollowCone` |
| `Nosecone Roundness` | 锥体圆滑度 | 仅 `Cone` / `HollowCone` 显示，范围 `0..1` |
| `Glass` | 玻璃标记 | 影响材质与导出 `partType` |
| `Offset (Length / Rise / Run)` | 前后截面中心偏移 | 可以同时控制长度和前后截面的空间偏移 |

### 3.3 前后截面参数

`FuselagePart` 有两个独立截面：`Rear Section` 和 `Front Section`。
它们决定机身两端的外形，运行时会在两者之间做 loft。

每个截面都包含同一套参数：

| 参数 | 含义 | 说明 |
| --- | --- | --- |
| `Width` | 截面宽度 | 外轮廓宽度 |
| `Height` | 截面高度 | 外轮廓高度 |
| `Trapezium` | 梯形度 | 控制上下宽度差带来的梯形倾斜 |
| `Thickness` | 厚度 | 主要影响 `Hollow` / `HollowCone` 的内腔厚度 |
| `Smooth` | 接缝平滑 | 控制该端面是否参与邻接机身法线平滑 |

#### Corner Styles

每个截面有四个角：`Top Right`、`Bottom Right`、`Bottom Left`、`Top Left`。

每个角都可以设置为：

| 模式 | 含义 | 输入方式 |
| --- | --- | --- |
| `Rounded` | 圆角 | 以米为单位输入半径 |
| `Stretched` | 拉伸圆角 | 以百分比输入归一化半径 |

#### Edge Curvature

每个截面有四条边：`Right`、`Bottom`、`Left`、`Top`。

| 参数 | 含义 |
| --- | --- |
| `Edge Curvature` | 控制对应边从直线过渡成弧线/鼓包的程度 |

#### Slice Cutting

每个截面四个方向都可以做切边：`Top`、`Right`、`Bottom`、`Left`。

| 参数 | 含义 |
| --- | --- |
| `Slice Cutting` | 在该侧启用切边，并用滑块控制切边量 |

### 3.4 当前支持的额外行为

| 功能 | 说明 |
| --- | --- |
| 相邻机身法线平滑 | 通过连接端点或空间匹配对接缝做 smoothing |
| `CopySectionRear` / `CopySectionFront` | 从已连接机身复制对应端截面 |
| `Auto Connect Selected` | 自动连接两段选中的机身 |
| `Snap Selected Rear` / `Snap Selected Front` | 将机身端面对齐到已连接邻居 |
| 定向切割 | 被 `WindowPart` / `BayPart` 显式目标选中后，可执行布尔切割 |

### 3.5 当前注意事项

- `Thickness` 对实心 `Body` / `Cone` 的外轮廓没有直接作用，主要用于空心样式。
- `Smooth` 只决定该端面是否允许参与接缝法线平滑，不会改变本体截面形状。
- `Glass` 目前更接近“材质/类型标记”，不是独立造型参数。

## 4. WindowPart

### 4.1 形状

`WindowPart` 是一个用于切割机身的程序化窗框。
它的二维轮廓是“上边和下边可分别设置跨度的四边形”，再经过圆角处理，最后沿深度方向拉伸为一个闭体切割器。

可以把它理解成：

- 一个可做成梯形的圆角窗洞
- 一个只在被显式指定目标时才会参与机身布尔的 cutter

### 4.2 参数

| 参数 | 含义 | 说明 |
| --- | --- | --- |
| `Upper Span` | 上边左右范围 | `Vector2(xMin, xMax)` |
| `Lower Span` | 下边左右范围 | `Vector2(xMin, xMax)` |
| `Height` | 窗洞高度 | 决定上下边距离 |
| `Depth` | 切割深度 | 沿局部 z 方向拉伸 |
| `Corner Radius` | 圆角半径 | 作用于二维轮廓 |
| `Hide Glass` | 隐藏玻璃标记 | 当前主要用于 XML 状态保存 |
| `Target Mode` / `Target Part Ids` | 目标选择 | 决定切哪个 `FuselagePart` |

### 4.3 当前行为

| 行为 | 说明 |
| --- | --- |
| 预览 | 线框拉伸体 |
| 实际切割 | 构建闭体 `PreviewMeshData`，对目标机身做布尔减法 |
| 无目标时 | 不会切割任何机身 |

### 4.4 当前注意事项

- `Hide Glass` 目前会被读取和写回 XML，但不会改变当前切割器几何。
- `WindowPart` 没有专用 Inspector，走通用 `PartEditor`，所以字段名会按序列化字段直接显示。

## 5. BayPart

### 5.1 形状

`BayPart` 是一个用于切割机身的程序化舱口/舱位 cutter。
它的二维轮廓是圆角矩形，然后沿深度方向拉伸为闭体。

可以把它理解成：

- 一个圆角矩形开口
- 一个比 `WindowPart` 更接近“舱门/舱位”的简单切割器

### 5.2 参数

| 参数 | 含义 | 说明 |
| --- | --- | --- |
| `Width` | 舱口宽度 | 二维轮廓宽度 |
| `Height` | 舱口高度 | 二维轮廓高度 |
| `Depth` | 切割深度 | 沿局部 z 方向拉伸 |
| `Corner Radius` | 圆角半径 | 作用于二维轮廓 |
| `Door Style` | 门样式字符串 | 当前主要用于 XML 状态保存 |
| `Start Open` | 初始是否开启 | 当前主要用于 XML 状态保存 |
| `Target Mode` / `Target Part Ids` | 目标选择 | 决定切哪个 `FuselagePart` |

### 5.3 当前行为

| 行为 | 说明 |
| --- | --- |
| 预览 | 线框拉伸体 |
| 实际切割 | 构建闭体 `PreviewMeshData`，对目标机身做布尔减法 |
| 无目标时 | 不会切割任何机身 |

### 5.4 当前注意事项

- `Door Style` 和 `Start Open` 目前只做状态存储，不会改变当前预览或切割体几何。
- `BayPart` 同样没有专用 Inspector，走通用 `PartEditor`。

## 6. OtherPart

### 6.1 形状

`OtherPart` 是所有暂未专门实现零件的兜底组件。

当前表现为：

- 不生成渲染网格
- 仅在 Scene 里绘制一个轻量的线框球 Gizmo 作为占位

### 6.2 参数

`OtherPart` 没有自己的专用状态参数，只有通用 `Part` 参数：

- 位置、旋转、缩放
- `materials`
- 连接端点
- 目标选择
- 缓存 XML

### 6.3 主要用途

- 导入未知 `partType` 时不至于丢失零件
- 保留原始 XML 和场景位置
- 在编辑器里有一个可见占位物，方便后续手工处理或继续实现

## 7. Inspector 支持情况

| 零件 | Inspector 情况 |
| --- | --- |
| `FuselagePart` | 有专用 Inspector，参数分组最完整 |
| `WindowPart` | 走通用 `PartEditor` |
| `BayPart` | 走通用 `PartEditor` |
| `OtherPart` | 走通用 `PartEditor` |

## 8. 目前实现范围总结

如果只从“当前已经真正支持到可以工作”的角度看，可以简单理解为：

- `FuselagePart`：可变截面机身，支持实心、空心、锥体、空心锥体
- `WindowPart`：程序化开窗 cutter
- `BayPart`：程序化开舱 cutter
- `OtherPart`：未知零件占位

如果后面继续扩展新的零件类型，建议直接在本文件追加新章节，并同步更新第 1 节总览表。