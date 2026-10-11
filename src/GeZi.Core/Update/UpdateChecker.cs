// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Api;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 从夸克分享里找出「最新版本的更新包」。
    ///
    /// 【职责边界】这里<b>只负责找包</b>：取凭证 → 列目录 → 筛出符合命名规范的 zip → 取版本号最大的。
    /// 建目录、转存、下载、启动安装统统不归它管（那是上层 <c>UpdateService</c> 的事）。
    ///
    /// 【为什么不需要登录】列分享目录用的是 <c>sharepage/token</c> + <c>sharepage/detail</c>，
    /// 走的是公开分享的凭证，与账号 Cookie 无关。所以即使没登录也能查出"有没有新版本"，
    /// 只有真正要下载（转存 or 取直链）时才需要登录态。
    ///
    /// 【实测的目录结构】（2026-10-11）
    /// <code>
    /// 分享根 (pdir_fid=0)
    /// └── GeZi更新/                       ← 目录
    ///     └── GeZi-v2.2.0-win64.zip       ← 更新包
    /// </code>
    /// 所以必须**下钻一层**才看得到包。这里按"递归到 <see cref="UpdateConstants.MaxScanDepth"/> 层、
    /// 沿途收集所有合规文件"来实现 —— 这样以后把包直接放根目录、或换文件夹名，都不用改代码。
    ///
    /// ⚠️ <b>异常处理</b>：本类<b>不吞异常</b>。取凭证/列目录失败会原样抛出
    /// <see cref="QuarkApiException"/> 或 <see cref="System.Net.Http.HttpRequestException"/>，
    /// 由上层决定提示文案 —— 因为"分享失效""网络不通""接口改版"需要给用户看不同的话。
    /// 只有"确实找到了但没有合规的包"才返回 <c>null</c>。
    ///
    /// ⚠️ <b>线程安全</b>：本类自身无状态（除注入的 client）。但 <see cref="QuarkClient"/> 内部
    /// 持有 Cookie jar 并会被服务端 Set-Cookie 改写，<b>不要并发调用</b>本方法 ——
    /// 启动检查本来也只需跑一次。
    /// </summary>
    public sealed class UpdateChecker
    {
        private readonly QuarkClient _client;

        /// <param name="client">
        /// 用来发请求的客户端。<b>可以是不带登录态的</b>（<c>new QuarkClient("")</c>）——
        /// 列分享目录不需要 Cookie。调用方也可以把已登录的 client 传进来复用。
        /// </param>
        public UpdateChecker(QuarkClient client)
        {
            if (client == null) throw new ArgumentNullException("client");
            _client = client;
        }

        /// <summary>
        /// 用**匿名**（未登录）客户端建一个检查器。
        /// 适用场合：软件刚启动、还没确定登录态时先查一次有没有新版本。
        /// </summary>
        public static UpdateChecker CreateAnonymous()
        {
            return new UpdateChecker(new QuarkClient(""));
        }

        /// <summary>
        /// 找出最新版本。
        /// </summary>
        /// <param name="pwdId">分享 ID，默认用 <see cref="UpdateConstants.SharePwdId"/>。</param>
        /// <param name="ct">
        /// 取消令牌。<b>只能在翻页之间生效</b> —— 底层 <c>ShareDetailPageAsync</c>
        /// 不接受 CancellationToken，单次 HTTP 无法中途取消。
        /// </param>
        /// <returns>最新版本的包；一个合规的都没找到则返回 <c>null</c>。</returns>
        /// <exception cref="QuarkApiException">取凭证或列目录被服务端拒绝（分享失效、接口变更）。</exception>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> 被取消。</exception>
        public async Task<UpdateManifest> FindLatestAsync(
            string pwdId = UpdateConstants.SharePwdId,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(pwdId)) pwdId = UpdateConstants.SharePwdId;

            // ① 取分享凭证。更新源没有提取码，passcode 传空。
            string stoken = await _client.GetStokenAsync(pwdId, "").ConfigureAwait(false);
            if (string.IsNullOrEmpty(stoken)) return null;

            // ② 递归收集所有合规的更新包
            var found = new List<UpdateManifest>();
            await CollectAsync(pwdId, stoken, UpdateConstants.RootPdirFid, 0, found, ct).ConfigureAwait(false);

            // ③ 逐段比大小取最大值（主 > 次 > 补）
            //    同版本多个文件：任选其一 —— 这里取"先遇到的那个"（CompareTo 严格大于才替换）。
            UpdateManifest best = null;
            for (int i = 0; i < found.Count; i++)
            {
                if (!found[i].IsValid) continue;
                if (best == null || found[i].Version.CompareTo(best.Version) > 0)
                    best = found[i];
            }
            return best;
        }

        /// <summary>
        /// 判断找到的包是否比当前版本新。抽出来是为了可单测（不碰网络）。
        /// </summary>
        public static bool IsNewer(UpdateManifest manifest, UpdateVersion current)
        {
            if (manifest == null || !manifest.IsValid || current == null) return false;
            return manifest.Version.CompareTo(current) > 0;
        }

        /// <summary>
        /// 递归扫描一层目录：文件按命名规范收集，目录继续下钻。
        /// </summary>
        private async Task CollectAsync(string pwdId, string stoken, string pdirFid,
            int depth, List<UpdateManifest> found, CancellationToken ct)
        {
            if (depth > UpdateConstants.MaxScanDepth) return;

            for (int page = 1; page <= UpdateConstants.MaxScanPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                QuarkClient.DirPage p =
                    await _client.ShareDetailPageAsync(pwdId, stoken, pdirFid, page).ConfigureAwait(false);
                if (p == null || p.Items == null || p.Items.Count == 0) break;

                for (int i = 0; i < p.Items.Count; i++)
                {
                    var it = p.Items[i];
                    if (it == null || string.IsNullOrEmpty(it.Name)) continue;

                    UpdateVersion v;
                    if (!it.IsDir && UpdateVersion.TryParsePackageFileName(it.Name, out v))
                    {
                        found.Add(new UpdateManifest
                        {
                            Version = v,
                            FileName = it.Name,
                            Fid = it.Fid,
                            FidToken = it.Token,
                            // 🚨 必须带上**列目录时用的那个 stoken**：share_fid_token 与它绑定，
                            //    换一个 stoken 去转存/取链会被服务端判「token校验异常」(41020)。
                            //    详见 UpdateManifest.Stoken 的注释。
                            Stoken = stoken,
                            Size = it.Size,
                        });
                        continue;
                    }

                    // 目录：只要还没到深度上限就下钻。
                    // ⚠️ 不挑目录名（不假设叫"GeZi更新"）—— 万一以后改名了也照样能找到包。
                    if (it.IsDir && !string.IsNullOrEmpty(it.Fid) && depth < UpdateConstants.MaxScanDepth)
                    {
                        await CollectAsync(pwdId, stoken, it.Fid, depth + 1, found, ct).ConfigureAwait(false);
                    }
                }

                if (!p.HasMore) break;
            }
        }
    }
}
