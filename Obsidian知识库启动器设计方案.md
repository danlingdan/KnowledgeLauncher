# Obsidian 知识库一条龙启动器设计方案

## 1. 项目目标

开发一个面向个人分享场景的 Windows 软件，让其他人无需理解 Git、仓库、分支或插件安装流程，即可打开并持续更新一个完整的 Obsidian 知识库。

期望的用户体验：

1. 用户运行启动器。
2. 启动器检查本机是否已安装 Obsidian。
3. 未安装时，引导用户通过官方渠道安装。
4. 启动器自动下载知识库。
5. 启动器安装并锁定知识库需要的插件、主题和配置。
6. 启动器通过 Obsidian 打开指定首页。
7. 后续运行时自动检查知识库更新。

普通用户界面不出现 `clone`、`pull`、`commit`、分支等 Git 概念。

## 2. 产品边界

### 2.1 Obsidian 本体

启动器修改 Obsidian，在交付包中携带 Obsidian 二进制文件。已获得Obsidian 官方分发许可。

推荐方式：

- 检测本机是否已安装 Obsidian；
- 未安装时，经用户确认调用 WinGet 或打开官方下载页面；
- 安装完成后，由启动器配置和打开知识库；
- 如果未来获得 Obsidian 官方分发许可，只需替换安装来源模块，无需修改整体架构。

### 2.2 插件兼容性

由于知识库必须支持现有 Obsidian 插件，软件不重新实现 Markdown 阅读器，而是使用真正的 Obsidian 运行知识库和插件。

启动器负责：

- 安装指定插件；
- 锁定插件版本；
- 同步插件配置；
- 检查插件文件完整性；
- 检查插件要求的最低 Obsidian 版本；
- 在更新失败时回滚。

### 2.3 安全边界

可以采用“受信任知识库”模型：知识库维护者负责其中笔记、主题和插件的安全性，启动器不审核插件的具体代码行为。

但启动器至少应保证：

- 下载来源正确；
- 文件没有在传输过程中被篡改；
- 插件及知识库版本符合发布清单；
- 更新失败不会破坏现有知识库；
- 首次运行时明确提示用户将执行社区插件代码。

## 3. 推荐技术栈

第一版采用：

- 平台：Windows 10/11；
- 开发框架：.NET 10；
- 界面：WPF；
- 网络访问：`HttpClient`；
- 配置格式：JSON；
- 发布方式：自包含单文件 EXE；
- 知识库来源：GitHub Releases；
- Obsidian 安装：自带或者官方渠道或 WinGet；
- Obsidian 启动：`obsidian://` URI。

选择 WPF 的原因是该程序主要处理 Windows 进程、文件系统、下载、更新和注册表检测，不需要复杂的网页渲染环境。

## 4. 整体架构

```text
KnowledgeLauncher.exe
├── ObsidianDetector       检测 Obsidian 安装状态和版本
├── ObsidianInstaller      调用官方安装渠道
├── ReleaseClient          访问 GitHub Releases API
├── VaultDownloader        下载知识库发布包
├── PluginResolver         解析并安装锁定版本插件
├── ManifestVerifier       校验清单和 SHA-256
├── VaultUpdater           更新、合并和回滚知识库
├── ObsidianLauncher       打开仓库及指定首页
├── SettingsService        管理本地配置
└── LoggingService         记录运行和错误日志
```

整体流程：

```text
作者使用 Obsidian
    ↓
推送知识库到 GitHub
    ↓
GitHub Actions 生成 Release ZIP 和清单
    ↓
启动器检查最新版本
    ↓
下载到 staging 并验证
    ↓
更新本地知识库和插件
    ↓
调用 Obsidian 打开首页
```

## 5. 本地目录结构

建议将程序数据保存到：

```text
%LOCALAPPDATA%\KnowledgeLauncher\
├── app\
│   └── launcher.json
├── vault\
│   └── 当前使用的知识库
├── staging\
│   └── 正在下载和验证的新版本
├── backup\
│   └── 上一个可用版本
├── cache\
│   └── 发布包和插件下载缓存
└── logs\
    └── launcher.log
```

