# KnowledgeLauncher

[![CI](https://github.com/danlingdan/KnowledgeLauncher/actions/workflows/ci.yml/badge.svg)](https://github.com/danlingdan/KnowledgeLauncher/actions/workflows/ci.yml)

面向 `danlingdan/ComputerKnowledgeBase` 的 Windows Obsidian 知识库启动器。目前完成开发计划的阶段 0 至阶段 4，支持首次安装、打开、更新、回滚与修复知识库。

## 已实现

- .NET 10 WPF、Core、Infrastructure、单元测试和集成测试工程；
- `%LOCALAPPDATA%\KnowledgeLauncher` 目录、配置恢复和滚动日志；
- Obsidian 注册表、安装路径、URI 协议和版本检测；
- GitHub 最新 Release 查询、超时、有限重试和流式下载；
- SHA-256 校验、安全 ZIP 解压和清单结构验证；
- 锁定版本插件安装及 `community-plugins.json` 更新；
- 不写入正式 Vault 的最小端到端技术验证流程；
- Windows CI 和自包含单文件发布配置；
- 首次安装状态机、遗留暂存清理和目录级正式启用；
- 多插件锁定安装、社区插件信任确认和本地安装记录；
- WinGet/官方下载页 Obsidian 安装与版本更新引导；
- 首次 Vault 注册提示及后续一键打开；
- 远程版本检查、保留/合并策略、单版本备份和失败回滚；
- 一键检查更新与修复安装，Obsidian 运行时阻止文件切换。

## 本地运行

```powershell
dotnet restore KnowledgeLauncher.slnx
dotnet test KnowledgeLauncher.slnx --configuration Release
dotnet run --project src/KnowledgeLauncher.App/KnowledgeLauncher.App.csproj
```

发布：

```powershell
dotnet publish src/KnowledgeLauncher.App/KnowledgeLauncher.App.csproj `
  --configuration Release `
  --output artifacts/win-x64
```

## 自动构建与发布

- 推送到 `main` 或提交 Pull Request 时，GitHub Actions 会自动还原、编译、测试并上传 Windows x64 构建产物。
- 推送形如 `v0.3.0` 的标签时，会自动创建对应 GitHub Release，附带 Windows x64 ZIP 和 SHA-256 校验文件。
- 也可以在 GitHub Actions 的 `release` 工作流中手动输入 `major.minor.patch` 版本号发布。

例如：

```powershell
git tag v0.3.0
git push origin v0.3.0
```

## Release 资产契约

目标仓库的最新正式 Release 必须包含：

```text
knowledge-v<version>.zip
manifest.json
checksums.json
```

ZIP 根目录必须含 `launcher/manifest.json`、`launcher/plugin-lock.json`、`launcher/update-policy.json` 和入口笔记。外部 `manifest.json` 的 `id`、`version` 必须与 ZIP 内清单一致。

`checksums.json` 支持直接对象，或以 `files`/`assets` 包装的对象：

```json
{
  "files": {
    "knowledge-v2026.7.16.1.zip": "64位十六进制SHA-256",
    "manifest.json": "64位十六进制SHA-256"
  }
}
```

插件 Release 必须以锁定版本或带 `v` 前缀的版本作为标签，并分别提供 `main.js`、`manifest.json` 和清单声明的可选 `styles.css`。

## 当前真实联调状态

目标仓库已于 2026-07-16 发布首个规范版本 `vault-v2026.07.16.1`，包含 `knowledge-v2026.07.16.1.zip`、`manifest.json` 和 `checksums.json`。未认证的 GitHub latest Release API 请求返回 HTTP 200，外部清单哈希验证通过，启动器不再因缺少 Release 返回 `KL3004`。
