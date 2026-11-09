// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Net;
using System.Threading;

namespace GeZi.Core.Support
{
    /// <summary>.NET Framework 网络参数调优：默认值对下载器很不友好，必须显式调整。</summary>
    public static class NetworkConfig
    {
        /// <summary>
        /// 线程池"最小工作线程数"的下限。
        /// 自从文件流改用 <c>FileOptions.Asynchronous</c>（写盘走 overlapped IO、不占线程），
        /// worker 已不需要独占线程，所以**不再按连接数预创建线程**。
        /// 保留这个下限是为了应对仍然同步的 DNS 解析（每次新建连接一次）。
        /// </summary>
        private const int ThreadPoolMinFloor = 64;

        public static void Apply(int connectionLimit = 64)
        {
            try
            {
                // Default for clients without an explicit per-handler limit, not a shared budget.
                // SegmentedDownloader sets its own MaxConnectionsPerServer.
                if (connectionLimit < 64) connectionLimit = 64;
                if (connectionLimit > 8192) connectionLimit = 8192;
                ServicePointManager.DefaultConnectionLimit = connectionLimit;
                // 每次新建连接都刷新 DNS：夸克 CDN 有多个 IP，这样才能命中不同节点提速
                ServicePointManager.DnsRefreshTimeout = 0;
                // 省掉 Expect: 100-continue 的一次往返
                ServicePointManager.Expect100Continue = false;
                // 关闭 Nagle 算法，降低小请求（API 调用）延迟
                ServicePointManager.UseNagleAlgorithm = false;
                // 空闲连接 2 分钟后再回收，利于复用
                ServicePointManager.MaxServicePointIdleTime = 120000;

                // Async HTTP/file workers borrow pool threads rather than occupying one each.
                // Keep a floor for synchronous DNS without preallocating one thread per connection.
                int min = Math.Max(ThreadPoolMinFloor, Environment.ProcessorCount * 8);
                if (min > 32767) min = 32767;
                int ioMin;
                ThreadPool.GetMinThreads(out _, out ioMin);
                ThreadPool.SetMinThreads(min, Math.Max(ioMin, min));
            }
            catch { }
        }
    }
}
