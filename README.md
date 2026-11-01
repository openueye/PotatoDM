# 土豆下载器（PotatoDM）

类 IDM 的 Windows 下载器——先做可复用的下载引擎（分段并行、断点续传、故障恢复），
再做 WPF 界面与浏览器接管。本仓库当前完成 **Milestone 0/1：直链 HTTP/HTTPS 下载引擎 + CLI + 本地故障注入测试服务器**、
**Milestone 2：WPF 桌面客户端**、**Milestone 3：Chrome / Edge 浏览器接管**，以及
**Milestone 4：HLS/DASH 媒体合并、DRM 拒绝、BT/磁力与稀疏工作文件**。

## 当前能力

- `Range` 探测与分段并行下载，按文件大小自动选择 1/4/8/16 连接，也可手动指定
- 预分配 + 绝对偏移写入（等价 `pwrite`），无分片合并
- 断点续传：Sidecar 状态文件、原子替换、每段独立偏移、崩溃/杀进程后可恢复
- 掉电安全：快照先冻结进度并 `FlushFileBuffers` 数据文件，再原子写 sidecar，最多重下约 1 秒
- 身份校验：总长度 + 强 ETag / Last-Modified，变化时拒绝续传或 `--restart-on-change`
- 重试与退避：408/429/5xx/网络异常，指数退避 + 抖动，`Retry-After` 优先
- 停滞看门狗：长时间无数据自动断线重连，从最后成功偏移继续
- 协议异常处理：Range 被忽略时自动降级单流、短 206、错误 `Content-Length`、强制 gzip 拒绝、Cookie 质询、重定向
- 并发上限：每主机上限 + 单任务连接上限（应用设置「每任务最大连接数」或 CLI `--max-connections-per-job`）
- 动态段拆分（可选）：先下完的连接接过另一段尚未完成的尾部，单个慢响应不再拖住整个任务
- 稀疏工作文件（可选）：`--sparse` 或应用设置，工作文件只占用已下载范围，适合超大文件
- 全局限速：所有任务共享一个进程级带宽预算，应用设置或 CLI `--max-speed` 配置，0 表示不限
- HLS：master/media playlist、AES-128-CBC、`EXT-X-MAP`、`BYTERANGE`、`DISCONTINUITY/GAP`；
  直播清单明确拒绝
- DASH：`SegmentTemplate`（含 `$Number$` / `$Time$`、`SegmentTimeline`）、`SegmentList`、
  `SegmentBase` 与多层 `BaseURL`；动态、多 Period 清单明确拒绝
- 媒体合并：分段先落到任务目录内的 `.potatodm/media` 缓存（CLI 为 `<target>.potatodm-media`），
  完成后封装输出；音视频分离的清单调用外部 `ffmpeg` 合并（可用设置或 `--ffmpeg` 指定路径）
- DRM：识别 Widevine / PlayReady / FairPlay、SAMPLE-AES 与 DASH CENC/`ContentProtection` 后
  **明确拒绝并报错**，不尝试解密或绕过；普通 AES-128 HLS 密钥不受影响
- BT/磁力：libtorrent-rasterbar 2.0.10（经 csdl 1.2.1 绑定，独立 `PotatoDM.Torrent` 程序集，
  Core 仍零第三方依赖），支持磁力链接与 `.torrent`、uTP、MSE 协议加密、DHT / PEX；
  磁力元数据先由 MonoTorrent 或公共元数据缓存取得，再由 libtorrent 传输；DHT 从多个公共
  路由引导，公共种子始终补充一组公共 tracker 并向所有 tracker/tier 宣告（私有种子除外）；
  默认下载完成后停止做种
- 多任务队列：同时下载任务数可配置（默认 3），超出的任务显示“排队中”，按入队顺序自动开始
- CLI：`probe` / `download` / `resume` / `torrent`，人类可读进度或 NDJSON 事件流
- 测试服务器：确定性内容 + 参数化故障注入 + 请求日志
- WPF 客户端：任务列表、实时进度/速度/剩余时间、暂停/继续、移除、打开文件/文件夹，
  HTTP 分段与种子任务都显示分块进度条（种子为 48 块），关闭时保存任务、下次启动自动恢复
  （含媒体缓存与 BT 缓存/元数据的续传）
- 浏览器接管：Chrome / Edge 扩展把 http/https 下载、HLS/DASH 清单与磁力链接经 Native Messaging
  交给客户端；普通下载被拒绝或不可用时浏览器立即回退重下，媒体/DRM/磁力任务不回退；
  Cookie、Referer、UA 传给引擎并在重启后续传时复用
- 单实例：第二次启动会唤起已有窗口并退出；浏览器宿主可在客户端未运行时自动拉起
- 托盘常驻：关闭窗口后下载继续，支持托盘新建、暂停全部、继续全部和安全退出
- 应用内设置：自动连接数、固定连接数、同时下载任务数、全局限速、动态段拆分、稀疏文件、
  ffmpeg 路径、系统代理、关闭到托盘、静默启动和开机启动
- 资源发现：扩展列出当前页面的视频、音频、HLS/DASH 清单与常见媒体直链，可手动发送到客户端
- 接管规则：全局开关、当前站点开关、小文件阈值和右键“使用土豆下载器下载”