不得在验证完成之前直接覆盖正式知识库。

## 6. 首次运行流程

1. 启动器读取本地配置。
2. 检测 Obsidian 是否安装以及版本是否满足要求。
3. 如果未安装，显示安装说明并由用户确认。
4. 通过官方渠道或 WinGet 启动安装过程。
5. 获取 GitHub 最新 Release 信息。
6. 下载知识库 ZIP、`manifest.json` 和校验文件。
7. 在 `staging` 目录解压。
8. 验证文件哈希和版本要求。
9. 根据插件锁定文件下载指定版本插件。
10. 生成 `.obsidian/community-plugins.json`。
11. 将验证完成的知识库移动到正式目录。
12. 启动 Obsidian。
13. 第一次由用户确认将该文件夹作为 Vault 打开。
14. 以后通过 Obsidian URI 直接打开指定首页。

示例 URI：

```text
obsidian://open?vault=我的知识库&file=首页
```

也可以使用绝对路径：

```text
obsidian://open?path=C%3A%5CUsers%5CUser%5CAppData%5CLocal%5CKnowledgeLauncher%5Cvault%5C首页.md
```

使用绝对路径时仍要求对应文件夹已经被 Obsidian 注册为 Vault，因此首次运行可能需要一次人工确认。

## 7. GitHub 发布方式

读者端不直接使用 Git，而是通过 GitHub Releases 下载构建后的知识库包。

每次正式发布生成：

```text
knowledge-v2026.07.15.1.zip
manifest.json
checksums.json
manifest.sig             可选，后续加入数字签名
```

公开仓库可以直接使用 GitHub Releases API：

```text
GET /repos/{owner}/{repo}/releases/latest
```

GitHub Actions 的职责：

- 排除不应分享的文件；
- 生成版本号；
- 打包知识库；
- 生成 SHA-256 校验信息；
- 创建或更新 Release；
- 上传发布资产。

## 8. 知识库清单

仓库中增加启动器专用目录：

```text
launcher/
├── manifest.json
├── plugin-lock.json
└── update-policy.json
```

### 8.1 `manifest.json`

```json
{
  "id": "my-knowledge-base",
  "name": "我的知识库",
  "version": "2026.07.15.1",
  "entry": "首页.md",
  "minimumObsidianVersion": "1.11.0",
  "pluginLock": "launcher/plugin-lock.json",
  "updatePolicy": "launcher/update-policy.json"
}
```

### 8.2 `plugin-lock.json`

```json
{
  "plugins": [
    {
      "id": "dataview",
      "version": "0.5.68",
      "repository": "blacksmithgu/obsidian-dataview",
      "minimumObsidianVersion": "1.5.0",
      "sha256": {
        "main.js": "待填写",
        "manifest.json": "待填写",
        "styles.css": "待填写"
      }
    }
  ]
}
```

插件文件安装到：

```text
vault\.obsidian\plugins\{plugin-id}\
├── manifest.json
├── main.js
├── styles.css
└── data.json
```

`data.json` 通常包含用户配置，更新时默认保留，不随插件程序文件一起覆盖。

### 8.3 `update-policy.json`

```json
{
  "overwrite": [
    "**/*.md",
    "assets/**",
    ".obsidian/plugins/*/main.js",
    ".obsidian/plugins/*/manifest.json",
    ".obsidian/plugins/*/styles.css"
  ],
  "preserve": [
    ".obsidian/workspace.json",
    ".obsidian/workspace-mobile.json",
    ".obsidian/hotkeys.json",
    ".obsidian/plugins/*/data.json"
  ],
  "merge": [
    ".obsidian/app.json",
    ".obsidian/appearance.json"
  ]
}
```

## 9. 插件管理

插件必须精确锁定版本，不能默认安装最新版。新版插件可能改变接口、查询结果或渲染方式，从而破坏已有知识库。

插件安装流程：

1. 读取 `plugin-lock.json`。
2. 从插件对应的 GitHub Release 下载锁定版本。
3. 获取 `manifest.json`、`main.js` 和可选的 `styles.css`。
4. 计算 SHA-256 并与锁定文件比较。
5. 写入 `.obsidian/plugins/{plugin-id}`。
6. 更新 `.obsidian/community-plugins.json`。
7. 保留已有的插件 `data.json`。

