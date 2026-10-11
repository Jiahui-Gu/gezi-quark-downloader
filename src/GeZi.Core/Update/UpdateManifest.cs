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

namespace GeZi.Core.Update
{
    /// <summary>
    /// 从更新源里找到的「一个可用的更新包」。
    ///
    /// 【为什么 fid 和 token 都要带】这两个是夸克分享场景下的**成对**凭证：
    /// 转存要 <c>fid_list</c> + <c>fid_token_list</c>，免转存取直链也要 <c>fid</c> + <c>share_fid_token</c>。
    /// 少了 token，后续任何一步都做不了，所以找到包的那一刻就一起存下来。
    /// </summary>
    public sealed class UpdateManifest
    {
        /// <summary>从文件名里解析出的版本号。</summary>
        public UpdateVersion Version { get; set; }

        /// <summary>分享里的原始文件名，如 <c>GeZi-v2.5.0-win64.zip</c>（下载时用它当目标文件名）。</summary>
        public string FileName { get; set; }

        /// <summary>分享里的文件 ID。</summary>
        public string Fid { get; set; }

        /// <summary>分享场景的 <c>share_fid_token</c>，与 <see cref="Fid"/> 配对使用。</summary>
        public string FidToken { get; set; }

        /// <summary>
        /// 取到 <see cref="FidToken"/> 时所用的分享凭证（<c>stoken</c>）。
        ///
        /// 🚨🚨 【2026-10-11 实测定论 —— 这个字段是修 bug 加的，别删】
        /// 夸克的 <c>share_fid_token</c> **与"列目录时用的那个 stoken"绑定**：
        /// 用 A 次 <c>sharepage/token</c> 拿到的 token，配 B 次的 stoken 去请求，
        /// 服务端一律返回 <c>HTTP 403 / code 41020「转存文件token校验异常」</c>。
        ///
        /// 实测（同一份分享、同一个已登录 Cookie）：
        /// <code>
        ///   stoken1 + 用 stoken1 列的 token  → 200 OK
        ///   stoken2 + 用 stoken1 列的 token  → 403 / 41020   ← 就是线上那个报错
        ///   stoken2 + 用 stoken2 重新列的 token → 200 OK
        /// </code>
        /// 而且**每次调用 <c>sharepage/token</c> 返回的 stoken 都不同**，所以"下载时再取一次"
        /// 必然踩坑 —— 之前的更新流程就是这么挂的（检查用匿名客户端列目录，
        /// 下载又新取一个 stoken）。
        ///
        /// ⇒ <b>规矩：fid / fid_token / stoken 三者必须来自同一次列目录，整组传递。</b>
        /// </summary>
        public string Stoken { get; set; }

        /// <summary>文件字节数（展示用）。</summary>
        public long Size { get; set; }

        /// <summary>是否可用（至少要有版本号和 fid）。防止后面拿着残缺对象去转存。</summary>
        public bool IsValid
        {
            get
            {
                return Version != null
                    && !string.IsNullOrEmpty(Fid)
                    && !string.IsNullOrEmpty(FileName);
            }
        }

        public override string ToString()
        {
            return string.Format("v{0} — {1}（{2} 字节）",
                Version == null ? "?" : Version.ToString(),
                FileName ?? "?", Size);
        }
    }
}
