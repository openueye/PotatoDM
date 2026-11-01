# 第三方许可声明 / Third-Party Notices

PotatoDM 本体以 MIT 许可证发布（见 [LICENSE](LICENSE)）。本文件列出随发布物
**以二进制形式一并分发**的第三方组件，以及各自的许可证、归属声明与分发文件名。

PotatoDM itself is licensed under the MIT License (see [LICENSE](LICENSE)).
This file lists every third-party component that is **redistributed in binary form**
with our release artifacts, together with its license, attribution and shipped file names.

## 许可证全文 / Full license texts

下列许可证全文位于仓库与本发布物的 `LICENSES/` 子目录中，随发布物一并交付。
The full texts below live in the `LICENSES/` subdirectory of both the repository and
every release artifact, and are delivered together with it.

| 文件 / File | 许可证 / License |
| --- | --- |
| [LICENSES/Apache-2.0.txt](LICENSES/Apache-2.0.txt) | Apache License 2.0 全文（§1–§9 + END OF TERMS AND CONDITIONS） |
| [LICENSES/BSD-3-Clause.txt](LICENSES/BSD-3-Clause.txt) | BSD 3-Clause 全文（libtorrent，Arvid Norberg） |
| [LICENSES/MIT.txt](LICENSES/MIT.txt) | MIT 全文 + MonoTorrent / Mono.Nat / ReusableTasks 版权行 |

所有组件仅由 `PotatoDM.Torrent` 项目引用；`PotatoDM.Core` 保持零第三方依赖。

All components below are referenced only by the `PotatoDM.Torrent` project;
`PotatoDM.Core` keeps zero third-party dependencies.

---

## 1. csdl 1.2.1

- **许可证 / License**: Apache-2.0
- **归属 / Attribution**: `csdl Copyright (c) 2024, Albie Spriddell`
- **项目主页 / Project**: https://github.com/aspriddell/csdl
- **用途 / Purpose**: libtorrent 的 .NET 绑定，提供 BT/DHT/uTP 的托管调用入口。
  Managed .NET bindings for libtorrent; the managed entry point for BT/DHT/uTP.
- **分发形式 / Distributed as**: `csdl-net.dll`（托管程序集 / managed assembly）

## 2. csdl.Native 1.2.0

- **许可证 / License**: Apache-2.0（`libcsdl`）+ BSD-3-Clause（`libtorrent`）
- **归属 / Attribution**: `csdl Copyright (c) 2024, Albie Spriddell`；
  libtorrent `Copyright (c) 2003-2020, Arvid Norberg`
- **项目主页 / Project**: https://github.com/aspriddell/csdl
- **用途 / Purpose**: 原生二进制的实际载体包——`libcsdl` C 接口层，并通过
  `runtimes/<rid>/native/` 携带 libtorrent 与 OpenSSL 的原生库。
  The carrier package for the native binaries: the `libcsdl` C shim plus the
  libtorrent and OpenSSL native libraries under `runtimes/<rid>/native/`.
- **分发形式 / Distributed as**: `runtimes/<rid>/native/` 下的 `csdl.dll`（win-x64）、
  `torrent-rasterbar.dll`、`libssl-3-x64.dll`、`libcrypto-3-x64.dll`
  （其他平台为对应的 `.so` / `.dylib`）

## 3. libtorrent-rasterbar 2.0.10

- **许可证 / License**: BSD-3-Clause
- **归属 / Attribution**: `Copyright (c) 2003-2020, Arvid Norberg`
- **项目主页 / Project**: https://github.com/arvidn/libtorrent
- **用途 / Purpose**: BT/DHT/uTP 传输引擎，负责种子下载、扩频与 Peer 发现。
  The BitTorrent/DHT/uTP transport engine behind torrent transfers.
- **分发形式 / Distributed as**: `torrent-rasterbar.dll`（win-x64），经 `csdl.Native` 包分发

## 4. OpenSSL 3.3.2

