VirtualGuidance B 部分独立场景视觉包
====================================

内容：
1. VirtualGuidance_B_Root.prefab：统一根物体。
2. Environment：room 场馆、灯具、书架、植物和休息区。
3. ExhibitVisuals：E1-E8 玻璃展柜、实体模型和中文铭牌。

导入：
1. 在目标项目中导入 VirtualGuidance_B_ScenePackage.unitypackage。
2. 将 Prefabs/VirtualGuidance_B_Root.prefab 拖入场景根层级。
3. 或使用菜单 VirtualGuidance > B 部分 > 将独立视觉包放入当前场景。

兼容说明：
- 本包只提供视觉内容，不复制后端、导航、RDW、到达检测和实验状态逻辑。
- 如目标场景已有 room 或旧展板，请先禁用对应旧视觉物体，避免重叠。
- E1-E8 的展柜视觉保持原世界坐标，可与原实验节点对齐。