明确边界：受 DRM 保护的内容只识别、拒绝，不做解密或绕过；音视频分离的媒体需要本机安装
ffmpeg；BT 默认下载完成后停止做种（CLI `--seed` 可保持做种直到 Ctrl+C）。

## 仓库结构

```
PotatoDM.sln
global.json                        # 锁定 .NET SDK 8.0.415
Directory.Build.props              # C# 12、Nullable、TreatWarningsAsErrors
src/
  PotatoDM.Core/                   # 引擎（仅 BCL，零第三方依赖）
    Http/                          # 探测、请求构造、头解析、文件名净化
    Planning/                      # SegmentPlanner、SegmentSplitter
    Transfer/                      # DownloadEngine、SegmentWorker、重试、限流、速度
    Media/                         # HLS/DASH 解析、AES-128、MediaDownloader、FfmpegMuxer
    Storage/                       # 预分配/稀疏文件、JSON 状态存储、锁、路径规则
    Models/  Exceptions/  Internal/
  PotatoDM.Torrent/                # BT/磁力适配层（libtorrent 传输；MonoTorrent 取磁力元数据）
  PotatoDM.AppCore/                # UI 无关的桌面编排层（net8.0）
    Job/                           # DownloadManager、DownloadJob 视图模型
    Services/                      # 作业注册表、设置、路径、UI 线程抽象
    Models/
  PotatoDM.App/                    # WPF 客户端（net8.0-windows，产品程序集名 PotatoDM）
  PotatoDM.Cli/                    # 控制台客户端（程序集名 potatodm）
  PotatoDM.BrowserIpc/             # 浏览器协议契约、原生消息帧、命名管道（零依赖）
  PotatoDM.BrowserHost/            # Native Messaging 宿主（stdio ↔ 命名管道）
browser/
  shared/                          # 扩展根目录：background.js、popup 与共用模块（Chrome / Edge 共用）
  chromium/                        # manifest（构建时复制到客户端输出）
  test/                            # 扩展纯逻辑测试（node:test，零依赖）
tests/
  Shared/                          # 测试项目共享的辅助类型（TempDirectory）
  PotatoDM.TestServer/             # 故障注入服务器（ASP.NET Core Minimal API）
  PotatoDM.Core.Tests/             # 引擎与媒体解析单元测试（xUnit）
  PotatoDM.App.Tests/              # 编排层单元测试（假引擎、假 BT 下载器）
  PotatoDM.Torrent.Tests/          # BT 回环做种/下载与磁力元数据测试
  PotatoDM.BrowserIpc.Tests/       # 帧格式、扩展 ID、管道并发/超时
  PotatoDM.IntegrationTests/       # loopback 集成测试（含真实子进程与宿主进程）
```

## 构建与测试

需要 .NET SDK 8.0.x。

```powershell
dotnet build PotatoDM.sln
dotnet test PotatoDM.sln
```

单元测试覆盖解析、请求构造、分段规划、重试策略、状态存储、进度/限流、HLS/DASH 清单与
AES-128 等纯逻辑；`PotatoDM.App.Tests` 用假引擎与假 BT 下载器测试任务编排、媒体/磁力
持久化与恢复；`PotatoDM.Torrent.Tests` 在同一进程内用两个 MonoTorrent 引擎经 loopback
完成 `.torrent` 与磁力下载（含元数据交换），不访问互联网；libtorrent 传输路径依赖
原生库与真实网络，由手动验证覆盖；
`PotatoDM.BrowserIpc.Tests` 覆盖原生消息帧、扩展 ID 与命名管道；
集成测试启动真实 HTTP 服务器、真实 CLI 与浏览器宿主子进程，只访问 loopback。

WPF 客户端仅支持 Windows，直接运行：

```powershell
dotnet run --project src/PotatoDM.App
```

浏览器扩展的纯逻辑另有一套零依赖测试（Node 内置 `node:test`，不安装任何包）：

```powershell
cd browser
npm test
```

它覆盖 `shared/settings.js` 的阈值解析、`shared/resources.js` 的资源去重/排序/分类/命名，
以及 `shared/protocol.js` 的接管判定与超时链关系；`test/background-wiring.test.js` 会在
一个带 `chrome` stub 的沙箱里真实执行 `background.js`，验证 `importScripts` 接线并回归
「阈值设为 0 后重启又回来」这个缺陷。扩展本身仍无需构建步骤，`shared/` 里的文件原样打包。

扩展是 Chromium 直接加载的目录：少一个文件不会抛异常，只会让 service worker 在
`importScripts()` 处静默启动失败，用户看到的是「下载器没反应」。所以安装前由
`BrowserExtensionLayout.RequiredSourceFiles` 逐项校验，**往 `shared/` 加脚本或往 manifest
加引用资源时必须同步加进这个清单**；`BrowserExtensionLayoutTests` 会拿 `browser/shared/`
和 `manifest.json` 的实际内容对照清单，漏了直接红。

单独重跑进程级恢复测试：

```powershell
dotnet test PotatoDM.sln --filter "FullyQualifiedName~Killed_process_resumes"
```

## WPF 客户端

