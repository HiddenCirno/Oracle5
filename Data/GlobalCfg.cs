using BepInEx.Configuration;
using EFT.Communications;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Data
{
    /// <summary>
    /// 全局配置定义, 其实现在只剩绘制部分了
    /// </summary>
    [OracleCfgOrder(0)]
    internal class GlobalCfg : IOracleCfg, IOracleKeyUpdate
    {
        //配置定义
        internal static ConfigEntry<KeyCode> UniGUIKey { get; set; }
        internal static ConfigEntry<bool> UniGUI { get; set; }
        internal static ConfigEntry<bool> MuteNotice { get; set; }
        internal static ConfigEntry<bool> FPSLimit { get; set; }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        /// <summary>
        /// 按键监听, 挂载到Update里
        /// </summary>
        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(UniGUIKey.Value))
            {
                UniGUI.Value = !UniGUI.Value;
                var value = UniGUI.Value;
                OracleNotify.Message(
                    string.Format(
                        "message_uni_gui_enable".i18n(),
                        value ? "text_enable".i18n() : "text_disable".i18n()
                    ),
                    value ? ENotificationIconType.Default : ENotificationIconType.Alert,
                    MuteNotice.Value
                );
            }
        }

        /// <summary>
        /// 配置项初始化
        /// </summary>
        /// <param name="config">传入配置实例</param>
        public void Initialize(ConfigFile config)
        {
            UniGUI = config.Bind(
                "0. 联觉信标 / Draw Module",
                "启用绘制",
                true,
                new ConfigDescription(
                    "cfg_global_module_uni_gui_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_uni_gui_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 400
                    }
                )
            );
            UniGUIKey = config.Bind(
                "0. 联觉信标 / Draw Module",
                "切换全局绘制",
                KeyCode.Insert,
                new ConfigDescription(
                    "cfg_global_module_uni_gui_enable_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_uni_gui_enable_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 399
                    }
                )
            );
            MuteNotice = config.Bind(
                "0. 联觉信标 / Draw Module",
                "静默提示",
                false,
                new ConfigDescription(
                    "cfg_global_module_mute_notice_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_mute_notice_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 398
                    }
                )
            );
            FPSLimit = config.Bind(
                "0. 联觉信标 / Draw Module",
                "开启帧数限制",
                true,
                new ConfigDescription(
                    "cfg_global_module_overlay_fps_limit_enable_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_overlay_fps_limit_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 396
                    }
                )
            );
        }
    }
}
