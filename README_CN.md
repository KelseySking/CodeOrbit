# CodeOrbit

[English](README.md)

CodeOrbit 是一个中心化基座。它负责接入本机 CLI hook 事件，归一化会话、审批和问答状态，并通过带 token 认证的 REST/WebSocket 接口把同一份状态提供给多个展示端。

这个仓库负责：

- `CodeOrbit.Contracts`：公开 REST/WebSocket DTO 合同。
- `CodeOrbit.Core`：hook 模型、source adapter、响应构造、transcript 读取、settings、IPC 协议。
- `CodeOrbit.Hub`：Runtime 状态、HookServer、source service、REST API、WebSocket 广播、本地 token 存储。
- `CodeOrbit.RuntimeHost`：独立 Runtime 进程。
- `CodeOrbit.Bridge`：短生命周期 CLI hook bridge。
- Runtime 侧测试、文档和外部展示端示例。

Windows HUD 只是官方展示客户端。它应该通过 `CodeOrbit.Contracts` 和 RuntimeHost/Bridge 可执行产物集成，不应该继续编译依赖内部实现。

## 拓扑

默认本地 managed 模式：

```text
Windows HUD -> 启动 127.0.0.1 上的 CodeOrbit.RuntimeHost -> REST/WebSocket
CLI hook -> CodeOrbit.Bridge -> named pipe -> 状态管理
```

共享远程模式必须显式开启。只有当用户明确希望手机、Web、硬件屏幕或其他设备通过局域网连接时，才使用 `--host 0.0.0.0` 或 `api_bind_host=0.0.0.0`。默认不要开放局域网监听。

## 构建

```powershell
dotnet build CodeOrbit.slnx
dotnet test CodeOrbit.slnx
```

开发时启动：

```powershell
dotnet run --project src/CodeOrbit.RuntimeHost -- --token dev-token --port 32145 --no-repair
```

展示端连接 `http://127.0.0.1:32145`，token 使用 `dev-token`。

## 可扩展性

CodeOrbit 支持通过**插件系统**扩展 CLI 源。这使你可以添加新的 AI CLI 工具支持，无需重新编译。

### 插件系统功能

- **自动 CLI 检测**：插件可以定义进程名、环境变量、路径模式来自动检测正在运行的 CLI
- **Hook 安装**：插件指定如何将 hook 安装到 CLI 的配置文件中
- **内置插件**：自带 Claude Code、Codex 等内置支持
- **用户插件**：将 JSON 文件放入 `%AppData%\CodeOrbit\sources\` 即可注册自定义 CLI

### 快速开始

在 `%AppData%\CodeOrbit\sources\` 中创建插件文件（例如 `my-cli.json`）：

```json
{
  "schema_version": "2.0",
  "source": {
    "key": "my-cli",
    "display_name": "My CLI",
    "icon_name": "terminal",
    "permission_response_style": "claude-style"
  },
  "detection": {
    "process_names": ["my-cli"],
    "priority": 100
  },
  "hook_installation": {
    "format": "flat",
    "config_path": "~/.my-cli/hooks.json",
    "events": ["PreToolUse", "PostToolUse"],
    "timeout_seconds": 10
  }
}
```

然后使用 ConfigInstaller 安装 hook：

```csharp
using CodeOrbit.Core.Services;

bool success = ConfigInstaller.InstallPlugin("my-cli");
```

### 文档

- **中文**：[插件系统指南](docs/source-plugins.md) | [插件 Schema 参考](docs/plugin-schema.md)
- **English**: [Plugin System Guide](docs/source-plugins.en.md) | [Plugin Schema Reference](docs/plugin-schema.en.md)

### 内置插件

自带以下内置 CLI 插件：

- **Claude Code** (`claude.json`) - 12 个事件，claude-matcher 格式
- **Codex CLI** (`codex.json`) - 7 个事件，nested 格式，支持 config.toml

更多内置插件即将推出。

## 接口和展示端开发

- [中文文档索引](docs/README_CN.md)
- [完整 API 文档](docs/api-reference.md)
- [其他应用集成方式](docs/integration-guide.md)
- [职责边界](docs/runtime-display-contract.zh-CN.md)
- [展示端快速开始](docs/external-display-client.md)

英文文档：

- [Documentation index](docs/README.md)
- [Full API reference](docs/api-reference.en.md)
- [Integration patterns for other apps](docs/integration-guide.en.md)
- [Ownership contract](docs/runtime-display-contract.md)
- [Display client quickstart](docs/external-display-client.en.md)

最小控制台展示端位于 `samples/external-display-console`：

```powershell
dotnet run --project samples/external-display-console -- --help
```

## 发布产物

生成包含 `CodeOrbit.RuntimeHost.exe`、`CodeOrbit.Bridge.exe` 和 `runtime-manifest.json` 的 ZIP：

```powershell
.\scripts\publish-runtime.ps1 -Runtime win-x64
```

Windows HUD 可以读取更新 manifest，下载 ZIP，并把 payload 提升到本机缓存目录。

## 前端集成建议

前端展示端只负责 UI、交互、动画、主题和设备适配。它应该：

- 启动时读取 Runtime `/api/health`、`/api/capabilities`、`/api/sessions`、`/api/pending`。
- 通过 `WS /api/events` 订阅变化，断线重连后重新拉取 REST 快照。
- 对审批、拒绝、问答、关闭等用户操作调用 REST action endpoint。
- 保持 UI-only 状态在本地，例如选中项、窗口位置、主题、动画、声音。
- 不读取 Hub/Core/Bridge 内部类型，不直接读 transcript 文件，不自己实现 hook response。

官方 Windows HUD 的默认体验是：启动 HUD 时启动本地进程，退出 HUD 时只关闭自己拥有的本地私有进程；如果显式绑定到 `0.0.0.0` 进入共享远程模式，HUD 退出时不关闭进程，避免断开手机或其他展示端。