- 任务列表：文件名、大小、进度条、速度、剩余时间、状态、活动连接数
- 工具栏：新建下载、暂停、继续、移除、打开文件、打开文件夹、浏览器集成；双击已完成任务直接打开
- 新建下载对话框：URL 或 magnet 磁力链接、保存目录、自动连接或固定连接数（1/4/8/16/32）
- 状态栏汇总下载中任务数与总速度
- 暂停通过取消令牌触发，引擎把进度原子写入 sidecar；继续调用引擎的 `ResumeAsync`
- 任务队列：同时下载任务数（默认 3，可在设置中修改），超出部分显示“排队中”；
  任务完成或暂停后按入队顺序自动启动下一个；排队中的任务可以暂停（尚未开始时从头重下）或移除
- 关闭窗口默认隐藏到托盘且任务继续；托盘“退出”会停止浏览器接管、暂停任务并保存状态
- 每个下载任务使用独立文件夹：最终文件在文件夹根，sidecar / 工作文件 / 分片与 BT 缓存集中在
  `.potatodm` 子目录
- 移除未完成任务会删除整个任务文件夹（含过程文件）；移除已完成任务只清理 `.potatodm`，
  保留下载文件
- 下载失败会自动删除该任务未完成的文件夹与过程文件；失败任务仍留在列表里，重试时从头开始
- 单实例：重复启动会唤起并激活已有窗口（`Local\PotatoDM.SingleInstance.v1`）
- 品牌图标：`PotatoDM.exe`、窗口、托盘和浏览器扩展共用土豆下载图标；设置页可创建桌面或开始菜单快捷方式

持久化位置：

```
%LOCALAPPDATA%\PotatoDM\jobs.json       # 任务列表（URL、目标路径、状态文件路径、浏览器请求元数据）
%LOCALAPPDATA%\PotatoDM\settings.json   # 连接数、任务并发数、全局限速、动态段拆分、稀疏文件、ffmpeg 路径、上次保存目录、每主机上限、系统代理
%LOCALAPPDATA%\PotatoDM\startup.log     # 启动失败或未处理异常（仅出错时写入）
%LOCALAPPDATA%\PotatoDM\browser\        # 扩展、宿主清单与 host.log（见“浏览器接管”）
```

直链任务以 `.potatodm` 目录里的 `.download.json` sidecar 为进度真值；HLS/DASH 用同一目录下的
`media/` 缓存续传，BT 用 `torrent-cache/` 里的元数据缓存续传（libtorrent 暂未保存 resume
data，恢复时会重新校验已有文件）。`jobs.json` 只负责记住任务列表；
sidecar/缓存与目标文件都不存在时，该任务在启动时自动从列表剔除。

下载目录布局（桌面客户端为每个任务创建一个文件夹）：

```
<下载目录>/
  <文件名去扩展名>/
    <文件名>                          # 最终下载文件（完成前是工作文件）
    .potatodm/
      <文件名>.download.json          # 进度 sidecar
      <文件名>.download.json.bak      # 上一次快照
      <文件名>.download.json.tmp      # 原子写入临时文件
      <文件名>.download.json.lock     # 排他锁
      <文件名>.potatodm-part          # 工作文件（预分配）
      media/                          # HLS/DASH 分片缓存
      torrent-cache/                  # BT 元数据缓存（.torrent）
```

文件夹名取服务器或浏览器建议的文件名（去掉扩展名并净化非法字符）；重名自动追加 `(1)`、`(2)`。
CLI 不经过这一层：`-o` 直接指定输出文件时，sidecar 与工作文件仍放在目标文件旁。

WPF 项目关闭了 `InvariantGlobalization`（WPF 绑定需要 ICU 文化数据）；引擎、CLI 与测试仍保持
invariant 全球化。

系统代理默认启用（`UseProxy = true`），与应用设置一致；自动化测试显式关闭。

## 浏览器接管（Chrome / Edge）

### 安装

1. 启动 PotatoDM，点击工具栏“浏览器集成”，再点“安装 / 修复”。安装器会：
   - 把扩展复制到 `%LOCALAPPDATA%\PotatoDM\browser\chromium`
   - 写入宿主清单 `com.potatodm.dm.chromium.json`
   - 在 HKCU 注册 `com.potatodm.dm` 给 Chrome、Edge
2. Chrome / Edge：扩展页 → 开启开发者模式 → “加载已解压的扩展程序” → 选择上面的扩展目录。
3. 点击扩展图标可查看客户端状态并开关接管。

### 行为

- 所有 http/https 下载在 `downloads.onCreated` 中同步取消并交给 PotatoDM；
  客户端拒绝（无文件长度、探测超时、服务器拒绝、保存目录不可用、非 http）或
  宿主/客户端不可用时，扩展立即用 `downloads.download` 重下。
- HLS/DASH 清单以 `kind=manifest`、磁力链接以 `kind=magnet` 转发。DRM、无法解析的清单、
  缺少 ffmpeg、无效磁力属于“已明确拒绝”，浏览器**不会**回退重下（否则只会得到同一个失败）。
- 扩展弹窗可直接粘贴 magnet 链接发送；页面资源列表把清单标为媒体条目。
- 保存目录：客户端上次使用的目录；不存在或不可写时退回系统 Downloads，重名自动加 `(1)` 后缀。
- 文件名优先用服务器 `Content-Disposition`；探测名为空或 `download.bin` 时用浏览器建议名。
- Cookie 以结构化列表传入 `CookieContainer`；跨源重定向不携带 `Cookie` / `Authorization`。
  重启续传时从 `jobs.json` 用 DPAPI 解密恢复 Cookie、Referer、UA 与白名单请求头。
