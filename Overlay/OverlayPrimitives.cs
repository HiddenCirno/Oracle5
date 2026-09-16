using System;
using System.Collections.Generic;

namespace Oracle.Overlay
{
    /// <summary>
    /// 叠加层数据桥原语层。
    /// 主线程将 3D 数据预计算为屏幕空间 2D 绘制原语（坐标统一为左上原点、y 向下的窗口像素坐标系），
    /// 渲染线程只消费原语、不做任何投影/深度计算。原语结构为紧凑值类型，零解析成本。
    /// </summary>

    /// <summary>
    /// 线段原语（骨骼线 / 圆圈 / 目标线 / 绊雷线）
    /// </summary>
    public struct OverlayLine
    {
        public float X1, Y1, X2, Y2;
        /// <summary>ARGB 色值（0xAARRGGBB）</summary>
        public uint Color;
        /// <summary>透明度 0-255。255 = GDI 不透明线；&lt;255 = 软件 Bresenham 半透明线（如自瞄 FOV 圈 α=0.3）</summary>
        public byte Alpha;
    }

    /// <summary>
    /// 文本原语的一个彩色段（同行的纯文本 + 颜色）
    /// </summary>
    public struct OverlayTextSegment
    {
        /// <summary>纯文本（数据层缓存的字符串引用，非本帧新建）</summary>
        public string Text;
        /// <summary>ARGB 色值（0xAARRGGBB）</summary>
        public uint Color;
    }

    /// <summary>
    /// 文本原语：在给定矩形内居中绘制最多 4 个彩色段（等级/阵营/距离分色）
    /// 矩形坐标系与 OnGUI 的 GUI.Label 一致（x,y 为左上角，w,h 为尺寸）
    /// </summary>
    public struct OverlayText
    {
        public float X, Y, W, H;
        public byte SegmentCount;
        public OverlayTextSegment Seg0;
        public OverlayTextSegment Seg1;
        public OverlayTextSegment Seg2;
        public OverlayTextSegment Seg3;
    }

    /// <summary>
    /// 矩形原语（血条背景槽 / 血条填充）
    /// </summary>
    public struct OverlayRect
    {
        public float X, Y, W, H;
        /// <summary>ARGB 色值（0xAARRGGBB）</summary>
        public uint Color;
    }

    /// <summary>
    /// 每帧原语块：预分配容量，主线程填充、渲染线程只读，零分配复用
    /// </summary>
    public class OverlayPrimitiveBlock
    {
        public OverlayLine[] Lines = new OverlayLine[8192];
        public int LineCount;
        public OverlayText[] Texts = new OverlayText[16384];
        public int TextCount;
        public OverlayRect[] Rects = new OverlayRect[1024];
        public int RectCount;

        //脏区跟踪：本帧所有原语的包围盒并集（渲染线程用于清屏 + alpha 修正，避免全屏像素操作）
        public float DirtyX0, DirtyY0, DirtyX1, DirtyY1;
        public bool HasDirty;

        public void Reset()
        {
            LineCount = 0;
            TextCount = 0;
            RectCount = 0;
            DirtyX0 = DirtyY0 = float.MaxValue;
            DirtyX1 = DirtyY1 = float.MinValue;
            HasDirty = false;
        }

        public void AddLine(in OverlayLine line)
        {
            if (LineCount >= Lines.Length) Array.Resize(ref Lines, Lines.Length * 2);
            Lines[LineCount++] = line;
            ExpandDirty(line.X1, line.Y1);
            ExpandDirty(line.X2, line.Y2);
        }

        public void AddText(in OverlayText text)
        {
            if (TextCount >= Texts.Length) Array.Resize(ref Texts, Texts.Length * 2);
            Texts[TextCount++] = text;
            ExpandDirty(text.X, text.Y);
            ExpandDirty(text.X + text.W, text.Y + text.H);
        }

