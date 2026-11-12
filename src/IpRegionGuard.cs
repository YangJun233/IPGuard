// IP 区域守卫 (IP Region Guard) - Windows 11 托盘小程序
// 定时检测出口 IP 的地理位置，命中"禁止区域"或不在"允许区域"时弹窗提醒。
// 目标：C# 5 / .NET Framework 4.x，用系统自带 csc.exe 即可编译，无第三方依赖。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IpRegionGuard
{
    // ================================================================ 本地化

    static class I18n
    {
        public static bool En = true;    // 默认英文；false = 中文，true = English
        public static void Set(string lang) { En = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase); }
        public static string Cur { get { return En ? "en" : "zh"; } }
        // 每个调用点同时给出中/英文，避免漏 key。
        public static string T(string zh, string en) { return En ? en : zh; }
    }

    // ================================================================ 工具

    static class Util
    {
        public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static string PathOf(string name) { return Path.Combine(BaseDir, name); }

        // 归一化：去掉音标、只留字母数字、转小写。用于区域名比较（Tokyo == Tokyo）。
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static bool SameRegion(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            return x.Length > 0 && x == y;
        }

        static readonly object logLock = new object();
        public static int LogMaxBytes = 256 * 1024;   // 文件大小硬上限（可被配置覆盖）
        public static int LogRetentionDays = 14;       // 超过这么多天的行自动清理

        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    string f = PathOf("ip_guard.log");
                    File.AppendAllText(f,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                    // 超过上限就砍掉最旧的，保留最近约 70%。
                    if (LogMaxBytes > 0 && new FileInfo(f).Length > LogMaxBytes)
                    {
                        string[] all = File.ReadAllLines(f, Encoding.UTF8);
                        long budget = (long)(LogMaxBytes * 0.7);
                        long used = 0; int start = all.Length;
                        for (int i = all.Length - 1; i >= 0; i--)
                        {
                            used += all[i].Length + 2;
                            if (used > budget) break;
                            start = i;
                        }
                        if (start > 0)
                            File.WriteAllLines(f, all.Skip(start).ToArray(), Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        // 按天数清理：丢弃时间戳早于 LogRetentionDays 天的行。
        public static void PruneLogByAge()
        {
            try
            {
                if (LogRetentionDays <= 0) return;
                lock (logLock)
                {
                    string f = PathOf("ip_guard.log");
                    if (!File.Exists(f)) return;
                    string[] all = File.ReadAllLines(f, Encoding.UTF8);
                    DateTime cutoff = DateTime.Now.AddDays(-LogRetentionDays);
                    List<string> kept = new List<string>(all.Length);
                    bool dropping = false, changed = false;
                    foreach (string line in all)
                    {
                        DateTime dt;
                        if (line.Length >= 19 &&
                            DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                        {
                            dropping = dt < cutoff;   // 一旦遇到不够旧的行，后面都保留
                        }
                        if (dropping) { changed = true; continue; }
                        kept.Add(line);
                    }
                    if (changed) File.WriteAllLines(f, kept.ToArray(), Encoding.UTF8);
                }
            }
            catch { }
        }

        // 信号质量 0..100 的红→琥珀→绿渐变取色。
        public static Color QualityColor(int q)
        {
            if (q < 0) q = 0; if (q > 100) q = 100;
            int r0, g0, b0, r1, g1, b1; double f;
            if (q <= 50) { f = q / 50.0; r0 = 222; g0 = 58; b0 = 48; r1 = 232; g1 = 170; b1 = 20; }
            else { f = (q - 50) / 50.0; r0 = 232; g0 = 170; b0 = 20; r1 = 46; g1 = 170; b1 = 70; }
            int r = (int)(r0 + (r1 - r0) * f);
            int g = (int)(g0 + (g1 - g0) * f);
            int b = (int)(b0 + (b1 - b0) * f);
            return Color.FromArgb(r, g, b);
        }

        // 托盘图标：外圈=信号质量弧（12 点起顺时针，满圈=100，实心按质量取色），
        // 中心圆点=地区状态色。offline=true 时整圈灰色、质量视为 0。
        // dotOn=false 用于异常闪烁时把中心点隐藏（环保留）。
        // 自适应：按系统当前 DPI 要求的托盘小图标尺寸输出（100%=16 / 150%=24 / 200%=32…），
        //   并在内部用高分辨率绘制再缩放，保证各缩放下都清晰。
        // 尽量大：环外径推到贴边（0.49），几乎填满整个图标格子。
        public static Icon RingIcon(Color status, int quality, bool offline, bool dotOn)
        {
            int target = 16;
            try { target = SystemInformation.SmallIconSize.Width; } catch { }
            if (target < 16) target = 16;
            if (target > 128) target = 128;
            return RingIconAt(status, quality, offline, dotOn, target);
        }

        // 指定输出像素尺寸绘制。内部 3× 超采样再缩放（比 4× 少糊，曲线仍平滑）。
        // 常规比例：四周留边距（不填满），环与中心点之间留明显透明间隔做分隔。
        public static Icon RingIconAt(Color status, int quality, bool offline, bool dotOn, int outSize)
        {
            int DRAW = outSize * 3;
            if (DRAW > 192) DRAW = 192;
            using (Bitmap big = new Bitmap(DRAW, DRAW))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float cx = DRAW / 2f, cy = DRAW / 2f;
                    float t = DRAW * 0.135f;          // 环粗
                    float rOuter = DRAW * 0.49f;      // 基本填满，与相邻托盘图标同大（留 1px 安全边）
                    float rMid = rOuter - t / 2f;
                    float rInner = rMid - t / 2f;     // 环内缘
                    RectangleF ring = new RectangleF(cx - rMid, cy - rMid, rMid * 2, rMid * 2);

                    using (Pen tp = new Pen(Color.FromArgb(70, 120, 120, 120), t)) g.DrawEllipse(tp, ring);

                    if (offline)
                    {
                        using (Pen gp = new Pen(Color.FromArgb(150, 150, 150), t)) g.DrawArc(gp, ring, -90, 360);
                    }
                    else if (quality > 0)
                    {
                        float sweep = quality / 100f * 360f;
                        using (Pen ap = new Pen(QualityColor(quality), t))
                        {
                            ap.StartCap = LineCap.Round; ap.EndCap = LineCap.Round;
                            g.DrawArc(ap, ring, -90, sweep);
                        }
                    }

                    // 中心圆点（方案 B）：适中大小，与环之间留清楚间隔。
                    float rDot = DRAW * 0.27f;
                    if (rDot > rInner - DRAW * 0.03f) rDot = rInner - DRAW * 0.03f;   // 至少留 0.03 间隔
                    RectangleF dot = new RectangleF(cx - rDot, cy - rDot, rDot * 2, rDot * 2);
                    using (Pen wp = new Pen(Color.White, DRAW * 0.028f)) g.DrawEllipse(wp, dot);   // 白描边增强分离
                    if (dotOn)
                        using (SolidBrush db = new SolidBrush(status)) g.FillEllipse(db, dot);
                }

                using (Bitmap small = new Bitmap(outSize, outSize))
                {
                    using (Graphics gs = Graphics.FromImage(small))
                    {
                        gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gs.SmoothingMode = SmoothingMode.AntiAlias;
                        gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gs.Clear(Color.Transparent);
                        gs.DrawImage(big, new Rectangle(0, 0, outSize, outSize));
                    }
                    IntPtr h = small.GetHicon();
                    using (Icon tmp = Icon.FromHandle(h))
                    {
                        Icon copy = (Icon)tmp.Clone();
                        NativeMethods.DestroyIcon(h);
                        return copy;
                    }
                }
            }
        }

        public static string Cut(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // 任务管理器那样的友好名：优先 exe 的 FileDescription，取不到就用 fallback（进程名）。
        public static string FriendlyName(string path, string fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                    if (fvi != null && !string.IsNullOrEmpty(fvi.FileDescription))
                        return fvi.FileDescription.Trim();
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(fallback)) return fallback;
            try { return Path.GetFileNameWithoutExtension(path); } catch { return path; }
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        // 空闲时回收工作集：把物理内存交还系统，需要时再由系统按需换回。
        // 用于让这个常驻小程序在两次检测之间的空档几乎不占物理内存。
        public static void TrimWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    // ================================================================ 网络质量

    // 方案①：不 ping 任何第三方。质量完全来自“每次本来就要发的那次 IP 定位 HTTP
    // 请求”的往返耗时（延迟），再用最近几次耗时的波动（抖动）反映稳定性。
    // 零额外流量、不联系任何新主机。
    static class NetQuality
    {
        public static int LastMs = -1;              // 最近一次往返延迟（ms），-1 未知
        public static int LastJitter = 0;           // 最近几次的抖动（ms）
        static readonly Queue<long> recent = new Queue<long>();  // 最近耗时样本

        // httpElapsedMs：本轮 IP 定位请求耗时（ms）。ok=false 表示这次请求失败。
        // 返回 0..100，-1 未知。
        public static int Measure(long httpElapsedMs, bool ok)
        {
            if (!ok || httpElapsedMs <= 0) { LastMs = -1; return -1; }

            LastMs = (int)httpElapsedMs;
            recent.Enqueue(httpElapsedMs);
            while (recent.Count > 5) recent.Dequeue();

            // 抖动：最近样本的最大-最小差
            long min = long.MaxValue, max = 0;
            foreach (long v in recent) { if (v < min) min = v; if (v > max) max = v; }
            LastJitter = recent.Count >= 2 ? (int)(max - min) : 0;

            int q = FromLatency(httpElapsedMs);
            // 抖动大额外扣分（链路不稳）
            if (LastJitter > 400) q -= 15; else if (LastJitter > 200) q -= 8;
            return q < 5 ? 5 : q > 100 ? 100 : q;
        }

        // HTTP 往返（含服务器处理+TLS），阈值比纯 ping 宽松。
        static int FromLatency(long ms)
        {
            if (ms <= 250) return 100;
            int q = (int)(100 - (ms - 250) * 100.0 / 2750.0);   // 250ms→100, 3000ms→0
            return q;
        }
    }

    // ================================================================ 进程关闭（整棵进程树）

    // 关闭指定程序：把它拉起的插件/子进程一并清掉。
    // - 同名多实例全部纳入（如浏览器一个 app 开一堆同名进程）；
    // - 用 Toolhelp32 的父子关系收集所有后代进程；
    // - 非强制时先对有窗口的发正常关闭(WM_CLOSE)，等 2 秒，再对残留从子到父强制结束。
    // 结束同一用户的进程不需要管理员权限（目标以管理员运行时才需要）。
    static class ProcKiller
    {
        const uint TH32CS_SNAPPROCESS = 0x00000002;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 pe);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 pe);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        struct Snap { public Dictionary<int, int> parent; public Dictionary<int, string> name; }

        static Snap Enumerate()
        {
            Snap s = new Snap();
            s.parent = new Dictionary<int, int>();
            s.name = new Dictionary<int, string>();
            IntPtr h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return s;
            try
            {
                PROCESSENTRY32 pe = new PROCESSENTRY32();
                pe.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(h, ref pe))
                {
                    do
                    {
                        int pid = (int)pe.th32ProcessID;
                        s.parent[pid] = (int)pe.th32ParentProcessID;
                        s.name[pid] = pe.szExeFile ?? "";
                    } while (Process32Next(h, ref pe));
                }
            }
            finally { CloseHandle(h); }
            return s;
        }

        // 目标以 exe 完整路径给出；按文件名匹配运行中的根进程（路径可读时再按全路径确认）。
        static bool IsRoot(Process p, HashSet<string> targetNames, HashSet<string> targetPaths)
        {
            try
            {
                string nm = p.ProcessName + ".exe";
                if (!targetNames.Contains(nm.ToLowerInvariant())) return false;
                // 文件名匹配即算命中；若能读到全路径且用户给的是全路径，则要求全路径一致。
                try
                {
                    string full = p.MainModule != null ? p.MainModule.FileName : null;
                    if (full != null && targetPaths.Count > 0)
                        return targetPaths.Contains(full.ToLowerInvariant());
                }
                catch { /* 读不到路径（权限/位数差异）就只按文件名算命中 */ }
                return true;
            }
            catch { return false; }
        }

        // 返回被关闭的进程数。
        public static int KillTargets(IEnumerable<string> paths, bool forceImmediate)
        {
            HashSet<string> names = new HashSet<string>();
            HashSet<string> fulls = new HashSet<string>();
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try { names.Add(Path.GetFileName(p).ToLowerInvariant()); } catch { }
                fulls.Add(p.ToLowerInvariant());
            }
            if (names.Count == 0) return 0;

            Snap snap = Enumerate();

            // 找根：运行中、exe 名（或全路径）匹配的进程。
            List<int> roots = new List<int>();
            foreach (Process p in Process.GetProcesses())
            {
                if (IsRoot(p, names, fulls)) roots.Add(p.Id);
                p.Dispose();
            }
            if (roots.Count == 0) return 0;

            // 由父子关系收集所有后代，连同根组成待关闭集合。
            HashSet<int> set = new HashSet<int>(roots);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (KeyValuePair<int, int> kv in snap.parent)
                {
                    if (!set.Contains(kv.Key) && set.Contains(kv.Value) && kv.Key != 0)
                    {
                        set.Add(kv.Key); grew = true;
                    }
                }
            }

            int ownPid = 0;
            try { ownPid = Process.GetCurrentProcess().Id; } catch { }
            set.Remove(ownPid);   // 绝不关自己

            // 取得 Process 句柄
            List<Process> procs = new List<Process>();
            foreach (int pid in set)
            {
                try { procs.Add(Process.GetProcessById(pid)); } catch { }
            }
            if (procs.Count == 0) return 0;

            if (!forceImmediate)
            {
                // 先对有主窗口的发正常关闭
                foreach (Process p in procs)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                }
                // 等最多 2 秒
                DateTime until = DateTime.Now.AddMilliseconds(2000);
                while (DateTime.Now < until)
                {
                    bool anyAlive = false;
                    foreach (Process p in procs) { try { if (!p.HasExited) { anyAlive = true; break; } } catch { } }
                    if (!anyAlive) break;
                    Thread.Sleep(150);
                }
            }

            // 强制结束残留：按"子在前、父在后"排序（后代 PID 一般更大，逆序近似）
            procs.Sort(delegate(Process a, Process b)
            {
                int ia = 0, ib = 0; try { ia = a.Id; } catch { } try { ib = b.Id; } catch { }
                return ib.CompareTo(ia);
            });
            int killed = 0;
            foreach (Process p in procs)
            {
                try
                {
                    if (!p.HasExited) { p.Kill(); killed++; }
                    else killed++;   // 正常关闭已退出的也算
                }
                catch { /* 已退出 / 拒绝访问（目标以管理员运行） */ }
                finally { try { p.Dispose(); } catch { } }
            }
            return killed;
        }
    }

    // ================================================================ 数据模型

    public class RegionRule
    {
        public string Country { get; set; }      // 国家/地区英文名，如 "China"
        public string CountryCode { get; set; }  // ISO2，如 "CN"
        public string State { get; set; }        // 省/州；空 = 整个国家
        public string City { get; set; }         // 城市；空 = 整个省/州

        public RegionRule() { Country = ""; CountryCode = ""; State = ""; City = ""; }

        public bool Matches(GeoLocation loc)
        {
            if (loc == null) return false;
            bool okCountry = Util.SameRegion(Country, loc.Country)
                || (!string.IsNullOrEmpty(CountryCode) && !string.IsNullOrEmpty(loc.CountryCode)
                    && string.Equals(CountryCode, loc.CountryCode, StringComparison.OrdinalIgnoreCase));
            if (!okCountry) return false;
            if (!string.IsNullOrEmpty(State) && !Util.SameRegion(State, loc.Region)) return false;
            if (!string.IsNullOrEmpty(City) && !Util.SameRegion(City, loc.City)) return false;
            return true;
        }

        public override string ToString()
        {
            string s = Country;
            if (!string.IsNullOrEmpty(CountryCode)) s += " (" + CountryCode + ")";
            if (string.IsNullOrEmpty(State)) return s + I18n.T("   —— 整个国家/地区", "  — whole country/region");
            s += " / " + State;
            if (string.IsNullOrEmpty(City)) return s + I18n.T("   —— 整个省/州", "  — whole state/province");
            return s + " / " + City;
        }

        public string Key()
        {
            return Util.Norm(Country) + "|" + Util.Norm(State) + "|" + Util.Norm(City);
        }
    }

    public class GeoLocation
    {
        public string Ip { get; set; }
        public string Country { get; set; }
        public string CountryCode { get; set; }
        public string Region { get; set; }
        public string City { get; set; }
        public string Source { get; set; }

        public GeoLocation() { Ip = ""; Country = ""; CountryCode = ""; Region = ""; City = ""; Source = ""; }

        public string Display()
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(Country)) parts.Add(Country);
            if (!string.IsNullOrEmpty(Region) && !Util.SameRegion(Region, Country)) parts.Add(Region);
            if (!string.IsNullOrEmpty(City) && !Util.SameRegion(City, Region)) parts.Add(City);
            string s = string.Join(" / ", parts.ToArray());
            return s.Length == 0 ? I18n.T("未知区域", "Unknown") : s;
        }
    }

    public class AppConfig
    {
        public List<RegionRule> Forbidden { get; set; }  // 处于其中 -> 报警
        public List<RegionRule> Allowed { get; set; }    // 不处于其中 -> 报警
        public int IntervalSeconds { get; set; }         // 正常时的检测间隔（秒）
        public int AbnormalIntervalSeconds { get; set; } // 处于非期望区域时的检测间隔（秒）；0 = 与正常相同
        public bool Popup { get; set; }
        public bool Sound { get; set; }
        public bool StartWithWindows { get; set; }
        public string Lang { get; set; }                  // "en"（默认）或 "zh"
        public bool DiagLog { get; set; }                 // 详细日志：每次检测都记一行；默认 false
        public int LogRetentionDays { get; set; }         // 日志保留天数（超过自动清理）；0=不按天清
        public int LogMaxKB { get; set; }                 // 日志文件大小上限（KB）；0=不限
        public bool KillApps { get; set; }                // 异常区域时是否关闭指定程序
        public bool KillForce { get; set; }               // true=立即强制结束；false=先正常关再强制
        public List<string> KillTargets { get; set; }     // 要关闭的程序 exe 完整路径列表
        public bool StrictPreKill { get; set; }           // 严格模式：网络一变先关软件再查（默认关）

        public AppConfig()
        {
            Forbidden = new List<RegionRule>();
            Allowed = new List<RegionRule>();
            IntervalSeconds = 60;
            AbnormalIntervalSeconds = 10;
            Popup = true;
            Sound = true;
            StartWithWindows = false;
            Lang = "en";
            DiagLog = false;
            LogRetentionDays = 14;
            LogMaxKB = 256;
            KillApps = false;
            KillForce = false;
            KillTargets = new List<string>();
            StrictPreKill = false;
        }

        public void Clamp()
        {
            if (Forbidden == null) Forbidden = new List<RegionRule>();
            if (Allowed == null) Allowed = new List<RegionRule>();
            if (KillTargets == null) KillTargets = new List<string>();
            if (!string.Equals(Lang, "zh", StringComparison.OrdinalIgnoreCase)) Lang = "en";
            if (LogRetentionDays < 0) LogRetentionDays = 0;
            if (LogRetentionDays > 3650) LogRetentionDays = 3650;
            if (LogMaxKB != 0 && LogMaxKB < 16) LogMaxKB = 16;
            if (LogMaxKB > 1048576) LogMaxKB = 1048576;
            if (IntervalSeconds < 5) IntervalSeconds = 5;
            if (IntervalSeconds > 86400) IntervalSeconds = 86400;
            if (AbnormalIntervalSeconds != 0)
            {
                if (AbnormalIntervalSeconds < 5) AbnormalIntervalSeconds = 5;
                if (AbnormalIntervalSeconds > 86400) AbnormalIntervalSeconds = 86400;
            }
        }

        public AppConfig Clone()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            AppConfig c = js.Deserialize<AppConfig>(js.Serialize(this));
            c.Clamp();
            return c;
        }

        public static AppConfig Load()
        {
            try
            {
                string f = Util.PathOf("config.json");
                if (File.Exists(f))
                {
                    AppConfig c = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(f, Encoding.UTF8));
                    if (c != null) { c.Clamp(); return c; }
                }
            }
            catch (Exception ex) { Util.Log("读取配置失败: " + ex.Message); }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                Clamp();
                File.WriteAllText(Util.PathOf("config.json"),
                    new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex) { Util.Log("保存配置失败: " + ex.Message); }
        }
    }

    // ================================================================ 网络 / 地理数据

    class TimedWebClient : WebClient
    {
        public int TimeoutMs = 12000;
        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest r = base.GetWebRequest(address);
            r.Timeout = TimeoutMs;
            HttpWebRequest h = r as HttpWebRequest;
            if (h != null)
            {
                h.ReadWriteTimeout = TimeoutMs;
                h.UserAgent = "IPGuard/1.0";
                // 关键：不复用 TCP 连接。切换 VPN 后，被复用的旧连接可能仍走旧出口，
                // 导致 IP 定位服务看到的还是旧 IP、返回旧地区。每次都开新连接 =
                // 每次都反映“当前出口”，从而及时读到区域变化。
                h.KeepAlive = false;
                h.Pipelined = false;
                h.ServicePoint.ConnectionLeaseTimeout = 0;   // 连接用完即弃
                h.ServicePoint.MaxIdleTime = 1000;
                // 禁用任何中间缓存，避免拿到旧的定位结果。
                h.CachePolicy = new System.Net.Cache.RequestCachePolicy(
                    System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                h.Headers["Cache-Control"] = "no-cache, no-store";
                h.Headers["Pragma"] = "no-cache";
            }
            return r;
        }
    }

    static class Geo
    {
        static readonly object cacheLock = new object();
        static Dictionary<string, string[]> cache;   // "COUNTRIES" / "S|country" / "C|country|state"

        public static void Init()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.MaxServicePointIdleTime = 1000;  // 空闲连接 1 秒即回收，避免复用旧出口
            try { ServicePointManager.SetTcpKeepAlive(false, 0, 0); } catch { }
        }

        static string Http(string url)
        {
            using (TimedWebClient wc = new TimedWebClient())
            {
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return v.ToString();
            return "";
        }

        // ---------------- 当前位置 ----------------

        static int probeSeq;   // 递增，用作 URL cache-buster

        public static GeoLocation GetLocation()
        {
            int n = System.Threading.Interlocked.Increment(ref probeSeq);
            Exception first = null;
            try
            {
                string json = Http("http://ip-api.com/json/?fields=status,message,country,countryCode,regionName,city,query&_=" + n);
                Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                if (Str(d, "status") == "success")
                {
                    GeoLocation g = new GeoLocation();
                    g.Ip = Str(d, "query");
                    g.Country = Str(d, "country");
                    g.CountryCode = Str(d, "countryCode");
                    g.Region = Str(d, "regionName");
                    g.City = Str(d, "city");
                    g.Source = "ip-api.com";
                    return g;
                }
                first = new Exception("ip-api: " + Str(d, "message"));
            }
            catch (Exception ex) { first = ex; }

            try
            {
                string json = Http("https://ipinfo.io/json?_=" + n);
                Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                GeoLocation g = new GeoLocation();
                g.Ip = Str(d, "ip");
                g.CountryCode = Str(d, "country");
                g.Country = CountryNameOf(g.CountryCode);
                g.Region = Str(d, "region");
                g.City = Str(d, "city");
                g.Source = "ipinfo.io";
                if (!string.IsNullOrEmpty(g.Ip)) return g;
            }
            catch { }

            throw first != null ? first : new Exception("无法获取位置");
        }

        // ---------------- 缓存 ----------------

        static void EnsureCache()
        {
            if (cache != null) return;
            try
            {
                string f = Util.PathOf("geo_cache.json");
                if (File.Exists(f))
                    cache = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }
                        .Deserialize<Dictionary<string, string[]>>(File.ReadAllText(f, Encoding.UTF8));
            }
            catch { }
            if (cache == null) cache = new Dictionary<string, string[]>();
        }

        static void SaveCache()
        {
            try
            {
                File.WriteAllText(Util.PathOf("geo_cache.json"),
                    new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Serialize(cache), Encoding.UTF8);
            }
            catch { }
        }

        static string[] Cached(string key, Func<string[]> fetch)
        {
            lock (cacheLock)
            {
                EnsureCache();
                string[] v;
                if (cache.TryGetValue(key, out v) && v != null) return v;
            }
            string[] fresh = fetch();
            lock (cacheLock)
            {
                EnsureCache();
                cache[key] = fresh;
                SaveCache();
            }
            return fresh;
        }

        // ---------------- 国家 / 省州 / 城市 ----------------

        // 返回 "China|CN" 形式
        public static string[] GetCountries()
        {
            return Cached("COUNTRIES", delegate
            {
                string json = Http("https://countriesnow.space/api/v0.1/countries/iso");
                Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                object arr;
                List<string> outp = new List<string>();
                if (d.TryGetValue("data", out arr) && arr is object[])
                {
                    foreach (object o in (object[])arr)
                    {
                        Dictionary<string, object> it = o as Dictionary<string, object>;
                        if (it == null) continue;
                        string name = Str(it, "name");
                        string iso2 = Str(it, "Iso2");
                        if (iso2.Length == 0) iso2 = Str(it, "iso2");
                        if (name.Length > 0) outp.Add(name + "|" + iso2);
                    }
                }
                outp.Sort(StringComparer.OrdinalIgnoreCase);
                if (outp.Count == 0) throw new Exception("国家列表为空");
                return outp.ToArray();
            });
        }

        public static string CountryNameOf(string iso2)
        {
            if (string.IsNullOrEmpty(iso2)) return "";
            try
            {
                foreach (string s in GetCountries())
                {
                    string[] p = s.Split('|');
                    if (p.Length == 2 && string.Equals(p[1], iso2, StringComparison.OrdinalIgnoreCase)) return p[0];
                }
            }
            catch { }
            return iso2;
        }

        public static string[] GetStates(string country)
        {
            return Cached("S|" + Util.Norm(country), delegate
            {
                string json = Http("https://countriesnow.space/api/v0.1/countries/states/q?country="
                                   + Uri.EscapeDataString(country));
                Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                List<string> outp = new List<string>();
                object data;
                if (d.TryGetValue("data", out data))
                {
                    Dictionary<string, object> dd = data as Dictionary<string, object>;
                    object states;
                    if (dd != null && dd.TryGetValue("states", out states) && states is object[])
                    {
                        foreach (object o in (object[])states)
                        {
                            Dictionary<string, object> it = o as Dictionary<string, object>;
                            if (it == null) continue;
                            string name = Str(it, "name");
                            if (name.Length > 0) outp.Add(name);
                        }
                    }
                }
                outp.Sort(StringComparer.OrdinalIgnoreCase);
                return outp.ToArray();
            });
        }

        public static string[] GetCities(string country, string state)
        {
            return Cached("C|" + Util.Norm(country) + "|" + Util.Norm(state), delegate
            {
                string json = Http("https://countriesnow.space/api/v0.1/countries/state/cities/q?country="
                                   + Uri.EscapeDataString(country) + "&state=" + Uri.EscapeDataString(state));
                Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                List<string> outp = new List<string>();
                object data;
                if (d.TryGetValue("data", out data) && data is object[])
                {
                    foreach (object o in (object[])data)
                    {
                        if (o == null) continue;
                        string name = o.ToString();
                        if (name.Length > 0) outp.Add(name);
                    }
                }
                outp.Sort(StringComparer.OrdinalIgnoreCase);
                return outp.ToArray();
            });
        }
    }

    // ================================================================ 报警窗口

    class AlertForm : Form
    {
        Label lbl;
        static AlertForm inst;

        AlertForm()
        {
            Text = I18n.T("IPGuard — 区域异常", "IPGuard — Region Alert");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(430, 168);
            BackColor = Color.White;

            PictureBox pic = new PictureBox();
            pic.Image = SystemIcons.Warning.ToBitmap();
            pic.SizeMode = PictureBoxSizeMode.CenterImage;
            pic.Bounds = new Rectangle(16, 20, 40, 40);
            Controls.Add(pic);

            lbl = new Label();
            lbl.Bounds = new Rectangle(66, 16, 348, 92);
            lbl.Font = new Font("Microsoft YaHei UI", 10f);
            Controls.Add(lbl);

            Button ok = new Button();
            ok.Text = I18n.T("知道了", "Dismiss");
            ok.Bounds = new Rectangle(322, 120, 92, 30);
            ok.Click += delegate { Hide(); };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cfg = new Button();
            cfg.Text = I18n.T("打开设置", "Settings");
            cfg.Bounds = new Rectangle(218, 120, 92, 30);
            cfg.Click += delegate { Hide(); if (OnOpenSettings != null) OnOpenSettings(); };
            Controls.Add(cfg);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        public static Action OnOpenSettings;

        public static void Popup(string message, bool sound)
        {
            if (inst == null || inst.IsDisposed) inst = new AlertForm();
            inst.lbl.Text = message;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            inst.Location = new Point(wa.Right - inst.Width - 24, wa.Bottom - inst.Height - 24);
            if (!inst.Visible) inst.Show();
            inst.TopMost = true;
            inst.BringToFront();
            inst.Activate();
            if (sound) { try { System.Media.SystemSounds.Exclamation.Play(); } catch { } }
        }

        public static void Kill()
        {
            if (inst != null && !inst.IsDisposed) { inst.Dispose(); inst = null; }
        }

        // 回到正常区域时主动隐藏报警窗（不销毁，下次异常可复用）。
        public static void Dismiss()
        {
            if (inst != null && !inst.IsDisposed && inst.Visible) inst.Hide();
        }

        public static bool IsShownForTest()
        {
            return inst != null && !inst.IsDisposed && inst.Visible;
        }
    }

    // ================================================================ 托盘主程序

    class TrayApp : ApplicationContext
    {
        readonly Form sync = new Form();            // 隐藏窗体，仅用于线程封送
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer blinkTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer netDebounce = new System.Windows.Forms.Timer();  // 网络变化防抖
        readonly System.Windows.Forms.Timer probeTimer = new System.Windows.Forms.Timer();   // 本机出口探测
        readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();   // 事件后快速重试
        string lastEgressSrc = "";       // 上次本机出口源 IP（用于探测变化）
        int fastRetriesLeft;             // 事件触发后，离线时的快速重试次数
        bool eventTriggeredCheck;        // 本轮检查是否由事件触发（供离线快速重试判定）
        static readonly Color colGreen = Color.FromArgb(46, 160, 67);
        static readonly Color colRed = Color.FromArgb(218, 54, 51);
        static readonly Color colYellow = Color.FromArgb(219, 171, 10);
        static readonly Color colGray = Color.FromArgb(150, 150, 150);
        Icon curIcon;                 // 当前托盘图标（换新时释放旧的）
        Color curStatus = Color.FromArgb(150, 150, 150);
        int curQuality = -1;          // 信号质量 0..100，-1 未知
        bool curOffline;              // 无网络（灰环）
        bool blinkOn;

        AppConfig cfg;
        GeoLocation last;
        string lastError = "";
        string lastSig = "";
        DateTime lastAlert = DateTime.MinValue;
        DateTime lastCheck = DateTime.MinValue;
        DateTime lastTrim = DateTime.MinValue;
        DateTime dndUntil = DateTime.MinValue;   // 免打扰截止时间
        DateTime lastPruneDate = DateTime.MinValue;  // 上次按天清理日志的日期
        bool busy;
        bool paused;
        SettingsForm settings;

        ToolStripMenuItem miStatus, miCheck, miPause, miDnd, miSettings, miLog, miExit;

        public TrayApp()
        {
            cfg = AppConfig.Load();
            I18n.Set(cfg.Lang);
            ApplyLogConfig();
            Util.PruneLogByAge();
            lastPruneDate = DateTime.Now.Date;

            // 异常时红色每秒闪烁：500ms 翻转一次 = 1Hz 闪。闪的是中心圆点，信号环保留。
            blinkTimer.Interval = 500;
            blinkTimer.Tick += delegate
            {
                blinkOn = !blinkOn;
                ApplyIcon(blinkOn);
            };

            // 隐藏宿主窗体
            sync.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            sync.ShowInTaskbar = false;
            sync.StartPosition = FormStartPosition.Manual;
            sync.Location = new Point(-4000, -4000);
            sync.Size = new Size(1, 1);
            sync.Load += delegate { sync.Hide(); };
            sync.Show();
            sync.Hide();

            ContextMenuStrip menu = new ContextMenuStrip();
            miStatus = new ToolStripMenuItem();
            miStatus.Enabled = false;
            miCheck = new ToolStripMenuItem("", null, delegate { RunCheck(true); });
            miPause = new ToolStripMenuItem("", null, delegate { TogglePause(); });
            miDnd = new ToolStripMenuItem();
            miSettings = new ToolStripMenuItem("", null, delegate { OpenSettings(); });
            miLog = new ToolStripMenuItem("", null, delegate { OpenLog(); });
            miExit = new ToolStripMenuItem("", null, delegate { ExitApp(); });
            menu.Items.AddRange(new ToolStripItem[] {
                miStatus, new ToolStripSeparator(), miCheck, miPause, miDnd,
                new ToolStripSeparator(), miSettings, miLog,
                new ToolStripSeparator(), miExit });
            BuildDndSubmenu();
            RelocalizeMenu();

            curStatus = colGray; curQuality = -1; curOffline = false;
            ApplyIcon(true);
            tray.Text = "IPGuard";
            tray.Visible = true;
            tray.ContextMenuStrip = menu;
            menu.Opening += delegate { UpdateDndMenu(); };
            tray.DoubleClick += delegate { OpenSettings(); };
            tray.BalloonTipClicked += delegate { OpenSettings(); };

            AlertForm.OnOpenSettings = OpenSettings;

            timer.Interval = cfg.IntervalSeconds * 1000;
            timer.Tick += delegate { RunCheck(false); };
            timer.Start();

            // 网络变化即触发检测：切 VPN / 网卡 / 地址变动会触发，防抖 1.5 秒后查一次
            // （合并连续抖动、让新出口稳定）。平时仍按原间隔低频轮询，不额外刷请求。
            netDebounce.Interval = 1500;
            netDebounce.Tick += delegate
            {
                netDebounce.Stop();
                if (!paused)
                {
                    lastSig = "";            // 触发源变化后允许立即重新报警
                    fastRetriesLeft = 3;     // 事件后若离线，快速重试几次
                    eventTriggeredCheck = true;
                    Util.Log(pendingKickWhy + "，触发检测");
                    RunCheck(false);
                }
            };

            // C：事件后快速重试（网络还没就绪导致离线时，退避重查，不等下一个轮询）
            retryTimer.Interval = 3000;
            retryTimer.Tick += delegate
            {
                retryTimer.Stop();
                if (!paused && fastRetriesLeft > 0) { eventTriggeredCheck = true; RunCheck(false); }
            };

            // B′：本机出口探测——每 4 秒本地算一次“访问公网会用哪个源 IP”（不发包、不碰限流），
            // 变了就触发完整检测。抓双网卡路由切换、以及不少 VPN 静默失效（本地源 IP 会变）。
            lastEgressSrc = EgressSourceIp();
            probeTimer.Interval = 4000;
            probeTimer.Tick += delegate
            {
                if (paused) return;
                string cur = EgressSourceIp();
                if (cur != lastEgressSrc)
                {
                    string prev = lastEgressSrc;
                    lastEgressSrc = cur;
                    KickCheck("出口变化(" + (prev.Length == 0 ? "无" : prev) + "→" + (cur.Length == 0 ? "无" : cur) + ")");
                }
            };
            probeTimer.Start();

            try { NetworkChange.NetworkAddressChanged += OnNetAddrChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged += OnNetAvailChanged; } catch { }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { }
            try { Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch; } catch { }

            Util.Log("程序启动，检测间隔 " + cfg.IntervalSeconds + " 秒");
            RunCheck(false);   // 启动即检测一次
        }

        // 访问公网时本机会选用的源 IP（UDP connect 只查路由表、不发任何数据包）。
        static string EgressSourceIp()
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 65530);
                    IPEndPoint ep = s.LocalEndPoint as IPEndPoint;
                    return ep != null ? ep.Address.ToString() : "";
                }
            }
            catch { return ""; }   // 完全离线等情况
        }

        // 网络变化 / 唤醒等事件（可能在任意线程触发）→ 封送到 UI 线程重启防抖计时器。
        // 唤醒后网卡会陆续恢复、不断刷新防抖，等网络稳定后只查一次。
        // 严格模式：事件一来先关软件（最早时点），再进入防抖去查。
        void KickCheck(string why)
        {
            try
            {
                sync.BeginInvoke((MethodInvoker)delegate
                {
                    pendingKickWhy = why;
                    if (cfg.StrictPreKill) EnforceKill();   // 严格模式：先关，后查
                    netDebounce.Stop(); netDebounce.Start();
                });
            }
            catch { }
        }

        string pendingKickWhy = "网络变化";
        void OnNetAddrChanged(object sender, EventArgs e) { KickCheck("网络变化"); }
        void OnNetAvailChanged(object sender, NetworkAvailabilityEventArgs e) { KickCheck(e.IsAvailable ? "网络恢复" : "网络断开"); }
        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) KickCheck("睡眠唤醒");
        }
        void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock
                || e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect
                || e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
                KickCheck("会话切换");
        }

        void RelocalizeMenu()
        {
            miCheck.Text = I18n.T("立即检测", "Check now");
            miPause.Text = paused ? I18n.T("恢复检测", "Resume") : I18n.T("暂停检测", "Pause");
            miSettings.Text = I18n.T("设置…", "Settings…");
            miLog.Text = I18n.T("查看日志", "View log");
            miExit.Text = I18n.T("退出", "Exit");
            if (miStatus.Text.Length == 0) miStatus.Text = I18n.T("尚未检测", "Not checked yet");
            BuildDndSubmenu();
            UpdateDndMenu();
        }

        // ---------------- 检测 ----------------

        void RunCheck(bool manual)
        {
            if (busy) return;
            if (paused && !manual) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                GeoLocation loc = null;
                string err = null;
                Stopwatch sw = Stopwatch.StartNew();
                try { loc = Geo.GetLocation(); }
                catch (Exception ex) { err = ex.Message; }
                sw.Stop();
                // 网络质量：用这次定位请求的耗时算，不额外 ping 任何主机。
                int q = NetQuality.Measure(sw.ElapsedMilliseconds, loc != null);

                GeoLocation l = loc;
                string e = err;
                int qq = q;
                try
                {
                    sync.BeginInvoke((MethodInvoker)delegate { OnResult(l, e, manual, qq); });
                }
                catch { busy = false; }
            });
        }

        void OnResult(GeoLocation loc, string err, bool manual, int quality)
        {
            curQuality = quality;
            busy = false;
            lastCheck = DateTime.Now;
            // 每天清理一次超期日志。
            if (DateTime.Now.Date != lastPruneDate)
            {
                lastPruneDate = DateTime.Now.Date;
                Util.PruneLogByAge();
            }
            // 处理完这次结果后，若设置窗口没开着（空闲态），回收工作集省内存。
            // 加最少 25 秒的间隔闸：短检测周期下不反复 GC/回收，避免换页抖动。
            if ((settings == null || settings.IsDisposed)
                && (DateTime.Now - lastTrim).TotalSeconds >= 25)
            {
                lastTrim = DateTime.Now;
                sync.BeginInvoke((MethodInvoker)delegate { NativeMethods.TrimWorkingSet(); });
            }

            if (loc == null)
            {
                lastError = err;
                last = null;
                StopBlink();
                // 检测失败通常意味着网络不通：灰点 + 灰环（信号 0）。断网不算 abnormal，不关软件。
                curStatus = paused ? colYellow : colGray;
                curOffline = true;
                ApplyIcon(true);
                SetTip(I18n.T("检测失败：", "Check failed: ") + err);
                miStatus.Text = I18n.T("检测失败（", "Check failed (") + DateTime.Now.ToString("HH:mm:ss") + "）";
                Util.Log("检测失败: " + err);
                // C：事件触发后若因网络未就绪而离线，快速退避重试，不等下一个轮询。
                if (eventTriggeredCheck && fastRetriesLeft > 0 && !paused)
                {
                    fastRetriesLeft--;
                    retryTimer.Stop(); retryTimer.Start();
                }
                else eventTriggeredCheck = false;
                if (manual) MessageBox.Show(I18n.T("获取位置失败：\r\n", "Failed to get location:\r\n") + err, "IPGuard",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            lastError = "";
            last = loc;
            curOffline = false;
            fastRetriesLeft = 0;            // 已拿到结果，停止快速重试
            eventTriggeredCheck = false;
            lastEgressSrc = EgressSourceIp();   // 对齐探测基线，避免这次变化又被探测重复触发
            List<string> violations = Evaluate(loc);
            string disp = loc.Display();

            // 详细日志（默认关）：每次检测都记一行，便于排查检测节奏/恢复延迟。
            // 关闭后只记有意义的事件（异常、恢复、失败、配置变更等），日志几乎不增长。
            if (cfg.DiagLog)
                Util.Log("check: " + disp + "  ip=" + loc.Ip
                         + "  " + (violations.Count > 0 ? "ABNORMAL" : "ok")
                         + "  q=" + quality + " (" + NetQuality.LastMs + "ms)"
                         + "  intv=" + (timer.Interval / 1000) + "s");

            miStatus.Text = disp + "  ·  " + DateTime.Now.ToString("HH:mm:ss");
            SetTip(disp + "\r\nIP " + loc.Ip + "\r\n" + NetTip());

            if (settings != null && !settings.IsDisposed) settings.ShowCurrent(loc, violations);

            ApplyInterval(violations.Count > 0);

            if (violations.Count == 0)
            {
                StopBlink();
                AlertForm.Dismiss();          // 回到正常区域，立刻收起报警窗
                curStatus = paused ? colYellow : colGreen;
                ApplyIcon(true);
                if (lastSig.Length > 0) Util.Log("恢复正常：" + disp);
                lastSig = "";
                if (manual) MessageBox.Show(
                    I18n.T("当前区域符合设定：\r\n\r\n", "Current region matches your settings:\r\n\r\n") + disp + "\r\nIP: " + loc.Ip,
                    "IPGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string sig = string.Join(" ;; ", violations.ToArray());
            bool changed = sig != lastSig;
            if (changed) Util.Log("区域异常：" + sig + "  (IP " + loc.Ip + ")");
            lastSig = sig;
            curStatus = colRed;

            // 处于异常区域：关闭指定程序（含其插件/子进程）。独立于免打扰，每次检测都执行一遍
            // （重开的下次也会被关）。放后台线程做，避免正常关闭的 2 秒等待卡住界面。
            EnforceKill();

            // 免打扰期间：图标只变红、不闪烁、不弹窗/不出声。菜单里显示剩余时间。
            if (InDnd())
            {
                StopBlink();
                ApplyIcon(true);
                UpdateDndMenu();
                return;
            }

            // 正常异常态：红色每秒闪烁（中心点闪，信号环保留）。
            StartBlink();

            // 检测到异常即报警（不再有重复提醒频率的概念）。
            string msg = string.Join("\r\n\r\n", violations.ToArray())
                         + "\r\n\r\nIP: " + loc.Ip + I18n.T("   (来源 ", "   (source ") + loc.Source + ")";
            if (cfg.Popup) AlertForm.Popup(msg, cfg.Sound);
            else
            {
                try
                {
                    tray.ShowBalloonTip(10000, I18n.T("IP 区域异常", "IP Region Alert"), Util.Cut(msg, 200), ToolTipIcon.Warning);
                    if (cfg.Sound) System.Media.SystemSounds.Exclamation.Play();
                }
                catch { }
            }
        }

        // 根据当前状态（curStatus/curQuality/curOffline）构建并设置托盘图标。
        // dotOn=false 用于闪烁的“灭”帧（隐藏中心点，信号环保留）。
        void ApplyIcon(bool dotOn)
        {
            Icon ico = Util.RingIcon(curStatus, curQuality, curOffline, dotOn);
            tray.Icon = ico;
            if (curIcon != null) curIcon.Dispose();
            curIcon = ico;
        }

        void ApplyLogConfig()
        {
            Util.LogRetentionDays = cfg.LogRetentionDays;
            Util.LogMaxBytes = cfg.LogMaxKB * 1024;
        }

        int killBusy;   // 0/1，避免同一轮重入
        void EnforceKill()
        {
            if (!cfg.KillApps || cfg.KillTargets == null || cfg.KillTargets.Count == 0) return;
            if (Interlocked.CompareExchange(ref killBusy, 1, 0) != 0) return;
            List<string> targets = new List<string>(cfg.KillTargets);
            bool force = cfg.KillForce;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0;
                try { n = ProcKiller.KillTargets(targets, force); }
                catch (Exception ex) { Util.Log("关闭程序出错: " + ex.Message); }
                if (n > 0) Util.Log("已关闭程序进程 " + n + " 个（含子进程）");
                Interlocked.Exchange(ref killBusy, 0);
            });
        }

        void StartBlink()
        {
            if (!blinkTimer.Enabled) { blinkOn = true; ApplyIcon(true); blinkTimer.Start(); }
        }

        void StopBlink()
        {
            if (blinkTimer.Enabled) blinkTimer.Stop();
        }

        // 托盘悬停里显示的网络一行。
        string NetTip()
        {
            if (curOffline || curQuality < 0) return I18n.T("网络：不可用", "Network: n/a");
            string ms = NetQuality.LastMs >= 0 ? "  " + NetQuality.LastMs + "ms" : "";
            return I18n.T("网络：信号 ", "Network: ") + curQuality + "%" + ms;
        }

        // ---------------- 免打扰 (Do Not Disturb) ----------------

        bool InDnd() { return dndUntil > DateTime.Now; }

        static string DurLabel(int minutes)
        {
            int h = minutes / 60, m = minutes % 60;
            if (I18n.En)
            {
                if (h == 0) return m + " min";
                if (m == 0) return h + (h == 1 ? " hour" : " hours");
                return h + "h " + m + "m";
            }
            if (h == 0) return m + " 分钟";
            if (m == 0) return h + " 小时";
            return h + " 小时 " + m + " 分";
        }

        void BuildDndSubmenu()
        {
            miDnd.DropDownItems.Clear();
            int[] mins = { 10, 30, 60, 120, 240, 480 };
            foreach (int m in mins)
            {
                int mm = m;
                miDnd.DropDownItems.Add(DurLabel(mm), null, delegate { StartDnd(mm); });
            }
            miDnd.DropDownItems.Add(I18n.T("自定义…", "Custom…"), null, delegate { CustomDnd(); });
            miDnd.DropDownItems.Add(new ToolStripSeparator());
            miDnd.DropDownItems.Add(I18n.T("取消免打扰", "Turn off DND"), null, delegate { CancelDnd(); });
        }

        void UpdateDndMenu()
        {
            if (miDnd == null) return;
            if (InDnd())
            {
                int left = (int)Math.Ceiling((dndUntil - DateTime.Now).TotalMinutes);
                miDnd.Text = I18n.T("免打扰中（剩 ", "Do Not Disturb (") + left + I18n.T(" 分）", " min left)");
                miDnd.Checked = true;
            }
            else
            {
                miDnd.Text = I18n.T("免打扰…", "Do Not Disturb…");
                miDnd.Checked = false;
            }
        }

        void StartDnd(int minutes)
        {
            if (minutes < 10) minutes = 10;
            dndUntil = DateTime.Now.AddMinutes(minutes);
            AlertForm.Kill();                 // 关掉当前可能开着的报警窗
            if (blinkTimer.Enabled)           // 若正在闪烁，立刻改为只红不闪
            {
                StopBlink();
                curStatus = colRed;
                ApplyIcon(true);
            }
            UpdateDndMenu();
            Util.Log("开启免打扰 " + minutes + " 分钟，至 " + dndUntil.ToString("HH:mm"));
        }

        void CancelDnd()
        {
            if (!InDnd()) return;
            dndUntil = DateTime.MinValue;
            UpdateDndMenu();
            Util.Log("取消免打扰");
            RunCheck(false);                  // 立即复查，必要时马上重新提醒
        }

        void CustomDnd()
        {
            int m = DndDialog.Ask();
            if (m > 0) StartDnd(m);
        }

        // 根据是否处于异常区域切换检测频率：异常时用更快的间隔以便快速重复提醒。
        void ApplyInterval(bool abnormal)
        {
            if (paused) return;
            int sec = (abnormal && cfg.AbnormalIntervalSeconds > 0)
                      ? cfg.AbnormalIntervalSeconds : cfg.IntervalSeconds;
            if (sec < 5) sec = 5;
            int ms = sec * 1000;
            if (timer.Interval != ms)
            {
                timer.Interval = ms;
                timer.Stop();
                timer.Start();   // 立刻按新间隔重新计时
            }
        }

        List<string> Evaluate(GeoLocation loc)
        {
            List<string> v = new List<string>();
            if (cfg.Forbidden.Count > 0)
            {
                RegionRule hit = cfg.Forbidden.FirstOrDefault(r => r.Matches(loc));
                if (hit != null)
                    v.Add(I18n.T("⚠ 你当前处于【禁止区域】\r\n当前位置：", "⚠ You are in a FORBIDDEN region\r\nCurrent: ")
                          + loc.Display() + I18n.T("\r\n命中规则：", "\r\nMatched rule: ") + hit.ToString());
            }
            if (cfg.Allowed.Count > 0)
            {
                if (!cfg.Allowed.Any(r => r.Matches(loc)))
                    v.Add(I18n.T("⚠ 你当前【不在允许区域】内\r\n当前位置：", "⚠ You are NOT in any allowed region\r\nCurrent: ")
                          + loc.Display());
            }
            return v;
        }

        void SetTip(string s)
        {
            // NotifyIcon.Text 上限 63 字符
            tray.Text = Util.Cut("IPGuard\r\n" + s, 62);
        }

        // ---------------- 菜单动作 ----------------

        void TogglePause()
        {
            paused = !paused;
            miPause.Text = paused ? I18n.T("恢复检测", "Resume") : I18n.T("暂停检测", "Pause");
            if (paused)
            {
                timer.Stop();
                StopBlink();
                curStatus = colYellow;
                ApplyIcon(true);
                SetTip(I18n.T("已暂停检测", "Paused"));
                miStatus.Text = I18n.T("已暂停", "Paused");
                Util.Log("已暂停检测");
            }
            else
            {
                timer.Start();
                Util.Log("已恢复检测");
                RunCheck(false);
            }
        }

        public void OpenSettings()
        {
            if (settings != null && !settings.IsDisposed)
            {
                settings.WindowState = FormWindowState.Normal;
                settings.Activate();
                return;
            }
            settings = new SettingsForm(cfg.Clone(), last);
            settings.RequestCheckNow = delegate { RunCheck(false); };
            if (settings.ShowDialog() == DialogResult.OK)
            {
                bool langChanged = !string.Equals(cfg.Lang, settings.Result.Lang, StringComparison.OrdinalIgnoreCase);
                cfg = settings.Result;
                cfg.Save();
                I18n.Set(cfg.Lang);
                ApplyLogConfig();
                if (langChanged) { RelocalizeMenu(); AlertForm.Kill(); }  // 让托盘菜单与弹窗按新语言重建
                ApplyAutoStart(cfg.StartWithWindows);
                timer.Stop();
                timer.Interval = cfg.IntervalSeconds * 1000;
                if (!paused) timer.Start();
                lastSig = "";
                lastAlert = DateTime.MinValue;
                Util.Log("配置已更新：间隔 " + cfg.IntervalSeconds + " 秒，禁止 "
                         + cfg.Forbidden.Count + " 条，允许 " + cfg.Allowed.Count + " 条");
                RunCheck(false);
            }
            settings.Dispose();
            settings = null;
        }

        void OpenLog()
        {
            string f = Util.PathOf("ip_guard.log");
            try
            {
                if (!File.Exists(f)) File.WriteAllText(f, "", Encoding.UTF8);
                System.Diagnostics.Process.Start("notepad.exe", f);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("打不开日志：", "Cannot open log: ") + ex.Message, "IPGuard");
            }
        }

        public static void ApplyAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (k.GetValue("IpRegionGuard") != null) k.DeleteValue("IpRegionGuard", false); // 清理旧名
                    if (on) k.SetValue("IPGuard", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("IPGuard") != null) k.DeleteValue("IPGuard", false);
                }
            }
            catch (Exception ex) { Util.Log("设置开机自启失败: " + ex.Message); }
        }

        public static bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return k != null && (k.GetValue("IPGuard") != null || k.GetValue("IpRegionGuard") != null);
                }
            }
            catch { return false; }
        }

        void ExitApp()
        {
            Util.Log("程序退出");
            try { NetworkChange.NetworkAddressChanged -= OnNetAddrChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged -= OnNetAvailChanged; } catch { }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            netDebounce.Stop();
            probeTimer.Stop();
            retryTimer.Stop();
            timer.Stop();
            blinkTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (curIcon != null) curIcon.Dispose();
            AlertForm.Kill();
            if (settings != null && !settings.IsDisposed) settings.Dispose();
            sync.Dispose();
            ExitThread();
        }
    }

    // ================================================================ 设置窗口

    class SettingsForm : Form
    {
        public AppConfig Result;
        public Action RequestCheckNow;

        readonly AppConfig cfg;
        GeoLocation current;

        Label lblCur, lblVerdict;
        ComboBox cboCountry, cboState, cboCity, cboLang;
        ListBox lstF, lstA;
        NumericUpDown numInterval, numAbnormal;
        CheckBox chkPopup, chkSound, chkAuto;
        Button btnAddF, btnAddA, btnCurF, btnCurA;

        string[] countries = new string[0];
        int stateToken, cityToken;

        public SettingsForm(AppConfig c, GeoLocation cur)
        {
            cfg = c;
            current = cur;
            Build();
            LoadFromConfig();
            // 关键：不要在构造函数里就起后台线程 BeginInvoke —— 那时窗口句柄还没创建，
            // 后台线程抢先回调会抛 InvalidOperationException 导致闪退。等 Load 之后再启动。
            Shown += delegate { OnShownOnce(); };
        }

        bool started;
        void OnShownOnce()
        {
            if (started) return;
            started = true;
            if (current != null) ShowCurrent(current, null);
            ThreadPool.QueueUserWorkItem(delegate { LoadCountries(); });
        }

        // 安全的跨线程封送：句柄没创建 / 已释放时直接跳过，绝不抛异常。
        void SafeBegin(MethodInvoker mi)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(mi); }
            catch { }
        }

        // ---------------- 界面 ----------------

        void Build()
        {
            Text = I18n.T("IPGuard — 设置", "IPGuard — Settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(780, 656);
            Font = new Font("Microsoft YaHei UI", 9f);
            ShowInTaskbar = true;

            // --- 当前位置 ---
            GroupBox gCur = new GroupBox();
            gCur.Text = I18n.T("当前位置", "Current location");
            gCur.Bounds = new Rectangle(12, 8, 756, 92);
            Controls.Add(gCur);

            lblCur = new Label();
            lblCur.Bounds = new Rectangle(14, 24, 520, 22);
            lblCur.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            lblCur.Text = I18n.T("检测中…", "Checking…");
            gCur.Controls.Add(lblCur);

            lblVerdict = new Label();
            lblVerdict.Bounds = new Rectangle(14, 48, 520, 34);
            lblVerdict.ForeColor = Color.DimGray;
            gCur.Controls.Add(lblVerdict);

            Button btnRefresh = new Button();
            btnRefresh.Text = I18n.T("刷新", "Refresh");
            btnRefresh.Bounds = new Rectangle(548, 22, 90, 28);
            btnRefresh.Click += delegate { Refresh_Click(); };
            gCur.Controls.Add(btnRefresh);

            btnCurF = new Button();
            btnCurF.Text = I18n.T("当前→禁止", "Now→Forbid");
            btnCurF.Bounds = new Rectangle(548, 54, 90, 28);
            btnCurF.Click += delegate { AddCurrent(true); };
            gCur.Controls.Add(btnCurF);

            btnCurA = new Button();
            btnCurA.Text = I18n.T("当前→允许", "Now→Allow");
            btnCurA.Bounds = new Rectangle(646, 54, 96, 28);
            btnCurA.Click += delegate { AddCurrent(false); };
            gCur.Controls.Add(btnCurA);

            // --- 选择区域 ---
            GroupBox gPick = new GroupBox();
            gPick.Text = I18n.T("选择区域（下拉，可精确到城市；省/州、城市留空即代表整个上一级）",
                                "Pick a region (dropdowns, down to city; leave state/city blank = the whole parent)");
            gPick.Bounds = new Rectangle(12, 108, 756, 116);
            Controls.Add(gPick);

            gPick.Controls.Add(Lab(I18n.T("国家/地区", "Country"), 14, 30));
            cboCountry = Combo(90, 26, 260);
            cboCountry.SelectedIndexChanged += delegate { OnCountryChanged(); };
            gPick.Controls.Add(cboCountry);

            gPick.Controls.Add(Lab(I18n.T("省/州", "State"), 366, 30));
            cboState = Combo(416, 26, 150);
            cboState.SelectedIndexChanged += delegate { OnStateChanged(); };
            gPick.Controls.Add(cboState);

            gPick.Controls.Add(Lab(I18n.T("城市", "City"), 578, 30));
            cboCity = Combo(620, 26, 122);
            gPick.Controls.Add(cboCity);

            btnAddF = new Button();
            btnAddF.Text = I18n.T("↓ 加入禁止列表", "↓ Add to Forbidden");
            btnAddF.Bounds = new Rectangle(90, 70, 160, 30);
            btnAddF.Click += delegate { AddPicked(true); };
            gPick.Controls.Add(btnAddF);

            btnAddA = new Button();
            btnAddA.Text = I18n.T("↓ 加入允许列表", "↓ Add to Allowed");
            btnAddA.Bounds = new Rectangle(266, 70, 160, 30);
            btnAddA.Click += delegate { AddPicked(false); };
            gPick.Controls.Add(btnAddA);

            Label hint = new Label();
            hint.Text = I18n.T("提示：列表为空 = 忽略该条件", "Tip: an empty list = ignore that condition");
            hint.ForeColor = Color.DimGray;
            hint.Bounds = new Rectangle(444, 76, 300, 20);
            gPick.Controls.Add(hint);

            // --- 两个列表 ---
            GroupBox gF = new GroupBox();
            gF.Text = I18n.T("禁止区域（处于其中 → 报警）", "Forbidden regions (in one → alert)");
            gF.Bounds = new Rectangle(12, 232, 374, 250);
            Controls.Add(gF);
            lstF = new ListBox();
            lstF.Bounds = new Rectangle(12, 24, 350, 180);
            lstF.HorizontalScrollbar = true;
            gF.Controls.Add(lstF);
            Button dF = new Button();
            dF.Text = I18n.T("删除选中", "Remove");
            dF.Bounds = new Rectangle(12, 210, 100, 28);
            dF.Click += delegate { Remove(lstF, cfg.Forbidden); };
            gF.Controls.Add(dF);
            Button cF = new Button();
            cF.Text = I18n.T("清空", "Clear");
            cF.Bounds = new Rectangle(120, 210, 80, 28);
            cF.Click += delegate { cfg.Forbidden.Clear(); Rebind(); };
            gF.Controls.Add(cF);

            GroupBox gA = new GroupBox();
            gA.Text = I18n.T("允许区域（不在其中 → 报警）", "Allowed regions (in none → alert)");
            gA.Bounds = new Rectangle(394, 232, 374, 250);
            Controls.Add(gA);
            lstA = new ListBox();
            lstA.Bounds = new Rectangle(12, 24, 350, 180);
            lstA.HorizontalScrollbar = true;
            gA.Controls.Add(lstA);
            Button dA = new Button();
            dA.Text = I18n.T("删除选中", "Remove");
            dA.Bounds = new Rectangle(12, 210, 100, 28);
            dA.Click += delegate { Remove(lstA, cfg.Allowed); };
            gA.Controls.Add(dA);
            Button cA = new Button();
            cA.Text = I18n.T("清空", "Clear");
            cA.Bounds = new Rectangle(120, 210, 80, 28);
            cA.Click += delegate { cfg.Allowed.Clear(); Rebind(); };
            gA.Controls.Add(cA);

            // --- 其它设置 ---
            GroupBox gOpt = new GroupBox();
            gOpt.Text = I18n.T("检测与提醒", "Checking & alerts");
            gOpt.Bounds = new Rectangle(12, 490, 756, 100);
            Controls.Add(gOpt);

            // 第 1 行：正常间隔 + 异常间隔（均以秒为单位）
            gOpt.Controls.Add(Lab(I18n.T("正常检测间隔", "Normal interval"), 14, 28));
            numInterval = new NumericUpDown();
            numInterval.Bounds = new Rectangle(I18n.En ? 122 : 104, 24, 66, 26);
            numInterval.Minimum = 5;
            numInterval.Maximum = 86400;
            gOpt.Controls.Add(numInterval);
            gOpt.Controls.Add(Lab(I18n.T("秒", "sec"), I18n.En ? 192 : 178, 28));

            gOpt.Controls.Add(Lab(I18n.T("异常区域时改为每", "When in an unwanted region, every"), 224, 28));
            numAbnormal = new NumericUpDown();
            numAbnormal.Bounds = new Rectangle(I18n.En ? 448 : 350, 24, 66, 26);
            numAbnormal.Minimum = 0;
            numAbnormal.Maximum = 86400;
            gOpt.Controls.Add(numAbnormal);
            gOpt.Controls.Add(Lab(I18n.T("秒检测（0 = 与正常相同）", "sec (0 = same as normal)"), I18n.En ? 518 : 420, 28));

            // 第 2 行：开关 + 语言
            chkPopup = new CheckBox();
            chkPopup.Text = I18n.T("弹窗提醒", "Popup");
            chkPopup.Bounds = new Rectangle(14, 62, I18n.En ? 74 : 90, 24);
            gOpt.Controls.Add(chkPopup);

            chkSound = new CheckBox();
            chkSound.Text = I18n.T("提示音", "Sound");
            chkSound.Bounds = new Rectangle(I18n.En ? 96 : 110, 62, 72, 24);
            gOpt.Controls.Add(chkSound);

            chkAuto = new CheckBox();
            chkAuto.Text = I18n.T("开机自动启动", "Start with Windows");
            chkAuto.Bounds = new Rectangle(I18n.En ? 174 : 196, 62, I18n.En ? 150 : 130, 24);
            gOpt.Controls.Add(chkAuto);

            gOpt.Controls.Add(Lab(I18n.T("语言", "Language"), I18n.En ? 340 : 340, 64));
            cboLang = new ComboBox();
            cboLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cboLang.Bounds = new Rectangle(I18n.En ? 414 : 384, 60, 110, 26);
            cboLang.Items.Add("English");
            cboLang.Items.Add("中文");
            gOpt.Controls.Add(cboLang);

            gOpt.Controls.Add(Lab(I18n.T("异常即报警（免打扰见托盘右键）",
                                        "Alerts on every abnormal check"),
                                  I18n.En ? 542 : 512, 64));

            // 异常时关闭程序（打开独立管理对话框，直接编辑本克隆配置）
            Button btnKill = new Button();
            btnKill.Text = I18n.T("异常时关闭程序…", "Kill apps when abnormal…");
            btnKill.Bounds = new Rectangle(12, 602, 220, 30);
            btnKill.Click += delegate { using (KillAppsDialog d = new KillAppsDialog(cfg)) d.ShowDialog(this); };
            Controls.Add(btnKill);

            // --- 确定 / 取消 ---
            Button ok = new Button();
            ok.Text = I18n.T("确定", "OK");
            ok.Bounds = new Rectangle(572, 606, 92, 30);
            ok.Click += delegate { Commit(); };
            Controls.Add(ok);
            AcceptButton = ok;

            Button cancel = new Button();
            cancel.Text = I18n.T("取消", "Cancel");
            cancel.Bounds = new Rectangle(676, 606, 92, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            Label langNote = new Label();
            langNote.Text = I18n.T("切换语言后点“确定”生效", "Language applies after you click OK");
            langNote.ForeColor = Color.DimGray;
            langNote.Bounds = new Rectangle(244, 612, 320, 20);
            Controls.Add(langNote);
        }

        static Label Lab(string t, int x, int y)
        {
            Label l = new Label();
            l.Text = t;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            return l;
        }

        static ComboBox Combo(int x, int y, int w)
        {
            ComboBox c = new ComboBox();
            c.Bounds = new Rectangle(x, y, w, 26);
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.MaxDropDownItems = 18;
            c.Enabled = false;
            return c;
        }

        // ---------------- 数据加载 ----------------

        void LoadCountries()
        {
            string[] list;
            try { list = Geo.GetCountries(); }
            catch (Exception ex)
            {
                SafeBegin(delegate
                {
                    cboCountry.Items.Clear();
                    cboCountry.Items.Add(I18n.T("（国家列表加载失败：", "(failed to load countries: ") + ex.Message + "）");
                    cboCountry.SelectedIndex = 0;
                });
                return;
            }
            SafeBegin(delegate
            {
                countries = list;
                cboCountry.BeginUpdate();
                cboCountry.Items.Clear();
                foreach (string s in list)
                {
                    string[] p = s.Split('|');
                    cboCountry.Items.Add(p[0] + (p.Length > 1 && p[1].Length > 0 ? "  (" + p[1] + ")" : ""));
                }
                cboCountry.EndUpdate();
                cboCountry.Enabled = true;
                if (current != null) SelectCountry(current.Country);
            });
        }

        void SelectCountry(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            for (int i = 0; i < countries.Length; i++)
            {
                if (Util.SameRegion(countries[i].Split('|')[0], name)) { cboCountry.SelectedIndex = i; return; }
            }
        }

        string PickedCountryName()
        {
            int i = cboCountry.SelectedIndex;
            if (i < 0 || i >= countries.Length) return "";
            return countries[i].Split('|')[0];
        }

        string PickedCountryIso()
        {
            int i = cboCountry.SelectedIndex;
            if (i < 0 || i >= countries.Length) return "";
            string[] p = countries[i].Split('|');
            return p.Length > 1 ? p[1] : "";
        }

        void OnCountryChanged()
        {
            string country = PickedCountryName();
            cboCity.Items.Clear();
            cboCity.Enabled = false;
            cboState.Items.Clear();
            cboState.Enabled = false;
            if (country.Length == 0) return;

            cboState.Items.Add(I18n.T("（加载中…）", "(loading…)"));
            cboState.SelectedIndex = 0;
            int token = ++stateToken;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string[] st;
                try { st = Geo.GetStates(country); }
                catch { st = new string[0]; }
                string[] r = st;
                try
                {
                    SafeBegin(delegate
                    {
                        if (token != stateToken) return;
                        cboState.BeginUpdate();
                        cboState.Items.Clear();
                        cboState.Items.Add(I18n.T("（全部 · 整个国家/地区）", "(All · whole country)"));
                        foreach (string s in r) cboState.Items.Add(s);
                        cboState.EndUpdate();
                        cboState.Enabled = true;
                        cboState.SelectedIndex = 0;
                        if (current != null && Util.SameRegion(current.Country, country))
                            SelectItem(cboState, current.Region);
                    });
                }
                catch { }
            });
        }

        void OnStateChanged()
        {
            cboCity.Items.Clear();
            cboCity.Enabled = false;
            if (cboState.SelectedIndex <= 0) return;
            string country = PickedCountryName();
            string state = cboState.SelectedItem.ToString();
            if (country.Length == 0) return;

            cboCity.Items.Add(I18n.T("（加载中…）", "(loading…)"));
            cboCity.SelectedIndex = 0;
            int token = ++cityToken;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string[] ct;
                try { ct = Geo.GetCities(country, state); }
                catch { ct = new string[0]; }
                string[] r = ct;
                try
                {
                    SafeBegin(delegate
                    {
                        if (token != cityToken) return;
                        cboCity.BeginUpdate();
                        cboCity.Items.Clear();
                        cboCity.Items.Add(I18n.T("（全部 · 整个省/州）", "(All · whole state)"));
                        foreach (string s in r) cboCity.Items.Add(s);
                        cboCity.EndUpdate();
                        cboCity.Enabled = true;
                        cboCity.SelectedIndex = 0;
                        if (current != null && Util.SameRegion(current.Region, state))
                            SelectItem(cboCity, current.City);
                    });
                }
                catch { }
            });
        }

        static void SelectItem(ComboBox c, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 1; i < c.Items.Count; i++)
                if (Util.SameRegion(c.Items[i].ToString(), value)) { c.SelectedIndex = i; return; }
        }

        // ---------------- 增删 ----------------

        void AddPicked(bool forbidden)
        {
            string country = PickedCountryName();
            if (country.Length == 0)
            {
                MessageBox.Show(I18n.T("请先选择国家/地区。", "Please pick a country first."), "IPGuard");
                return;
            }
            RegionRule r = new RegionRule();
            r.Country = country;
            r.CountryCode = PickedCountryIso();
            r.State = (cboState.Enabled && cboState.SelectedIndex > 0) ? cboState.SelectedItem.ToString() : "";
            r.City = (cboCity.Enabled && cboCity.SelectedIndex > 0) ? cboCity.SelectedItem.ToString() : "";
            Add(r, forbidden);
        }

        void AddCurrent(bool forbidden)
        {
            if (current == null)
            {
                MessageBox.Show(I18n.T("还没有当前位置，请先点【刷新】。", "No current location yet — click Refresh first."), "IPGuard");
                return;
            }
            RegionRule r = new RegionRule();
            r.Country = current.Country;
            r.CountryCode = current.CountryCode;
            r.State = current.Region;
            r.City = current.City;
            Add(r, forbidden);
        }

        void Add(RegionRule r, bool forbidden)
        {
            List<RegionRule> list = forbidden ? cfg.Forbidden : cfg.Allowed;
            string k = r.Key();
            if (list.Any(x => x.Key() == k))
            {
                MessageBox.Show(I18n.T("这条区域已经在列表里了。", "This region is already in the list."), "IPGuard");
                return;
            }
            list.Add(r);
            Rebind();
        }

        void Remove(ListBox lb, List<RegionRule> list)
        {
            int i = lb.SelectedIndex;
            if (i < 0 || i >= list.Count) return;
            list.RemoveAt(i);
            Rebind();
        }

        void Rebind()
        {
            lstF.BeginUpdate();
            lstF.Items.Clear();
            foreach (RegionRule r in cfg.Forbidden) lstF.Items.Add(r.ToString());
            lstF.EndUpdate();

            lstA.BeginUpdate();
            lstA.Items.Clear();
            foreach (RegionRule r in cfg.Allowed) lstA.Items.Add(r.ToString());
            lstA.EndUpdate();

            UpdateVerdict();
        }

        // ---------------- 当前位置显示 ----------------

        void Refresh_Click()
        {
            lblCur.Text = "检测中…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                GeoLocation g = null;
                string err = null;
                try { g = Geo.GetLocation(); }
                catch (Exception ex) { err = ex.Message; }
                GeoLocation gg = g;
                string ee = err;
                SafeBegin(delegate
                {
                    if (gg != null) { current = gg; ShowCurrent(gg, null); if (cboCountry.Enabled) SelectCountry(gg.Country); }
                    else lblCur.Text = I18n.T("检测失败：", "Check failed: ") + ee;
                });
            });
        }

        public void ShowCurrent(GeoLocation loc, List<string> violations)
        {
            if (InvokeRequired) { SafeBegin(delegate { ShowCurrent(loc, violations); }); return; }
            if (!IsHandleCreated) { current = loc; return; }
            current = loc;
            lblCur.Text = loc.Display() + "     IP: " + loc.Ip + I18n.T("     (来源 ", "     (source ") + loc.Source + ")";
            UpdateVerdict();
        }

        void UpdateVerdict()
        {
            if (current == null) { lblVerdict.Text = ""; return; }
            List<string> v = new List<string>();
            if (cfg.Forbidden.Count > 0 && cfg.Forbidden.Any(r => r.Matches(current))) v.Add(I18n.T("命中禁止区域", "in a forbidden region"));
            if (cfg.Allowed.Count > 0 && !cfg.Allowed.Any(r => r.Matches(current))) v.Add(I18n.T("不在允许区域内", "not in any allowed region"));
            if (v.Count == 0)
            {
                lblVerdict.ForeColor = Color.FromArgb(20, 130, 50);
                lblVerdict.Text = I18n.T("按当前设定：正常 ✓", "Under current settings: OK ✓");
            }
            else
            {
                lblVerdict.ForeColor = Color.FromArgb(200, 40, 40);
                lblVerdict.Text = I18n.T("按当前设定：会报警 —— ", "Under current settings: would alert — ") + string.Join(I18n.T("；", "; "), v.ToArray());
            }
        }

        // ---------------- 载入 / 提交 ----------------

        void LoadFromConfig()
        {
            numInterval.Value = Math.Max(5, Math.Min(86400, cfg.IntervalSeconds));
            numAbnormal.Value = Math.Max(0, Math.Min(86400, cfg.AbnormalIntervalSeconds));
            chkPopup.Checked = cfg.Popup;
            chkSound.Checked = cfg.Sound;
            chkAuto.Checked = TrayApp.AutoStartEnabled();
            cboLang.SelectedIndex = I18n.En ? 0 : 1;   // 0=English, 1=中文
            Rebind();
        }

        void Commit()
        {
            cfg.IntervalSeconds = (int)numInterval.Value;
            cfg.AbnormalIntervalSeconds = (int)numAbnormal.Value;
            cfg.Popup = chkPopup.Checked;
            cfg.Sound = chkSound.Checked;
            cfg.StartWithWindows = chkAuto.Checked;
            cfg.Lang = (cboLang.SelectedIndex == 1) ? "zh" : "en";
            cfg.Clamp();
            Result = cfg;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ================================================================ 免打扰自定义时长对话框

    class DndDialog : Form
    {
        NumericUpDown num;

        DndDialog()
        {
            Text = I18n.T("免打扰时长", "Do Not Disturb");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(320, 128);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label l = new Label();
            l.Text = I18n.T("免打扰时长（分钟，10 的倍数）：", "Duration (minutes, in steps of 10):");
            l.Bounds = new Rectangle(14, 18, 292, 20);
            Controls.Add(l);

            num = new NumericUpDown();
            num.Bounds = new Rectangle(14, 46, 120, 26);
            num.Minimum = 10; num.Maximum = 1440; num.Increment = 10; num.Value = 30;
            Controls.Add(num);

            Button ok = new Button();
            ok.Text = I18n.T("确定", "OK");
            ok.Bounds = new Rectangle(116, 90, 90, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok); AcceptButton = ok;

            Button c = new Button();
            c.Text = I18n.T("取消", "Cancel");
            c.Bounds = new Rectangle(216, 90, 90, 28);
            c.DialogResult = DialogResult.Cancel;
            Controls.Add(c); CancelButton = c;
        }

        public static int Ask()
        {
            using (DndDialog d = new DndDialog())
            {
                if (d.ShowDialog() == DialogResult.OK)
                {
                    int v = (int)d.num.Value;
                    v = (v / 10) * 10;          // 吸附到 10 的倍数
                    return v < 10 ? 10 : v;
                }
            }
            return 0;
        }
    }

    // ================================================================ 关闭程序：管理对话框

    class KillAppsDialog : Form
    {
        readonly AppConfig cfg;   // 传入的（克隆）配置；仅在本对话框 OK 时写回
        readonly List<string> work;   // 目标列表的工作副本
        CheckBox chkEnable, chkForce, chkStrict;
        ListBox lst;

        public KillAppsDialog(AppConfig c)
        {
            cfg = c;
            work = new List<string>(c.KillTargets);
            Text = I18n.T("异常时关闭程序", "Kill apps when abnormal");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 448);
            Font = new Font("Microsoft YaHei UI", 9f);

            chkEnable = new CheckBox();
            chkEnable.Text = I18n.T("启用：处于禁止/非允许区域时，关闭下列程序（含其插件/子进程）",
                                    "Enable: when in a forbidden / non-allowed region, kill the apps below (incl. their plugins/child processes)");
            chkEnable.Bounds = new Rectangle(14, 10, 540, 24);
            chkEnable.Checked = cfg.KillApps;
            Controls.Add(chkEnable);

            chkForce = new CheckBox();
            chkForce.Text = I18n.T("立即强制结束（不勾则先尝试正常关闭、2 秒后再强制）",
                                   "Force-kill immediately (unchecked: try graceful close first, then force after 2s)");
            chkForce.Bounds = new Rectangle(14, 34, 540, 24);
            chkForce.Checked = cfg.KillForce;
            Controls.Add(chkForce);

            chkStrict = new CheckBox();
            chkStrict.Text = I18n.T("严格模式：网络一变就先关这些程序，再去查 IP（最早动手，但切到正常网络也会先关）",
                                    "Strict: kill on ANY network change first, then check IP (earliest, but also kills when switching to a good network)");
            chkStrict.Bounds = new Rectangle(14, 58, 540, 24);
            chkStrict.Checked = cfg.StrictPreKill;
            Controls.Add(chkStrict);

            lst = new ListBox();
            lst.Bounds = new Rectangle(14, 90, 532, 268);
            lst.HorizontalScrollbar = true;
            Controls.Add(lst);
            Rebind();

            Button bAddRun = new Button();
            bAddRun.Text = I18n.T("从运行列表添加…", "Add from running…");
            bAddRun.Bounds = new Rectangle(14, 366, 150, 28);
            bAddRun.Click += delegate { AddFromRunning(); };
            Controls.Add(bAddRun);

            Button bBrowse = new Button();
            bBrowse.Text = I18n.T("浏览 .exe…", "Browse .exe…");
            bBrowse.Bounds = new Rectangle(172, 366, 120, 28);
            bBrowse.Click += delegate { Browse(); };
            Controls.Add(bBrowse);

            Button bDel = new Button();
            bDel.Text = I18n.T("删除选中", "Remove");
            bDel.Bounds = new Rectangle(300, 366, 100, 28);
            bDel.Click += delegate { int i = lst.SelectedIndex; if (i >= 0 && i < work.Count) { work.RemoveAt(i); Rebind(); } };
            Controls.Add(bDel);

            Button bOk = new Button();
            bOk.Text = I18n.T("确定", "OK");
            bOk.Bounds = new Rectangle(352, 410, 92, 28);
            bOk.Click += delegate
            {
                cfg.KillApps = chkEnable.Checked;
                cfg.KillForce = chkForce.Checked;
                cfg.StrictPreKill = chkStrict.Checked;
                cfg.KillTargets = new List<string>(work);
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(bOk); AcceptButton = bOk;

            Button bCancel = new Button();
            bCancel.Text = I18n.T("取消", "Cancel");
            bCancel.Bounds = new Rectangle(454, 410, 92, 28);
            bCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(bCancel); CancelButton = bCancel;
        }

        void Rebind()
        {
            lst.BeginUpdate(); lst.Items.Clear();
            foreach (string p in work)
            {
                string name; try { name = Path.GetFileName(p); } catch { name = p; }
                string friendly = Util.FriendlyName(p, Path.GetFileNameWithoutExtension(p));
                lst.Items.Add(friendly + "  (" + name + ")   —   " + p);
            }
            lst.EndUpdate();
        }

        void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (string e in work)
                if (string.Equals(e, path, StringComparison.OrdinalIgnoreCase)) return;   // 去重
            work.Add(path);
            Rebind();
        }

        void AddFromRunning()
        {
            using (RunningPicker rp = new RunningPicker())
            {
                if (rp.ShowDialog(this) == DialogResult.OK)
                    foreach (string p in rp.Selected) Add(p);
            }
        }

        void Browse()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";
                ofd.CheckFileExists = true;
                if (ofd.ShowDialog(this) == DialogResult.OK) Add(ofd.FileName);
            }
        }
    }

    // 运行中程序选择器：列出有窗口标题的用户程序，多选，返回 exe 路径（取不到则用进程名.exe）。
    class RunningPicker : Form
    {
        CheckedListBox clb;
        readonly List<string> paths = new List<string>();
        public List<string> Selected = new List<string>();

        public RunningPicker()
        {
            Text = I18n.T("选择要关闭的程序", "Pick apps to kill");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 420);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label l = new Label();
            l.Text = I18n.T("勾选要在异常区域时关闭的程序（只列出有窗口的）：",
                            "Tick the apps to kill when in a bad region (windowed apps only):");
            l.Bounds = new Rectangle(12, 10, 500, 20);
            Controls.Add(l);

            clb = new CheckedListBox();
            clb.Bounds = new Rectangle(12, 34, 496, 336);
            clb.CheckOnClick = true;
            clb.HorizontalScrollbar = true;
            Controls.Add(clb);
            Populate();

            Button bOk = new Button();
            bOk.Text = I18n.T("添加选中", "Add selected");
            bOk.Bounds = new Rectangle(312, 382, 96, 28);
            bOk.Click += delegate
            {
                foreach (int i in clb.CheckedIndices) if (i >= 0 && i < paths.Count) Selected.Add(paths[i]);
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(bOk); AcceptButton = bOk;

            Button bCancel = new Button();
            bCancel.Text = I18n.T("取消", "Cancel");
            bCancel.Bounds = new Rectangle(412, 382, 96, 28);
            bCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(bCancel); CancelButton = bCancel;
        }

        void Populate()
        {
            // 按 exe 路径去重：一个程序一行。label 用任务管理器那样的“友好名 (进程名.exe)”。
            Dictionary<string, string[]> byKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            int ownPid = 0; try { ownPid = Process.GetCurrentProcess().Id; } catch { }
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == ownPid) continue;
                    if (string.IsNullOrEmpty(p.MainWindowTitle)) continue;   // 只列有窗口的用户程序
                    string procName = p.ProcessName;
                    string path;
                    try { path = p.MainModule != null ? p.MainModule.FileName : procName + ".exe"; }
                    catch { path = procName + ".exe"; }     // 读不到全路径就用进程名
                    string key = path.ToLowerInvariant();
                    if (byKey.ContainsKey(key)) continue;
                    string label = Util.FriendlyName(path, procName) + "  (" + Path.GetFileName(path) + ")";
                    byKey[key] = new string[] { label, path };
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            List<string[]> rows = new List<string[]>(byKey.Values);
            rows.Sort(delegate(string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
            clb.BeginUpdate();
            foreach (string[] r in rows) { paths.Add(r[1]); clb.Items.Add(r[0]); }
            clb.EndUpdate();
        }
    }

    // ================================================================ 入口

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex m = new Mutex(true, "IpRegionGuard_SingleInstance_v1", out createdNew))
            {
                if (!createdNew)
                {
                    I18n.Set(AppConfig.Load().Lang);
                    MessageBox.Show(
                        I18n.T("IPGuard 已经在运行了（看右下角托盘图标）。", "IPGuard is already running (see the tray icon)."),
                        "IPGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Geo.Init();

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Util.Log("未处理异常: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Util.Log("致命异常: " + e.ExceptionObject);
                };

                Application.Run(new TrayApp());
            }
        }
    }
}
