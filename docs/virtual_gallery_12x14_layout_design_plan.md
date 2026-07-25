# VirtualGuidance 12m x 14m 展厅布局与语义同步设计指导计划

## 1. 文档目的

本文档用于指导 `MainScene` 中 `VirtualGuidance_B_Root` 的后续空间改造。目标是在 12m x 14m 的有效虚拟地面内重新布置 8 个展品及展柜，并建立场景对象、展品元数据和 `scene_semantics.json` 之间可检查、可重复执行的同步规则。

本文档是设计草案和实施计划，不代表已经修改场景。首轮尺寸和坐标是可执行的布局基线，最终值必须在 Unity 中基于真实 `Renderer.bounds`、碰撞体和 XR 视角复测后冻结。

## 2. 已确认的项目现状

### 2.1 场景结构

- 当前场景为 `Assets/Scenes/MainScene.unity`。
- 新场景根节点 `VirtualGuidance_B_Root` 已启用。
- 原有根节点 `Environment` 和 `Exhibits` 已停用。
- 新根节点包含 `Environment`、`ExhibitVisuals` 以及 `E1_Visual` 至 `E8_Visual`。
- 新的 `E*_Visual` 当前主要提供视觉对象和 Transform，原有停用展品仍持有 `ExhibitMetadata`。旧 Metadata 的展示名称、描述、标签和关联关系对应旧展品内容，不能直接复制到 B 版本。

### 2.2 运行时依赖

当前运行时并不读取 `Assets/Configs/scene_semantics.json`。实际导航数据来自场景中处于激活状态的 `ExhibitMetadata`：

- `NavigationTargetManager` 通过 `FindObjectsOfType<ExhibitMetadata>()` 建立 E1-E8 索引。
- `ArrivalDetector` 使用 `ExhibitMetadata.ViewPoint` 进行到达判定，默认半径为 0.75m。
- `GuideAvatarController` 使用 `GazeTarget` 执行看向目标动作。
- `ExhibitHighlightManager` 从 `ExhibitMetadata` 根节点向下查找 MeshRenderer 并执行高亮。

因此，仅启用新的视觉节点而不迁移 `ExhibitMetadata`、`ViewPoint` 和 `GazeTarget`，会导致导航注册、到达判断、Avatar 指向和目标高亮失效。停用的旧展品不会被默认扫描到。

### 2.3 语义配置现状

当前 `scene_semantics.json` 包含 E1-E8 的 ID、名称、语义角色、位置、观看点、注视点、标签、关联展品和描述，但部分坐标、语义内容已经与新视觉结点信息或布局不一致。例如 E3/E4 和 E6/E7 的 X 坐标仍是旧值，

该 JSON 目前是最初开发时的旧值旧结构，而不是运行时真值。此次改造应将其提升为明确的布局与语义配置基线，并增加自动或半自动一致性检查。

### 2.4 虚拟展厅与物理空间必须分离

12m x 14m 表示虚拟展厅中用于摆放展柜和展品的有效地面，不等同于用户真实行走空间。`physical_wide_room.json` 等 `physical_*.json` 描述真实房间边界，供 RDW 风险计算使用。

本次不能因为虚拟地面扩展为 12m x 14m，就同步把物理空间配置改成 12m x 14m。物理配置只有在重新测量真实实验场地和重新校准后才允许修改。

## 3. 总体设计原则

1. 1 Unity Unit 按 1m 解释。所有长度、宽度、距离均以米为单位。
2. 有效地面固定为宽 12m、长 14m，总面积 168m2。
3. 地面坐标原点位于入口侧中央，地面中心为 `(0, 0, 7)`。
4. X 轴表示左右，范围为 `[-6, 6]`；Z 轴表示入口到展厅深处，范围为 `[0, 14]`；Y 轴向上。
5. 入口位于 Z=0 一侧，主参观方向为 +Z。
6. `VirtualGuidance_B_Root` 和每个 `E*_Visual` 的缩放保持 `(1,1,1)`。只缩放视觉内容、展柜和铭牌，不缩放语义锚点根节点。
7. 展品 ID E1-E8、语义角色和任务关系保持稳定。空间调整不能通过改 ID 规避配置同步。
8. 展柜尺寸以最终世界空间外廓为准，不以模型导入缩放值作为验收依据。
9. `ViewPoint` 是用户到达和观看位置；`GazeTarget` 是视觉关注位置，二者不能与展柜中心混用。
10. 所有场景、JSON 和 Inspector 修改必须在同一变更批次中完成并通过一致性验证。

