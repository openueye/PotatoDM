# 土豆下载器（PotatoDM）

Windows 下载器，提供 WPF 桌面客户端、CLI 和 Chrome / Edge 浏览器接管。
支持 HTTP/HTTPS 分段下载、断点续传、HLS/DASH 媒体合并及 BT/磁力下载。

## 功能与限制

- **直链下载**：自动或手动连接数、并行分段、重试退避、停滞重连、Range 不可用时降级单流。
- **恢复与调度**：状态快照、远端文件身份校验、多任务队列、动态段拆分、稀疏工作文件和限速。
- **桌面客户端**：进度与分块显示、暂停/继续、搜索与筛选、批量操作、浅色/深色主题、托盘常驻与完成通知、单实例、开机启动及下载目录设置。
- **浏览器接管**：传递 Cookie、Referer、UA，支持站点规则、小文件阈值和页面媒体资源发现。
- **媒体与 BT**：HLS 普通 AES-128 解密、DASH 音视频合并、`.torrent` 与磁力链接、DHT / PEX。

音视频分离的媒体需要本机安装 **ffmpeg**，可放入 PATH 或在设置中指定路径。
DRM 内容、直播 HLS、动态或多 Period DASH 不支持；浏览器扩展仅支持 Chrome / Edge，
不处理 `blob:`、`data:`、`file:`、POST 生成的下载与 MSE 流。

BT 默认完成后停止做种，CLI `--seed` 可保持做种。恢复时会重新校验已有文件；
磁力元数据获取仍受 peer 与公共缓存可用性影响。BT 和 HTTP 使用独立限速器。

## 构建与运行

需要 Windows 和 .NET SDK 8.0.x（[global.json](global.json) 指定 8.0.415）；扩展测试需要 Node.js。

```powershell
dotnet build PotatoDM.sln -c Release --no-incremental
dotnet run --project src/PotatoDM.App

# .NET 与浏览器扩展测试
dotnet test PotatoDM.sln -c Release --no-build
npm --prefix browser test
```

.NET 构建启用分析器并将警告视为错误；扩展测试使用 Node 内置测试框架，无需安装依赖。
集成测试使用本地 HTTP 服务器和真实 CLI / 浏览器宿主子进程；真实浏览器 UI 与公网 BT 仍需手动验证。

## 浏览器接管

1. 启动客户端，打开“浏览器集成”并点击“安装 / 修复”。
2. 在 Chrome / Edge 扩展页开启开发者模式，选择“加载已解压的扩展程序”，载入
   `%LOCALAPPDATA%\PotatoDM\browser\chromium`。
3. 点击扩展图标查看连接状态、调整接管规则，或扫描当前页面的媒体资源。

扩展通过 Native Messaging → `PotatoDM.BrowserHost` → 命名管道连接客户端，必要时自动启动客户端。
普通下载接管失败时交回浏览器；显式媒体清单和磁力请求被拒绝时不自动回退。
资源列表只有视频详情页链接时，需要打开详情页并播放后再扫描。

## CLI

```powershell
# 查看完整选项
dotnet run --project src/PotatoDM.Cli -- --help

# 探测、下载与续传
dotnet run --project src/PotatoDM.Cli -- probe "https://example.com/file.zip"
dotnet run --project src/PotatoDM.Cli -- download "https://example.com/file.zip" -o .\file.zip -c 8
dotnet run --project src/PotatoDM.Cli -- resume .\file.zip.download.json

# BT / 磁力（将路径替换为 .torrent 文件或 magnet 链接）
dotnet run --project src/PotatoDM.Cli -- torrent .\example.torrent -o .\downloads
```

`download` 自动识别 HLS/DASH 清单。常用选项：