- **许可证 / License**: Apache-2.0
- **归属 / Attribution**: `Copyright (c) 1998-2021 The OpenSSL Project Authors`
- **项目主页 / Project**: https://www.openssl.org
- **用途 / Purpose**: libtorrent 的 TLS 依赖，用于 HTTPS Tracker 与加密 Peer 连接。
  libtorrent's TLS dependency (HTTPS trackers and encrypted peer connections).
- **分发形式 / Distributed as**: `libcrypto-3-x64.dll`、`libssl-3-x64.dll`（win-x64），
  经 `csdl.Native` 包分发；构建版本 3.3.2 取自二进制内嵌字符串
  （version 3.3.2 as embedded in the shipped binaries）

## 5. Mono.Nat 3.0.0

- **许可证 / License**: MIT
- **归属 / Attribution**: `Copyright (C) 2006 Alan McGovern`、
  `Copyright (C) 2007 Ben Motmans`、`Copyright (C) 2013 Nicholas Terry`
- **项目主页 / Project**: https://github.com/mono/Mono.Nat
- **用途 / Purpose**: NAT-PMP / UPnP 端口映射（MonoTorrent 的依赖），用于提升做种与
  对等连接的可达性。NAT-PMP/UPnP port forwarding, a MonoTorrent dependency.
- **分发形式 / Distributed as**: `Mono.Nat.dll`

## 6. ReusableTasks 4.0.0

- **许可证 / License**: MIT
- **归属 / Attribution**: `Copyright (C) 2019 Alan McGovern`
- **项目主页 / Project**: https://github.com/alanmcgovern/ReusableTasks
- **用途 / Purpose**: 可重用的 awaitable 任务对象（MonoTorrent 的依赖），用于在高频
  异步路径上避免 Task 分配。Awaitable reusable task objects, a MonoTorrent dependency.
- **分发形式 / Distributed as**: `ReusableTasks.dll`

## 7. MonoTorrent 3.0.2

- **许可证 / License**: MIT
- **归属 / Attribution**: `Copyright (C) 2006 Alan McGovern, Olivier Dufour`
- **项目主页 / Project**: https://github.com/alanmcgovern/monotorrent
- **用途 / Purpose**: 磁力链接元数据交换（magnet metadata exchange），在交给
  libtorrent 之前取回 torrent 元信息。Used for magnet metadata exchange only.
- **分发形式 / Distributed as**: `MonoTorrent.dll` 及其子程序集
  （`MonoTorrent.BEncoding.dll`、`.Client`、`.Connections`、`.Dht`、`.Factories`、
  `.Messages`、`.PiecePicking`、`.PieceWriter`、`.PortForwarding`、`.Trackers`）

---

## 关于 Apache-2.0 组件 / Note on Apache-2.0 components

本项目分发 Apache-2.0 许可的组件 `csdl`、`csdl.Native`（`libcsdl`）与 `OpenSSL`。
按 Apache License 2.0 §4(a) 的要求，我们向每一位接收者**随发布物提供许可证副本**，
即本目录下的 `LICENSES/Apache-2.0.txt`（完整 §1–§9 正文 + END OF TERMS AND CONDITIONS），
并在此保留各组件的版权与归属声明。上述组件均未经修改地以二进制形式分发。

This project redistributes the Apache-2.0 licensed components `csdl`, `csdl.Native`
(`libcsdl`) and `OpenSSL`. As required by Apache License 2.0 §4(a), **a copy of the
License is delivered to every recipient as part of the release artifact**, namely
`LICENSES/Apache-2.0.txt` (the complete text of §1–§9 plus END OF TERMS AND CONDITIONS),
and the copyright and attribution notices of each component are retained above.
All of these components are redistributed unmodified, in binary form only.

BSD-3-Clause 与 MIT 组件同样随附全文：`LICENSES/BSD-3-Clause.txt`、`LICENSES/MIT.txt`。

The BSD-3-Clause and MIT components ship their full texts as well, in
`LICENSES/BSD-3-Clause.txt` and `LICENSES/MIT.txt`.
