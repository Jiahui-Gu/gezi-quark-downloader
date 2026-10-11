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

using System.Text.RegularExpressions;

namespace GeZi.Core.Update
{
    /// <summary>
    /// 「检查更新」用到的固定参数。
    ///
    /// 【为什么集中放】分享地址、文件名格式、保留时长这些是会随发布流程变的量，
    /// 散在各处的话改一次要动好几个文件。这里集中，改一处即可。
    /// </summary>
    public static class UpdateConstants
    {
        /// <summary>
        /// 更新源的分享 ID（`https://pan.quark.cn/s/10eb852fa43d` 的最后一段）。
        /// 该分享<b>没有提取码</b> —— 取 stoken 时传空 passcode 即可。
        /// </summary>
        public const string SharePwdId = "10eb852fa43d";

        /// <summary>更新源的完整分享地址（仅用于展示 / 日志 / 出错时让用户手动去下载）。</summary>
        public const string ShareUrl = "https://pan.quark.cn/s/" + SharePwdId;

        /// <summary>分享根目录的 pdir_fid。夸克约定根目录就是 "0"。</summary>
        public const string RootPdirFid = "0";

        /// <summary>
        /// 转存时用的网盘文件夹名。自动创建、已存在则复用（<see cref="UpdateChecker"/> 只负责找包，
        /// 建目录由上层调用 <c>QuarkClient.PathFidAsync</c> / <c>MkdirAsync</c> 完成）。
        /// </summary>
        public const string NetdiskUpdateFolder = "GeZi 更新";

        /// <summary>
        /// 转存的文件在网盘里保留多久后自动删除（小时）。需求定为 2.5 天 = 60 小时。
        /// 之所以留这么久：用户可能今天下、明天重装，删太早会让人白下一次。
        /// </summary>
        public const double DeleteAfterHours = 60.0;

        /// <summary>
        /// 更新包的**严格**文件名格式：`GeZi-v{主}.{次}.{补}-win64.zip`
        /// 例：`GeZi-v2.5.0-win64.zip`、`GeZi-v3.0.12-win64.zip`。
        ///
        /// 【为什么必须严格】分享里可能同时放着说明文档、旧包、其它附件，
        /// 宽松匹配会把无关文件认成新版本，导致误报更新。不匹配的一律忽略。
        ///
        /// ⚠️ <c>Regex</c> 实例创建后不可变，<b>可跨线程共用</b>（只调用 <c>Match</c> 是线程安全的）。
        /// </summary>
        public static readonly Regex PackageFileNamePattern = new Regex(
            @"^GeZi-v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)-win64\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 扫描分享目录时最多下钻几层（根 = 0）。
        /// 实测更新源是「根 → GeZi更新/ → 包」，即包在第 1 层；给到 2 留一点余量。
        /// ⚠️ 这是防呆上限：分享万一被改成很深的目录树，不至于无限递归。
        /// </summary>
        public const int MaxScanDepth = 2;

        /// <summary>
        /// 每层最多翻几页（每页 50 条，由 <c>QuarkClient.DirPageSize</c> 决定）。
        /// 同样是防呆上限，避免接口异常返回 <c>HasMore=true</c> 时死循环。
        /// </summary>
        public const int MaxScanPages = 20;
    }
}