- 单实例：浏览器拉起客户端时若已运行，只发 `activate` 唤起窗口，不会产生第二个进程。
- 关闭客户端时先停止命名管道接单，再暂停任务，避免退出过程中接受新的浏览器任务。

### 资源嗅探

弹窗里的“当前页面发现的资源”汇总多个来源，同一 URL 合并，优先保留等级更高的条目
（清单 > 视频/音频 > 其他），并保留任一来源提供的 Content-Type、大小与 Referer：

- `webRequest` 观察到的 media/xhr 请求，附带响应头中的 Content-Type、Content-Length
  和发起页 Referer；
- 隔离世界内容脚本扫描 `<video>` / `<audio>` / `<source>`、媒体直链 `<a>`、
  `performance.getEntriesByType("resource")` 以及内联脚本中的 URL；
- MAIN 世界脚本在 `document_start` 挂钩 `JSON.parse`、`fetch`、`XMLHttpRequest`，
  从被解密或动态拼接的接口 JSON 与响应体中提取 m3u8 / mp4 / mp3 等地址；
- 列表页中的视频详情链接：锚文本为数字、路径含 play/watch/video/vod/detail/view，
  或查询串含 `aid` / `vid` / `id=<数字>`，且同形链接不少于 4 个时，以 `kind=page`
  列出，弹窗中显示“打开”。打开并开始播放后即可嗅探到实际媒体地址。

资源按标签页暂存在 `chrome.storage.session`，MV3 service worker 被挂起后会自动恢复；
标签页关闭或整页重新加载时清空。弹窗打开时自动扫描，也可点“重新扫描”主动触发。

### 架构

```
扩展 downloads.onCreated
  → runtime.connectNative("com.potatodm.dm")        20 s 超时
  → PotatoDM.BrowserHost.exe（stdio：4 字节小端长度 + UTF-8 JSON）
  → \\.\pipe\PotatoDM.Browser.v1.<session>（当前用户，换行分隔 JSON）
  → PotatoDM.exe → DownloadManager.AddFromBrowserAsync（8 s 探测超时）
```

宿主在管道不可用时启动同目录 `PotatoDM.exe`，并在 15 s 预算内重试；每次原生消息处理一次后退出。
调试时可覆盖 `POTATODM_PIPE_NAME`、`POTATODM_APP_PATH`、`POTATODM_APP_TIMEOUT_MS`。

### 已知边界

- 不支持 Firefox：Firefox Release 只能临时载入未签名扩展，本版本不再提供 Firefox 接管。
- 不处理 `blob:` / `data:` / `file:` / POST 生成的下载与 MSE 流；HLS/DASH 只处理可直接
  获取的清单地址。
- 列表页只包含详情页链接时，嗅探无法凭空得到视频地址：需要先打开详情页并开始播放
  （部分站点还要求登录），扩展会把这类链接以“视频页面”列出以便逐个打开。
- 受 DRM 保护的内容（Widevine / PlayReady / FairPlay / SAMPLE-AES / CENC）会被识别并拒绝，
  本项目不实现解密或绕过。
- 直播 HLS、动态或多 Period DASH 会被明确拒绝，而不是产出不完整的文件。
- 隐身窗口、多 Chrome profile 与 CHIPS 分区 Cookie 不在本里程碑范围。
- 接受响应丢失时扩展会回退重下，存在极小概率的重复下载窗口。

### 相关位置

```
%LOCALAPPDATA%\PotatoDM\browser\
  chromium\                        # 已安装的扩展
  com.potatodm.dm.chromium.json      # 宿主清单（Chrome / Edge）
  host.log                         # 宿主错误日志（≤1 MiB，不记录 Cookie / 查询串）
```

扩展源码在仓库 `browser/`。Chrome / Edge 扩展 ID 由 manifest 的固定 `key` 推导。
构建输出中的 `browser-ext/` 是安装器的源目录。

## CLI

构建后可直接运行 `src/PotatoDM.Cli/bin/Debug/net8.0/PotatoDM.exe`，或：

```powershell
dotnet run --project src/PotatoDM.Cli -- probe "http://127.0.0.1:5099/dl?token=demo&size=8388608"
```

### 合同

```
potatodm probe <url> [--referer URL] [--cookie STR] [--user-agent STR] [--json]
potatodm download <url> [-o PATH] [--connections N] [--min-segment-size BYTES]
                    [--max-speed BYTES] [--state PATH] [--restart] [--restart-on-change]
                    [--dynamic-segments] [--sparse] [--ffmpeg PATH]
                    [--stall-timeout SEC] [--max-retries N] [--max-retry-delay SEC]
                    [--max-per-host N] [--max-connections-per-job N]
                    [--referer URL] [--cookie STR] [--user-agent STR]
                    [--no-proxy] [--json] [--quiet]
potatodm resume <state.json | working-file> [--restart-on-change]
              [--dynamic-segments] [--sparse] [--ffmpeg PATH] [--max-speed BYTES]
              [--stall-timeout SEC] [--max-retries N] [--max-retry-delay SEC]
              [--max-per-host N] [--max-connections-per-job N]
              [--referer URL] [--cookie STR] [--user-agent STR]
              [--no-proxy] [--json] [--quiet]
potatodm torrent <magnet | .torrent> [-o DIR] [--port N] [--max-speed BYTES]
               [--max-upload BYTES] [--seed] [--no-dht] [--no-pex]
               [--json] [--quiet]
```

