# 原版对照与统计

对照的是 2026-11-05 拉取的作者仓库源码，并非根据原作者停止维护的时间推断差异。

| 模块 | 上游路径 | 上游提交 |
| --- | --- | --- |
| Core | kyubuns/AkyuiUnity `Assets/AkyuiUnity` | `ab89b5a842c0787c98edc1204ce8eceff5445b2e` |
| XD | kyubuns/AkyuiUnity `Assets/AkyuiUnity.Xd` | `ab89b5a842c0787c98edc1204ce8eceff5445b2e` |
| Slicing | kyubuns/Auto9Slicer `Assets/Auto9Slicer` | `c466144f5af0f20b504fbe76d562bc1e32614b90` |

基线链接：[AkyuiUnity](https://github.com/kyubuns/AkyuiUnity/tree/ab89b5a842c0787c98edc1204ce8eceff5445b2e)、[Auto9Slicer](https://github.com/kyubuns/Auto9Slicer/tree/c466144f5af0f20b504fbe76d562bc1e32614b90)。

## C# 源码规模

| 模块 | 上游文件数 | UniXD 文件数 | 新增文件 | 改动原有文件 | 未变文件 | 上游行数 | UniXD 行数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Core | 30 | 34 | 4 | 22 | 8 | 3,135 | 5,341 |
| XD | 27 | 40 | 13 | 16 | 11 | 4,414 | 7,316 |
| Slicing | 4 | 5 | 1 | 2 | 2 | 295 | 481 |
| 合计 | 61 | 79 | 18 | 40 | 21 | 7,844 | 13,138 |

统计口径：按模块和重命名前的文件身份比较 `.cs`，排除内置 Utf8Json；忽略换行差异，并还原命名空间、主要类型名和编译符号后比较，避免仅重命名就全部算成修改。原版已有 TMP 和九宫格，不能将它们整体计作新增功能。改动文件数量包含菜单、迁移标记等维护调整，行数包括注释与空行；它们用于描述规模，不代表测试覆盖率或独立功能数。

## 主要新增源文件

- `Core/Editor/ImportContentCache.cs`：内容和处理缓存。
- `Core/Editor/CommonTrigger/SharedSpriteResourceTrigger.cs`：共享资源库与迁移。
- `Core/Editor/CommonTrigger/SpriteAtlasAreaFilter.cs`：图集面积和类型策略。
- `Core/Runtime/VectorCanvasChannels.cs`：SVG 渐变 Canvas 通道。
- `Xd/Editor/XdArtboardSelectionWindow.cs`：画板选择与图层配置窗口。
- `Xd/Editor/XdFlattenWindowView.cs`：缓存、严格搜索、视口绘制与范围勾选。
- `Xd/Editor/XdExportHistory.cs`、`XdExportRules.cs`、`XdComponentPreview.cs`：图层决定、命名与组件预览。
- `Xd/Editor/XdAutomationRecipe.cs`：可保存与审核的导出计划。
- `Xd/Editor/Svg/XdShadowRenderer.cs`：独立高斯软阴影。
- `Xd/Editor/Svg/XdVectorExport.cs`：矢量输出与合图边界。
- `Xd/Editor/XdTransparencyMaterials.cs`：最终混合材质生成与共享。
- `Xd/Extensions/AutoComponentRulesTrigger.cs`、`AutoFlattenVectorGroupParser.cs`、`ConfiguredFlattenGroupParser.cs`、`NormalizeLayerNamesTrigger.cs`：可配置工作流。

新增源码之外还包括透明混合 Shader、包结构和文档。完整功能与限制以 README 及实际实现为准。

切片修复后新增可选 `Slicing/Burst/BurstSlicer.cs`，加入整段容差检查、严格模式快速路径及不切片时保留原文件的路径。独立 `Tests~` 不纳入上述三个实现模块的源码数量。
