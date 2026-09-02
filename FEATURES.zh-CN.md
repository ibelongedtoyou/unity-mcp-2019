# 功能总览

MCP for Unity 2019 通过 48 个公开工具和 25 个公开
Resource/Resource Template，让可信 MCP 客户端控制 Unity `2019.4` Editor。
它同时支持 stdio 和 Streamable HTTP，所有 Unity API 操作都回到 Editor
主线程执行。

[English](FEATURES.md)

## 已实现能力

| 领域 | 具体能力 |
| --- | --- |
| Editor 工作流 | Ready 状态、Play/Pause/Stop、Undo/Redo、Console、菜单执行、AssetDatabase 刷新、工具组开关和多实例选择。 |
| 场景与对象 | 场景创建/加载/保存/校验、Hierarchy、GameObject 增删改查、Transform/父子关系、Component 增删和序列化属性修改、Prefab 工作流。 |
| 资源与内容 | 资源导入和 CRUD、材质、Shader、程序纹理、Sprite、ScriptableObject、Animator/AnimationClip 和 Package Manager。 |
| C# 代码 | 脚本创建/读取/删除、SHA 前置校验、正则搜索、原子文本编辑、结构化编辑、语法校验、反射、自定义工具和可选内存执行。 |
| 渲染与模拟 | Camera/截图、2D/3D 物理、Graphics、光照烘焙、渲染统计、Profiler、粒子、Line/Trail Renderer，以及可选渲染管线能力。 |
| 构建与测试 | 构建目标/设置/场景、异步构建、EditMode/PlayMode 测试及异步结果查询。 |
| 生成与导入 | 本地模型导入，以及需要用户凭据的图片、音频、3D 模型和 Sketchfab 流程。 |
| MCP Resources | Editor/项目状态、Tag/Layer、选择和窗口、菜单、Camera、Volume、Renderer Feature、测试、Prefab、GameObject 和组件序列化数据。 |

## 推荐调用顺序

1. 读取 `mcpforunity://editor/state`，确认 `readyForTools=true`。
2. 读取相关能力 Resource，或先调用 `find_gameobjects` 获取 InstanceId。
3. 使用 InstanceId 或 Assets 路径执行最小修改。
4. 重新读取目标 Resource，并用 `read_console` 检查错误。
5. 验证型写操作结束后使用 Undo 或重载场景清理。

场景写操作会注册 Unity Undo；要求 EditMode 的操作在 Play Mode 中会被拒绝。
高风险删除或丢弃操作在适用时要求显式确认。

## 支持等级

- **核心支持：** 直接基于 Unity 2019.4 Editor API 实现。
- **条件支持：** 已实现，但要求兼容的可选 Unity 包或渲染管线。
- **外部服务：** 已实现，但要求网络和用户自行配置 Provider 凭据。
- **不可用：** Unity 2019 本身不存在的新版 Unity 概念。

详细边界见 [Unity 2019 支持矩阵](Documentation~/support-matrix.md)。

## 完整参考

- [48 个工具逐项说明](Documentation~/tools-reference.md)
- [25 个 Resource/Template 逐项说明](Documentation~/resources-reference.md)
- [Unity 2019 支持矩阵](Documentation~/support-matrix.md)
- [安全模型](SECURITY.md)

具体参数类型、必填字段和安全注解以 MCP `tools/list` 返回的运行时 Schema
为准。
