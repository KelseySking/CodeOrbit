# Changelog

All notable changes to CodeOrbit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- 多设备审批协同：`pending.resolved` 实时事件新增 `resolution`（`PendingResolutionDto`），携带 `decision`、`actor`、`reason`、`resolvedAtUtc`，让未操作的展示端也能知道该审批被谁、以何种方式结束
- 审批历史接口 `GET /api/pending/history?limit=`，断线重连或后加入的展示端可补看已结束审批的决策记录（进程内环形缓冲，上限 200 条）
- 决策请求体（`PermissionDecisionRequest` / `QuestionAnswerRequest` / `QuestionCurrentAnswerRequest`）新增可选 `actor` 字段，用于自报发起端标识

### Changed
- 超时结束的 pending 也会记入历史并广播 `pending.resolved`（`decision="timeout"`），此前超时不广播任何决定信号

## [1.0.1] - 2026-06-18

### Added
- 新增 GitHub Copilot 和 Cline 的 Hook 安装支持

### Changed
- 优化会话与超时处理，提升运行稳定性
- 更新内置插件列表与文档说明

### Fixed
- 修正版本号显示

## [1.0.0] - 2026-06-16

### Added
- 初始版本发布
- 支持 Claude Code 和 Codex CLI 源
- REST API 和 WebSocket 实时事件推送
- Named Pipe IPC 桥接
- Session 和 Pending Action 队列管理
- Windows x64 平台支持

### Documentation
- API 参考文档（中英双语）
- 集成指南
- Runtime/Display 契约文档

[1.0.1]: https://github.com/KelseySking/CodeOrbit/releases/tag/v1.0.1
[1.0.0]: https://github.com/KelseySking/CodeOrbit/releases/tag/v1.0.0
