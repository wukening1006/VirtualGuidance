# Gallery Layout V2 验收记录

验收日期：2026-07-27

Unity：2022.3.62f3
分支：`agent/gallery-layout-v2`

## 自动化结果

- 场景生成：通过，`LayoutValidationReport.txt` 为 `PASS`，错误 0、警告 0。
- Unity EditMode：93 项通过，0 项失败，0 项跳过。
- 后端 pytest：65 项通过，0 项失败。
- JSON v2：解析、Apply、Export、语义文本保护和 Scene/JSON 零差异测试通过。

## 空间验收

- 有效地面：12.00m × 14.00m，中心 `(0.00, 7.00)`。
- 主通道：E1–E2 为 2.17m，E2–E5 为 2.16m，E5–E8 为 2.17m，均不低于 2.00m。
- 柜间最小净距、0.75m ViewPoint 无碰撞区域和 0.8m 边界要求均通过。
- E1–E8 实测展柜 Bounds 与 JSON 目标尺寸差值均不超过 0.02m。
- 墙体、地面 Collider、天花板和灯光最终启用；旧 `Environment`、`Exhibits` 保持停用。

## 功能验收

- 激活的 `ExhibitMetadata` 恰好 8 个，ID 为 E1–E8，无重复。
- 每个 E 根节点 Scale 为 `(1,1,1)`，并直接绑定 `ViewPoint` 与 `GazeTarget`。
- MainScene 中 E1–E8 注册、高亮 Renderer 和 `look_at_target` 联动通过。
- Arrival 单次触发、T2 的 E3/E4 绕行、E5 最终目标、转向提示和重规划测试通过。
- `physical_*.json` 未参与虚拟布局同步，真实 RDW 空间契约保持独立。

## 视觉产物

- 更新前基线仅保留轻量测量记录：`BaselineMeasurements.txt`；旧基线截图不纳入版本库。
- 最终效果：`Final_Entrance.png`、`Final_Top.png`、`Final_XR_Sim.png`。
- 本轮无 PICO 设备，XR 验收采用 Editor Simulation；真机 SDK 与硬件测试留待设备可用时执行。
