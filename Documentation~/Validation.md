# 验证说明

环境：Unity 6000.3.27f1、Windows。验证区分编译、隔离 Unity 原生渲染和真实项目资源迁移，不把编译通过当作所有设计稿视觉正确。

## 功能回归

以下是维护阶段执行过的检查，检查项有交叉，不应简单相加作为覆盖率：

| 范围 | 检查 |
| --- | --- |
| 软阴影 | 239 项：真实按钮与弹窗，默认/自动/手动合图、导出缩放、偏移/旋转、留边、尺寸/文字/对象映射及重复输出 |
| PNG/SVG 切换 | 35 项：几何 Sprite、Graphic 替换、targetGraphic、GUID/对象标识、空占位框、AO、二次导出与 Canvas 辅助组件 |
| 图层搜索与性能 | 118 项：严格搜索、命名、范围勾选、一万行列表及真实公共资源画板，视口只绘制附近行 |
| 原生 Shift 点击 | 13 项：画板与合图勾选框/标题，正反向选择和取消、搜索过滤及隐藏锚点 |
| Linear 透明混合与共享材质 | 51 项：PNG、颜色 alpha、CanvasGroup、SVG、渐变、TMP、RectMask2D、stencil Mask、动态透明度、参考底色和共享/独立材质 |
| Gamma | 48 项：确认不启用 Linear 补偿，保留原始混合 |
| 真实 SVG 渐变 | 14 项：两个真实 XD 矩形按钮，透明与斜向高光、端点精度、换父 Canvas、已有通道及尺寸颜色；采样区与独立栅格参考 RGB 误差为 0 |

图片示例取自无文字的几何按钮渲染，不包含原项目的 XD、字体或业务资源。

## UniXD 迁移验证

迁移时编译了 7 个 UniXD 自有程序集，以及 Logging、UIPanelStack、VContainer 扩展和项目 Core/Runtime/Editor 消费者，共 14 个程序集。内置 Utf8Json 保留上游程序集身份。

已把仓库根作为本地 UPM 包安装到隔离 Unity 工程，移除了原测试工程预编译的旧 DLL 和占位包，直接编译仓库源码。最终 API 简化后复跑：PNG/SVG 切换35项、原生 Shift13项、真实渐变14项、Linear透明与共享材质55项、搜索/范围选择/大列表118项、软阴影239项，均通过。材质检查项数量包含已生成文件的重复写入检查，不同缓存状态会影响该数量，不作为独立覆盖率指标。

另外直接加载了改名前生成的 Meta Prefab，确认 `PrefabMeta`、根对象和 EID 映射仍可读取且没有丢失脚本；旧 `akyuiOutputPath` 字段可读取到新的 `layoutOutputPath`。迁移保留230个既有 GUID，仅退役两个被合并的嵌套 package.json GUID；没有重复 GUID。

测试使用宿主提供的 XD 和字体夹具；它们不随公开工具包分发。机器可读摘要见 [ValidationResults.json](ValidationResults.json)。

## 切片正确性与可选加速

2026-11-05：修复非零容差的渐变累计误判，增加可选 Burst 1.8+ / Jobs 后端，并减少未切片图片的分配及编码。原生专项81项、公开 EditMode 测试12项全部通过，覆盖渐变、合法九宫格、四种像素格式的 CPU/Burst 一致性、输入只读、无需切片时保留原字节以及 PNG/JPEG 尺寸读取。未安装 Burst 的独立源包工程也通过编译及 C# 后备路径检查。本轮复跑 PNG/SVG 35项、软阴影239项均通过。可选后端为第8个自有程序集，不改变迁移阶段14个程序集的统计口径。

测试源码见 `Tests~/Editor`：将该目录复制到测试工程 `Assets/UniXDTests`，安装 Unity Test Framework，运行 EditMode 测试。Burst 专项在未安装 Burst 时跳过。实测环境、首次编译限制和性能口径见 [SlicingPerformance.md](SlicingPerformance.md)。

## 验证边界

未声称验证所有 Unity 版本、所有 XD 特效、自定义多 Pass Shader、所有字体或所有移动 GPU。项目框架的业务场景、服务器、真机 IL2CPP、发布包与热更新基线不属于这个工具仓库的通用测试产物。运行时程序集改名后，具体项目应重建自己的主包及相关裁剪/热更新生成物。
