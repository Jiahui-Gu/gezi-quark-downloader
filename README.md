# 鸽子下载（GeZi）

> **Jiahui 自维护版 v2.3.4**：[维护仓库](https://github.com/Jiahui-Gu/gezi-quark-downloader) fork 自
> [huaotem-bot/gezi-quark-downloader](https://github.com/huaotem-bot/gezi-quark-downloader)。
> 保留原作者 Yi Yuan 的署名、品牌、赞赏入口及 GPL-3.0-or-later / 第三方许可。

> 当前分支新增的请求阶段保护、重试计数与失败诊断属于**未发布候选**，不是已安装 2.3.4 的公网提速承诺。
> 候选交付保留数字版本 2.3.4，以独立的 `InformationalVersion`、提交和文件哈希区分，不自动安装或替换正式包。

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
- **连接数直接按设置**：每个文件独立使用设置的连接数（最高 512），同时下载数只控制几个文件一起跑；512 路 × 3 文件可各用 512 路，不共享总预算
- **暂停 / 续传**：断点续传，重启后自动恢复未完成任务
- **必要错误重试**：HTTP 429/503 等暂时错误保留逐请求退避，不自动降低连接数，也不分批错峰启动；实际活跃连接受剩余分片、非 Range 响应与网络限制
- **慢连接采样只看响应体**：建连、等待响应头和错误退避不再误判为慢速/假死；响应体开始后重新计龄，保留真正慢连接的抢占与有界重试。诊断的活跃数仍只计响应体读写，不把等待中的请求算成有效传输
- **请求阶段有界等待（候选修复）**：每次请求从发起到收到响应头最多等待 100 秒，超时进入既有失败/退避预算，不冒充用户取消或慢体抢占；响应头返回即结束该计时，不限制正常长时间读体。不会额外重叠补位或提高并发设置
- **重试止损与脱敏诊断（候选修复）**：单文件本次执行的 500 次整轮回队额度采用原子预留，所有 worker 共享、成功不重置，不提高预算；每片仍最多首次加 3 次回队，每轮首次加 4 次重试。分片异常记录请求头/读体/写盘阶段与异常链类型、HResult、可用网络状态码，不记录异常原文、URL 或请求凭据；终态前三种错误先去重，不把多个 worker 同报错当成预算倍增
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

1. 从[个人维护版 Releases](https://github.com/Jiahui-Gu/gezi-quark-downloader/releases) 下载 `GeZi-v2.3.4-win64.zip` 并解压到任意目录（需维护者另行发布；本次整合不创建 Release）
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
- 更新检查、手动下载和关于页源码入口仅指向个人 fork；无个人 Release 时不会退回上游更新。版本保持数字格式，便携附件名为 `GeZi-v2.3.4-win64.zip`。
- 新任务、复用直链、免转存和启动续传统一采用设置的单文件连接数；移除按大小分配、隐藏总预算、启动分批错峰与自动缩减连接数。保留分片规划、窃取、必要错误退避及完整性校验。修改设置不打断正在运行的批次，续传使用当前设置。
- `tools\Recovery` 保留非破坏性候选恢复和精确缺口获取工具，见 [使用说明](tools/Recovery/USAGE.md)；不随包附带任何账号、真实断点或下载数据。

后续同步先添加上游（已存在则跳过）：`git remote add upstream https://github.com/huaotem-bot/gezi-quark-downloader.git`，
再 `git fetch upstream`、`git merge upstream/main`，保留维护版更新源与安全修复，运行下面全部回归后正常推送；不要 force push。
SDK 8 请直接构建上述 csproj；较新 SDK 也可使用 `GeZi.slnx`。

### 下载完整性回归测试

测试是零 NuGet 依赖的 net48 x64 控制台程序，在 Windows 上运行：

```powershell
dotnet build .\tests\GeZi.Core.RegressionTests\GeZi.Core.RegressionTests.csproj -c Release
.\tests\GeZi.Core.RegressionTests\bin\Release\net48\GeZi.Core.RegressionTests.exe

# 512 路真实请求：延迟响应头、稳定慢流、真正慢片抢占、整波及长时间 HTTP 429
.\tests\GeZi.Core.RegressionTests\bin\Release\net48\GeZi.Core.RegressionTests.exe --utilization --512

# 384 条首次请求永不回头 + 32 条正常等头 80 秒 + 96 条持续读体（约 4 分钟）
.\tests\GeZi.Core.RegressionTests\bin\Release\net48\GeZi.Core.RegressionTests.exe --utilization --header-waits --512

# 保活/关闭补位、用户取消、脱敏异常链、并发预算边界、终态去重及真实发送失败止损
.\tests\GeZi.Core.RegressionTests\bin\Release\net48\GeZi.Core.RegressionTests.exe --request-lifecycle
```

仅使用本机回环 HTTP Range 服务和合成数据，不需要账号或真实下载链接。
覆盖未提交分片尾部的合并、失败后重试、断点恢复、暂停/恢复、对账失败及全新分片下载，
对最终文件逐字节比较。测试显式构造 net48 异步写入先扩长文件、后完成写入的中间状态，
不需要填满磁盘或强杀进程。分片的可信进度只在无托管写缓冲的写入成功后更新；
合并和重试不再把物理文件长度当作提交点。这不等同于断电后的硬件级持久性保证，
也不能检测或修复旧版本已经完成并清理断点的损坏文件。

连接利用率回归使用 384 MiB 合成续传数据，分别统计完成时间、总吞吐、实际 Range 请求数/秒、
5 秒快照的响应体槽位占比、抢占次数及逐字节一致性。延迟响应头场景前 45 秒内的特定请求
每次需等 22 秒，错误重连会重新等待；长时间 429 场景验证不会借抢占绕过每片尝试/回队预算。
稳定慢流是对照组：占满 512 个响应体也不会突破服务端设定的发送速率。该测试不是互联网测速，
不能据此承诺真实 CDN 更快或所有环境恒定 512 路。

混合等头回归另使用 128 MiB 合成数据：125 秒时检查请求/响应体阶段、等待年龄和读体等待，
随后必须在测试端不释放永久静默请求的情况下自行完成并逐字节一致。测试专用包装器仍使用
生产 HTTP handler，只观测发起/返回及内容流读等待，不修改生产诊断口径；
`bodyNonReadPhase` 只是磁盘写入、调度、暂停等非读等待阶段的合计，不能据此单独归因磁盘。
`--observe-only` 仅用于旧版本对照：125 秒后由测试端放行以校验字节，此类被强制放行的
总完成时间不能和候选自行恢复的总完成时间当作公平测速。100 秒取自框架 HttpClient 默认值，
是保守请求等待上界而非 CDN 最优值；正常 22 秒及 80 秒响应头均不应取消，长体读取仍独立。
超时只会重新尝试既有有限次数；若外部正常响应头确需超过 100 秒，也会超时，不能承诺公网提速。

500 不是 HTTP 请求次数，也不是单个 worker 的次数：只有某片耗完一轮失败后再次入队才消耗 1。
运行库还可能透明重试已关闭的连接，真实线上请求数不能直接由应用尝试数反推。
永久发送失败回归区分应用调用、服务端实际收到的请求和回队次数，并验证停止后断点保留；
并发预算回归验证第 500 次允许、第 501 次拒绝及 100000 次并发争用恰好允许 500 次。
某个底层错误如果已被框架折叠为 `WebExceptionStatus.ConnectionClosed`，诊断不会编造不存在的
SocketException/错误码；已经仅保存顶层文本的旧日志也无法恢复内层异常。失败worker只有在
该片或本次总预算真正耗尽、或遇到不可恢复错误时退出；本候选不通过扩大预算或自动重启失败任务来维持连接数。

### 续传回归验证（Windows）

```powershell
# WPF 线程亲和性、混合任务、取链失败与启动错误报告
.\tests\ResumeRegression\Run.ps1

# 额外验证 11 × 256、3 × 512 实际 HTTP Range 并发、任务数限制及断点内容完整性
.\tests\ResumeRegression\Run.ps1 --download
```

测试从源码提取续传编排方法，以异步模拟取链和真实 WPF 集合视图验证线程切回；
下载验证使用真实调度器、下载引擎与仅监听回环地址的本地 HTTP 服务。
所有断点和文件均为合成数据，不启动主程序，也不读取账户或真实下载配置。
下载验证最高短时建立 2816 条本地连接；成功后自动清理测试文件，失败时输出保留目录。
额外覆盖三文件各 512 路新下载（不等分片响应再启动其他连接）、三文件各 512 路续传、
同时任务数为 1 时依次运行三文件，以及一整波 HTTP 429 后保留设置连接数并逐字节完成。
新下载使用约 770 MiB 有界合成数据；测试不读取真实配置或进度。
各入队路径还覆盖不同文件大小、未知大小、512 档位与配置保存后的读回。
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
