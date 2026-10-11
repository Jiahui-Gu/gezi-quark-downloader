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
// WITHOUT even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Windows;
using GeZi.Core.Update;

namespace GeZi
{
    /// <summary>
    /// 「更新详情」窗口（2026-10-11 第三步）。
    ///
    /// 【职责】展示"发现新版本"的关键信息（版本号 / 大小 / 更新说明入口），
    /// 让用户**二选一**下载方式，然后把选择回传给主窗口去真正下载。
    /// 本窗口**不碰任何网络** —— 取链、下载、安装都交给 MainWindow，保持职责单一、便于单测。
    ///
    /// 【为什么用回调而不是反向拿 MainWindow】关于窗口是临时 new 出来的，
    /// 让本窗口硬编码依赖 MainWindow 类型会让依赖变绕；用 <see cref="DownloadRequested"/>
    /// 把"用户选了什么"抛给调用方，由调用方决定怎么接（关于页把它接到 MainWindow.StartUpdateDownload）。
    ///
    /// 【未登录的处理】"转存到网盘"必须登录，未登录时那个选项直接禁用并默认选中免转存，
    /// 同时在说明里讲清楚"需登录"—— 不静默降级，让用户知道有更优选项但需要先登录。
    /// </summary>
    public partial class UpdatePromptWindow : Window
    {
        private readonly UpdateManifest _manifest;
        private readonly bool _loggedIn;

        /// <summary>用户点了「开始下载」后回调：参数为（更新包, 是否转存到网盘）。</summary>
        public Action<UpdateManifest, bool> DownloadRequested { get; set; }

        /// <summary>GitHub Releases 地址（查看更新说明）。</summary>
        private const string ReleasesUrl =
            "https://github.com/huaotem-bot/gezi-quark-downloader/releases";

        public UpdatePromptWindow(UpdateManifest manifest, bool loggedIn)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            _manifest = manifest;
            _loggedIn = loggedIn;

            InitializeComponent();

            NewVersionText.Text = "发现新版本 v" + _manifest.Version;
            CurrentVersionText.Text = "当前版本 v" + (UpdateService.CurrentVersion != null
                ? UpdateService.CurrentVersion.ToString() : "?");
            SizeText.Text = FormatSize(_manifest.Size);

            // 未登录：禁用"转存到网盘"，默认选免转存并说明
            if (!_loggedIn)
            {
                RbSave.IsEnabled = false;
                RbSave.Content = new System.Windows.Controls.TextBlock
                {
                    Text = "转存到网盘后下载（需登录）",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextTertiaryBrush"),
                };
            }

            // 默认选中：登录 → 转存；未登录 → 免转存
            if (_loggedIn)
                RbSave.IsChecked = true;
            else
                RbNoSave.IsChecked = true;

            Loaded += (s, e) =>
            {
                DialogChrome.ApplyRoundedCorners(this);
                OnModeChanged(null, null);   // 初始化说明文案
            };
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "大小未知";
            if (bytes >= 1024 * 1024)
                return (bytes / 1048576.0).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                bool save = RbSave.IsChecked == true;
                if (save)
                {
                    ModeNote.Text = _loggedIn
                        ? "转存到你的网盘后再下载，速度更稳。转存的副本会在 "
                          + UpdateConstants.DeleteAfterHours.ToString("0") + " 小时后自动从网盘删除，不会永久占用空间。"
                        : "需先登录才能转存到网盘。";
                }
                else
                {
                    ModeNote.Text = _loggedIn
                        ? "不写入网盘，直接从分享取链下载（不占空间，但游客可能受额度限制）。"
                        : "未登录：仅能免转存下载（游客可能受额度限制）。";
                }
            }
            catch { }
        }

        private void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            bool save = RbSave.IsChecked == true && _loggedIn;
            try
            {
                if (DownloadRequested != null)
                    DownloadRequested(_manifest, save);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, "启动更新下载失败：" + ex.Message, "更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            try { Close(); } catch { }
        }

        private void OnReleaseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ShellLaunch.OpenUrl(ReleasesUrl))
                    AppDialog.Show(this, "打开浏览器失败，请手动访问：\n\n" + ReleasesUrl, "更新说明");
            }
            catch { }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }
    }
}