`download` 会自动识别 `.m3u8` / `.mpd` 清单并切换到媒体模式；`.torrent` 与磁力链接请用
`torrent` 命令（桌面客户端两者都接受）。`--sparse` 关闭预分配，只占用已写入范围。

默认值：8 连接、最小段 1 MiB、低于 2 MiB 的文件单流、停滞 30 s、每段最多重试 5 次、
每主机 8 连接、单任务最多 64 连接、不限速、动态段拆分关闭。`--max-speed` 的单位是字节/秒，0 表示不限。
`--max-connections-per-job` 是**单个任务**的连接上限，与 `--connections` 取较小者；它不是进程级
总连接数（进程级只限制同时运行的任务数）。`--global-connections` 是它的旧名，仍然接受。
`--dynamic-segments` 让空闲连接接管慢段尾部；默认关闭以保持分段映射可预测。
`-o` 既可以给文件名，也可以给目录（目录时使用服务器建议文件名，冲突时自动追加 `(1)` 等后缀）。

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 未分类错误 |
| 2 | 参数错误 |
| 3 | 探测失败 |
| 4 | HTTP 永久失败（含不可接受的响应编码） |
| 5 | 远端资源已变化（身份不匹配） |
| 6 | 磁盘 / 权限错误 |
| 7 | 状态损坏或任务被占用（锁冲突） |
| 8 | 不支持的媒体（DRM 保护、直播/多 Period 清单等） |
| 130 | 用户取消（状态保留为 Paused，可 `resume`） |

### `--json` 事件流

每行一个 JSON 对象（NDJSON），`type` 取值：

- `probe`：`url`、`finalUrl`、`totalLength`、`supportsRanges`、`etag`、`lastModified`、`suggestedFileName`、`contentType`
- `state`：`status`（`downloading` / `paused` / `completed` / `failed`）
- `progress`：`bytes`、`total`、`percent`、`speed`、`etaSeconds`、`connections`
- `completed`：`path`、`bytes`、`mode`（`singlestream` / `segmented`）、`durationSeconds`
- `torrent`：`name`、`total`、`bytes`、`percent`、`downloadSpeed`、`uploadSpeed`、`peers`、`metadata`
- `failed`：`error`、`exitCode`、`canceled`

## 引擎行为

### 探测

先用 `GET` + `Range: bytes=0-0` 获取长度、Range 支持与校验器（避免 HEAD 被拒）；
HEAD 失败时回退到普通 GET 读头后取消。所有请求强制 `Accept-Encoding: identity`——
Range 必须作用在未压缩字节上。重定向手动跟随并记录最终 URI。解析
`Content-Range`、`Content-Length`、`ETag`、`Last-Modified`、`Content-Disposition`
（含 RFC 5987 `filename*`），文件名经过非法字符、保留名、路径穿越与长度净化。

### 分段与写入

- 总长 ≤ `MinBytesForSegmented`（默认 2 MiB）或服务器不支持 Range → 单段
- 否则按连接数切分，余数分配给前几段；段大小不小于 `MinSegmentSize`
- 所有 worker 共享同一个 `SafeFileHandle`，用 `RandomAccess.WriteAsync(..., offset)` 写绝对偏移；
  每段单写者，无合并步骤。默认每个偏移只有一个写者；启用动态段拆分时，拆分边界附近的
  少量字节可能被两侧各写一次（内容相同），记账只归一侧
- 启动时 `preallocationSize` 预分配，并用 `DriveInfo` 预检可用空间
- 分段请求强制 HTTP/1.1（`RequestVersionExact`），避免 HTTP/2 单连接多路复用削弱并行效果
- `If-Range` 只在强 ETag 或 `Last-Modified` 可用时发送；弱 ETag 从不使用
- 每个响应校验 `Content-Range` 起始偏移与长度，不符则丢弃本次数据并重试

### 动态段拆分

- 默认关闭，可用 `--dynamic-segments` 或应用设置启用；开启后，先完成的连接不再空闲，
  而是把“已开始且剩余最多”的段对半拆开，自己接管后半段
- 只在所有段都已开始（`bytesDone > 0`）时拆分；仍有 worker 在等待连接许可时不拆，
  避免只是增加请求数
- 两半都不小于 `MinSegmentSize` 才拆；段越拆越多但连接数不变
- 拆分与写入记账共用段内锁：写入方在边界收缩后最多记到新边界，越界字节由接管方重取；
  尾部重叠写的是同一份内容，因此不会破坏文件
- 段表仍保持无缝覆盖，sidecar 快照与拆分在同一把锁内完成，续传按起始偏移校验
- 已知代价：段映射变得依赖时序；慢段判定只看剩余字节数，不做历史吞吐估计

### 重试、停滞与降级

- 可重试：408、429、5xx、网络异常；不可重试：403、404 等
- 指数退避 `250ms × 2^n` + ±20% 抖动，上限 `MaxRetryDelay`；`Retry-After` 优先（也受上限约束）
- 每次有实际进展时重置尝试计数；退避等待期间释放连接许可
- 读循环看门狗：超过 `StallTimeout` 无字节增长即取消该段请求并重连，从最后成功写入偏移继续
- 探测声称支持 Range、但后续 Range 请求返回 200：整个任务降级为单流从头重下（仅一次）
- 服务器强制返回 gzip/br：明确失败，不落盘
- `Content-Length` 错误 / 短响应：重试耗尽后失败并保留状态，不会挂起

