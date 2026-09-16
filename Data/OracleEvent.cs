using BepInEx.Configuration;
using System;
using System.Linq;
using UnityEngine;
using System.Reflection;

namespace Oracle.Data
{
    /// <summary>
    /// 事件管理器
    ///
    /// IL2CPP 移植说明：这里的三处反射自举（配置/按键/事件订阅扫描）
    /// 作用对象是【插件自身程序集】，不涉及游戏 IL2CPP 类型，因此在 IL2CPP 下依然有效。
    /// 唯一调整是把 Assembly.GetExecutingAssembly() 换成 typeof(X).Assembly，
    /// 后者在插件被 CoreCLR 加载时定位更可靠。
    /// </summary>
    internal class OracleEvent
    {
        //事件定义
        public static Action OnUpdate;
        public static Action OnKeyUpdate;
        public static Action OnDrawManagerGUI;
        public static Action OnDrawESP;
        public static Action OnDrawAimbot;
        public static Action OnDrawCrosshair;

        /// <summary>绘制事件执行包装</summary>
        public static void Draw()
        {
            OnDrawESP?.Invoke();
            OnDrawAimbot?.Invoke();
        }

        /// <summary>准星绘制事件执行包装</summary>
        public static void DrawCrosshair()
        {
            OnDrawCrosshair?.Invoke();
        }

        /// <summary>管理面板绘制事件执行包装</summary>
        public static void DrawManagerGUI()
        {
            OnDrawManagerGUI?.Invoke();
        }

        /// <summary>更新事件执行包装</summary>
        public static void Update()
        {
            OnUpdate?.Invoke();
            OnKeyUpdate?.Invoke();
        }

        /// <summary>
        /// 配置项初始化
        /// </summary>
        public static void InitializeConfigs(ConfigFile config)
        {
            Type targetInterface = typeof(OracleInterface.IOracleCfg);
            Type[] allTypes = typeof(OracleEvent).Assembly.GetTypes();

            //通过标签排序
            var ordered = allTypes
                .Where(t => targetInterface.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Select(t => new
                {
                    Type = t,
                    Order = t.GetCustomAttribute<OracleCfgOrderAttribute>()?.Order ?? 100
                })
                .OrderBy(x => x.Order)
                .Select(x => (OracleInterface.IOracleCfg)Activator.CreateInstance(x.Type));

            //遍历并实例化, 初始化配置项
            foreach (var cfg in ordered)
            {
                try
                {
                    cfg.Initialize(config);
                    Debug.Log($"[Oracle] 初始化配置: {cfg.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Oracle] 初始化失败 {cfg.GetType().Name}: {ex}");
                }
            }
        }

        /// <summary>
        /// 初始化按键监听
        /// </summary>
        public static void InitializeKeyUpdate()
        {
            Type targetInterface = typeof(OracleInterface.IOracleKeyUpdate);
            Type[] allTypes = typeof(OracleEvent).Assembly.GetTypes();

            foreach (Type type in allTypes)
            {
                if (targetInterface.IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                {
                    try
                    {
                        OracleInterface.IOracleKeyUpdate configInstance =
                            (OracleInterface.IOracleKeyUpdate)Activator.CreateInstance(type);
                        configInstance.RegisterKeyUpdate();

                        Debug.Log($"[Oracle] 成功挂载监听: {type.Name}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Oracle] 挂载监听 {type.Name} 失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 初始化事件订阅器
        /// </summary>
        public static void InitializeEventSubscribe()
        {
            Type targetInterface = typeof(OracleInterface.IOracleEventSubscribe);
            Type[] allTypes = typeof(OracleEvent).Assembly.GetTypes();

            foreach (Type type in allTypes)
            {
                if (targetInterface.IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                {
                    try
                    {
                        OracleInterface.IOracleEventSubscribe configInstance =
                            (OracleInterface.IOracleEventSubscribe)Activator.CreateInstance(type);
                        configInstance.SubscribeEvent();

                        Debug.Log($"[Oracle] 成功挂载事件: {type.Name}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Oracle] 挂载事件 {type.Name} 失败: {ex.Message}");
                    }
                }
            }
        }
    }
}
