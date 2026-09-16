using Oracle.Data;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Oracle.Overlay
{
    /// <summary>
    /// 叠加层 GDI 渲染线程执行器。
    /// 后台线程消费 OverlayPrimitiveStore 的原语块，用 GDI 直接绘制到 DIB，再 UpdateLayeredWindow。
    /// 不做任何投影/深度/排序计算——主线程已经算好全部屏幕空间数据。
    ///
    /// ⚠ alpha 陷阱：GDI 向 32bpp DIB 绘制只写 RGB、不写 alpha（保持 0 = 全透明），
    /// 必须在每帧绘制后对脏区做一次 alpha 修正（RGB≠0 → alpha=255），否则画了看不见。
    /// 文字使用非抗锯齿字体，避免 GDI 灰度抗锯齿在 a=255 修正下产生暗色 halo。
    /// </summary>
    public static class OverlayGdiRenderer
    {
        // ---- GDI 基础对象（渲染线程独占） ----
        private static IntPtr _memDC;
        private static IntPtr _dibBitmap;
        private static IntPtr _dibBits;
        private static IntPtr _oldBmp;
        private static IntPtr _font;
        private static int _dibWidth, _dibHeight;

        // ---- 窗口 / 数据桥 ----
        private static IntPtr _hwnd;
        private static OverlayPrimitiveStore _store;
        private static Thread _renderThread;
        private static volatile bool _running;

        //屏幕尺寸缓存（渲染线程读取，不依赖 UnityEngine.Screen）
        private static int _screenW, _screenH;

        /// <summary>调试开关：强制绘制自检测试帧（红块+文字），绕过数据桥，验证窗口/GDI 管线是否通畅</summary>
        public static bool ForceTestFrame;

        /// <summary>
        /// 失焦自动停止：游戏窗口失焦时置 true，渲染线程跳过取块/绘制，保留最后一帧画面省性能。
        /// 仅暂停绘制，不 SW_HIDE 窗口——避免全屏窗口化下 isFocused 抖动导致叠加层闪黑。
        /// 主线程 UpdateNativeOverlay 每帧同步，渲染线程只读。
        /// </summary>
        public static volatile bool RenderPaused;

        //上一帧脏区（清屏用：union(prev, cur)）
        private static int _prevDirtyX0, _prevDirtyY0, _prevDirtyX1, _prevDirtyY1;
        private static bool _hasPrevDirty;

        //行缓冲复用（清屏零行 + 修正暂存）
        private static byte[] _zeroRow;
        private static byte[] _scratchRow;

        //Render 块内容统计日志节流（每 1 秒一条）
        private static long _lastRenderLogMs;

        //渲染帧率上限（保留模式：上一帧窗口内容不消失，可安全降频）
        private const int FrameRateMs = 33;

        // ---- P/Invoke ----
        //⚠ 所有 *W 函数必须 CharSet.Unicode：默认 Ansi marshaling 会把宽字符函数当 ANSI 调，
        //   CreateFontW/TextOutW/GetTextExtentPoint32W 的字符串会全部错乱
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
        [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lppt);
        [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int x, int y);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);
        [DllImport("user32.dll")] private static extern bool FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, int bItalic, int bUnderline, int bStrikeOut, int iCharSet, int iOutPrecision, int iClipPrecision, int iQuality, int iPitchAndFamily, string pszFaceName);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int iBkMode);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint crColor);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

        //灰度抗锯齿（配合 FixAlphaText 用最亮通道还原覆盖率，实现平滑边缘）
        private const int ANTIALIASED_QUALITY = 4;
        private const int FW_BOLD = 700;
        private const int DEFAULT_CHARSET = 1;
        private const int TRANSPARENT = 1;

        /// <summary>
        /// 初始化渲染线程（叠加层启用时调用，主线程）
        /// </summary>
        public static void Initialize(IntPtr hwnd, int w, int h, OverlayPrimitiveStore store)
        {
            _hwnd = hwnd;
            _dibWidth = w;
            _dibHeight = h;
            _screenW = w;
            _screenH = h;
            _store = store;
            _hasPrevDirty = false;

            IntPtr screenDC = GetDC(IntPtr.Zero);
            try
            {
                _memDC = CreateCompatibleDC(screenDC);

                //top-down DIB（负高度）：GDI 坐标 (0,0)=左上、y 向下，与 Builder 输出坐标系一致
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;
                _dibBitmap = CreateDIBSection(screenDC, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
                if (_dibBitmap != IntPtr.Zero)
                {
                    _oldBmp = SelectObject(_memDC, _dibBitmap);
                    System.Console.WriteLine($"[Oracle][Overlay] CreateDIBSection: 成功 dibBitmap=0x{_dibBitmap.ToInt64():X} dibBits=0x{_dibBits.ToInt64():X} size={w}x{h}(负高度top-down)");
                }
                else
                {
                    //创建失败防御：ppvBits 内容未定义，必须清零，否则渲染线程会按非零指针写内存
                    _dibBits = IntPtr.Zero;
                    System.Console.WriteLine("ERR " + $"[Oracle][Overlay] CreateDIBSection 失败! GetLastError={Marshal.GetLastWin32Error()}");
                }

                //抗锯齿粗体 12px 字体（中文支持用微软雅黑，Win10 内置）
                _font = CreateFontW(-12, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, "Microsoft YaHei UI");
                if (_font != IntPtr.Zero)
                {
                    SelectObject(_memDC, _font);
                }

                SetBkMode(_memDC, TRANSPARENT);

                //行缓冲（一次分配整行，脏区按行复用）
                _zeroRow = new byte[w * 4];
                _scratchRow = new byte[w * 4];
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }

            System.Console.WriteLine($"[Oracle][Overlay] 叠加层渲染初始化: hwnd=0x{hwnd.ToInt64():X} memDC=0x{_memDC.ToInt64():X} dibBitmap=0x{_dibBitmap.ToInt64():X} dibBits=0x{_dibBits.ToInt64():X} font=0x{_font.ToInt64():X} size={w}x{h}");
            if (_dibBits == IntPtr.Zero)
            {
                System.Console.WriteLine("ERR " + "[Oracle][Overlay] DIB 未创建成功，叠加层将无法渲染");
            }

            //启动渲染线程
            _running = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "OracleOverlayGDI"
            };
            _renderThread.Start();
            System.Console.WriteLine($"[Oracle][Overlay] 渲染线程已启动: Name={_renderThread.Name} IsBackground={_renderThread.IsBackground}");
        }

        /// <summary>
        /// 停止渲染线程并释放全部 GDI 对象（叠加层销毁时调用，主线程）
        /// </summary>
        public static void Destroy()
        {
            _running = false;
            if (_renderThread != null)
            {
                //后台线程最多睡眠 33ms，Join 500ms 足够让其自然退出；IsBackground 保证即使卡住也不阻塞进程退出
                _renderThread.Join(500);
                _renderThread = null;
            }

            if (_memDC != IntPtr.Zero)
            {
                if (_oldBmp != IntPtr.Zero) SelectObject(_memDC, _oldBmp);
                if (_dibBitmap != IntPtr.Zero) DeleteObject(_dibBitmap);
                DeleteDC(_memDC);
            }
            if (_font != IntPtr.Zero) DeleteObject(_font);

            _memDC = _dibBitmap = _dibBits = _oldBmp = _font = IntPtr.Zero;
        }

        // ═══════════════════ 渲染线程主循环 ═══════════════════

        private static void RenderLoop()
        {
            //后台线程不碰 UnityEngine.Time，用 Stopwatch 节流
            Stopwatch clock = Stopwatch.StartNew();
            long lastFrameMs = 0;
            long lastStatusMs = 0;
            int framesTaken = 0;       // 累计消费到的块
            int framesNull = 0;        // 累计 TakePublished 返回 null
            System.Console.WriteLine("[Oracle][Overlay] RenderLoop: 渲染线程主循环开始");
            bool wasPaused = false;
            while (_running)
            {
                try
                {
                    //失焦暂停：不消费数据桥、不绘制，窗口保留最后一帧（省性能）
                    if (RenderPaused)
                    {
                        if (!wasPaused)
                        {
                            System.Console.WriteLine("[Oracle][Overlay] RenderLoop: 失焦，渲染暂停（保留最后一帧）");
                            wasPaused = true;
                        }
                    }
                    else
                    {
                        if (wasPaused)
                        {
                            System.Console.WriteLine("[Oracle][Overlay] RenderLoop: 聚焦，渲染恢复");
                            wasPaused = false;
                        }
                        //调试自检帧：绕过数据桥直接画，定位黑屏是"窗口/GDI 问题"还是"数据桥问题"
                        if (ForceTestFrame)
                        {
                            RenderTestFrame();
                            framesTaken++;
                        }
                        else
                        {
                            OverlayPrimitiveBlock block = _store.TakePublished();
                            if (block != null)
                            {
                                Render(block);
                                _store.ReturnBlock(block);
                                framesTaken++;
                            }
                            else
                            {
                                framesNull++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    //⚠ 渲染线程异常必须兜住：线程静默死亡会导致主线程 3 块耗尽后停摆，永久全黑
                    System.Console.WriteLine("ERR " + $"[Oracle][Overlay] 叠加层渲染线程异常: {ex}");
                }

                //每 ~1 秒输出一次状态（判断数据桥是否通：framesTaken/framesNull）
                long now = clock.ElapsedMilliseconds;
                if (now - lastStatusMs >= 1000)
                {
                    System.Console.WriteLine($"[Oracle][Overlay] RenderLoop 状态: ForceTestFrame={ForceTestFrame} 近1秒 taken={framesTaken} null={framesNull} store可写块数未知(锁内)");
                    lastStatusMs = now;
                    framesTaken = 0;
                    framesNull = 0;
                }

                //保留模式：无新帧时窗口保留上一帧内容，仅节流到 30fps
                long elapsed = now - lastFrameMs;
                lastFrameMs = now;
                if (elapsed < FrameRateMs)
                {
                    Thread.Sleep((int)(FrameRateMs - elapsed));
                }
            }
            System.Console.WriteLine("[Oracle][Overlay] RenderLoop: 渲染线程主循环退出");
        }

        private static void Render(OverlayPrimitiveBlock block)
        {
            if (_hwnd == IntPtr.Zero || _memDC == IntPtr.Zero || _dibBits == IntPtr.Zero)
            {
                System.Console.WriteLine("ERR " + $"[Oracle][Overlay] Render 提前返回: hwnd=0x{_hwnd.ToInt64():X} memDC=0x{_memDC.ToInt64():X} dibBits=0x{_dibBits.ToInt64():X}");
                return;
            }

            //脏区（清除上一帧残留 + 本帧新内容）
            int x0 = (int)block.DirtyX0, y0 = (int)block.DirtyY0;
            int x1 = (int)block.DirtyX1 + 1, y1 = (int)block.DirtyY1 + 1;
            if (_hasPrevDirty)
            {
                if (_prevDirtyX0 < x0) x0 = _prevDirtyX0;
                if (_prevDirtyY0 < y0) y0 = _prevDirtyY0;
                if (_prevDirtyX1 > x1) x1 = _prevDirtyX1;
                if (_prevDirtyY1 > y1) y1 = _prevDirtyY1;
            }
            Clamp(ref x0, ref y0, ref x1, ref y1);
            if (x1 > x0 && y1 > y0)
            {
                //1. 清屏（透明黑 = alpha 0）
                ClearRegion(x0, y0, x1, y1);
                //2. 绘制图形（矩形 + 线段，统一软件光栅化，alpha 直写）
                DrawShapes(block);
                //3. 图形 alpha 修正：把仍全透明的 GDI 残留像素置 255（实心）
                FixAlphaOpaque(x0, y0, x1, y1);
                //4. 绘制文本（抗锯齿字体）
                DrawTexts(block);
                //5. 文本 alpha 修正：文字画在透明背景上 RGB=文字色×覆盖率，
                //   用最亮通道近似 alpha，实现真抗锯齿（图形像素 alpha≠0 不受影响）
                FixAlphaText(x0, y0, x1, y1);
            }

            //4. 上屏
            IntPtr screenDC = GetDC(IntPtr.Zero);
            bool ulwOk = false;
            try
            {
                POINT ptSrc = new POINT { x = 0, y = 0 };
                POINT ptDst = new POINT { x = 0, y = 0 };
                SIZE size = new SIZE { cx = _dibWidth, cy = _dibHeight };
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                ulwOk = UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size, _memDC, ref ptSrc, 0, ref blend, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }

            //每 ~1 秒输出一次块内容统计（确认数据桥是否真有元素被画）
            long nowMs = Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond;
            if (nowMs - _lastRenderLogMs >= 1000)
            {
                System.Console.WriteLine($"[Oracle][Overlay] Render: block lines={block.LineCount} texts={block.TextCount} rects={block.RectCount} dirty=({x0},{y0})-({x1},{y1}) ULW={ulwOk} 上次GetLastError={Marshal.GetLastWin32Error()}");
                _lastRenderLogMs = nowMs;
            }

            //记录本帧脏区供下帧清除
            _prevDirtyX0 = (int)block.DirtyX0;
            _prevDirtyY0 = (int)block.DirtyY0;
            _prevDirtyX1 = (int)block.DirtyX1 + 1;
            _prevDirtyY1 = (int)block.DirtyY1 + 1;
            _hasPrevDirty = true;
        }

        /// <summary>
        /// 自检测试帧：绕过数据桥，直接全屏清空 + 画红色矩形 + 白色文字 + alpha 修正 + 上屏。
        /// 打开配置若能看到红块白字，说明窗口/GDI/ULW 管线通畅，问题在数据桥侧；
        /// 若看不到，问题在窗口创建或 GDI 绘制本身。
        /// </summary>
        private static void RenderTestFrame()
        {
            if (_hwnd == IntPtr.Zero || _memDC == IntPtr.Zero || _dibBits == IntPtr.Zero) return;

            //全屏清空（透明黑）
            for (int y = 0; y < _dibHeight; y++)
            {
                IntPtr row = IntPtr.Add(_dibBits, y * _dibWidth * 4);
                Marshal.Copy(_zeroRow, 0, row, _dibWidth * 4);
            }

            //红色矩形（GDI FillRect，COLORREF = 0x000000FF）
            RECT rc = new RECT
            {
                Left = _dibWidth / 2 - 120,
                Top = _dibHeight / 2 - 60,
                Right = _dibWidth / 2 + 120,
                Bottom = _dibHeight / 2 + 60
            };
            IntPtr brush = CreateSolidBrush(0x000000FF);
            if (brush != IntPtr.Zero)
            {
                FillRect(_memDC, ref rc, brush);
                DeleteObject(brush);
            }

            //白色文字
            SetTextColor(_memDC, 0x00FFFFFF);
            TextOutW(_memDC, _dibWidth / 2 - 100, _dibHeight / 2 - 15, "Oracle GDI TEST", 15);

            //alpha 修正（全屏）：图形实心 + 文本抗锯齿
            FixAlphaOpaque(0, 0, _dibWidth, _dibHeight);
            FixAlphaText(0, 0, _dibWidth, _dibHeight);

            //上屏
            IntPtr screenDC = GetDC(IntPtr.Zero);
            try
            {
                POINT ptSrc = new POINT { x = 0, y = 0 };
                POINT ptDst = new POINT { x = 0, y = 0 };
                SIZE size = new SIZE { cx = _dibWidth, cy = _dibHeight };
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                bool ok = UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size, _memDC, ref ptSrc, 0, ref blend, 2);
                if (!ok)
                {
                    System.Console.WriteLine("ERR " + $"[Oracle] 测试帧 ULW 失败! GetLastError={Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        // ═══════════════════ GDI 绘制 ═══════════════════

        private static void DrawShapes(OverlayPrimitiveBlock block)
        {
            //矩形（血条）→ FillRect（已验证可用）
            for (int i = 0; i < block.RectCount; i++)
            {
                OverlayRect r = block.Rects[i];
                RECT rc = new RECT
                {
                    Left = (int)r.X,
                    Top = (int)r.Y,
                    Right = (int)(r.X + r.W),
                    Bottom = (int)(r.Y + r.H)
                };
                IntPtr brush = CreateSolidBrush(ArgbToColorRef(r.Color));
                if (brush != IntPtr.Zero)
                {
                    FillRect(_memDC, ref rc, brush);
                    DeleteObject(brush);
                }
            }

            //线段（骨骼/圆圈/目标线/绊雷线）→ 统一软件 Bresenham。
            //⚠ 不走 GDI CreatePen/MoveToEx/LineTo：实测该路径在叠加层不显示，
            //   软件光栅化直写 DIB 像素（带 alpha）更可靠，半透明 FOV 圈已验证
            for (int i = 0; i < block.LineCount; i++)
            {
                SoftwareLine(block.Lines[i]);
            }
        }

        private static void DrawTexts(OverlayPrimitiveBlock block)
        {
            for (int i = 0; i < block.TextCount; i++)
            {
                DrawTextPrimitive(block.Texts[i]);
            }
        }

        private static void DrawTextPrimitive(OverlayText t)
        {
            int segCount = t.SegmentCount;
            if (segCount <= 0) return;

            //空间隔宽度（OnGUI 富文本中段间以空格连接）
            SIZE spaceSize;
            GetTextExtentPoint32W(_memDC, " ", 1, out spaceSize);
            int spaceW = spaceSize.cx;

            int[] widths = new int[4];
            int totalWidth = 0;
            for (int s = 0; s < segCount; s++)
            {
                string text = GetSegText(t, s);
                if (string.IsNullOrEmpty(text)) continue;
                SIZE sz;
                GetTextExtentPoint32W(_memDC, text, text.Length, out sz);
                widths[s] = sz.cx;
                totalWidth += sz.cx;
                if (s > 0) totalWidth += spaceW;
            }

            //水平居中 + 垂直居中（字体行高）
            int lineH = GetFontHeight();
            int startX = (int)(t.X + (t.W - totalWidth) / 2f);
            int y = (int)(t.Y + (t.H - lineH) / 2f);

            int cursorX = startX;
            for (int s = 0; s < segCount; s++)
            {
                string text = GetSegText(t, s);
                if (string.IsNullOrEmpty(text) || widths[s] <= 0) continue;
                SetTextColor(_memDC, ArgbToColorRef(GetSegColor(t, s)));
                TextOutW(_memDC, cursorX, y, text, text.Length);
                cursorX += widths[s] + spaceW;
            }
        }

        private static string GetSegText(OverlayText t, int idx)
        {
            switch (idx)
            {
                case 0: return t.Seg0.Text;
                case 1: return t.Seg1.Text;
                case 2: return t.Seg2.Text;
                default: return t.Seg3.Text;
            }
        }

        private static uint GetSegColor(OverlayText t, int idx)
        {
            switch (idx)
            {
                case 0: return t.Seg0.Color;
                case 1: return t.Seg1.Color;
                case 2: return t.Seg2.Color;
                default: return t.Seg3.Color;
            }
        }

        private static int GetFontHeight()
        {
            //近似行高：-12 像素字体 ≈ 16 逻辑行高，够垂直居中用
            return 16;
        }

        // ═══════════════════ 像素操作（alpha 修正 + 清屏） ═══════════════════

        private static void ClearRegion(int x0, int y0, int x1, int y1)
        {
            IntPtr ptr = _dibBits;
            int width = _dibWidth;
            for (int y = y0; y < y1; y++)
            {
                IntPtr row = IntPtr.Add(ptr, (y * width + x0) * 4);
                //零行缓冲已全 0（alpha=0 = 全透明）
                Marshal.Copy(_zeroRow, 0, row, (x1 - x0) * 4);
            }
        }

        private static void FixAlphaOpaque(int x0, int y0, int x1, int y1)
        {
            IntPtr ptr = _dibBits;
            int width = _dibWidth;
            int rowBytes = (x1 - x0) * 4;
            for (int y = y0; y < y1; y++)
            {
                IntPtr row = IntPtr.Add(ptr, (y * width + x0) * 4);
                Marshal.Copy(row, _scratchRow, 0, rowBytes);
                for (int i = 0; i < rowBytes; i += 4)
                {
                    //仅修正「仍然完全透明」的像素（alpha==0 且 RGB≠0 → 置 255）。
                    //软件预混合的半透明像素（alpha>0）保持原样，不被覆盖。
                    if (_scratchRow[i + 3] == 0 && (_scratchRow[i] | _scratchRow[i + 1] | _scratchRow[i + 2]) != 0)
                    {
                        _scratchRow[i + 3] = 255;
                    }
                }
                Marshal.Copy(_scratchRow, 0, row, rowBytes);
            }
        }

        /// <summary>
        /// 文本 alpha 修正：抗锯齿文字画在透明背景上时，边缘像素 RGB = 文字色×覆盖率（已预乘）。
        /// 用最亮通道近似覆盖率作为 alpha，得到平滑的抗锯齿边缘。
        /// 图形像素在 FixAlphaOpaque 后 alpha≠0，此处跳过不受影响。
        /// </summary>
        private static void FixAlphaText(int x0, int y0, int x1, int y1)
        {
            IntPtr ptr = _dibBits;
            int width = _dibWidth;
            int rowBytes = (x1 - x0) * 4;
            for (int y = y0; y < y1; y++)
            {
                IntPtr row = IntPtr.Add(ptr, (y * width + x0) * 4);
                Marshal.Copy(row, _scratchRow, 0, rowBytes);
                for (int i = 0; i < rowBytes; i += 4)
                {
                    if (_scratchRow[i + 3] == 0)
                    {
                        byte b = _scratchRow[i];
                        byte g = _scratchRow[i + 1];
                        byte r = _scratchRow[i + 2];
                        if ((b | g | r) != 0)
                        {
                            //alpha = max(R,G,B)：白色/亮色文字边缘最亮通道≈覆盖率
                            byte a = r > g ? r : g;
                            if (b > a) a = b;
                            _scratchRow[i + 3] = a;
                        }
                    }
                }
                Marshal.Copy(_scratchRow, 0, row, rowBytes);
            }
        }

        private static void Clamp(ref int x0, ref int y0, ref int x1, ref int y1)
        {
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > _dibWidth) x1 = _dibWidth;
            if (y1 > _dibHeight) y1 = _dibHeight;
        }

        /// <summary>
        /// 软件 Bresenham 画线（半透明线专用，如自瞄 FOV 圈 alpha=76）。
        /// 把 RGB 按 alpha 与现有像素做 src-over 混合，alpha 预乘进像素。
        /// 每像素一次字节写入，仅用于少量半透明线，成本可忽略。
        /// </summary>
        private static void SoftwareLine(in OverlayLine l)
        {
            int x0 = (int)l.X1, y0 = (int)l.Y1;
            int x1 = (int)l.X2, y1 = (int)l.Y2;
            //ARGB 拆通道（alpha 由 l.Alpha 字段单独携带，用于半透明线）
            uint argb = l.Color;
            byte sr = (byte)((argb >> 16) & 0xFF); // R
            byte sg = (byte)((argb >> 8) & 0xFF);  // G
            byte sb = (byte)(argb & 0xFF);         // B
            int a = l.Alpha;

            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            int width = _dibWidth;
            IntPtr ptr = _dibBits;
            while (true)
            {
                if (x0 >= 0 && y0 >= 0 && x0 < width && y0 < _dibHeight)
                {
                    IntPtr p = IntPtr.Add(ptr, (y0 * width + x0) * 4);
                    //DIB 32bpp 内存布局是 BGRA：byte0=B, byte1=G, byte2=R, byte3=A
                    byte db = Marshal.ReadByte(p);
                    byte dg = Marshal.ReadByte(p, 1);
                    byte dr = Marshal.ReadByte(p, 2);
                    //src-over 预乘合成：RGB 与现有像素混合，alpha 保持源透明度。
                    //半透明线（如自瞄 FOV 圈 α=0.3）像素 α=源 α，FixAlpha 不会覆盖它（α≠0）。
                    byte r = (byte)((sr * a + dr * (255 - a)) / 255);
                    byte g = (byte)((sg * a + dg * (255 - a)) / 255);
                    byte b = (byte)((sb * a + db * (255 - a)) / 255);
                    Marshal.WriteByte(p, 0, b);
                    Marshal.WriteByte(p, 1, g);
                    Marshal.WriteByte(p, 2, r);
                    Marshal.WriteByte(p, 3, (byte)a);
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>ARGB(0xAARRGGBB) → GDI COLORREF(0x00BBGGRR)</summary>
        private static uint ArgbToColorRef(uint argb)
        {
            return ((argb & 0xFF) << 16) | (argb & 0x00FF00) | ((argb >> 16) & 0xFF);
        }
    }
}