## 4. 12m x 14m 空间契约

### 4.1 硬约束

| 项目 | 约束 |
| --- | --- |
| 有效地面 | 12m x 14m，X `[-6,6]`，Z `[0,14]` |
| 展柜到地面边界 | 推荐不小于 1.2m，绝对不得小于 0.8m |
| 展柜之间的净距 | 本布局目标不小于 2.0m；实测调整后不得小于 1.4m |
| 主通道净宽 | 推荐 2.2m-2.6m，最低 2.0m |
| 支路/侧向观看区净宽 | 推荐不小于 1.6m |
| ViewPoint 到展柜可视边缘 | 0.9m-1.2m |
| ViewPoint 周围无障碍区 | 以 ViewPoint 为中心至少保留 0.75m 半径 |
| 转身/Reset 区 | 关键节点保留直径 1.8m-2.2m 的净空 |
| 展柜总占地率 | 建议不超过有效地面的 18% |
| 展柜及其安全缓冲占地率 | 建议不超过有效地面的 35% |

这里的“净距”必须使用展柜、铭牌和其他带碰撞视觉物的世界空间边界计算，不能只比较各展品根节点中心。

### 4.2 观看朝向约定

为避免位置和旋转含义不统一，规定 `E*_Visual.forward` 指向展品正面观看区，也就是从展柜朝向 `ViewPoint` 的方向：

- 中轴展品 E1、E2、E5、E8 朝入口，建议 Yaw 为 180 度。
- 左侧展品 E3、E6 朝向展厅中心，建议 Yaw 为 90 度。
- 右侧展品 E4、E7 朝向展厅中心，建议 Yaw 为 -90 度。

上述角度以模型本地 +Z 为正面时成立。如果资源模型的原始正面不是 +Z，应调整 `VisualContent` 的局部旋转，而不是破坏 `E*_Visual` 的统一朝向契约。

## 5. 8 个展品的首轮布局基线

### 5.1 宽敞优先的推荐坐标和尺寸

尺寸顺序为“正面宽度 x 前后深度 x 总高度”。位置为 `E*_Visual` 根节点世界坐标。首轮建议如下：

| ID | 位置 `(x,y,z)` | Yaw | 展柜目标尺寸 `(W x D x H)` | ViewPoint | GazeTarget |
| --- | --- | ---: | --- | --- | --- |
| E1 | `(0,0,2.20)` | 180 | `1.8 x 1.0 x 1.5` | `(0,0,0.80)` | `(0,1.25,2.20)` |
| E2 | `(0,0,5.42)` | 180 | `1.8 x 1.1 x 1.6` | `(0,0,3.82)` | `(0,1.35,5.42)` |
| E3 | `(-4.35,0,3.80)` | 90 | `1.8 x 1.1 x 1.5` | `(-2.70,0,3.80)` | `(-4.35,1.30,3.80)` |
| E4 | `(4.35,0,3.80)` | -90 | `1.8 x 1.1 x 1.5` | `(2.70,0,3.80)` | `(4.35,1.30,3.80)` |
| E5 | `(0,0,8.83)` | 180 | `2.4 x 1.4 x 1.7` | `(0,0,7.08)` | `(0,1.40,8.83)` |
| E6 | `(-4.35,0,10.50)` | 90 | `2.4 x 1.3 x 1.7` | `(-2.55,0,10.50)` | `(-4.35,1.40,10.50)` |
| E7 | `(4.35,0,10.50)` | -90 | `2.4 x 1.3 x 1.7` | `(2.55,0,10.50)` | `(4.35,1.40,10.50)` |
| E8 | `(0,0,12.45)` | 180 | `3.0 x 1.5 x 1.8` | `(0,0,10.65)` | `(0,1.45,12.45)` |

