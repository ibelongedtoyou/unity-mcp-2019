# Unity 2019 MCP

这是一个面向 Unity `2019.4` 的开源 UPM 包，包含 Unity Editor Bridge 和
无第三方依赖的 Python MCP Server，并对齐
[CoplayDev/unity-mcp](https://github.com/CoplayDev/unity-mcp) 的公开工具、
Resource 与客户端使用方式。

## 实现了什么

默认 MCP Catalog 包含 48 个工具、19 个固定 Resource 和 6 个 Resource
Template。覆盖场景和 GameObject、Component 和 Prefab、资源/材质/Shader/
纹理、C# 脚本编辑、动画、Camera/截图、物理、渲染、Profiler、Package、
构建、测试、可选内容 Provider 和 Unity API 检查。

- [中文功能总览](FEATURES.zh-CN.md)
- [48 个工具逐项说明](Documentation~/tools-reference.md)
- [25 个 Resource/Template 逐项说明](Documentation~/resources-reference.md)
- [Unity 2019 支持矩阵](Documentation~/support-matrix.md)

## 环境要求

- Unity `2019.4` 或更高版本；发布验证基线为 `2019.4.28f1`。
- Python `3.10` 或更高版本，并已加入 `PATH`。
- 通过 Git URL 安装时，Git 必须已加入 `PATH`。
- Codex、Claude 等支持 MCP 的客户端。

ProBuilder、Visual Effect Graph、模型导入器等属于可选能力。缺少对应包时，
工具会返回明确的能力错误，不会伪装成功。

## 安装

在 Unity 中打开 **Window > Package Manager**，选择
**Add package from git URL...**，输入：

```text
https://github.com/ibelongedtoyou/unity-mcp-2019.git#v0.3.0
```

本地开发时选择 **Add package from disk...**，然后选择包根目录的
`package.json`。

## 启动

1. 打开 **Window > MCP for Unity 2019**。
2. 在 **Connect > Advanced** 确认 Python 可执行文件。
3. 启动 MCP；默认地址为 `http://127.0.0.1:6500/mcp`。
4. 在 **Clients** 中配置或复制对应客户端配置。
5. 在 **Diagnostics** 中依次运行协议自检、Bridge 冒烟和 HTTP 健康检查。

Codex：

```powershell
codex mcp add unity-mcp-2019-http --url http://127.0.0.1:6500/mcp
```

Claude Code：

```powershell
claude mcp add --scope local --transport http UnityMCP http://127.0.0.1:6500/mcp
```

## 安全边界

MCP 客户端可以修改场景、资源、构建设置和客户端配置，也可以在显式调用时执行
内存 C#。服务只绑定 loopback，但本机不可信进程仍可能尝试访问端口，因此只应
在可信开发环境中启用。生成式资产工具只有在用户配置 Provider Key 并主动调用时
才会访问第三方服务。

详细说明见 [SECURITY.md](SECURITY.md) 与
[Documentation~/index.md](Documentation~/index.md)。
发布标签前请按 [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) 逐项检查。

## 验证

```powershell
python "Tools~/validate_release.py"
python "Tools~/server.py" --project "<Unity 工程目录>" --self-test
python "Tools~/server.py" --project "<Unity 工程目录>" --bridge-smoke
```

项目采用 MIT 许可证，并在
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 中保留上游归属信息。
