using UnityEngine;

namespace Oracle.Utils
{
    /// <summary>
    /// 鼠标光标样式。
    ///
    /// ══════════════ IL2CPP 移植要点（本文件是一次**有意的功能降级**）══════════════
    ///
    /// 4.1 的实现是纯反射：
    ///     PatchConstants.EftTypes
    ///         .SelectMany(t => t.GetMethods(...))
    ///         .FirstOrDefault(m => m.Name == "SetCursor" && 参数是 ECursorType)
    ///     setCursorMethod?.Invoke(null, new object[] { type });
    ///
    /// 这在 IL2CPP 下**必然失败**，而且是静默失败 ——
    ///   托管侧的类型是 Il2CppInterop 生成的代理，反射拿到的 MethodInfo 与原生方法无关，
    ///   Invoke 要么抛异常、要么调用到一个空实现。这正是"切到 IL2CPP 后失去反射"的典型。
    ///
    /// 5.0 中确实存在对应能力：EFT.UI.CursorSwitcher.SetCursor(ECursorType)（实例方法），
    /// 但它没有找到公开的全局访问点（不在 ClientApplication 上，也不是 MonoBehaviour 单例），
    /// 直接 new 一个又缺少 Init() 所需的数据。
    ///
    /// 因此这里**降级为只做功能上必要的事** —— 切换光标的可见性与锁定状态。
    /// 这足以让管理面板可以正常点击操作（这是打开面板的真正目的）。
    ///
    /// 已知代价：失去游戏自带的光标皮肤（打开面板时鼠标是系统默认箭头，而非塔科夫的样式）。
    /// 若后续找到 CursorSwitcher 的可靠获取途径，把下面两行替换成 SetCursor 调用即可。
    /// </summary>
    public static class CursorSettings
    {
        /// <summary>设置光标样式（当前为功能降级实现，见类注释）</summary>
        public static void SetCursor(EFT.UI.ECursorType type)
        {
            // Invisible 用于游戏内（锁定并隐藏），其余用于面板打开时（可见可点）
            bool visible = type != EFT.UI.ECursorType.Invisible;
            Cursor.visible = visible;
        }
    }
}