这组展柜占地约 21.84m2，占 168m2 有效地面的约 13%。与上一版相比，展柜尺寸不变，但位置改为“中轴均匀展开、侧翼前后错开”，把空间优先留给通道、独立观看区和转身动作。

### 5.2 净距测算

以下结果按照表中展柜目标外廓和 Yaw 计算；侧展柜旋转 90 度后，世界 X/Z 外廓需要交换计算：

| 检查项 | 净距 | 结果 |
| --- | ---: | --- |
| E1 后沿到 E2 前沿 | 2.17m | 达到宽敞目标 |
| E2 后沿到 E5 前沿 | 2.16m | 达到宽敞目标 |
| E5 后沿到 E8 前沿 | 2.17m | 达到宽敞目标 |
| E3 后沿到 E6 前沿 | 4.60m | 形成大面积左侧开放区 |
| E4 后沿到 E7 前沿 | 4.60m | 形成大面积右侧开放区 |
| E6/E7 内沿到 E8 侧沿 | 2.20m | 大于全局最小净距 |
| E3/E4 到左右边界 | 1.10m | 保留墙边视觉余量 |
| E6/E7 到左右边界 | 1.00m | 高于 0.8m 硬下限 |
| E8 后沿到 Z=14 边界 | 0.80m | 达到边界硬下限 |
| E6 与 E7 内沿之间中央视廊 | 7.40m | 形成宽阔中央开放区 |

在目标外廓成立时，全布局的理论最小展柜净距约为 2.16m。原始首版最小净距约为 1.70m，优化后增加约 0.46m；提升最明显的是同侧 E3-E6 与 E4-E7，净距增加到 4.60m。该方案已经接近在保留 E1 前方观看点和 0.8m 后边界硬下限条件下，中轴四柜可获得的等距上限。最终验收仍以 Unity 中包含铭牌的真实 Renderer/Collider Bounds 为准。

### 5.3 俯视布局示意

```text
Z=14  ┌────────────────────────────────────────────┐
      │                    E8                      │
      │                                            │
      │  E6                                   E7   │
      │                    E5                      │
      │                                            │
      │                                            │
      │                    E2                      │
      │  E3                                   E4   │
      │                                            │
      │                    E1                      │
Z=0   └───────────────────入口─────────────────────┘
       X=-6                  X=0                  X=6
```

图中只表示空间关系，不表示展柜实际比例。E3/E4 和 E6/E7 面向中央；中轴展品面向入口。侧翼两组展柜不再与中轴展柜挤在同一横排，从入口看过去会形成连续开阔的中央视廊。

### 5.4 布局意图

- E1、E2、E5、E8 构成清晰的中轴叙事顺序。
- E3/E4 前移到第一开放区两侧，形成左右引导节点，并继续保留 T2 中安全绕行候选的职责。
- E6/E7 后移到第二开放区两侧，与 E3/E4 拉开 4.6m 净距，用于语境补充、对比和转向。
- E8 位于展厅深处但不贴墙，保留总结观看区和后方视觉余量。
- 两组侧展品朝向中间，但不侵占中央主轴；用户可以在中央视廊中自由选择进入左右观看区。

### 5.5 尺寸调整规则

首轮不要直接把所有展柜统一缩放为相同百分比。应先测量当前展柜和展品模型的组合边界，再按目标外廓反算局部缩放：

```text
scaleX = targetWidth / measuredWidth
scaleY = targetHeight / measuredHeight
scaleZ = targetDepth / measuredDepth
```