### 限速

- `BandwidthLimiter` 用预约时间片的方式限速：每个读取块在共享队列中占据一个时间槽，
  并发分段按顺序排队，不会各自独立 sleep 导致总速率超标
- 限速在数据写盘与进度记账之后执行；等待被取消不会丢失已写入的字节
- 客户端所有任务共享同一个实例，因此“全局限速”是进程级的；CLI 的 `--max-speed`
  约束该进程内的所有任务
- 速率为 0 时完全跳过等待；运行中修改设置会作用于后续读取块
- 时间源通过 `TimeProvider` 注入，便于单独测试

### 状态与恢复

桌面客户端的目标文件位于 `<下载目录>/foo/foo.bin`，过程文件全部在 `<下载目录>/foo/.potatodm/`
（见“下载目录布局”）。CLI 直接指定 `-o` 时，过程文件与目标文件同目录：

```
foo.bin.potatodm-part        # 工作文件（预分配）
foo.bin.download.json        # 状态 sidecar
foo.bin.download.json.bak    # 上一次快照
foo.bin.download.json.tmp    # 原子写入临时文件
foo.bin.download.json.lock   # FileShare.None 排他锁
```

- 状态写入：`.tmp` → `File.Replace` → 保留 `.bak`；读取失败回退 `.bak`，再失败则报错而非静默重下
- 崩溃语义：**先写盘成功，再推进 `bytesDone`**；状态约每 1 s 原子快照一次
- 快照顺序：冻结各段计数 → `FlushFileBuffers` 刷数据文件 → 原子写 sidecar；
  sidecar 因此不会记录掉电后可能丢失的字节，最多重下约 1 秒
- `resume` 接受状态文件或工作文件路径；重新探测并比对长度/ETag/Last-Modified
- 身份变化：默认失败（退出码 5）；`--restart-on-change` 时丢弃工作文件从头重下
- 工作文件被外部截断：明确失败，要求 `--restart`
- 完成：刷盘 → 关闭句柄 → 工作文件重命名为目标文件 → 状态置 `Completed`
- 成功后 sidecar 仍保留（`Completed` 快照，位于 `.potatodm/`）；移除该任务时会自动清理
- 明确边界：进程崩溃与电源掉电都不会让 sidecar 领先于数据文件；
  文件重命名与目录项的持久化依赖 NTFS 元数据日志

### 媒体（HLS / DASH）

- 探测阶段按最终 URL 的扩展名与 `Content-Type` 识别清单；`download`、WPF 与浏览器接管
  共用同一套 `MediaDownloader`
- 分片写入任务目录内的 `.potatodm/media`（CLI 为 `<target>.potatodm-media`；含清单指纹），
  成功后组装为目标文件并删除缓存；暂停/崩溃后缓存仍在，恢复时重新校验清单后继续
- HLS AES-128-CBC 在内存中逐段解密；`EXT-X-MAP` 初始化段先写入，`BYTERANGE` 与
  `DISCONTINUITY/GAP` 按规范处理
- DASH 选择每个 AdaptationSet 中带宽最高的可用 Representation，音视频分离时分别下载后
  交给 ffmpeg 合并；没有 ffmpeg 时明确报 `MediaMergeException`
- DRM：解析阶段命中 Widevine/PlayReady/FairPlay、SAMPLE-AES 或 DASH `ContentProtection`
  即抛 `DrmProtectedException`（退出码 8）。**不包含任何解密逻辑**
- 直播 HLS（无 `EXT-X-ENDLIST`）、动态或多 Period DASH 明确拒绝，避免生成残缺文件

### BT / 磁力

- 传输引擎是 libtorrent-rasterbar 2.0.10（`csdl` 1.2.1 绑定），提供 uTP、MSE 协议加密、
  DHT、PEX 与多 tracker 并发；`PotatoDM.Core` 保持 BCL-only
- csdl 的托管接口不能直接接收磁力链接，所以磁力先取元数据：并行请求公共元数据缓存
  （itorrents，只发送 info hash）并用 MonoTorrent 做 peer-wire 元数据交换，取回后按 info hash
  校验，再交给 libtorrent 下载。`.torrent` 直接进入 libtorrent
- 支持磁力与 `.torrent`，默认启用 DHT 与 PEX，监听端口由系统分配（CLI `--port` 可指定）；
  `listen_interfaces` 的普通形式同时监听 TCP 与 uTP
- DHT 从 `dht.transmissionbt.com`、`dht.libtorrent.org`、`router.bittorrent.com`、
  `router.utorrent.com`、`dht.aelitis.com` 等公共路由引导；单个路由被屏蔽不会导致
  磁力完全找不到节点
- 公共种子（无论是否自带 tracker）都追加一小份公共 tracker（HTTP + UDP），每个 URL 单独
  一个 announce-list tier，避免 BEP 12 在同一 tier 内串行尝试；同时开启
  `announce_to_all_trackers` / `announce_to_all_tiers`，失效的站点 tracker 不会挡住其余
  tracker；带 `private` 标记的种子保持原样，不会泄露到公共 tracker
