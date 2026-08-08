# Exhibit Visuals 配置工具使用说明

## 概述

本工具集用于自动配置 `VirtualGuidance_B_Root/ExhibitVisuals` 下的 E1-E8 展品，包括位置摆放、锚点创建和元数据挂载。

---

## 快速开始（推荐）

### 一键配置所有内容

1. 在 Unity 菜单栏选择：**VirtualGuidance > Quick Setup > 1. Configure All Exhibits + Update JSON**
2. 确认对话框，点击"确认"
3. 等待配置完成
4. 保存场景（Ctrl+S）

**此操作将：**
- 自动查找 `VirtualGuidance_B_Root/ExhibitVisuals` 节点
- 为 E1-E8 设置正确的位置和旋转
- 创建 ViewPoint 和 GazeTarget 子对象
- 添加并配置 ExhibitMetadata 组件
- 更新 `Assets/Configs/scene_semantics.json` 为 v2 格式
- 自动备份原 JSON 文件为 `.backup`

---

## 分步操作菜单

如果您希望分步执行，可以使用以下菜单：

### 1. 仅配置展品
**VirtualGuidance > Quick Setup > 2. Only Configure Exhibits**

- 只配置展品的位置、锚点和元数据
- 不修改 JSON 文件

### 2. 仅更新 JSON
**VirtualGuidance > Quick Setup > 3. Only Update JSON**

- 根据场景中已有的展品配置生成/更新 JSON
- 不修改场景中的展品

### 3. 验证配置
**VirtualGuidance > Quick Setup > 4. Validate Configuration**

- 检查所有展品是否正确配置
- 生成验证报告，列出错误和警告

---

## 高级工具（窗口模式）

### Exhibit Visuals Configurator
**VirtualGuidance > Configure Exhibit Visuals**

打开窗口进行详细配置：
- 手动选择 ExhibitVisuals Root
- 查看调试信息
- 单独执行配置或验证

### Scene Semantics Updater
**VirtualGuidance > Update Scene Semantics JSON**

打开窗口管理 JSON 配置：
- 自定义 JSON 文件路径
- 生成新的 v2 格式 JSON
- 导出为其他文件名

---

## 配置内容详解

### 每个展品将被配置：

#### 1. 位置和旋转
- 根据设计文档的 12m x 14m 布局设置世界坐标
- 中轴展品（E1/E2/E5/E8）朝向入口（Yaw 180°）
- 左侧展品（E3/E6）朝向中心（Yaw 90°）
- 右侧展品（E4/E7）朝向中心（Yaw -90°）
- 根节点 Scale 保持 (1,1,1)

#### 2. 子对象结构
```
E*_Visual (Scale = 1,1,1)
├── VisualContent (视觉内容，可缩放)
├── ViewPoint (观看位置锚点)
└── GazeTarget (注视目标锚点)
```

#### 3. ExhibitMetadata 组件
- **ID**: E1-E8
- **Exhibit Name**: 中文名称（如"智造序章"）
- **Description**: 中文描述
- **Semantic Role**: 实验任务角色（如 introduction, main_target）
- **Tags**: 英文标签数组（用于程序逻辑）
- **Related Exhibits**: 相关展品 ID 列表
- **ViewPoint**: 引用到 ViewPoint 子对象
- **GazeTarget**: 引用到 GazeTarget 子对象

---

## 展品配置数据

| ID | 名称 | 位置 (x,y,z) | 朝向 | ViewPoint | GazeTarget |
|---|---|---|---:|---|---|
| E1 | 智造序章 | (0, 0, 2.20) | 180° | (0, 0, 0.80) | (0, 1.25, 2.20) |
| E2 | 机械化起步 | (0, 0, 5.42) | 180° | (0, 0, 3.82) | (0, 1.35, 5.42) |
| E3 | 传动与控制 | (-4.35, 0, 3.80) | 90° | (-2.70, 0, 3.80) | (-4.35, 1.30, 3.80) |
| E4 | 数字化转型 | (4.35, 0, 3.80) | -90° | (2.70, 0, 3.80) | (4.35, 1.30, 3.80) |
| E5 | 智能机器人 | (0, 0, 8.83) | 180° | (0, 0, 7.08) | (0, 1.40, 8.83) |
| E6 | 仓储与物流 | (-4.35, 0, 10.50) | 90° | (-2.55, 0, 10.50) | (-4.35, 1.40, 10.50) |
| E7 | 搬运方式对比 | (4.35, 0, 10.50) | -90° | (2.55, 0, 10.50) | (4.35, 1.40, 10.50) |
| E8 | 智能工厂全景 | (0, 0, 12.45) | 180° | (0, 0, 10.65) | (0, 1.45, 12.45) |