需要额外检查每个第三方插件的许可证及再分发条件。如果不直接携带插件文件，而是在用户设备上从插件官方 Release 下载，可以减少相关分发问题。

## 10. 更新与回滚

更新过程必须在 Obsidian 未使用该 Vault 时执行，避免文件被占用或配置在更新过程中被重新写入。

推荐过程：

```text
检查远程版本
→ 下载到 staging
→ 验证清单和哈希
→ 请求关闭 Obsidian Vault
→ 备份现有版本
→ 保留本地状态文件
→ 应用新版本
→ 再次验证
→ 启动 Obsidian
```

如果任何步骤失败：

```text
停止更新
→ 删除不完整的新版本
→ 从 backup 恢复
→ 记录错误日志
→ 向用户显示“修复”或“重试”按钮
```

第一版建议只保留一个上一个可用版本，避免备份无限增长。

## 11. 本地修改策略

必须提前确定读者是否允许编辑知识库。

第一版建议采用“内容由上游管理，本地只保留个人状态”的模式：

- 上游控制笔记、附件、模板、插件程序和基础配置；
- 本地保留工作区、快捷键及插件个人配置；
- 用户对上游笔记的直接修改可能在下一次更新时被覆盖；
- 界面中应明确提示这一行为。

如果以后要支持用户编辑，需要增加：

- 本地修改检测；
- 三方合并；
- 冲突提示；
- 用户分支或补丁导出；
- GitHub 身份认证和写入权限。

这部分不进入第一版。

## 12. 界面设计

第一版可以只有一个主窗口：

```text
┌────────────────────────────────────────┐
│ 我的知识库                             │
│                                        │
│ 状态：已安装，版本 2026.07.15.1        │
│ Obsidian：已就绪                       │
│ 插件：12 个，全部正常                  │
│                                        │
│ [打开知识库]  [检查更新]  [修复安装]   │
│                                        │
│ 上次更新：2026-07-15 23:30             │
└────────────────────────────────────────┘
```

首次运行时增加进度页面：

```text
正在准备知识库
[✓] 检查 Obsidian
[✓] 下载知识库
[✓] 验证文件
[…] 配置插件
[ ] 打开首页
```

## 13. 第一版 MVP 范围

### 包含

- Windows 10/11；
- 单个公开 GitHub 仓库；
- 检测 Obsidian；
- 引导官方安装；
- 下载 GitHub Release ZIP；
- 解析知识库清单；
- 安装锁定版本插件；
- SHA-256 校验；
- 首次注册 Vault；
- 一键打开指定首页；
- 检查更新；
- 更新失败回滚；
- 日志与修复安装。

### 暂不包含

- 私有仓库授权；
- 多个知识库；
- 用户修改的 Git 合并；
- macOS 和 Linux；
- 后台常驻服务；
- 自动升级到插件最新版；
- 无人值守安装 Obsidian；
- 插件代码安全审计。

## 14. 建议开发顺序

1. 创建 WPF 启动器空项目。
2. 实现 Obsidian 检测。
3. 实现配置和日志模块。
4. 实现 GitHub Release 查询和文件下载。
5. 实现 ZIP 解压及 SHA-256 校验。
6. 实现知识库清单解析。
7. 实现插件锁定和安装。
8. 实现首次 Vault 注册引导。
9. 实现 Obsidian URI 启动。
10. 实现更新、备份和回滚。
11. 制作 GitHub Actions 发布流程。
12. 打包测试版并在全新 Windows 环境验证。

## 15. 第一个技术验证目标

在正式设计完整界面之前，先完成一个命令行或极简窗口原型，跑通以下链路：

```text
检测 Obsidian
→ 下载一个测试知识库 Release
→ 安装一个锁定版本插件
→ 首次注册 Vault
→ 使用 Obsidian URI 打开首页
```

只有这条链路稳定后，再投入时间开发正式界面、自动更新和回滚功能。