原则上优先保持均匀缩放。如果展柜比例与目标尺寸明显不符，应调整展柜底座、玻璃和铭牌的结构尺寸，而不是对整体做严重的非均匀缩放。展品模型可以在 0.85-1.0 的可读范围内独立微调，但必须保证模型不穿出玻璃、不悬空、不遮挡铭牌。

## 6. 推荐 Hierarchy 和组件契约

建议将新根节点整理为以下逻辑结构：

```text
VirtualGuidance_B_Root                     Scale = (1,1,1)
├── Environment
│   ├── EffectiveFloor_12x14
│   ├── Walls
│   └── Lighting
└── ExhibitVisuals
    ├── E1_Visual                          Scale = (1,1,1)
    │   ├── VisualContent                  可单独缩放
    │   │   ├── ShowcaseVisual
    │   │   ├── ExhibitModels
    │   │   └── ChinesePlaque
    │   ├── ViewPoint                     不跟随 VisualContent 缩放
    │   └── GazeTarget                    不跟随 VisualContent 缩放
    ├── E2_Visual
    ├── ...
    └── E8_Visual
```

每个 `E*_Visual` 根节点必须满足：

- 根节点处于激活状态。
- 根节点名称与 ID 一一对应。
- 根节点挂载且只挂载一个 `ExhibitMetadata`。
- `ExhibitMetadata.id` 与节点编号一致。
- `ViewPoint` 和 `GazeTarget` 是直接或稳定层级的子节点，并在 Inspector 中显式引用。
- 根节点负责展品整体位置和朝向，保持 `(1,1,1)` 缩放。
- `VisualContent` 负责展柜、展品模型和铭牌的视觉尺寸。
- 高亮所需 MeshRenderer 必须位于 `ExhibitMetadata` 节点之下。

不建议继续让停用的旧 `Exhibits/Exhibit_E*` 承担语义组件。新视觉根节点应完整拥有自己的运行时配置。迁移完成并验证后，旧节点继续停用，是否删除应作为独立清理任务处理。

旧 `ExhibitMetadata` 只能作为组件结构参考，不能作为 B 版本语义数据源。B 版本必须以 `VirtualGuidance_B_Root` 中的实际模型和中文铭牌为准重新填写 Metadata。

## 7. ExhibitMetadata 同步要求

### 7.1 字段来源规则

B 版本 Metadata 采用以下来源优先级：

| 字段 | 数据来源 | 是否允许沿用旧值 |
| --- | --- | --- |
| `id` | B 根节点编号 E1-E8 | 允许；ID 是实验稳定标识 |
| `exhibitName` | B 中 `ChinesePlaqueTitle` 去掉 `E*` 前缀后的标题 | 不允许 |
| `description` | B 中 `ChinesePlaqueDescription` 的实际说明文字 | 不允许 |
| `tags` | 根据 B 展品模型、标题和说明重新整理 | 不允许直接复制 |
| `semanticRole` | RDW 实验任务定义 | 允许保留职责，但必须重新核对 B 内容是否承载该职责 |
| `relatedExhibits` | 根据 B 的工业发展叙事和实验替代关系重新建立 | 不允许直接复制 |
| `viewPoint` | 本文第 5 节 B 布局坐标 | 不允许 |
| `gazeTarget` | B 模型/铭牌的视觉中心 | 不允许 |

这里的原则是“展示语义以 B 为真，实验职责保持稳定”。例如 E3 仍承担左侧语义重定向职责，但它的名称必须是 B 中的“传动与控制”，不能继续使用旧名称 “Related Mechanism”。

### 7.2 B 版本 Metadata 完整基线

下表中的中文标题和描述来自 B Prefab 的铭牌；模型信息来自 B 的 `ExhibitModels`。英文 tags 用于程序筛选和后端语义输入，避免依赖中文字符串做逻辑判断。