---

## 验证清单

配置完成后，验证工具会检查：

### 必须通过的检查（错误）
- ✓ E1-E8 节点全部存在且激活
- ✓ 每个节点有 ExhibitMetadata 组件
- ✓ ViewPoint 和 GazeTarget 引用不为空
- ✓ ID 字段不为空且正确
- ✓ related_exhibits 引用的 ID 都存在

### 建议检查（警告）
- ✓ 根节点 Scale 为 (1,1,1)
- ✓ 节点处于激活状态

---

## scene_semantics.json v2 格式

配置工具会生成/更新 JSON 为以下格式：

```json
{
  "schema_version": 2,
  "scene_id": "dual_world_gallery",
  "scene_name": "Dual World Gallery - 12m x 14m Layout",
  "coordinate_system": "Unity X-Z ground plane, Y-up",
  "layout": {
    "units": "meters",
    "floor_origin": [0, 0, 0],
    "floor_center": [0, 0, 7],
    "floor_size_m": { "width": 12, "length": 14 },
    "floor_bounds": { "x_min": -6, "x_max": 6, "z_min": 0, "z_max": 14 },
    "entrance_side": "z_min"
  },
  "exhibits": [
    {
      "id": "E1",
      "name": "智造序章",
      "scene_path": "VirtualGuidance_B_Root/ExhibitVisuals/E1_Visual",
      "semantic_role": "introduction",
      "position": [0.00, 0.00, 2.20],
      "rotation_y_degrees": 180,
      "viewpoint": [0.00, 0.00, 0.80],
      "gaze_target": [0.00, 1.25, 2.20],
      "tags": ["introduction", "overview", "smart_manufacturing", "start"],
      "related_exhibits": ["E2", "E3", "E4"],
      "description": "工业技术从机械化走向数字化与智能化。"
    },
    ...
  ]
}
```

---

## 注意事项

1. **备份场景**：首次使用前建议备份场景文件
2. **Undo 支持**：所有操作支持 Ctrl+Z 撤销
3. **自动备份 JSON**：原 JSON 会自动备份为 `.backup` 文件
4. **保存场景**：配置完成后记得保存场景（Ctrl+S）
5. **不修改旧展品**：工具只操作 `VirtualGuidance_B_Root` 下的新展品，不会修改停用的旧 `Exhibits` 节点

---

## 故障排查

### 找不到 VirtualGuidance_B_Root
- 确认场景中存在该节点
- 确认节点名称拼写正确

### 找不到 ExhibitVisuals
- 确认 `VirtualGuidance_B_Root` 下存在 `ExhibitVisuals` 子节点
- 确认节点名称拼写正确

### 某些展品配置失败
- 确认节点名称为 `E1_Visual` 到 `E8_Visual`
- 检查 Console 查看详细错误信息
- 使用"Validate Configuration"查看具体问题

### JSON 更新失败
- 确认 `Assets/Configs` 目录存在
- 检查文件权限
- 查看 Console 的详细错误信息

---

## 工作流程建议

### 首次配置
1. 打开 MainScene
2. 运行：**Quick Setup > 1. Configure All Exhibits + Update JSON**
3. 运行：**Quick Setup > 4. Validate Configuration**
4. 保存场景

### 调整单个展品后
1. 在 Inspector 中手动调整展品
2. 运行：**Quick Setup > 3. Only Update JSON**
3. 保存场景

### 修改 JSON 后应用到场景
1. 手动编辑 `scene_semantics.json`
2. 运行：**Quick Setup > 2. Only Configure Exhibits**
3. 保存场景

---

## 脚本文件

- `Assets/Editor/ExhibitVisualsQuickSetup.cs` - 快速配置菜单（推荐）
- `Assets/Editor/ExhibitVisualsConfigurator.cs` - 配置窗口（高级）
- `Assets/Editor/SceneSemanticsUpdater.cs` - JSON 更新窗口（高级）

---

## 联系与反馈

如有问题或建议，请查看 Console 输出的详细日志，或联系开发团队。

**版本**: 1.0  
**日期**: 2026-08-06
