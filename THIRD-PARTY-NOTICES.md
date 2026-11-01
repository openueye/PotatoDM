# 第三方许可声明 / Third-Party Notices

PotatoDM 本体以 MIT 许可证发布（见 [LICENSE](LICENSE)）。本文件列出随发布物
（`publish/` 与 Release 压缩包）**以二进制形式一并分发**的第三方组件及其许可条款。

PotatoDM itself is licensed under the MIT License (see [LICENSE](LICENSE)).
This file lists the third-party components that are **redistributed in binary form**
with our release artifacts, together with their licenses.

所有组件仅由 `PotatoDM.Torrent` 项目引用；`PotatoDM.Core` 保持零第三方依赖。

All components below are referenced only by the `PotatoDM.Torrent` project;
`PotatoDM.Core` keeps zero third-party dependencies.

---

## 1. csdl 1.2.1

- **许可证 / License**: Apache-2.0
- **项目主页 / Project**: https://github.com/aspriddell/csdl
- **用途 / Purpose**: libtorrent 的 .NET 绑定，提供 BT/DHT/uTP 的托管调用入口。
  Managed .NET bindings for libtorrent; the managed entry point for BT/DHT/uTP.
- **分发形式 / Distributed as**: `csdl.dll`

## 2. libtorrent-rasterbar 2.0.10

- **许可证 / License**: BSD-3-Clause
- **项目主页 / Project**: https://github.com/arvidn/libtorrent
- **用途 / Purpose**: BT/DHT/uTP 传输引擎，负责种子下载、扩频与 Peer 发现。
  The BitTorrent/DHT/uTP transport engine behind torrent transfers.
- **分发形式 / Distributed as**: `torrent-rasterbar.dll` (win-x64)，经 `csdl.Native`
  包随 `csdl` 一并分发 / shipped via the `csdl.Native` package.

## 3. OpenSSL 3.x（构建版本 3.3.2）

- **许可证 / License**: Apache-2.0
- **项目主页 / Project**: https://www.openssl.org
- **用途 / Purpose**: libtorrent 的 TLS 依赖，用于 HTTPS Tracker 与加密 Peer 连接。
  libtorrent's TLS dependency (HTTPS trackers and encrypted peer connections).
- **分发形式 / Distributed as**: `libcrypto-3-x64.dll`、`libssl-3-x64.dll`，经
  `csdl.Native` 包随 `csdl` 一并分发 / shipped via the `csdl.Native` package.

## 4. MonoTorrent 3.0.2

- **许可证 / License**: MIT
- **项目主页 / Project**: https://github.com/alanmcgovern/monotorrent
- **用途 / Purpose**: 磁力链接元数据交换（magnet metadata exchange），在交给
  libtorrent 之前取回 torrent 元信息。Used for magnet metadata exchange only.
- **分发形式 / Distributed as**: `MonoTorrent.dll`

---

## 关于 Apache-2.0 组件的说明 / Note on Apache-2.0 components

本项目包含 Apache-2.0 许可的组件（`csdl`、`OpenSSL`）。按其条款要求，本文件保留上述
组件的版权与归属声明，并声明这些组件未经修改地随二进制发布；相应组件的完整许可证文本可
在上述项目主页获取，`csdl.Native` 包内亦随附其 `license.md`。

This project includes Apache-2.0 licensed components (`csdl`, `OpenSSL`). As required
by that license, this file preserves their copyright and attribution notices and states
that they are redistributed unmodified in binary form. The full license texts are
available at the project links above; the `csdl.Native` package also ships a copy as
`docs/license.md`.