| ID | B 展品模型 | `exhibitName` | `description` | `semanticRole` | `tags` | `relatedExhibits` |
| --- | --- | --- | --- | --- | --- | --- |
| E1 | Computer | 智造序章 | 工业技术从机械化走向数字化与智能化。 | `introduction` | `introduction`, `overview`, `smart_manufacturing`, `start` | E2, E3, E4 |
| E2 | EarlyMachine | 机械化起步 | 早期机械设备提高了生产效率与稳定性。 | `forward_next` | `mechanization`, `early_industry`, `production`, `timeline` | E1, E3, E4, E5 |
| E3 | ControlMachine | 传动与控制 | 传动结构与控制机构让机器能够精确协作。 | `left_related_anchor` | `transmission`, `control`, `mechanism`, `left_redirect` | E2, E4, E5 |
| E4 | DigitalController | 数字化转型 | 计算机控制让生产过程变得可编程、可监测。 | `right_alternative_anchor` | `digitalization`, `computer_control`, `programmable`, `right_redirect` | E2, E3, E5, E8 |
| E5 | RobotA, RobotB | 智能机器人 | 机器人感知环境并完成自动化协同生产。 | `main_target` | `robotics`, `perception`, `automation`, `collaboration`, `main` | E3, E4, E6, E7, E8 |
| E6 | StorageRack, Transporter | 仓储与物流 | 仓储系统与运输设备连接生产和交付环节。 | `side_context` | `storage`, `logistics`, `transport`, `reset_camouflage` | E5, E7, E8 |
| E7 | ForkliftA, ForkliftB | 搬运方式对比 | 对比不同搬运设备的结构、效率与转向能力。 | `comparison_object` | `material_handling`, `comparison`, `forklift`, `turn` | E5, E6, E8 |
| E8 | SummaryMachine, SummaryRobot, SummaryTransport | 智能工厂全景 | 机器、机器人与物流系统共同组成智能工厂。 | `summary` | `integration`, `smart_factory`, `system`, `summary`, `ending` | E4, E5, E6, E7 |

`relatedExhibits` 同时满足两类关系：一是 B 内容上的自然工业发展关系，二是实验中的左右替代和后续总结关系。运行时当前不会用它决定 T2 候选资格；T2 仍由既有候选策略固定使用 E3/E4 作为临时 Detour，E5 作为 FinalTarget。

### 7.3 Inspector 与 JSON 同步

每个新展品需要同步以下 Inspector 字段：

| Inspector 字段 | JSON 字段 | 同步规则 |
| --- | --- | --- |
| `id` | `id` | E1-E8，唯一且不可为空 |
| `exhibitName` | `name` | 完全一致 |
| `tags` | `tags` | 顺序可稳定化，内容完全一致 |
| `description` | `description` | 完全一致 |
| `semanticRole` | `semantic_role` | 完全一致 |
| `relatedExhibits` | `related_exhibits` | 所有 ID 必须存在 |
| `viewPoint` | `viewpoint` | Transform 引用非空，世界坐标一致 |
| `gazeTarget` | `gaze_target` | Transform 引用非空，世界坐标一致 |
| 根节点位置 | `position` | 世界坐标一致 |
| 根节点 Yaw | `rotation_y_degrees` | 世界 Y 旋转一致 |
| 展柜外廓 | `cabinet` | 世界空间测量值在容差内 |

展柜长宽高属于布局数据，不建议直接塞入现有 `ExhibitMetadata`。该组件当前职责是语义与导航锚点。除非运行时安全逻辑确实需要读取展柜外廓，否则尺寸只保存在 JSON，并通过编辑器验证工具与 Renderer/Collider 边界比较。这样可以保持单一职责。

## 8. scene_semantics.json v2 设计建议

### 8.1 配置定位

建议将 `scene_semantics.json` 定义为“设计与编辑器同步基线”：

- JSON 保存稳定语义、目标布局参数和锚点坐标。
- Unity 场景保存运行时真正使用的组件和 Transform。
- 编辑器同步/验证工具负责把 JSON 应用到场景，或比较两者差异。
- 构建后的运行时仍读取场景组件，避免此次改造同时引入不必要的运行时加载链。

在同步工具完成前，场景与 JSON 必须人工在同一提交中更新。同步工具完成后，JSON 作为编辑阶段的首要输入，场景作为生成并验证后的运行时产物。

