using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.Utils
{
    /// <summary>
    /// 日志工具 —— 专门用于防止「日志刷屏拖垮帧率」。
    ///
    /// 事故背景：GameEndPatch 曾在主菜单被调用 640,224 次，产出 40MB 日志并严重掉帧。
    /// 在 IL2CPP 下这类事故尤其容易发生——方法体合并会让一个挂钩波及全游戏，
    /// 覆盖率无法从源码推断。所以【任何可能被高频调用的日志都必须走这里】。
    ///
    /// 提供的三种语义：
    ///   Once      —— 每个 key 只输出一次（用于生命周期/初始化确认）
    ///   Throttled —— 每个 key 在间隔内最多一次（用于可能反复触发的事件）
    ///   Error     —— 同上，但输出 Error 级别
    /// </summary>
    public static class OracleLog
    {
        private static readonly HashSet<string> _logged = new HashSet<string>();
        private static readonly Dictionary<string, float> _lastTime = new Dictionary<string, float>();

        // ⚠ async 路径的续体可能跑在线程池线程上，日志调用会与主线程并发。
        //   HashSet / Dictionary 都不是线程安全的，并发写入可能直接损坏内部结构
        //   或抛出难以复现的异常。统一加锁 —— 日志本身不需要高性能。
        private static readonly object _gate = new object();

        /// <summary>默认节流间隔（秒）</summary>
        private const float DefaultInterval = 5f;

        /// <summary>每个 key 只输出一次。</summary>
        public static void Once(string key, string message)
        {
            bool first;
            lock (_gate) { first = _logged.Add(key); }
            if (!first) return;
            Write(message, false);
        }

        /// <summary>
        /// 一次性普通信息 —— 用于"只想知道发生过一次"的场景（如补丁桥接成功）。
        /// 与 Once 的区别仅在于语义命名：Once 强调去重，Info 强调这是一条状态通报。
        /// </summary>
        public static void Info(string message)
        {
            Write(message, false);
        }

        /// <summary>
        /// 节流警告 —— 用于不该刷屏、但需要留痕的异常情况。
        /// </summary>
        public static void Warning(string message)
        {
            Throttled("warn:" + message, "[Oracle] " + message);
        }

        /// <summary>按 key 节流输出，同一 key 在 interval 秒内只输出一次。</summary>
        public static void Throttled(string key, string message, float interval = DefaultInterval)
        {
            if (!ShouldLog(key, interval)) return;
            Write(message, false);
        }

        /// <summary>按 key 节流输出错误。</summary>
        public static void Error(string key, string message, float interval = DefaultInterval)
        {
            if (!ShouldLog(key, interval)) return;
            Write(message, true);
        }

        /// <summary>不节流的错误 —— 仅用于真正的一次性错误（如初始化失败）。</summary>
        public static void ErrorOnce(string key, string message)
        {
            bool first;
            lock (_gate) { first = _logged.Add(key); }
            if (!first) return;
            Write(message, true);
        }

        /// <summary>
        /// 节流用的时间源。
        ///
        /// ⚠ 这里【绝对不能】用 Time.unscaledTime。
        ///
        ///   实机踩坑：造物 / 掉落都是 async 路径，await 之后的续体可能被调度到
        ///   线程池线程上。Time.unscaledTime 是 Unity API，在非主线程读取会抛
        ///   UnityException，而它恰好位于所有 Warning / Throttled / Exception 的
        ///   必经之路上 —— 于是这些日志在异步路径里**全部失效**：
        ///   界面上弹了失败提示，日志里却一行都没有。这正是"战局内生成会弹失败
        ///   但是没日志"的成因。
        ///
        ///   Environment.TickCount 是纯托管实现，任何线程都安全；转 uint 是为了
        ///   正确跨过 24.9 天的回绕。语义与 unscaledTime 一致（单调秒数）。
        /// </summary>
        private static float NowSeconds()
        {
            return (uint)Environment.TickCount / 1000f;
        }

        private static bool ShouldLog(string key, float interval)
        {
            float now = NowSeconds();
            lock (_gate)
            {
                if (_lastTime.TryGetValue(key, out float last) && now - last < interval) return false;
                _lastTime[key] = now;
                return true;
            }
        }

        /// <summary>
        /// 日志出口的统一包装。
        ///
        /// 日志本身永远不该反过来破坏调用方 —— 尤其不能让"记录一次异常"再抛出
        /// 第二个异常（那会把真正的错误盖掉，实机排查时表现为日志凭空断掉）。
        /// </summary>
        private static void Write(string message, bool isError)
        {
            try
            {
                if (isError) Debug.LogError(message);
                else Debug.Log(message);
            }
            catch
            {
                // 忽略：日志失败不是业务失败
            }
        }

        /// <summary>
        /// 统计并节流报告异常 —— 用于钩子/每帧代码的异常出口。
        /// 一帧一抛的异常会在几秒内把日志撑到几百 MB，必须走这里。
        /// </summary>
        public static void Exception(string key, string where, Exception ex)
        {
            Throttled(key, $"[Oracle] {where} 异常: {ex}");
        }
    }
}
