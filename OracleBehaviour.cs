using Oracle.Data;
using Oracle.Overlay;
using Oracle.Utils;
using System;
using UnityEngine;

namespace Oracle
{
    /// <summary>
    /// Oracle 的每帧驱动组件。
    ///
    /// ⚠ IL2CPP 移植要点（这是与 Mono 时代最大的架构差异）：
    ///   BepInEx 6 的 BasePlugin **不是 MonoBehaviour**，没有 Update/OnGUI/StartCoroutine。
    ///   要让引擎回调我们，必须把托管类型注册进 IL2CPP 类型系统，再挂到 GameObject 上。
    ///   漏掉注册这一步的话，组件收不到任何引擎回调，且不报错、不崩溃、完全静默。
    ///
    /// 挂载必须走 BepInEx 的 BasePlugin.AddComponent&lt;T&gt;()，详见 PluginsCore.MountBehaviour。
    ///
    /// 构造函数必须是 (IntPtr) 形式 —— Il2CppInterop 注入类型统一用原生指针构造。
    /// </summary>
    public class OracleBehaviour : MonoBehaviour
    {
        /// <summary>调试 HUD 样式，首次绘制时创建并缓存</summary>
        private static GUIStyle _debugStyle;

        /// <summary>
        /// Il2CppInterop 要求注入类型提供 (IntPtr) 构造函数。
        /// </summary>
        public OracleBehaviour(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            try
            {
                // 能力模块的每帧逻辑。
                // 原版把这些做成挂在玩家身上的组件，这里统一并入本组件，
                // 省掉第二套类型注入与组件生命周期管理。
                Ability.InfinityStamina.UpdateTick();

                OracleEvent.Update();

                // 叠加层由 UpdateNativeOverlay 统一管理生命周期
                // （EnableNativeOverlay 首次启用时才初始化窗口与 GDI 渲染线程）
                NativeOverlay.UpdateNativeOverlay();
            }
            catch (Exception ex)
            {
                // 每帧异常必须节流 —— 一次异常风暴能在几秒内把日志撑到几百 MB
                OracleLog.Exception(nameof(Update), nameof(Update), ex);
            }
        }

        /// <summary>
        /// IMGUI 绘制入口（对应旧 PluginsCore.OnGUI）。
        ///
        /// 注意事件分发顺序：管理面板与准星必须在 EventType.Repaint 判断【之前】调用，
        /// 因为它们需要处理鼠标/键盘事件；而 ESP 与自瞄只在重绘阶段绘制，避免重复绘制。
        /// </summary>
        private void OnGUI()
        {
            try
            {
                // 全局绘制开关
                if (!GlobalCfg.UniGUI.Value) return;

                // 不受重绘限制的绘制事件（需要接收交互事件）
                OracleEvent.DrawManagerGUI();
                OracleEvent.DrawCrosshair();

                // 以下仅重绘阶段执行
                if (Event.current.type != EventType.Repaint) return;

                // 左上角状态 HUD —— 移植期验证游戏对象捕获链路用的，默认关闭。
                // 开关归在「0. 联觉信标 / Draw Module」分区（见 NativeOverlayCfg.OverlayDebugHud）。
                //
                // ⚠ 它是 IMGUI 绘制的，不走叠加层那条 GDI 路径，所以开启后会压在所有 ESP 之上。
                if (NativeOverlayCfg.OverlayDebugHud.Value)
                {
                    DrawDebugHud();
                }

                // ⚠ 两条绘制路径**互斥**：叠加层开启时由 GDI 渲染线程接管 ESP/自瞄的绘制，
                //   此时必须跳过 OnGUI 路径，否则同一份 ESP 会被画两遍。
                if (NativeOverlayCfg.EnableNativeOverlay.Value)
                {
                    // 数据桥：主线程把 3D 数据预计算成屏幕空间 2D 原语，
                    // 后台 GDI 渲染线程只消费原语 —— 不涉及 RenderTexture / GPU 回读。
                    var block = NativeOverlay.Store.AcquireWriteBlock();
                    if (block == null)
                    {
                        // ★ 池空的诊断原先走 System.Console.WriteLine（在 BepInEx IL2CPP 下是黑洞），
                        //   所以"池已耗尽"这个致命状态一直在发生、却从没进过日志。
                        OracleLog.Throttled("overlay_pool_empty",
                            "[叠加层] 原语池已耗尽，无法发布新帧 —— 画面会永久定格在最后一帧");
                        return;
                    }

                    try
                    {
                        Camera cam = Camera.main;

                        // ★★ 不在战局中（或相机无效）时，发布【空帧】而不是直接 return。
                        //
                        //  原实现是 `if (cam == null) return;` —— 渲染线程拿不到新帧，
                        //  而它是"保留模式"（无新帧则不动窗口），于是上一局最后一帧
                        //  被永久留在那个 WS_EX_TOPMOST 窗口上，盖住结算界面、主菜单
                        //  以及之后所有战局。
                        //
                        //  叠加层在非战局状态下本来就该是空的，所以这里发空帧是
                        //  与正常行为一致的，而不是特例。
                        if (OracleGameState.InRaid && cam != null)
                        {
                            OverlayPrimitiveBuilder.Build(cam, block);
                        }
                        // block 在 AcquireWriteBlock 里已 Reset，未 Build 时就是空帧
                        NativeOverlay.Store.Publish(block);
                    }
                    catch (Exception ex)
                    {
                        // ★★ 异常时【必须】把块还回池里。
                        //
                        //  原实现只 catch 了日志；AcquireWriteBlock 拿走的块既不 Publish
                        //  也不归还 → 块凭空消失。池里只有 3 块，连抛 3 帧即永久枯竭，
                        //  之后 AcquireWriteBlock 永远返回 null，叠加层再也不会更新。
                        NativeOverlay.Store.ReturnBlock(block);
                        OracleLog.Exception(nameof(OnGUI), "OverlayBuild", ex);
                    }
                    return;
                }

                OracleEvent.Draw();
            }
            catch (Exception ex)
            {
                OracleLog.Exception(nameof(OnGUI), nameof(OnGUI), ex);
            }
        }

        /// <summary>
        /// 调试 HUD —— 用于验证游戏对象捕获链路（Phase 2 验证用，验证后可删）。
        ///
        /// 刻意使用英文：中文字形会触碰到游戏 TMP 中文字体资源的问题
        /// （日志里那种 'SimSun-Chinese SDF' 警告）。
        /// </summary>
        private static void DrawDebugHud()
        {
            if (_debugStyle == null)
            {
                _debugStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold
                };
            }

            // InRaid 内部走 Unity 的伪 null 检测，战局结束后会自动变 false，
            // 所以这里不会读到已销毁的对象。
            if (OracleGameState.InRaid)
            {
                var player = OracleGameState.LocalPlayer;
                string pos = player != null ? player.Transform.position.ToString() : "<null>";

                _debugStyle.normal.textColor = Color.green;
                GUI.Label(
                    new Rect(10f, 10f, 1200f, 28f),
                    $"[Oracle] RAID | nick={OracleGameState.LocalNickname} " +
                    $"group={(string.IsNullOrEmpty(OracleGameState.LocalGroupId) ? "<solo>" : OracleGameState.LocalGroupId)} " +
                    $"alive={OracleGameState.AlivePlayerCount} pos={pos}",
                    _debugStyle);
            }
            else
            {
                _debugStyle.normal.textColor = Color.yellow;
                GUI.Label(new Rect(10f, 10f, 1200f, 28f), "[Oracle] LOADED | waiting for raid...", _debugStyle);
            }
        }
    }
}
