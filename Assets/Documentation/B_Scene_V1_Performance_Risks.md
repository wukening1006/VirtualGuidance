# B 部分场景 V1：资源与性能风险清单

## 本版策略

- 复用 `room.unitypackage` 的建筑主体，不改变 XR、Physical Space、Tracking、Calibration 和系统 Manager。
- 旧 `Environment` 仅关闭 `MeshRenderer`，保留碰撞体与物理空间逻辑。
- 展品对象、`Exhibit ID`、`Task ID`、`Semantic ID`、`ViewPoint`、`GazeTarget` 均不改名、不重建。
- 新增 1 盏软阴影方向光和 4 盏无阴影点光，避免大量实时阴影。
- Showroom 素材只选取两张长椅和两盆植物，避免无控制地堆叠资源。

## 已知风险

1. `Room（ours）.prefab` 来源包包含较多材质与网格。当前使用完整视觉预制体，但关闭其中的导入脚本、Canvas、视频、音频、灯光和碰撞体；真机前仍需按视线范围继续裁剪不可见模型。
2. 第一版仍使用实时方向光软阴影。PICO 真机联调时需观察 GPU 帧耗，必要时改为烘焙阴影或关闭阴影。
3. Showroom 素材的贴图分辨率需在真机前统一检查，建议对非重点装饰设置 1024 或更低的 Max Size。
4. 当前材质仍基于 Built-in Standard Shader。若后续切换 URP，需要统一升级材质，避免粉色材质。
5. Factory 素材包当前不用于主场景；其体积较大且手动导入后部分 GUID 可能失配，需通过 Unity 原生导包后再评估。

## 真机验收建议

- 目标帧率：以项目 PICO 设备设定为准，重点检查主体验路径和转头时的稳定性。
- 检查 SetPass、Batches、Triangles、实时灯光与阴影开销。
- 检查 Participant Canvas 在头显中的距离、字号和遮挡。
- 检查 Highlight、Movement Hint、Gaze Cue 在亮背景和暗背景下均清楚。
- 检查研究员 UI、风险调试、后端状态和请求 ID 不出现在参与者相机层。
