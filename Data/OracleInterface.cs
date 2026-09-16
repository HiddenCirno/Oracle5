using BepInEx.Configuration;

namespace Oracle.Data
{
    /// <summary>
    /// 接口和数据结构定义
    ///
    /// 这些接口是 Oracle 的自举核心：OracleEvent 会在启动时扫描本程序集，
    /// 找出所有实现类并实例化。因为它们作用在【插件自身程序集】上，
    /// 不涉及游戏的 IL2CPP 类型，所以反射自举在 IL2CPP 下依然有效。
    /// </summary>
    internal class OracleInterface
    {
        /// <summary>
        /// 通用配置接口
        /// 所有继承此接口的类将在启动时被自动扫描并注册
        /// </summary>
        public interface IOracleCfg
        {
            /// <summary>
            /// 配置项初始化
            /// </summary>
            /// <param name="config">传入配置实例</param>
            void Initialize(ConfigFile config);
        }

        /// <summary>
        /// 通用快捷键监听接口
        /// </summary>
        public interface IOracleKeyUpdate
        {
            /// <summary>
            /// 注册按键监听
            /// </summary>
            void RegisterKeyUpdate();
        }

        /// <summary>
        /// 事件订阅接口类
        /// </summary>
        public interface IOracleEventSubscribe
        {
            /// <summary>
            /// 订阅事件
            /// </summary>
            void SubscribeEvent();
        }

        /// <summary>
        /// ManagerGUI使用的订阅接口
        /// </summary>
        public interface IOracleManagerGUI : IOracleEventSubscribe
        {
        }

        /// <summary>
        /// ESP使用的订阅接口
        /// </summary>
        public interface IOracleESP : IOracleEventSubscribe
        {
        }

        /// <summary>
        /// 准星使用的订阅接口
        /// </summary>
        public interface IOracleCrosshair : IOracleEventSubscribe
        {
        }

        /// <summary>
        /// 自瞄使用的订阅接口
        /// </summary>
        public interface IOracleAimbot : IOracleEventSubscribe
        {
        }
    }
}
