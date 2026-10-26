# 鸽子下载（GeZi）

> **Jiahui 自维护版 v2.3.2**：[维护仓库](https://github.com/Jiahui-Gu/gezi-quark-downloader) fork 自
> [huaotem-bot/gezi-quark-downloader](https://github.com/huaotem-bot/gezi-quark-downloader)。
> 保留原作者 Yi Yuan 的署名、品牌、赞赏入口及 GPL-3.0-or-later / 第三方许可。

> 一个 Windows 桌面端的**夸克网盘下载器** —— 支持分享链接 / 网盘直链解析、
> 多线程分片下载、边下边播、多账号管理、深色模式。
> 基于 **.NET Framework 4.8 + WPF** 开发，**零 NuGet 依赖、可完全离线构建**。

<p align="center">
  <img src="src/GeZi/Resources/app-logo.png" width="120" alt="鸽子下载">
</p>
<p align="center">
  <img src="https://img.shields.io/github/repo-size/Jiahui-Gu/gezi-quark-downloader?style=social" alt="Repo Size">
  <img src="https://visitor-badge.laobi.icu/badge?page_id=Jiahui-Gu.gezi-quark-downloader&radius=12" alt="Visitors">
  <img src="https://img.shields.io/github/license/Jiahui-Gu/gezi-quark-downloader?style=social" alt="License">
  <img src="https://img.shields.io/github/stars/Jiahui-Gu/gezi-quark-downloader?style=social" alt="Stars">
</p>

---

## ✨ 功能特性

### 下载
- **两种写入方式**
  - `共写同一文件`：所有线程写同一个文件，**省磁盘空间**（默认）
  - `独立分片 + 顺序合并`：每个分片独立文件，**可边下边播**
- **高并发分片下载**：片数按文件大小自动计算（≥ 有效并发 × 8，上限 4096）
- **连接数按文件大小比例分配**：大文件多给连接，小文件不浪费
- **暂停 / 续传**：断点续传，重启后自动恢复未完成任务
- **限速自适应**：被服务端限流时自动退避，空闲后自动恢复
- **取消即清理**：取消任务会立刻删除本地残留（想保留进度请用「暂停」）

### 解析与登录
- **分享链接解析**：支持夸克分享链接（含带口令的链接）
- **免转存直链**：文件右键可选「免转存下载」，不占用网盘空间
- **二维码识别**：分享页只给二维码图片时，可截图识别（Ctrl+V / 选文件 / 拖拽）
- **扫码登录**：内置 OpenSSL 通道完成扫码登录与账号信息获取
- **多账号**：可保存多个账号，一键切换

### 体验
- **边下边播**：未下完也能用 VLC 播放已下载部分
- **深色模式**：亮色 / 深色 / 跟随系统
- **托盘常驻 + 悬浮小窗**：关掉主窗口也继续下载
- **总下载速度**：悬浮小窗与详细日志仅汇总网络下载速度，不计本地分片合并吞吐；单任务速度展示不变
- **导出直链**：纯直链 / curl / aria2c 三种格式
- **拖拽添加**：支持拖拽链接、文件到窗口

---

## 🖥 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | **Windows 10 1903+ / Windows 11**（推荐）<br>Windows 8.1 / Windows 7 SP1 理论可用，需自行安装 .NET Framework 4.8 |
| 运行时 | **.NET Framework 4.8**（Win10 1903 起系统内置） |
| 播放器（可选） | **VLC** —— 仅边下边播功能需要 |
| 架构 | x64 |

> ⚠️ **本软件仅支持 Windows**，不支持 Linux / macOS。
> （UI 基于 WPF、运行时为 .NET Framework，两者均为 Windows 独占。）

---

## 🚀 使用

### 方式一：下载发行版（推荐）

1. 从[个人维护版 Releases](https://github.com/Jiahui-Gu/gezi-quark-downloader/releases) 下载 `GeZi-v2.3.2-win64.zip` 并解压到任意目录（需维护者另行发布；本次整合不创建 Release）
2. 双击 `GeZi.exe` 即可运行
3. 若系统缺少 .NET Framework 4.8，Windows 会弹窗提示，按引导安装即可

> 无需安装 Python —— 扫码登录所需的 OpenSSL 已内置于 `lib/OpenSSL/` 目录。

### 方式二：从源码构建

```powershell
# 1. 克隆仓库
git clone https://github.com/Jiahui-Gu/gezi-quark-downloader.git
cd gezi-quark-downloader

# 2. 构建（Release）
dotnet build .\src\GeZi\GeZi.csproj -c Release -p:Platform=x64

# 3. 运行
.\src\GeZi\bin\x64\Release\net48\GeZi.exe
```

**无需 `nuget restore`** —— 本项目零 NuGet 依赖，所有第三方库以固化 DLL 形式随仓库分发，
任何机器 clone 下来即可直接构建（需已安装 .NET Framework 4.8 开发包 / MSBuild）。

### 自维护范围与后续同步

- 合入上游 [PR #11](https://github.com/huaotem-bot/gezi-quark-downloader/pull/11)：主窗口与悬浮窗总速度不计本地分片合并吞吐。
- 合入上游 [PR #12](https://github.com/huaotem-bot/gezi-quark-downloader/pull/12)：合并只读取已提交字节，重试覆盖未提交尾部；原 PR 保持独立，不在上游自动合并或关闭。
- 纳入启动续传的 WPF Dispatcher / 可见错误修复，以及按逻辑 S/E 区间对账、动态分裂重启完整性修复。缺失 partial 缓存仍显式拒绝，不补零；不自动把 Failed 转成 Pending，不改变现有关闭退出设置。
- 更新检查、手动下载和关于页源码入口仅指向个人 fork；无个人 Release 时不会退回上游更新。版本保持数字格式，便携附件名为 `GeZi-v2.3.2-win64.zip`。
- `tools\Recovery` 保留非破坏性候选恢复和精确缺口获取工具，见 [使用说明](tools/Recovery/USAGE.md)；不随包附带任何账号、真实断点或下载数据。

后续同步先添加上游（已存在则跳过）：`git remote add upstream https://github.com/huaotem-bot/gezi-quark-downloader.git`，
再 `git fetch upstream`、`git merge upstream/main`，保留维护版更新源与安全修复，运行下面全部回归后正常推送；不要 force push。
SDK 8 请直接构建上述 csproj；较新 SDK 也可使用 `GeZi.slnx`。

### 下载完整性回归测试

测试是零 NuGet 依赖的 net48 x64 控制台程序，在 Windows 上运行：

```powershell
dotnet build .\tests\GeZi.Core.RegressionTests\GeZi.Core.RegressionTests.csproj -c Release
.\tests\GeZi.Core.RegressionTests\bin\Release\net48\GeZi.Core.RegressionTests.exe
```

仅使用本机回环 HTTP Range 服务和合成数据，不需要账号或真实下载链接。
覆盖未提交分片尾部的合并、失败后重试、断点恢复、暂停/恢复、对账失败及全新分片下载，
对最终文件逐字节比较。测试显式构造 net48 异步写入先扩长文件、后完成写入的中间状态，
不需要填满磁盘或强杀进程。分片的可信进度只在无托管写缓冲的写入成功后更新；
合并和重试不再把物理文件长度当作提交点。这不等同于断电后的硬件级持久性保证，
也不能检测或修复旧版本已经完成并清理断点的损坏文件。

### 续传回归验证（Windows）

```powershell
# WPF 线程亲和性、混合任务、取链失败与启动错误报告
.\tests\ResumeRegression\Run.ps1

# 额外验证 11 个文件各 256 路实际 HTTP Range 并发与断点内容完整性
.\tests\ResumeRegression\Run.ps1 --download
```

测试从源码提取续传编排方法，以异步模拟取链和真实 WPF 集合视图验证线程切回；
下载验证使用真实调度器、下载引擎与仅监听回环地址的本地 HTTP 服务。
所有断点和文件均为合成数据，不启动主程序，也不读取账户或真实下载配置。
下载验证会短时建立 2816 条本地连接；成功后自动清理测试文件，失败时输出保留目录。
同一测试入口还从两处生产源码提取速度汇总表达式，执行 8 场景 × 2 窗口共 16 个断言，
并验证两程序集版本、个人 fork 的更新/源码入口及便携附件名解析。
另覆盖动态窃取生成乱序分片后的失败/重启：`pNNNNNN` 的编号保持不变，
续传对账只按元数据的逻辑 `S/E` 区间定位，不能按编号累加、删除或重新编号。
若目标停在某分片中间但对应缓存已丢失，下载器会保留原件并明确停止，
而不是截断唯一副本或把「所有 W 已满」当成完整文件。

重启续传会沿用已有分片规划及已下载进度，采用当前设置的单文件连接上限和同时任务数；
不需要为了调高连接数而删除或重建断点。实际活跃连接仍受剩余分片数、服务端限流与网络影响。
更换构建时须先退出旧进程，并保留原目标路径、分片根目录、配置及 `GeZi.state.json`；
不要通过「取消」或「丢弃」恢复任务，这些操作会清理下载数据。

---

## 🧱 项目结构

```
GeZi/
├── src/
│   ├── GeZi/                     # 主程序（WPF 界面层）
│   │   ├── MainWindow.xaml(.cs)  # 主窗口
│   │   ├── Resources/            # 主题色、样式、图标
│   │   │   ├── Colors.Light.xaml # 亮色配色（36 个键）
│   │   │   ├── Colors.Dark.xaml  # 深色配色（与亮色键一一对应）
│   │   │   └── ...
│   │   └── ...
│   └── GeZi.Core/                # 核心逻辑（无 UI 依赖，可单独复用）
│       ├── Api/                  # 夸克 API 客户端
│       ├── Download/             # 分片下载引擎
│       ├── Support/              # 二维码编解码等工具
│       ├── lib/ZXing/            # 固化的 ZXing DLL（二维码解码）
│       └── lib/OpenSSL/          # 固化的 OpenSSL 3.5.7（扫码登录通道）
├── LICENSE                       # GPL-3.0
└── THIRD-PARTY-NOTICES.md        # 第三方组件声明
```

---

## 📜 许可证

本项目以 **GNU General Public License v3.0** 发布，详见 [LICENSE](LICENSE)。

```
Copyright (C) 2026  Yi Yuan

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```

这意味着：你可以自由使用、修改、分发本软件，
但**任何分发（含修改版）都必须同样以 GPL-3.0 开源**，并提供完整源代码。

### 第三方组件

本项目使用了以下第三方开源组件，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)：

| 组件 | 许可证 |
|---|---|
| ZXing.Net | Apache-2.0 |
| OpenSSL | Apache-2.0 |

---

## ⚠️ 免责声明

1. 本项目**仅供学习与技术研究**使用。
2. 本项目通过调用**夸克网盘的非公开接口**实现功能，可能随服务端变更而失效。
3. 请勿将本软件用于**商业用途**，或用于**侵犯他人版权 / 违反服务条款**的行为。
4. 使用者应自行承担因使用本软件产生的一切后果，作者不承担任何责任。
5. 下载的内容版权归原权利人所有，请遵守相关法律法规。

---

## 🙏 致谢
感谢 [ZXing.Net](https://github.com/micjahn/ZXing.Net/) 提供的二维码解码能力，
以及 [Python](https://www.python.org/) 社区。

开发过程中参考了若干开源下载器的公开文档与设计思路（未复制其源代码），
在此一并致谢，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 第 5 节。

---

## ☕ 赞赏支持

如果这个项目对你有帮助，欢迎请作者喝杯咖啡 ☕

<p align="center">
  <img src="GeZi.png" width="260" alt="赞赏码">
</p>