- 元数据缓存（`<hash>.torrent`）放在任务目录的 `.potatodm/torrent-cache`
  （CLI 仍为下载目录的 `.potatodm-torrent-cache`），同一磁力重试时无需再次交换元数据
- 下载完成默认停止做种；`--seed` 保持做种直到 Ctrl+C
- 已知限制：磁力取元数据阶段仍使用 MonoTorrent（仅 TCP、无加密连接），只有公共缓存
  命中时才能绕过；若某个磁力只存在加密/uTP 的 peer 且缓存没有该种子，元数据可能取不到
- 已知限制：libtorrent 侧暂未保存 resume data，恢复未完成任务时会重新校验已有文件；
  显式指定 peer（测试用）仍走 MonoTorrent 回退路径
- libtorrent 与 HTTP 引擎各有独立的速率限制器，无法共享进程级 `BandwidthLimiter`；
  `--max-speed` / `--max-upload` 只约束 BT 引擎自身

## 测试服务器

```powershell
dotnet run --project tests/PotatoDM.TestServer -- --port 5099
```

默认监听 `http://127.0.0.1:5099`，内容由 token + size + version 确定性生成
（固定 FNV-1a 种子 + xorshift64），同一 token 多次请求内容一致。每个用例使用唯一 token 隔离状态。

### 端点

| 端点 | 作用 |
|---|---|
| `GET/HEAD /dl` | 主下载端点，按查询参数注入故障 |
| `GET /chunked` | 无 `Content-Length` 的分块响应，用于验证浏览器接管拒绝与回退 |
| `GET /checksum?token&size&version` | 返回 `{ length, sha256 }`，测试的唯一真值来源 |
| `GET /admin/log?token` | 请求日志：序号、方法、Range、If-Range、Cookie、状态码、起止时间、发送字节 |
| `GET /admin/stats?token` | `{ requests, maxConcurrent, bytesServed }` |
| `GET/POST /admin/config?token&...` | 运行中修改 `version`、`etag`、`throttle`、`failFirst`、`retryAfter`、`failStatus`、`failSegments` |
| `GET/POST /admin/reset?token` | 清空该 token 的日志与计数 |
| `GET /health` | 就绪探测 |

### `/dl` 参数

| 参数 | 语义 |
|---|---|
| `token` | 状态隔离键（默认 `default`） |
| `size` | 内容长度，默认 8 MiB |
| `range` | `on`（默认）/ `off`（忽略 Range 返回 200）/ `short`（只发一半）/ `bad-start`（起始偏移减一）/ `ignore-later`（探测照常，后续 Range 返回 200） |
| `throttle` | 每请求限速，字节/秒 |
| `stallAfter` + `stallMs` | 发送指定字节后暂停，触发停滞看门狗 |
| `slowStart` + `slowDelayMs` | 起始偏移等于 `slowStart` 的段在发出首块后暂停，用于制造单段慢连接 |
| `disconnectEvery` | 每发送指定字节后中断连接 |
| `etag` | `strong`（默认）/ `weak` / `random`（每次不同）/ `none` |
| `version` | 内容版本；影响 ETag、Last-Modified 与数据 |
| `redirect` | 重定向次数 |
| `cd` | `ascii`（`hello.bin`）/ `unicode`（RFC 5987 `中文.bin`） |
| `head` | `reject`：HEAD 返回 405 |
| `encoding` | `gzip`：故意违反 identity 返回压缩内容 |
| `wrongLength` | Content-Length 增量（正数时提前断开） |
| `failFirst` | 前 N 个请求返回 `failStatus` |
| `failStatus` / `retryAfter` | 故障状态码 / `Retry-After` 秒数 |
| `cookie` | `required`：无 `session=ok` 时经 `/cookie/start` 质询；`strict`：`session` 不等于 `cookieValue`（默认 `ok`）直接 401 |
| `cookieValue` | `cookie=strict` 时期望的 `session` 值 |

`failSegments` 只能通过 `/admin/config` 设置：它只让**分段请求**（Range 非 `bytes=0-0`）失败，
便于在探测已经成功之后注入 429/503。

## 已验证结果

- `dotnet build PotatoDM.sln`：0 warning / 0 error
- `dotnet test`（逐项目）：**337 passed, 0 failed**（218 引擎/媒体单元 + 45 编排单元 +
  13 BT + 22 浏览器 IPC + 39 集成）
- 集成场景覆盖：分段并行与并发峰值、小文件单请求、Range 忽略降级、429 + Retry-After、
  停滞重连、断线续传、Unicode 文件名、gzip 拒绝、Cookie 质询、每主机并发上限、
  全局限速（1 MiB/s 上限下 1 MiB 下载耗时符合预期且哈希一致）、
  动态段拆分（引擎与 CLI 两条路径：被暂停的首段被接管为第三条请求，哈希一致）、
  取消后续传、远端版本变化、外部截断、边界大小（0/1/2/7/8/4095/4096/1MiB±1）、
  短响应重试耗尽、真实 CLI 进程被杀后恢复
- 编排层真实引擎集成测试：4 MiB 文件下载并通过 SHA-256 校验；限速下载中暂停 →
  重建管理器恢复列表 → 继续下载 → 校验通过
- 编排层队列测试：并发上限与 FIFO 启动顺序、提高上限后立即放行、排队中暂停后重新开始、
  “暂停全部”同时清空队列