| 选项 | 用途 |
|---|---|
| `-o PATH` / `-c N` | 输出文件或目录 / 并行连接数（默认 8） |
| `--max-speed BYTES` | 限速，单位字节/秒，0 表示不限 |
| `--max-per-host N` / `--max-connections-per-job N` | 每主机 / 单任务连接上限 |
| `--dynamic-segments` / `--sparse` | 动态段拆分 / 稀疏工作文件 |
| `--restart` / `--restart-on-change` | 从头下载 / 远端文件变化时重新开始 |
| `--ffmpeg PATH` | 指定音视频合并工具 |
| `--referer URL` / `--cookie STR` / `--user-agent STR` | 请求上下文 |
| `--no-proxy` / `--json` / `--quiet` | 禁用系统代理 / NDJSON 事件 / 隐藏进度 |

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 未分类错误（含 BT 失败） |
| 2 | 参数错误 |
| 3 | 探测失败 |
| 4 | HTTP 永久失败 |
| 5 | 远端文件变化 |
| 6 | 文件访问错误 |
| 7 | 状态或锁冲突 |
| 8 | 不支持的媒体 |
| 9 | HTTP 暂时性失败，可稍后重试 |
| 130 | 用户取消 |

## 数据与恢复

应用数据位于 `%LOCALAPPDATA%\PotatoDM\`：`settings.json` 保存设置，`jobs.json` 保存任务列表，
`logs/` 保存运行日志，`browser/` 保存扩展、宿主清单与宿主日志。浏览器 Cookie 使用 Windows DPAPI 保护后保存。

桌面客户端为每个任务创建独立文件夹，最终文件位于根目录，过程数据集中在 `.potatodm/`：
直链使用 `.download.json` 状态快照和工作文件，媒体使用 `media/` 缓存，BT 使用 `torrent-cache/`。
CLI 直接指定输出文件时，状态和工作文件位于目标文件旁。

暂停或传输失败会保留可恢复数据。直链续传重新校验长度、ETag / Last-Modified；
媒体续传校验清单指纹并复用已完成分片。关闭窗口默认隐藏到托盘，使用托盘“退出”保存状态并结束进程。

## 代码结构

| 目录 | 职责 |
|---|---|
| [PotatoDM.Core](src/PotatoDM.Core/) | HTTP 引擎、分段、媒体解析与合并、状态存储；仅依赖 BCL |
| [PotatoDM.AppCore](src/PotatoDM.AppCore/) | UI 无关的任务编排、队列、设置和持久化 |
| [PotatoDM.App](src/PotatoDM.App/) | WPF 界面、托盘和浏览器安装器 |
| [PotatoDM.Cli](src/PotatoDM.Cli/) | 命令行入口 |
| [PotatoDM.Torrent](src/PotatoDM.Torrent/) | libtorrent 传输与 MonoTorrent 磁力元数据适配 |
| [PotatoDM.BrowserIpc](src/PotatoDM.BrowserIpc/) / [BrowserHost](src/PotatoDM.BrowserHost/) | 浏览器协议、消息帧和命名管道宿主 |
| [browser](browser/) / [tests](tests/) | Chromium 扩展及测试 / .NET 测试与故障注入服务器 |

`DownloadManager` 按 `Browser`、`Queue`、`Transfers`、`Persistence`、`Files` 拆分；
`DownloadEngine.Workers` 负责工作线程，`DownloadValidation` 负责校验；媒体模块由
`MediaManifestLoader`、`MediaHttp` 及 `MediaDownloader` 的 `Transfer` / `Output` 分部文件组成。
分部文件共享实例与同步锁，整理时保持公开 API、取消行为、持久化顺序和续传数据不变。
扩展新增脚本或 manifest 资源时，须同步更新 `BrowserExtensionLayout.RequiredSourceFiles`。

本地故障注入服务器：`dotnet run --project tests/PotatoDM.TestServer -- --port 5099`；
端点与参数见 [DownloadEndpoints.cs](tests/PotatoDM.TestServer/DownloadEndpoints.cs)。

## 许可证

[MIT](LICENSE)。第三方组件声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
