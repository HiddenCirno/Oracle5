using BepInEx.Configuration;
using EFT;
using Oracle.Data;
using Oracle.Utils;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Combat
{
    /// <summary>
    /// 闪现 —— 沿视线方向把玩家向前传送一段距离。
    ///
    /// 天堂支点模块最后一个补齐的功能。它是纯按键驱动，不含任何 Harmony 补丁，
    /// 因此在"补丁数量"对账里不可见，只在配置项 diff 中露出。
    ///
    /// IL2CPP 移植说明：本功能依赖的三个 API 在 SPT5 中签名均未变（已核对 interop 元数据）：
    ///   · Player.Position        —— UnityEngine.Vector3，get/set 均可用
    ///   · Player.Teleport        —— void Teleport(Vector3 position, bool onServerToo)
    ///   · Camera.main.transform.forward
    /// 故逻辑与原版逐行等价，仅按本工程规范补上状态防御。
    ///
    /// ⚠ 关于 onServerToo = true：
    ///   这是 4.1 的既有行为，会把新位置同步给服务器。保留它是刻意的 ——
    ///   否则客户端与服务器位置不一致，AI 会朝着"幽灵位置"开枪，且可能被判定为异常位移。
    /// </summary>
    public static class FlashPlayer
    {
        /// <summary>
        /// 向前传送。
        ///
        /// 与原版的唯一差异是增加了状态防御：IL2CPP 下被销毁对象的托管包装会
        /// 残留为"伪 null"（Unity 的 fake-null 语义），所以每个引用都要显式判空，
        /// 不能假定 LocalPlayer 非空就代表它对应的原生对象仍然存活。
        /// </summary>
        public static void TeleportPlayer()
        {
            // 不在战局中一律不传送：LocalPlayer 只由 SetInRaid 赋值，
            // 但仍显式检查 InRaid，避免上一局的残留引用被误用。
            if (!OracleGameState.InRaid) return;

            Player mainPlayer = OracleGameState.LocalPlayer;
            if (mainPlayer == null) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 forwardDir = cam.transform.forward;
            Vector3 targetPos = mainPlayer.Position + forwardDir * FlashPlayerCfg.FlashDistance.Value;

            // 第二个参数 onServerToo=true：同步给服务器（见类注释）
            mainPlayer.Teleport(targetPos, true);
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    [OracleCfgOrder(1)]
    public class FlashPlayerCfg : IOracleCfg, IOracleKeyUpdate
    {
        internal static ConfigEntry<KeyCode> FlashKey { get; set; }
        internal static ConfigEntry<float> FlashDistance { get; set; }

        public void Initialize(ConfigFile config)
        {
            FlashKey = config.Bind(
                "1. 天堂支点 / Combat Module",
                "闪现快捷键",
                KeyCode.Z,
                new ConfigDescription(
                    "cfg_combat_module_flash_key_desc".i18n(),
                    null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_flash_key_name".i18n(),
                        IsAdvanced = false,
                        Order = 270
                    }
                )
            );

            FlashDistance = config.Bind(
                "1. 天堂支点 / Combat Module",
                "闪现距离",
                3f,
                new ConfigDescription(
                    "cfg_combat_module_flash_distance_desc".i18n(),
                    new AcceptableValueRange<float>(0f, 1000f),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_combat_module_flash_distance_name".i18n(),
                        IsAdvanced = false,
                        Order = 269
                    }
                )
            );
        }

        public void RegisterKeyUpdate()
        {
            OracleEvent.OnUpdate += KeyUpdate;
        }

        /// <summary>
        /// 按键监听：按下即传送。
        ///
        /// 刻意不发游戏内通知 —— 闪现是高频重复动作，每次弹提示会刷屏。
        /// 4.1 同样没有提示，此处保持一致。
        /// </summary>
        public static void KeyUpdate()
        {
            if (Input.GetKeyDown(FlashKey.Value))
            {
                FlashPlayer.TeleportPlayer();
            }
        }
    }
}