### 8.2 建议结构

```json
{
  "schema_version": 2,
  "scene_id": "dual_world_gallery",
  "scene_name": "Dual World Gallery",
  "coordinate_system": "Unity X-Z ground plane, Y-up",
  "layout": {
    "units": "meters",
    "floor_origin": [0, 0, 0],
    "floor_center": [0, 0, 7],
    "floor_size_m": {
      "width": 12,
      "length": 14
    },
    "floor_bounds": {
      "x_min": -6,
      "x_max": 6,
      "z_min": 0,
      "z_max": 14
    },
    "entrance_side": "z_min"
  },
  "exhibits": [
    {
      "id": "E1",
      "name": "智造序章",
      "scene_path": "VirtualGuidance_B_Root/ExhibitVisuals/E1_Visual",
      "semantic_role": "introduction",
      "position": [0, 0, 2.2],
      "rotation_y_degrees": 180,
      "cabinet": {
        "width_m": 1.8,
        "depth_m": 1.0,
        "height_m": 1.5
      },
      "viewpoint": [0, 0, 0.8],
      "gaze_target": [0, 1.25, 2.2],
      "tags": ["introduction", "overview", "smart_manufacturing", "start"],
      "related_exhibits": ["E2", "E3", "E4"],
      "description": "工业技术从机械化走向数字化与智能化。"
    }
  ]
}
```

### 8.3 字段规则

- `scene_path`：用于编辑器工具精确定位对象，禁止仅依赖模糊名称查找。
- `position`：展品根节点世界坐标，顺序固定为 X、Y、Z。
- `rotation_y_degrees`：展品根节点世界 Yaw，单位为度。
- `cabinet.width_m`：展柜正面宽度。
- `cabinet.depth_m`：从展柜正面到背面的深度。
- `cabinet.height_m`：从地面到展柜最高可见点的总高度。
- `viewpoint`：用户推荐站位的世界坐标。
- `gaze_target`：Avatar、视线引导和指向动作关注点的世界坐标。
- `related_exhibits`：只表达语义关系，不改变当前 T2 候选资格。

## 9. 同步机制设计

### 9.1 推荐工具职责

后续实现一个仅在 Unity Editor 中运行的同步与校验工具，建议职责拆为：

1. `SceneSemanticsConfigLoader`：解析 JSON，并报告 schema/字段错误。
2. `SceneSemanticsApplier`：按 `scene_path` 定位 E1-E8，应用根节点位置、旋转、锚点坐标和 `ExhibitMetadata`。
3. `SceneSemanticsValidator`：只读比较 JSON、场景组件和真实边界，不自动修改。
4. Editor 菜单：提供“Validate”“Apply To Scene”“Export From Scene”三个明确操作。

避免在 Play Mode 启动时隐式覆盖场景配置。应用配置属于编辑器装配行为，运行时只消费已经验证过的场景。

### 9.2 冲突规则

- `Validate` 永远只读，不写 JSON 或场景。
- `Apply To Scene` 以 JSON 为输入，只修改目标路径内的布局和元数据。
- `Export From Scene` 只更新坐标、旋转、锚点和测量尺寸，不应静默覆盖名称、描述、标签等语义文本。
- 若 JSON 和场景都被手工修改，工具必须报告冲突，不能依据文件时间自动猜测。
- 位置误差容差建议为 0.01m，旋转误差为 0.1 度，尺寸误差为 0.02m。

### 9.3 校验失败条件

以下任一情况都应阻止将布局标记为完成：

- E1-E8 任一缺失、重复或未激活。
- `ExhibitMetadata` 缺失或 ID 与节点名称不一致。
- `ViewPoint`/`GazeTarget` 引用为空。
- `related_exhibits` 引用了不存在的 ID。
- 展柜边界超出 12m x 14m 有效地面。
- 展柜之间或展柜与 ViewPoint 的净距低于硬下限。
- ViewPoint 位于碰撞体内部或地面范围外。
- JSON 与场景字段超过容差。
- 旧停用展品被意外启用，造成重复 ID。

