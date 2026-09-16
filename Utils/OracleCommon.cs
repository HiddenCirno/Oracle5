using System;
using UnityEngine;

namespace Oracle.Utils
{
    /// <summary>
    /// 通用工具类
    /// </summary>
    public static class OracleCommon
    {
        /// <summary>
        /// 判断距离, O(1)单步搞定
        /// </summary>
        public static bool IsInRange(int maxDistance, Vector3 p1, Vector3 p2)
        {
            return (p1 - p2).sqrMagnitude <= maxDistance * maxDistance;
        }

        /// <summary>
        /// 全英文名判断
        /// </summary>
        public static bool IsAllEnglish(string str)
        {
            if (string.IsNullOrEmpty(str)) return false;
            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];
                // 允许大写 A-Z，小写 a-z，数字，以及空格
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == ' '))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 错误报告
        /// </summary>
        public static void ShowError(Exception err, string message = "")
        {
            // ⚠ 这里刻意【不做任何去重】。
            //
            //   去重史：键原先是固定值 "show_error_"，于是 OracleLog.ErrorOnce 把整个
            //   会话里所有 ShowError 当成同一条，只打印第一条；后来改成
            //   "类型 + 消息" 的复合键，看似合理，但仍会吞掉"同一处异常第二次以不同
            //   上下文发生"的情况 —— 实机排查时表现为日志凭空断掉，只能靠猜。
            //
            //   结论：ShowError 的每一个调用点都是失败分支（不是每帧热点），
            //   失败的【完整信息】比"不刷屏"重要得多。真要防刷屏，调用方应该用
            //   OracleLog.Exception（那才是为高频路径准备的有节流出口）。
            //
            //   最后整体 try/catch：记录错误时再抛异常，会把真正的错误盖掉，
            //   是整个插件里最糟糕的一种失败方式。
            try
            {
                string head = string.IsNullOrEmpty(message) ? "" : $"[{message}] ";
                string type = err?.GetType().Name ?? "null";
                string text = err?.Message ?? "(无消息)";

                OracleLog.Info($"[Oracle] {head}异常 {type}: {text}\n{err?.StackTrace}");

                // 走日志工具之外再补一条 Unity 侧的 Error —— 便于在 BepInEx 控制台
                // 与 LogOutput.log 里按等级过滤时一定能看到。
                UnityEngine.Debug.LogError($"[Oracle] {head}{type}: {text}\n{err?.StackTrace}");
            }
            catch
            {
                // 忽略：报告错误失败不应再引发一次失败
            }
        }
    }
}