        public void AddRect(in OverlayRect rect)
        {
            if (RectCount >= Rects.Length) Array.Resize(ref Rects, Rects.Length * 2);
            Rects[RectCount++] = rect;
            ExpandDirty(rect.X, rect.Y);
            ExpandDirty(rect.X + rect.W, rect.Y + rect.H);
        }

        private void ExpandDirty(float x, float y)
        {
            if (x < DirtyX0) DirtyX0 = x;
            if (y < DirtyY0) DirtyY0 = y;
            if (x > DirtyX1) DirtyX1 = x;
            if (y > DirtyY1) DirtyY1 = y;
            HasDirty = true;
        }
    }

    /// <summary>
    /// 原语块三缓冲池。
    /// 主线程 AcquireWriteBlock → 填充 → Publish；渲染线程 TakePublished → 绘制 → ReturnBlock。
    /// 渲染线程落后时（无空闲块）主线程返回 null 丢弃本帧，天然降频不堆积。
    /// </summary>
    public class OverlayPrimitiveStore
    {
        private readonly object _sync = new object();
        private readonly OverlayPrimitiveBlock[] _free;
        private int _freeCount;
        private OverlayPrimitiveBlock _published;

        //诊断日志节流（每 1 秒一条，避免刷屏）
        private long _lastLogMs;
        private int _acquireNullCount;   // 累计取块失败次数
        private int _publishCount;       // 累计发布次数

        public OverlayPrimitiveStore(int bufferCount = 3)
        {
            _free = new OverlayPrimitiveBlock[bufferCount];
            for (int i = 0; i < bufferCount; i++)
            {
                _free[i] = new OverlayPrimitiveBlock();
                _freeCount++;
            }
        }

        /// <summary>主线程：取一个可写块（无空闲返回 null，本帧跳过）</summary>
        public OverlayPrimitiveBlock AcquireWriteBlock()
        {
            lock (_sync)
            {
                if (_freeCount == 0)
                {
                    _acquireNullCount++;
                    TryLog("AcquireWriteBlock 无空闲块（主线程降帧/渲染线程积压）");
                    return null;
                }
                var block = _free[--_freeCount];
                block.Reset();
                return block;
            }
        }

        /// <summary>
        /// 主线程：发布已填充的块。
        /// 若上一帧还没被渲染线程取走（积压），先把旧块归还 free 池——
        /// 否则 block 凭空消失，3 块耗尽后 AcquireWriteBlock 永远返回 null，叠加层停摆全黑。
        /// </summary>
        public void Publish(OverlayPrimitiveBlock block)
        {
            lock (_sync)
            {
                _publishCount++;
                if (_published != null)
                {
                    _free[_freeCount++] = _published;
                }
                _published = block;
                TryLog($"Publish 完成: lines={block.LineCount} texts={block.TextCount} rects={block.RectCount} free={_freeCount}");
            }
        }

        private void TryLog(string msg)
        {
            long now = Environment.TickCount;
            if (now - _lastLogMs >= 1000)
            {
                System.Console.WriteLine($"[Oracle][Overlay] Store: {msg} 近1秒 publish={_publishCount} acquireNull={_acquireNullCount}");
                _lastLogMs = now;
                _publishCount = 0;
                _acquireNullCount = 0;
            }
        }

        /// <summary>渲染线程：取走已发布块（无新帧返回 null，窗口保留上一帧）</summary>
        public OverlayPrimitiveBlock TakePublished()
        {
            lock (_sync)
            {
                var block = _published;
                _published = null;
                return block;
            }
        }

        /// <summary>渲染线程：归还绘制完成的块</summary>
        public void ReturnBlock(OverlayPrimitiveBlock block)
        {
            lock (_sync)
            {
                _free[_freeCount++] = block;
            }
        }

        /// <summary>清空所有块并归还（销毁叠加层时调用）</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _published = null;
                _freeCount = 0;
                for (int i = 0; i < _free.Length; i++)
                {
                    _free[i] = new OverlayPrimitiveBlock();
                    _freeCount++;
                }
            }
        }
    }
}
