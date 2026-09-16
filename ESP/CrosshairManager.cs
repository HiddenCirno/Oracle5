using BepInEx.Configuration;
using EFT;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Oracle.Data;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.ESP
{
    /// <summary>
    /// 屏幕准星
    ///
    /// IL2CPP 移植要点：
    ///   1. 【字节数组】Texture2D 的图片解码 API 在 interop 中签名是
    ///        ImageConversion.LoadImage(Texture2D, Il2CppStructArray&lt;byte&gt;, bool)
    ///      参数不是托管 byte[]。好在 Il2CppStructArray&lt;T&gt; 提供了
    ///      op_Implicit(T[]) 隐式转换，托管数组可直接传入。
    ///   2. 【绘制时机】OnGUI 的绘制入口在 OracleBehaviour 中，
    ///      且准星需要在 EventType.Repaint 判断【之前】执行（与旧版一致），
    ///      因此这里不做 Repaint 判断 —— 由调用方保证时序。
    /// </summary>
    public class CrosshairManager : IOracleCrosshair
    {
        /// <summary>当前使用的准星图片缓存</summary>
        private static Texture2D _cachedCrosshairTex;

        /// <summary>上一次加载的准星名字（避免重复加载）</summary>
        private static string _lastLoadedCrosshairName = "";

        /// <summary>无图时的占位名</summary>
        public const string FallbackImageName = "No Image";

        /// <summary>准星目录</summary>
        public static string CrosshairDirectory => Path.Combine(OraclePaths.PluginDir, "crosshairs");

        public void SubscribeEvent()
        {
            OracleEvent.OnDrawCrosshair += DrawCrosshair;
        }

        /// <summary>从本地文件读取图片并生成 Texture2D</summary>
        public static void LoadCrosshairTexture()
        {
            string fileName = CrosshairManagerCfg.SelectedCrosshair?.Value;
            if (string.IsNullOrEmpty(fileName) || fileName == FallbackImageName) return;

            string fullPath = Path.Combine(CrosshairDirectory, fileName);
            if (!File.Exists(fullPath)) return;

            // 已加载且未换图则跳过
            if (_lastLoadedCrosshairName == fileName && _cachedCrosshairTex != null) return;

            try
            {
                // 手动释放旧纹理：UnityEngine.Object 不受 .NET GC 管理，
                // 不显式 Destroy 会在每次换图时泄漏一张纹理
                if (_cachedCrosshairTex != null)
                {
                    UnityEngine.Object.Destroy(_cachedCrosshairTex);
                    _cachedCrosshairTex = null;
                }

                byte[] fileData = File.ReadAllBytes(fullPath);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,

                    // ⚠ 这一行不可省略。
                    //
                    // HideAndDontSave 含 DontUnloadUnusedAsset，能阻止 Unity 在场景加载时
                    // 自动执行的 Resources.UnloadUnusedAssets() 回收本纹理。
                    //
                    // 踩坑记录：最初漏了这行，日志显示纹理已成功创建（40x40），但游戏内
                    // 完全不显示。原因是运行时 new 出来的 Texture2D **只被托管静态字段引用**，
                    // 原生侧无任何引用，场景加载时被判定为「未使用资源」销毁；
                    // 之后 _cachedCrosshairTex == null 走 Unity 伪 null 语义返回 true，
                    // 绘制函数直接提前返回 —— 全程无异常、无日志，纯静默失效。
                    //
                    // 对照：OracleRendering.EspMaterial 一直设了这行，所以 ESP 正常工作。
                    hideFlags = HideFlags.HideAndDontSave
                };

                // 显式写在类型处：让「托管数组 → Il2CppStructArray」的转换意图可见，
                // 而不是依赖隐式转换（后者在阅读时容易被误认为传了托管数组）
                tex.LoadImage((Il2CppStructArray<byte>)fileData);

                _cachedCrosshairTex = tex;
                _lastLoadedCrosshairName = fileName;

                OracleLog.Once("crosshair_loaded",
                    $"[Oracle] 准星已加载: {fileName} ({tex.width}x{tex.height})");
            }
            catch (Exception ex)
            {
                OracleLog.ErrorOnce($"crosshair_load_failed_{fileName}",
                    $"[Oracle] 准星图片加载失败 ({fileName}): {ex.Message}");
            }
        }

        /// <summary>绘制准星（屏幕正中）</summary>
        public static void DrawCrosshair()
        {
            if (!CrosshairManagerCfg.EnableCrosshair.Value) return;

            // 纹理可能因资源回收而失效（见 LoadCrosshairTexture 中的 hideFlags 说明）。
            // 这里做一次自愈：失效就重新加载，避免一次意外导致功能永久静默。
            if (_cachedCrosshairTex == null)
            {
                _lastLoadedCrosshairName = "";   // 清掉缓存标记，强制重载
                LoadCrosshairTexture();
                if (_cachedCrosshairTex == null) return;
            }

            Player me = OracleGameState.LocalPlayer;
            if (me == null) return;

            // 据枪瞄准时交出准星控制权给游戏本体
            var pwa = me.ProceduralWeaponAnimation;
            if (pwa != null && pwa.IsAiming) return;

            float texWidth = _cachedCrosshairTex.width;
            float texHeight = _cachedCrosshairTex.height;

            float x = (Screen.width - texWidth) / 2f;
            float y = (Screen.height - texHeight) / 2f;

            GUI.DrawTexture(new Rect(x, y, texWidth, texHeight), _cachedCrosshairTex);

            // 首次成功绘制时打点一次，便于确认链路（正常后不再输出）
            OracleLog.Once("crosshair_first_draw",
                $"[Oracle] 准星已绘制: {texWidth}x{texHeight} @ {x},{y} (屏幕 {Screen.width}x{Screen.height})");
        }
    }

    /// <summary>
    /// 配置项定义
    /// </summary>
    public class CrosshairManagerCfg : IOracleCfg
    {
        public static ConfigEntry<bool> EnableCrosshair;
        public static ConfigEntry<string> SelectedCrosshair;

        public void Initialize(ConfigFile config)
        {
            // 首次运行时目录可能不存在，先建出来再扫描
            if (!Directory.Exists(CrosshairManager.CrosshairDirectory))
            {
                Directory.CreateDirectory(CrosshairManager.CrosshairDirectory);
            }

            // 扫描目录下所有 png 作为可选项
            List<string> availableImages = new List<string>();
            try
            {
                foreach (string path in Directory.GetFiles(CrosshairManager.CrosshairDirectory, "*.png"))
                {
                    availableImages.Add(Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                OracleLog.ErrorOnce("crosshair_scan_failed",
                    $"[Oracle] 准星目录扫描失败: {ex.Message}");
            }

            if (availableImages.Count == 0)
            {
                availableImages.Add(CrosshairManager.FallbackImageName);
            }

            const string section = "0. 联觉信标 / Draw Module";

            EnableCrosshair = config.Bind(
                section, "启用自定义准星", true,
                new ConfigDescription("cfg_global_module_screen_crosshair_enable_desc".i18n(), null,
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_screen_crosshair_enable_name".i18n(),
                        IsAdvanced = false,
                        Order = 395
                    }));

            // ⚠ 这里用 AcceptableValueList 会在 F12 中生成下拉框，
            //   而本环境的 ConfigurationManager 下拉渲染依赖 GUI.SelectionGrid，
            //   该方法在 IL2CPP unstrip 阶段失败 → 展开该项会刷异常。
            //   这是环境缺陷（BepInEx.cfg 自身的下拉项同样受影响），非本插件问题。
            //   保持与原版一致；待 SPT 修复后自然恢复。
            SelectedCrosshair = config.Bind(
                section, "选择准星样式", availableImages[0],
                new ConfigDescription("cfg_global_module_choose_screen_crosshair_desc".i18n(),
                    new AcceptableValueList<string>(availableImages.ToArray()),
                    new ConfigurationManagerAttributes
                    {
                        DispName = "cfg_global_module_choose_screen_crosshair_name".i18n(),
                        IsAdvanced = false,
                        Order = 394
                    }));

            // 切换准星时重新加载纹理
            SelectedCrosshair.SettingChanged += (sender, args) => CrosshairManager.LoadCrosshairTexture();

            // 启动时加载一次（配置已就绪，但纹理要等到进入游戏后才画得出来）
            CrosshairManager.LoadCrosshairTexture();
        }
    }
}
