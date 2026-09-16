using BepInEx.Configuration;
using Oracle.Data;
using Oracle.Overlay;
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Utils
{
    /// <summary>
    /// 叠加层窗口宿主。
    /// 只负责 Win32 透明窗口的生命周期；绘制由 OverlayGdiRenderer（后台线程）消费
    /// OverlayPrimitiveStore 中的屏幕空间原语完成，不再传输图像流。
    /// </summary>
    public static class NativeOverlay
    {
        //叠加层状态
        private static bool isOverlayInitialized = false;

        //上次 EnableNativeOverlay 状态（用于记录开关变化）
        private static bool _lastEnableState;

        //上次焦点状态（用于记录失焦/聚焦变化）
        private static bool _lastFocusedState = true;

        //数据桥三缓冲 store（主线程构建原语 → 渲染线程消费）
        public static OverlayPrimitiveStore Store { get; private set; } = new OverlayPrimitiveStore();

        //引入Windows底层API
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        //自定义窗口类 + 光标（STATIC 类的类光标为 NULL，鼠标悬停时光标不可见）
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        //前台窗口检测（失焦判断）：Application.isFocused 在 Tarkov 全屏窗口化下不可靠，
        //切窗后仍返回 true。改用 Win32 查询当前前台窗口是否属于本进程。
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        //游戏进程 ID（懒缓存，用于前台窗口归属判断）
        private static uint _gamePid;
        private static bool _gamePidInited;

        /// <summary>
        /// 判断游戏是否处于前台（比 Application.isFocused 可靠：
        /// Tarkov 全屏窗口化下 isFocused 切窗后仍返回 true，导致失焦判断失效）。
        /// 前台窗口若属于本进程 → 聚焦；否则 → 失焦（切到别的窗口）。
        /// </summary>
        private static bool IsGameFocused()
        {
            try
            {
                if (!_gamePidInited)
                {
                    _gamePid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                    _gamePidInited = true;
                }
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                return pid == _gamePid;
            }
            catch
            {
                //异常时退化为 Unity 判断
                return Application.isFocused;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        //⚠ 不要用托管 delegate 作 WndProc（Marshal.GetFunctionPointerForDelegate 注册到
        //  RegisterClassExW 后，CreateWindowEx 同步派发 WM_NCCREATE/WM_CREATE 回调托管 thunk，
        //  在 Unity Mono 下会原生崩溃：0xc000041d STATUS_FATAL_USER_CALLBACK_EXCEPTION，
        //  崩溃日志无任何叠加层日志、故障模块 StackHash/unknown，实机实测 3 次必现）。
        //  叠加层窗口只需 UpdateLayeredWindow 上屏、无自定义消息处理，直接用
        //  user32!DefWindowProcW 的原生地址作窗口过程即可（纯 native 回调，零托管交互）。
        private const string OverlayClassName = "OracleESP_OverlayWnd";
        private static bool _classRegistered;

        private static void EnsureOverlayClass()
        {
            if (_classRegistered)
            {
                System.Console.WriteLine("[Oracle][Overlay] EnsureOverlayClass: 类已注册，复用");
                return;
            }
            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
            IntPtr user32 = GetModuleHandle("user32.dll");
            wc.lpfnWndProc = GetProcAddress(user32, "DefWindowProcW");
            wc.hInstance = GetModuleHandle(null);
            wc.hCursor = LoadCursor(IntPtr.Zero, 32512); // IDC_ARROW 系统箭头光标
            wc.lpszClassName = OverlayClassName;
            System.Console.WriteLine($"[Oracle][Overlay] EnsureOverlayClass: user32=0x{user32.ToInt64():X} lpfnWndProc=0x{wc.lpfnWndProc.ToInt64():X} hInstance=0x{wc.hInstance.ToInt64():X} hCursor=0x{wc.hCursor.ToInt64():X}");
            ushort atom = RegisterClassExW(ref wc);
            if (atom != 0)
            {
                _classRegistered = true;
                System.Console.WriteLine($"[Oracle][Overlay] EnsureOverlayClass: 注册成功 atom={atom}");
            }
            else
            {
                //ERROR_CLASS_ALREADY_EXISTS (1410)：叠加层反复启用/禁用后类已存在，视为成功
                int err = Marshal.GetLastWin32Error();
                if (err == 1410)
                {
                    _classRegistered = true;
                    System.Console.WriteLine("[Oracle][Overlay] EnsureOverlayClass: ERROR_CLASS_ALREADY_EXISTS(1410)，视为已注册");
                }
                else
                {
                    System.Console.WriteLine("ERR " + $"[Oracle][Overlay] EnsureOverlayClass: RegisterClassEx 失败! GetLastError={err}");
                }
            }
        }

        //定义原点和宽高
        private static IntPtr hwnd = IntPtr.Zero;
        private static int screenW, screenH;

        //当前显隐状态
        private static bool isVisible = true;

        /// <summary>
        /// 初始化覆盖层窗口（叠加层启用时调用，主线程）
        /// </summary>
        public static void Initialize(int w, int h)
        {
            screenW = w;
            screenH = h;
            System.Console.WriteLine($"[Oracle][Overlay] Initialize: {w}x{h} isOverlayInitialized={isOverlayInitialized}");
            //⚠ SW_SHOWNA(8) 而不是 SW_SHOW(5)：SW_SHOW 会激活窗口抢走游戏焦点，
            //  导致 Application.isFocused 变 false，下一帧 UpdateNativeOverlay 就把窗口隐藏，全黑。
            int exStyle = 0x80000 | 0x20 | 0x8 | 0x80; // WS_EX_LAYERED|TRANSPARENT|TOPMOST|TOOLWINDOW
            if (NativeOverlayCfg.OverlayDebugShowInTaskbar.Value)
            {
                //调试：去掉 TOOLWINDOW、改 APPWINDOW，让窗口出现在任务栏便于观察是否创建成功
                exStyle = 0x80000 | 0x20 | 0x8 | 0x40000; // WS_EX_LAYERED|TRANSPARENT|TOPMOST|APPWINDOW
            }
            int style = unchecked((int)0x80000000); // WS_POPUP
            System.Console.WriteLine($"[Oracle][Overlay] Initialize: exStyle=0x{exStyle:X} style=0x{(uint)style:X} class={OverlayClassName}");
            //⚠ 用自定义类而非 "STATIC"：STATIC 类光标为 NULL，鼠标悬停叠加层时光标不可见
            EnsureOverlayClass();
            hwnd = CreateWindowEx(exStyle, OverlayClassName, "OracleESP_Overlay", style, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            System.Console.WriteLine($"[Oracle][Overlay] CreateWindowEx: hwnd=0x{hwnd.ToInt64():X} GetLastError={Marshal.GetLastWin32Error()}");
            if (hwnd == IntPtr.Zero)
            {
                System.Console.WriteLine("ERR " + $"[Oracle][Overlay] 叠加层窗口创建失败! GetLastError={Marshal.GetLastWin32Error()}");
                return;
            }
            ShowWindow(hwnd, 8); // SW_SHOWNA
            System.Console.WriteLine("[Oracle][Overlay] ShowWindow(SW_SHOWNA) 完成");

            //启动 GDI 渲染线程（DIB 为 top-down，坐标与 Builder 输出一致）
            OverlayGdiRenderer.Initialize(hwnd, w, h, Store);
        }

        /// <summary>
        /// 摧毁叠加层（停线程 → 释放 GDI → 销毁窗口）
        /// </summary>
        public static void Destroy()
        {
            if (hwnd == IntPtr.Zero) return;

            try
            {
                //停渲染线程并释放 GDI 对象
                OverlayGdiRenderer.Destroy();
                //销毁窗口
                DestroyWindow(hwnd);
            }
            catch
            {
            }
            finally
            {
                hwnd = IntPtr.Zero;
                isVisible = false;
            }
        }

        /// <summary>
        /// 控制窗口显隐
        /// </summary>
        public static void SetVisible(bool show)
        {
            if (hwnd == IntPtr.Zero) return;

            if (show && !isVisible)
            {
                ShowWindow(hwnd, 8); // SW_SHOWNA
                isVisible = true;
            }
            else if (!show && isVisible)
            {
                ShowWindow(hwnd, 0); // SW_HIDE
                isVisible = false;
            }
        }

        /// <summary>
        /// 更新叠加层状态
        /// </summary>
        public static void UpdateNativeOverlay()
        {
            //调试自检帧开关同步（渲染线程轮询此标志，配置改动即时生效）
            OverlayGdiRenderer.ForceTestFrame = NativeOverlayCfg.OverlayDebugTestFrame.Value;

            //失焦判断：用 Win32 前台窗口（Application.isFocused 在全屏窗口化下切窗后仍为 true，不可靠）
            bool focused = IsGameFocused();
            if (focused != _lastFocusedState)
            {
                System.Console.WriteLine($"[Oracle][Overlay] UpdateNativeOverlay: 焦点 {_lastFocusedState}->{focused}");
                _lastFocusedState = focused;
            }
            //失焦时暂停渲染线程绘制（省 GDI/ULW 开销）；聚焦自动恢复
            OverlayGdiRenderer.RenderPaused = !focused;

            bool enable = NativeOverlayCfg.EnableNativeOverlay.Value;
            if (enable != _lastEnableState)
            {
                System.Console.WriteLine($"[Oracle][Overlay] UpdateNativeOverlay: EnableNativeOverlay {_lastEnableState}->{enable}, isOverlayInitialized={isOverlayInitialized}");
                _lastEnableState = enable;
            }

            if (enable)
            {
                //重新初始化
                if (!isOverlayInitialized)
                {
                    System.Console.WriteLine($"[Oracle][Overlay] UpdateNativeOverlay: 首次启用，调用 Initialize Screen={Screen.width}x{Screen.height} UniGUI={GlobalCfg.UniGUI.Value}");
                    Initialize(Screen.width, Screen.height);
                    isOverlayInitialized = true;
                }

                //叠加层显隐：失焦时 SW_HIDE 完全隐藏（避免 WS_EX_TOPMOST 残留最后一帧悬停桌面），聚焦时 SW_SHOWNA 恢复。
                //⚠ 之前不用 isFocused 控制显隐是因为它不可靠会误判；现在用 Win32 前台窗口判断是可靠的。
                SetVisible(GlobalCfg.UniGUI.Value && focused);
            }
            else
            {
                //摧毁叠加层
                if (isOverlayInitialized)
                {
                    System.Console.WriteLine("[Oracle][Overlay] UpdateNativeOverlay: 禁用，调用 Destroy");
                    Destroy();
                    isOverlayInitialized = false;
                }
            }
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    public class NativeOverlayCfg : IOracleCfg
    {

        internal static ConfigEntry<bool> EnableNativeOverlay { get; set; }
        /// <summary>调试：让叠加层窗口出现在任务栏（排查窗口是否创建成功）</summary>
        internal static ConfigEntry<bool> OverlayDebugShowInTaskbar { get; set; }
        /// <summary>调试：强制显示自检测试帧（红块+文字），验证窗口/GDI 管线是否通畅</summary>
        internal static ConfigEntry<bool> OverlayDebugTestFrame { get; set; }

        /// <summary>
        /// 配置项初始化
        /// </summary>
        /// <param name="config">传入配置实例</param>
        public void Initialize(ConfigFile config)
        {
            EnableNativeOverlay = config.Bind(
                "0. 联觉信标 / Draw Module",
                "启用叠加层",
                false,
                new ConfigDescription(
                    "cfg_global_module_overlay_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_overlay_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 397
                    }
                )
            );
            OverlayDebugShowInTaskbar = config.Bind(
                "0. 联觉信标 / Draw Module",
                "叠加层调试：任务栏可见",
                false,
                new ConfigDescription(
                    "cfg_global_module_overlay_debug_taskbar_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_overlay_debug_taskbar_name".i18n(),
                        IsAdvanced = true,
                        Order = 396
                    }
                )
            );
            OverlayDebugTestFrame = config.Bind(
                "0. 联觉信标 / Draw Module",
                "叠加层调试：自检测试帧",
                false,
                new ConfigDescription(
                    "cfg_global_module_overlay_debug_testframe_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_overlay_debug_testframe_name".i18n(),
                        IsAdvanced = true,
                        Order = 395
                    }
                )
            );
        }
    }
}
