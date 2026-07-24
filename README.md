# VirtualGuidance

VirtualGuidance 是一个面向 VR 展馆导览实验的 Unity 项目，结合重定向行走（RDW）、物理空间风险评估、任务导览和 FastAPI 后端，实现可记录、可复现实验流程。

## 当前阶段

- Unity 主场景已经嵌入 `room` 展馆建筑，并保留原有 E1–E8 导览路线、到达点、凝视目标和实验语义。
- 八个占位展板已经升级为玻璃展柜和 Low Poly 工业实体展品，主题统一为“工业科技发展”。
- 场馆已经完成墙面、地板、灯光、书架、座椅、植物和外部绿化布置。
- 支持无头显的桌面调试流程；接入设备后仍需进行 PICO/OpenXR 真机验收。
- FastAPI 后端提供健康检查和导览决策接口，Unity 默认连接 `127.0.0.1:8000`。

## 环境要求

- Unity `2022.3.62f3`
- Python `3.10+`
- Windows 10/11
- 可选：PICO/OpenXR 兼容头显

Unity Package Manager 会根据 `Packages/manifest.json` 自动安装 XR Interaction Toolkit、OpenXR、ProBuilder、TextMesh Pro 等依赖。

## 第三方素材

由于体积和授权限制，以下原始素材包不在 Git 仓库中二次分发：

- `room.unitypackage`
- `Factory Low Poly.unitypackage`
- `Showroom Environment gallery 1.0.unitypackage`

首次配置时请通过 Unity 的 `Assets > Import Package > Custom Package...` 导入素材包，并保留包内默认目录和 `.meta` 文件。当前场景依赖以下目录：

- `Assets/Low Poly Factory`
- `Assets/Showroom Environment - gallery`
- `Assets/ThirdParty/Display room`

缺少素材时，场景结构、导航和后端逻辑仍会保留，但展柜模型、书架、植物或部分材质可能显示为缺失。

## 启动后端

在项目根目录执行：

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r backend\requirements.txt
python -m uvicorn backend.app:app --reload --host 127.0.0.1 --port 8000
```

健康检查地址：

```text
http://127.0.0.1:8000/health
```

运行后端测试：

```powershell
python -m pytest backend\tests -q
```

## 启动 Unity 展示

1. 使用 Unity Hub 打开项目根目录。
2. 打开 `Assets/Scenes/MainScene.unity`。
3. 确认后端已经启动。
4. 点击 Unity 顶部的 Play 按钮。
5. 无设备调试时使用桌面模拟状态；连接头显后重新完成 Tracking 和 Calibration。

主界面显示 `Backend: Online` 后，可以选择参与者、实验条件、任务和物理空间，再执行校准与开始实验。

## E1–E8 展览内容

| 展项 | 中文主题 | 展示内容 |
| --- | --- | --- |
| E1 | 智造序章 | 工业技术从机械化走向数字化与智能化 |
| E2 | 机械化起步 | 早期机械设备与机械生产 |
| E3 | 传动与控制 | 机械结构、传动与控制机制 |
| E4 | 数字化转型 | 计算机和数字控制进入生产系统 |
| E5 | 智能机器人 | 协作机器人和自动化生产 |
| E6 | 仓储与物流 | 货架、运输和仓储流转 |
| E7 | 搬运方式对比 | 两类搬运设备的结构与转向差异 |
| E8 | 智能工厂全景 | 机器、机器人和物流系统的综合展示 |

## 编辑器工具

Unity 顶部菜单 `VirtualGuidance` 提供以下场景工具：

- `Room 嵌入/恢复并嵌入原场景布局`：重新生成 room 展馆和原实验布局。
- `Room 嵌入/生成 E1-E8 实体展柜`：重复生成八个实体展柜，不重复创建导航节点。
- `Room 嵌入/布置灯具书架与外部绿化`：重建环境装饰。
- `Room 嵌入/验证入口通行`：检查正门和入口通行状态。

构建器执行前会创建本地场景存档；存档目录已加入 `.gitignore`，不会进入版本库。

## 项目结构

```text
Assets/
  Configs/          实验任务、场景语义和物理空间配置
  Editor/           场景集成、展柜生成和预览工具
  Prefabs/          展品、房间及系统预制体
  Scenes/           Unity 主场景
  Scripts/          导览、RDW、实验、诊断和后端通信逻辑
backend/
  agents/           后端导览策略
  services/         风险、日志和任务服务
  tests/            FastAPI 与 RDW 状态测试
```

## 已知限制

- 当前主要完成了无设备桌面验证，头显定位、帧率和眩晕风险需要真机复测。
- 第三方素材必须由使用者自行取得并遵守其许可证。
- 展柜视觉层可以调整，但不要修改 E1–E8 的 ID、`semanticRole`、`relatedExhibits`、`ViewPoint` 和 `GazeTarget`。
