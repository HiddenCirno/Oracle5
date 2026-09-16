using Comfort.Common;
using EFT.UI;
using Oracle.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 光标与输入管理 —— 打开任意管理面板时解锁鼠标，关闭时重新锁定。
    ///
    /// ══════════════ 与 4.1 的结构性差异 ══════════════
    ///
    /// 4.1 直接硬编码查询两个面板的单例状态：
    ///     var unlock = RaidManagerGUI._isMenuOpen || ItemManagerGUI._isMenuOpen;
    ///
    /// 问题在于这让"面板"与"光标管理"互相纠缠：每新增一个面板都要回来改这一行，
    /// 而且 ItemManagerGUI（属奇迹之门）会因此反向依赖 RaidManagerGUI（属创世引擎）。
    ///
    /// 这里改成**注册表**：面板在初始化时把自己的开启状态查询器注册进来，
    /// MouseManager 只管汇总。新增面板无需改动本文件，两个模块也就解耦了。
    /// </summary>
    public static class MouseManager
    {
        private static GameObject _inputManager;

        /// <summary>各管理面板的"是否打开"查询器</summary>
        private static readonly List<Func<bool>> _openCheckers = new List<Func<bool>>();

        /// <summary>
        /// 注册一个管理面板。参数是返回"该面板当前是否打开"的委托。
        /// 须在面板初始化时调用一次。
        /// </summary>
        public static void RegisterMenu(Func<bool> isOpen)
        {
            if (isOpen == null) return;
            if (!_openCheckers.Contains(isOpen)) _openCheckers.Add(isOpen);
        }

        /// <summary>当前是否有任一管理面板处于打开状态</summary>
        public static bool AnyMenuOpen
        {
            get
            {
                for (int i = 0; i < _openCheckers.Count; i++)
                {
                    try
                    {
                        if (_openCheckers[i]()) return true;
                    }
                    catch (Exception ex)
                    {
                        // 单个面板的状态查询失败不应影响整体判断
                        OracleLog.ErrorOnce($"menu_check_{i}", $"[Oracle] 面板状态查询异常: {ex.Message}");
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// 依据当前所有面板的开关状态，重新设置光标与输入。
        /// 任意面板打开即解锁鼠标，全部关闭则重新锁定。
        /// </summary>
        public static void ToggleCursor()
        {
            bool unlock = AnyMenuOpen;

            if (_inputManager == null)
            {
                // 游戏的输入管理器对象；用于在面板打开时停用游戏内视角控制
                _inputManager = GameObject.Find("___Input");
            }

            Cursor.visible = unlock;

            if (unlock)
            {
                Cursor.lockState = CursorLockMode.None;
                CursorSettings.SetCursor(EFT.UI.ECursorType.Idle);
                PlaySound(EUISoundType.MenuContextMenu);
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                CursorSettings.SetCursor(EFT.UI.ECursorType.Invisible);
                PlaySound(EUISoundType.MenuDropdown);
            }

            // 面板打开时停用游戏输入，避免鼠标移动仍然带动视角
            if (_inputManager != null) _inputManager.SetActive(!unlock);
        }

        /// <summary>
        /// 播放 UI 音效。单例可能尚未就绪（例如在主菜单），因此必须容错 ——
        /// 原版直接访问 Instance 会在某些时机抛空引用。
        /// </summary>
        private static void PlaySound(EUISoundType soundType)
        {
            try
            {
                if (!Singleton<GUISounds>.Instantiated) return;
                var sounds = Singleton<GUISounds>.Instance;
                if (sounds == null) return;
                sounds.PlayUISound(soundType);
            }
            catch (Exception ex)
            {
                OracleLog.ErrorOnce("gui_sound_fail", $"[Oracle] UI 音效播放失败: {ex.Message}");
            }
        }
    }
}
