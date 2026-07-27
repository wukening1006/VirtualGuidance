# Gallery Layout V2

本目录记录 12m × 14m 展厅布局与语义同步闭环的验收产物。

## 场景基线

- 场景：`Assets/Scenes/MainScene.unity`
- 视觉根：`VirtualGuidance_B_Root`
- 展品根：`VirtualGuidance_B_Root/ExhibitVisuals/E1_Visual` 至 `E8_Visual`
- 配置：`Assets/Configs/scene_semantics.json`（schema v2）
- 实验契约：E1–E8、`semanticRole`、任务路线和后端接口保持不变
- 真实 RDW 空间：`physical_*.json` 不参与本布局同步

## 编辑器入口

在 Unity 菜单 `VirtualGuidance` 中使用：

- `Gallery Layout V2/Build Complete Layout`：规范化 Prefab、应用布局、安装场景、截图并验证
- `Scene Semantics/Validate`：只读比较 JSON、场景、Metadata、锚点、边界和净距
- `Scene Semantics/Apply To Scene`：以 JSON 覆盖场景布局与语义
- `Scene Semantics/Export From Scene`：仅导出坐标、旋转、锚点和柜体尺寸，不覆盖语义文本

## 验收结果

- 有效地面为 12m × 14m，中心 `(0, 0, 7)`
- 激活的 `ExhibitMetadata` 恰好 8 个，E 根节点缩放均为 `(1, 1, 1)`
- 墙体与天花板最终启用，旧 `Environment` 与 `Exhibits` 根保持停用
- E8 按确认例外保留 0.8m 后距
- 测量结果见 `LayoutValidationReport.txt`
- 入口、俯视和 XR 模拟截图分别见 `Final_Entrance.png`、`Final_Top.png`、`Final_XR_Sim.png`
- Unity EditMode 测试 `93/93` 通过，后端测试 `65/65` 通过
- MainScene 已验证 E1–E8 注册、高亮、`look_at_target`、到达单次触发和 T2 绕行/终点逻辑
- 完整测试记录见 `AcceptanceTestReport.md`

## 依赖说明

- 项目使用 Unity 2022.3.62f3、OpenXR 和 XR Interaction Toolkit，可直接进行 Editor Simulation。
- `Newtonsoft.Json` 已改为显式注册表依赖，避免依赖编辑器插件间接提供运行库。
- 已移除指向个人电脑 `D:` 盘的 PICO SDK、本地 Live Preview 路径，以及两个非运行必需的联网编辑器插件。
- 若之后进行 PICO 真机部署，请在目标电脑通过 Package Manager 重新安装匹配版本的 PICO SDK；这不影响本轮无设备验收。