## 10. 分阶段实施计划

### 阶段 0：冻结基线

- 保存当前 `MainScene`、B Root Prefab 和 `scene_semantics.json` 的版本状态。
- 导出现有 E1-E8 的世界位置、旋转、Renderer Bounds、Collider Bounds、ViewPoint 和 GazeTarget。
- 截取当前俯视图、入口视图和 XR 视图，作为拥挤程度对照。
- 确认本轮不修改 `physical_*.json`。

交付物：现状测量表和对照截图。

### 阶段 1：建立 12m x 14m 环境

- 将有效地面设置为 12m x 14m，中心放在 `(0,0,7)`。
- 调整墙体、踢脚线、天花板、灯光覆盖和烘焙范围。
- 使用明显但不进入最终构建的边界 Gizmo 标记 X/Z 范围。
- 检查地面 Collider 与视觉地面完全对应。

交付物：无展柜或暂时隐藏展柜的空展厅基线。

### 阶段 2：统一展柜外廓

- 对每个展品测量现有展柜、模型和铭牌组合 Bounds。
- 按第 5 节目标尺寸调整 `VisualContent`。
- 先处理 Base 和 Glass，再适配展品模型，最后调整铭牌。
- 确保灯光、玻璃、底座和铭牌没有因缩放产生穿插。
- 为每个展柜记录最终实测 W/D/H。

交付物：8 个尺寸受控、原点和朝向统一的展柜视觉单元。

### 阶段 3：应用布局和锚点

- 按首轮坐标放置 E1-E8。
- 根据展品正面调整 Yaw。
- 创建或迁移每个展品的 `ViewPoint` 和 `GazeTarget`。
- 用 0.75m 到达半径和 1.8m 转身直径检查观看区。
- 俯视检查通道，再以 1.6m-1.8m 眼高检查遮挡和视线。

交付物：空间布局 v1 和锚点测量表。

### 阶段 4：迁移运行时配置

- 在新 `E*_Visual` 根节点挂载 `ExhibitMetadata`。
- 不复制旧展品的名称、标签、描述和关联关系；按照第 7.2 节 B 版本基线重新填写。
- 仅保留 E1-E8 稳定 ID 和经过核对的实验 `semanticRole`，确保 T2 等实验任务兼容。
- 显式绑定新的 `ViewPoint` 和 `GazeTarget`。
- 保持旧 `Environment` 和 `Exhibits` 停用。
- 调用或触发相关 Registry 重建，确认 E1-E8 全部注册。

交付物：新视觉节点成为完整的运行时展品节点。

### 阶段 5：升级并同步 JSON

- 将 `scene_semantics.json` 升级为 schema v2。
- 写入 12m x 14m 布局信息、E1-E8 新坐标、旋转和展柜尺寸。
- 同步 ViewPoint、GazeTarget 和现有语义字段。
- 实现并运行编辑器校验；若暂不开发工具，则执行人工双人核对表。

交付物：与场景一致的语义配置和零差异校验报告。

### 阶段 6：功能与体验验证

- 验证导航注册、高亮、Avatar 指向、到达判定和任务推进。
- 验证 T2 仍以 E5 为 FinalTarget，E3/E4 仍是临时安全 Detour。
- 验证 E6/E7 的语义关系不会被误当成 T2 可执行候选。
- 在 XR 设备或等效 Camera Rig 中完成入口、主通道、侧向观看和转身测试。
- 根据实测只微调坐标和尺寸，不再改变坐标系或层级契约。

交付物：布局冻结版本和验收记录。

## 11. 验收清单

### 11.1 空间验收

- 地面有效尺寸实测为 12m x 14m。
- 所有展柜及铭牌 Bounds 位于有效地面内。
- 主通道连续净宽不小于 2.0m，目标值达到 2.2m。
- 所有 ViewPoint 周围 0.75m 半径内无展柜 Collider。
- 任意相邻展柜没有视觉穿插或碰撞体重叠。
- E8 后方仍有至少 1.0m 的视觉/边界余量。