- 媒体测试：HLS master/media 清单、AES-128 往返、`EXT-X-MAP`/`BYTERANGE`/`DISCONTINUITY`、
  DASH 模板/列表/时间线/BaseURL、DRM 与直播/多 Period 拒绝；`MediaDownloader` 的分片缓存、
  清单指纹与 ffmpeg 缺失错误路径
- BT 测试：同一进程内两个 MonoTorrent 引擎经 loopback 完成 `.torrent` 下载与磁力元数据交换，
  输出逐字节一致；元数据文件测试覆盖 info hash 校验、公共 tracker 注入（每个 URL 单独
  tier）、私有种子保持原样、已有 tracker 保留并追加公共 tier；编排层覆盖磁力进队、
  非法磁力拒绝、无 sidecar 恢复、预分配但未完成的种子恢复为暂停、完成标记与分块进度
- libtorrent 路径手动验证：用带可用 tracker 的 Ubuntu 种子实测 25 秒内 86 个 peer、
  ~7.7 MB/s（此前只向首个 tracker 宣告时仅 1 个 peer、~50 KB/s）；用户的磁力链接经
  HTTP 元数据缓存约 2 秒取到元数据后开始传输
- 掉电安全机制：快照与实时计数不共享引用（冻结后才刷盘），`FlushToDisk` 在完成前后调用均安全
- 动态段拆分单元测试：半段边界、绝对偏移、最小段限制、收缩后记账不越界、
  并发拆分后段表仍无缝覆盖
- 浏览器接管测试：
  - 原生消息帧的小端长度、UTF-8 中文、截断/负数/超限，Chrome 扩展 ID 推导，
    命名管道并发/畸形 JSON/处理器异常/超时
  - 扩展实际发送的 JSON 字段（`referer`，而非浏览器的 `referrer`）能反序列化到
    `BrowserRequest.Referer`，且序列化名保持 `referer`
  - 宿主子进程（stdio）经真实管道把下载转发给应用并返回 `jobId`/`targetPath`；
    应用不可用时快速返回 `app_unavailable`；非 http scheme 直接拒绝
  - 严格 Cookie 下载：浏览器元数据进入探测请求，暂停后重建管理器并续传，
    哈希一致且服务器日志每次请求都带 `session=letmein`
  - `/chunked`（无 `Content-Length`）→ `no_length`，不留下占位任务或 registry 行
- WPF 启动 smoke test：主窗口正常显示（`PotatoDM 下载器`），当前会话命名管道建立，
  宿主 `ping` 返回 `pong`；第二个实例发送 `activate` 后以 0 退出且只剩一个窗口；
  关闭窗口后管道消失，未生成 `startup.log`
- 宿主自动拉起 smoke test：客户端未运行时发送下载请求，宿主启动客户端并完成转发；
  不可达地址返回 `accepted:false`（`probe_timeout`）；宿主退出后客户端不持有
  原生消息的 stdio 句柄（否则浏览器会一直等不到断开）
- 安装器 smoke test：复制 shared + manifest 到 `%LOCALAPPDATA%\PotatoDM\browser`，
  生成含正确扩展 ID 的 Chromium 宿主清单，写入两个 HKCU 注册表键；
  `Uninstall()` 后注册表、清单与扩展目录全部清除
- 真实浏览器端到端（2026-10-18 本地手工执行，headless Chrome 155 / Edge 155，
  通过 CDP 加载解压扩展）：普通下载、严格 Cookie、`/chunked` 回退、客户端自动拉起、
  链接 referer 传递均通过，下载文件 SHA-256 与 `/checksum` 一致；其中 referer 场景
  发现并修复了扩展字段名与 IPC 契约不一致的问题。客户端 UI 的暂停/重启/恢复
  仍需人工验证，未纳入 CI；手工步骤见“浏览器接管”。
- 100 MiB loopback 性能 sanity（服务器每请求限速 2 MB/s）：
  - 8 连接：**6.66 s**
  - 1 连接：**50.35 s**
  - 加速比约 **7.6×**；两次下载的 SHA-256 均与 `/checksum` 一致

复现示例：

```powershell
dotnet run --project tests/PotatoDM.TestServer -- --port 5099

# 另一个终端
dotnet run --project src/PotatoDM.Cli -- probe "http://127.0.0.1:5099/dl?token=demo&size=8388608"
dotnet run --project src/PotatoDM.Cli -- download `
  "http://127.0.0.1:5099/dl?token=demo&size=8388608&throttle=2000000" `
  -o "$env:TEMP\potatodm-demo\demo.bin" -c 8 --no-proxy

# 与服务器真值比对
Invoke-RestMethod "http://127.0.0.1:5099/checksum?token=demo&size=8388608"
Get-FileHash "$env:TEMP\potatodm-demo\demo.bin" -Algorithm SHA256
```

## 下一步

1. 剩余人工验证：客户端 UI 的暂停/重启/恢复（含媒体与磁力任务）；
   Chrome / Edge 已用 headless + CDP 完成端到端回归（见“已验证结果”）
2. 媒体：需要本机 ffmpeg 的分离音视频合并仍属外部依赖，可考虑打包或引导安装
3. BT：可选做种策略、限速与应用设置的联动，以及私有 tracker 的凭据处理
4. 引擎扩展点：SQLite 状态存储
