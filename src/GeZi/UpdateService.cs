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
using System.Reflection;
using System.Threading.Tasks;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「检查更新」的 UI 层编排（2026-10-11 新增）。
    ///
    /// 【职责】把 Core 的 <see cref="UpdateChecker"/>（只管找包）接进界面：
    /// 记录检查时间、判定"算不算有更新"、记住用户忽略过哪个版本、
    /// 以及维护"网盘里到期要删的转存文件"清单。
    ///
    /// 【为什么不弹窗】需求明确要求**不打断用户**：启动时只做后台检查，
    /// 有新版本就点亮红点 + 弹一次气泡，用户不点就当没发生过。
    /// 真正的窗口只在用户**主动点**「检查更新」之后才出现。
    ///
    /// 【接口失效怎么办】启动时那次检查**吞掉所有异常并静默返回** ——
    /// 分享被删、接口改版、断网，都不该让用户看到一个"更新检查失败"的弹窗。
    /// 用户**手动点**「检查更新」时才把错误如实告诉他（那时他是真的想知道）。
    ///
    /// ⚠️ <b>线程约定</b>：除 <see cref="CheckNowAsync"/> 外，会改写 <c>AppSettings</c> 的
    /// 方法一律<b>只能在 UI 线程调用</b>（与设置页的 Save 同一条线程，避免并发写盘）。
    /// 启动检查本来就是从 <c>Dispatcher.BeginInvoke</c> 里发起的，await 之后仍在 UI 线程。
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// 两次检查之间的最小间隔（分钟）。
        /// 需求要求"每次启动都检查"，所以间隔取得很小 —— 只为挡住"反复重启调试"
        /// 那种几分钟内连开十几次的情况，正常一天开几次都会真的去查。
        /// </summary>
        public const double MinCheckIntervalMinutes = 10.0;

        /// <summary>
        /// 待删除记录最多保留多久（天）。超过就丢弃 ——
        /// 说明删了很多次都没删掉（比如文件早被用户手动删了），没必要一直记着。
        /// </summary>
        public const double MaxPendingDeleteDays = 7.0;

        static UpdateService()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                CurrentVersion = UpdateVersion.FromVersion(v);
            }
            catch
            {
                CurrentVersion = null;
            }
        }

        /// <summary>当前程序版本（从程序集读，不再手写一份）。</summary>
        public static UpdateVersion CurrentVersion { get; private set; }

        /// <summary>
        /// 最近一次**实际查询**得到的更新包（含 fid / token，下载要用）。
        /// ⚠️ 重启后为 null —— 落盘的只有版本号（字符串），要拿 fid 必须重新查一次。
        ///    所以「检查更新」按钮总是先查再动作，靠这里不会出现"有红点但下不了"的情况。
        /// </summary>
        public static UpdateManifest Latest { get; private set; }

        /// <summary>当前是否有（未被忽略的）新版本。红点与气泡都看它。</summary>
        public static bool HasUpdate { get; private set; }

        /// <summary>本次运行是否已完成过一次检查（供 UI 区分"还没查"和"查了没有"）。</summary>
        public static bool CheckCompleted { get; private set; }

        /// <summary>
        /// 由主窗口在启动时注入的配置对象。
        ///
        /// 【为什么要注入一份而不是到处传参】「关于」窗口是临时 <c>new</c> 出来的，
        /// 让它反向去拿主窗口的字段很别扭；这里由主窗口挂一次，各处（关于页/更新流程）
        /// 直接读同一个引用即可，落盘仍统一走 <c>SettingsStore.Save</c>。
        /// </summary>
        public static AppSettings Settings { get; private set; }

        /// <summary>主窗口启动时调用：注入配置并立即恢复红点状态。</summary>
        public static void Attach(AppSettings s)
        {
            Settings = s;
            Load(s);
        }

        // ---------------- 纯逻辑（不碰网络，可单测） ----------------

        /// <summary>距上次检查是否已超过最小间隔。<paramref name="lastTicks"/> 为 0 表示从没查过。</summary>
        public static bool ShouldCheck(long lastTicks, DateTime now)
        {
            if (lastTicks <= 0) return true;
            DateTime last;
            try { last = new DateTime(lastTicks); }
            catch { return true; }

            double minutes = (now - last).TotalMinutes;
            if (minutes < 0) return true;      // 时钟被改过，别卡住
            return minutes >= MinCheckIntervalMinutes;
        }

        /// <summary>
        /// 判定"算不算有更新"。三条同时成立才算：解析得出版本 &gt; 当前版本，且没被忽略。
        /// </summary>
        public static bool ComputeHasUpdate(string latestText, string ignoredText, UpdateVersion current)
        {
            UpdateVersion v;
            if (!UpdateVersion.TryParse(latestText, out v) || v == null) return false;
            if (current == null) return false;
            if (v.CompareTo(current) <= 0) return false;

            UpdateVersion ignored;
            if (UpdateVersion.TryParse(ignoredText, out ignored) && ignored != null && ignored.Equals(v))
                return false;                  // 用户明确忽略过这个版本

            return true;
        }

        /// <summary>
        /// 启动时调用：用**上次落盘的版本号**恢复红点状态。
        /// 这样即使本次因节流没去查，红点也能保持（否则用户重启一次红点就没了）。
        /// </summary>
        public static void Load(AppSettings s)
        {
            if (s == null) return;
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
        }

        /// <summary>用户点了「忽略此版本」。红点立即熄灭，且不再弹气泡。</summary>
        public static void IgnoreCurrent(AppSettings s)
        {
            if (s == null) return;
            s.UpdateIgnoredVersion = s.UpdateLatestVersion ?? "";
            HasUpdate = false;
        }

        /// <summary>这个版本是不是被用户忽略过。</summary>
        public static bool IsIgnored(UpdateVersion v)
        {
            if (v == null || Settings == null) return false;
            UpdateVersion ignored;
            if (!UpdateVersion.TryParse(Settings.UpdateIgnoredVersion, out ignored) || ignored == null)
                return false;
            return ignored.Equals(v);
        }

        /// <summary>
        /// 取消忽略（用户又想看到提醒了）。
        /// ⚠️ 必须**重新算** HasUpdate —— 只把字符串清空的话红点不会自己亮回来。
        /// </summary>
        public static void ClearIgnore(AppSettings s)
        {
            if (s == null) return;
            s.UpdateIgnoredVersion = "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
        }

        // ---------------- 检查 ----------------

        /// <summary>
        /// 启动检查。失败一律静默。
        /// </summary>
        /// <param name="onNewUpdate">确认有新版本时回调（UI 线程），用来弹气泡。</param>
        public static async Task RunStartupCheckAsync(AppSettings s, Action<UpdateManifest> onNewUpdate)
        {
            if (s == null) return;
            if (!ShouldCheck(s.UpdateLastCheckTicks, DateTime.Now)) return;

            UpdateManifest found = null;
            try
            {
                // 不需要登录：列分享目录用的是公开凭证。
                //
                // 🚨 【这里**不能**加 ConfigureAwait(false)】
                //    加了的后果（实测踩到）：await 之后回到**线程池线程**，
                //    于是下面的 Apply / onNewUpdate 也在非 UI 线程执行 ——
                //    写日志、弹气泡（NotifyToast 是 Window）都会抛异常，
                //    再被各自的 try/catch 吞掉，表现就是"什么都没发生、日志也没有一行"。
                //    启动检查本来就跑在 UI 线程上，让它原样回到 UI 线程即可。
                var checker = UpdateChecker.CreateAnonymous();
                found = await checker.FindLatestAsync();
            }
            catch
            {
                // 分享失效 / 接口改版 / 断网 —— 启动检查失败必须静默，不打扰用户
                return;
            }

            Apply(s, found, onNewUpdate);
        }

        /// <summary>
        /// 用户手动点「检查更新」：不做节流，失败也如实抛出（他是真的想知道结果）。
        /// </summary>
        public static Task<UpdateManifest> CheckNowAsync()
        {
            var checker = UpdateChecker.CreateAnonymous();
            return checker.FindLatestAsync();
        }

        /// <summary>把一次检查结果落到设置与内存状态里。</summary>
        public static void Apply(AppSettings s, UpdateManifest found, Action<UpdateManifest> onNewUpdate)
        {
            if (s == null) return;

            s.UpdateLastCheckTicks = DateTime.Now.Ticks;
            Latest = (found != null && found.IsValid) ? found : null;
            s.UpdateLatestVersion = Latest != null ? Latest.Version.ToString() : "";
            HasUpdate = ComputeHasUpdate(s.UpdateLatestVersion, s.UpdateIgnoredVersion, CurrentVersion);
            CheckCompleted = true;

            if (HasUpdate && onNewUpdate != null)
                onNewUpdate(Latest);
        }

        // ---------------- 网盘待删除清单 ----------------

        /// <summary>
        /// 待删除清单。老配置里没有这个节点时 <c>UpdatePendingDeletes</c> 是 null
        /// （XmlSerializer 不执行字段初始化器），这里补一个空表。
        /// </summary>
        public static List<NetdiskDeleteTask> PendingDeletes(AppSettings s)
        {
            if (s == null) return new List<NetdiskDeleteTask>();
            if (s.UpdatePendingDeletes == null)
                s.UpdatePendingDeletes = new List<NetdiskDeleteTask>();
            return s.UpdatePendingDeletes;
        }

        /// <summary>记一条"到期要删"。同一 fid 只留一条。</summary>
        public static void NotePendingDelete(AppSettings s, string fid, string name, DateTime dueAt)
        {
            if (s == null || string.IsNullOrEmpty(fid)) return;
            var list = PendingDeletes(s);
            list.RemoveAll(x => x != null && string.Equals(x.Fid, fid, StringComparison.Ordinal));
            list.Add(new NetdiskDeleteTask
            {
                Fid = fid,
                Name = name ?? "",
                DueTicks = dueAt.Ticks,
            });
        }

        /// <summary>删完了（或确认已不存在）就移除记录。</summary>
        public static void DropPendingDelete(AppSettings s, string fid)
        {
            if (s == null || s.UpdatePendingDeletes == null || string.IsNullOrEmpty(fid)) return;
            s.UpdatePendingDeletes.RemoveAll(
                x => x != null && string.Equals(x.Fid, fid, StringComparison.Ordinal));
        }

        /// <summary>已到期、现在该删的记录。</summary>
        public static List<NetdiskDeleteTask> DueDeletes(AppSettings s, DateTime now)
        {
            var list = PendingDeletes(s);
            return list.FindAll(x => x != null
                                     && !string.IsNullOrEmpty(x.Fid)
                                     && x.DueTicks > 0
                                     && x.DueTicks <= now.Ticks);
        }

        /// <summary>
        /// 清掉"过期太久还没删掉"的记录（说明目标早就不存在了）。
        /// 返回被清掉的数量，便于日志。
        /// </summary>
        public static int DropStalePendingDeletes(AppSettings s, DateTime now)
        {
            if (s == null || s.UpdatePendingDeletes == null) return 0;
            long cut = now.AddDays(-MaxPendingDeleteDays).Ticks;
            int before = s.UpdatePendingDeletes.Count;
            s.UpdatePendingDeletes.RemoveAll(x => x != null && x.DueTicks > 0 && x.DueTicks < cut);
            return before - s.UpdatePendingDeletes.Count;
        }
    }
}