### 11.2 场景契约验收

- 激活的 `ExhibitMetadata` 数量恰好为 8。
- 注册 ID 恰好为 E1-E8，无重复、无空 ID。
- 每个 Metadata 的两个 Anchor 引用均非空。
- 新展品根节点 Scale 均为 `(1,1,1)`。
- 旧 `Environment` 和 `Exhibits` 保持停用。

### 11.3 功能验收

- `NavigationTargetManager.GetRegisteredTargetIds()` 返回 E1-E8。
- 对 E1-E8 逐个执行 Highlight，目标视觉对象均被正确高亮。
- 对 E1-E8 逐个执行 `look_at_target`，Avatar 朝向对应 GazeTarget。
- 用户进入 ViewPoint 的 0.75m 半径时只触发一次到达。
- 用户离开目标后不会因旧 Reservation 误触发新任务。
- T2 候选集合和原实验条件行为无回归。

### 11.4 配置验收

- JSON schema 解析成功。
- E1-E8 的标题和描述与 B 中文铭牌逐字一致，tags 和 relatedExhibits 符合第 7.2 节基线。
- E1-E8 的 ID、语义字段、位置、旋转和 Anchor 与场景一致。
- 柜体实测尺寸与 JSON 目标尺寸误差不超过 0.02m。
- 所有 `related_exhibits` 引用均有效。
- `physical_*.json` 未因虚拟地面扩展被误改。

## 12. 风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| 缩放展品根节点 | Anchor 坐标、到达距离和子节点比例一起变化 | 根节点保持 1，缩放 VisualContent |
| 只迁移视觉物，不迁移 Metadata | 导航、高亮、到达和 Avatar 指向失效 | Metadata 与两个 Anchor 必须作为一个迁移单元 |
| 把旧 Metadata 复制到 B 展品 | 展示内容、MLLM 上下文和实际模型不一致 | 名称、描述、tags、关联关系必须按 B 重建 |
| JSON 和场景各自手改 | 坐标和语义持续漂移 | 同批修改，并尽快增加 Editor Validator |
| 用中心点判断空间是否拥挤 | 忽略展柜、铭牌和碰撞体实际占地 | 使用 Renderer/Collider 世界 Bounds 验收 |
| 将虚拟地面等同真实房间 | RDW 风险计算失真 | 保持 virtual layout 与 physical calibration 分离 |
| 统一百分比缩放所有展柜 | 大型组合展品不可读，小展品显得空 | 按 E1-E8 目标外廓分别适配 |
| 改变 E3/E4/E5 的任务意义 | T2 候选和实验逻辑回归 | 保持 ID 和角色，空间变化后做专项测试 |

## 13. 实施前需要确认的设计项

在实际修改场景前，建议对以下基线做一次确认：

1. 接受入口位于 Z=0、地面中心位于 `(0,0,7)` 的坐标约定。
2. 接受中轴 E1/E2/E5/E8、左右 E3/E4/E6/E7 的总体拓扑。
3. 接受第 5 节展柜尺寸作为首轮目标外廓，而不是最终不可调整尺寸。
4. 确认铭牌是否计入展柜组合 Bounds；本文建议计入。
5. B 铭牌中文作为 `exhibitName` 和 `description` 的正式展示语义；程序 tags 保持英文机器可读形式。
6. 确认先采用人工同步，还是本轮同时实现 Editor 同步/验证工具；推荐至少实现只读 Validator。

这些项目确认后，即可按阶段 0-6 进入场景实施。

## 14. 本次调研依据与限制

本计划依据当前 `MainScene`、`scene_semantics.json`、`ExhibitMetadata`、`NavigationTargetManager`、`ArrivalDetector`、`ExhibitHighlightManager`、`GuideAvatarController`、`SemanticTargetSelector` 和物理空间配置制定。
