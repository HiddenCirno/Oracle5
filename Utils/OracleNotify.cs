using EFT.Communications;
using Oracle.Data;
using System;

namespace Oracle.Utils
{
    /// <summary>
    /// 二次封装的游戏内提示方法
    ///
    /// ⚠ IL2CPP 移植要点（踩坑记录）：
    ///
    /// 1. 5.0 的 DisplayMessageNotification 是 5 参：
    ///      void(string, ENotificationDurationType, ENotificationIconType,
    ///           Il2CppSystem.Nullable&lt;Color&gt;, bool rawText)
    ///    比 4.1 多了尾参 rawText。（4.1 为 4 参静态方法，见 NotificationManager.cs:355）
    ///
    /// 2. 【关键】Nullable&lt;T&gt; 在 interop 程序集里被表示为**引用类型**
    ///    （Il2CppSystem.Nullable`1 : Il2CppSystem.ValueType : Il2CppObjectBase）。
    ///    因此传 null 能通过编译，但运行时会在
    ///    Il2CppObjectBaseToPtrNotNull 处抛 NullReferenceException。
    ///    必须传入**实际实例**：new Il2CppSystem.Nullable&lt;Color&gt;()（HasValue = false）。
    ///
    /// 3. 保留了不依赖 Nullable 的回退路径（DisplayWarningNotification /
    ///    DisplaySingletonNotification，均为 2 参重载）。
    ///    回退会损失图标类型（Quest 金色图标退化为默认图标），但保证功能可用。
    ///    首次失败后自动降级，不会每帧重试。
    /// </summary>
    internal static class OracleNotify
    {
        /// <summary>完整重载是否可用；首次失败后置 false 永久降级</summary>
        private static bool _fullOverloadUsable = true;

        /// <summary>弹出一个通知</summary>
        public static void Message(string message, ENotificationIconType notificationType = ENotificationIconType.Default, bool isMute = false)
        {
            if (isMute) return;
            Send(message, notificationType);
        }

        /// <summary>受全局静默影响的弹出通知</summary>
        public static void Message(string message, ENotificationIconType notificationType)
        {
            if (GlobalCfg.MuteNotice.Value) return;
            Send(message, notificationType);
        }

        /// <summary>弹出一条普通的通知</summary>
        public static void Message(string message)
        {
            Message(message, ENotificationIconType.Default, false);
        }

        /// <summary>弹出一条警告的通知</summary>
        public static void Warning(string message)
        {
            Message(message, ENotificationIconType.Alert, false);
        }

        /// <summary>弹出一条金色的通知</summary>
        public static void Success(string message)
        {
            Message(message, ENotificationIconType.Quest, false);
        }

        private static void Send(string message, ENotificationIconType iconType)
        {
            if (string.IsNullOrEmpty(message)) return;

            if (_fullOverloadUsable)
            {
                try
                {
                    // HasValue = false，语义等价于旧版的 Color? textColor = null
                    var noColor = new Il2CppSystem.Nullable<UnityEngine.Color>();

                    NotificationManager.DisplayMessageNotification(
                        message,
                        ENotificationDurationType.Default,
                        iconType,
                        noColor,
                        true); // rawText：消息已由 Oracle 自行本地化，无需游戏再处理
                    return;
                }
                catch (Exception ex)
                {
                    _fullOverloadUsable = false;
                    OracleLog.ErrorOnce("notify_full_overload_failed",
                        $"[Oracle] 完整通知重载不可用，已降级为简化重载（图标类型将丢失）: {ex.Message}");
                }
            }

            // 回退路径：这组重载只接受 (string, duration)，不涉及 Nullable
            if (iconType == ENotificationIconType.Alert)
            {
                NotificationManager.DisplayWarningNotification(message, ENotificationDurationType.Default);
            }
            else
            {
                NotificationManager.DisplaySingletonNotification(message, ENotificationDurationType.Default);
            }
        }
    }
}
