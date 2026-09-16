using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 全局 UI 样式（创世引擎各面板共用）。
    ///
    /// ══════════════ IL2CPP 移植要点 ══════════════
    ///
    /// 唯一但致命的改动在 MakeTex：
    ///
    ///   这些 GUIStyle 的背景贴图全部是**运行时创建**的 Texture2D。
    ///   若不设 hideFlags，它们会被 Resources.UnloadUnusedAssets() 当作无主资源回收 ——
    ///   之后所有面板都会变成"能点但看不见"的模样（文字/按钮背景消失）。
    ///
    ///   本工程已经踩过一次同类事故：准星贴图曾在场景加载后被回收，
    ///   表现为"日志显示已加载，但屏幕上什么都没有"。
    ///
    ///   HideAndDontSave = HideInHierarchy | DontSaveInEditor | NotEditable |
    ///                     DontSaveInBuild | DontUnloadUnusedAsset
    ///   —— 最后一项才是关键。
    ///
    /// 其余逻辑与 4.1 逐行等价（纯 IMGUI，无游戏 API 依赖）。
    /// </summary>
    public static class UIStyleManager
    {
        // 窗口样式
        public static GUIStyle WindowStyle { get; private set; }
        // 普通容器背景
        public static GUIStyle BoxStyle { get; private set; }
        // 选中容器背景（高亮）
        public static GUIStyle SelectedBoxStyle { get; private set; }
        // 普通按钮（灰色）
        public static GUIStyle NormalButtonStyle { get; private set; }
        // 红色按钮
        public static GUIStyle RedButtonStyle { get; private set; }
        // 蓝色按钮
        public static GUIStyle BlueButtonStyle { get; private set; }
        // 关闭按钮（复用红色按钮）
        public static GUIStyle CloseButtonStyle => RedButtonStyle;
        // 输入框样式
        public static GUIStyle TextFieldStyle { get; private set; }

        // 纵向滚动条背景与滑块
        public static GUIStyle ScrollbarStyle { get; private set; }
        public static GUIStyle ScrollbarThumbStyle { get; private set; }

        // 横向滚动条背景与滑块
        public static GUIStyle HScrollbarStyle { get; private set; }
        public static GUIStyle HScrollbarThumbStyle { get; private set; }

        // 顶部选项卡样式
        public static GUIStyle TabStyle { get; private set; }

        // 横向滑块（轨道 + 滑块头）
        // 注意：GUILayout.HorizontalSlider 只认 GUI.skin.horizontalSlider / horizontalSliderThumb，
        //       用的时候要临时换一下 GUI.skin（见各面板换 scrollbar 的写法）
        public static GUIStyle SliderStyle { get; private set; }
        public static GUIStyle SliderThumbStyle { get; private set; }

        // 下拉表（收起态按钮 + 展开条目 / 选中条目）
        public static GUIStyle DropdownStyle { get; private set; }
        public static GUIStyle DropdownItemStyle { get; private set; }
        public static GUIStyle DropdownItemSelectedStyle { get; private set; }

        // 按钮式开关（本项目不使用 Toggle 控件与勾选框，开关一律用按钮表达）
        public static GUIStyle ToggleOnStyle { get; private set; }
        public static GUIStyle ToggleOffStyle { get; private set; }

        // 表单用标签：左表头（浅灰）、右读数（白色右对齐）、区块标题（加粗）
        public static GUIStyle LabelStyle { get; private set; }
        public static GUIStyle ValueLabelStyle { get; private set; }
        public static GUIStyle SectionTitleStyle { get; private set; }

        private static bool _initialized;

        public static void EnsureInitialized()
        {
            // 贴图被回收时 normal.background 会变成伪 null，因此这里一并复查
            if (_initialized && WindowStyle?.normal.background != null) return;

            // ----- 1. 窗口样式 -----
            WindowStyle = new GUIStyle(GUI.skin.window);
            WindowStyle.normal.background = MakeTex(1, 1, new Color(0.15f, 0.16f, 0.18f, 1f));
            WindowStyle.focused.background = WindowStyle.normal.background;
            WindowStyle.onNormal.background = WindowStyle.normal.background;
            WindowStyle.normal.textColor = Color.white;
            WindowStyle.border = new RectOffset(1, 1, 20, 1);

            // ----- 2. 普通容器背景 -----
            BoxStyle = new GUIStyle(GUI.skin.box);
            BoxStyle.normal.background = MakeTex(1, 1, new Color(0.20f, 0.21f, 0.23f, 1f));
            BoxStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            BoxStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 3. 选中容器背景 -----
            SelectedBoxStyle = new GUIStyle(GUI.skin.box);
            SelectedBoxStyle.normal.background = MakeTex(1, 1, new Color(0.28f, 0.32f, 0.38f, 1f));
            SelectedBoxStyle.normal.textColor = Color.white;
            SelectedBoxStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 4. 普通按钮 -----
            NormalButtonStyle = new GUIStyle(GUI.skin.button);
            NormalButtonStyle.normal.background = MakeTex(1, 1, new Color(0.25f, 0.26f, 0.28f, 1f));
            NormalButtonStyle.hover.background = MakeTex(1, 1, new Color(0.35f, 0.36f, 0.39f, 1f));
            NormalButtonStyle.active.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            NormalButtonStyle.normal.textColor = Color.white;
            NormalButtonStyle.hover.textColor = Color.white;
            NormalButtonStyle.active.textColor = Color.gray;
            NormalButtonStyle.border = new RectOffset(0, 0, 0, 0);
            NormalButtonStyle.margin = new RectOffset(2, 2, 2, 2);

            // ----- 5. 红色按钮 -----
            RedButtonStyle = new GUIStyle(NormalButtonStyle);
            RedButtonStyle.normal.background = MakeTex(1, 1, new Color(0.5f, 0.2f, 0.2f));
            RedButtonStyle.hover.background = MakeTex(1, 1, new Color(0.7f, 0.3f, 0.3f));
            RedButtonStyle.active.background = MakeTex(1, 1, new Color(0.3f, 0.2f, 0.2f));
            RedButtonStyle.alignment = TextAnchor.MiddleCenter;

            // ----- 6. 蓝色按钮 -----
            BlueButtonStyle = new GUIStyle(NormalButtonStyle);
            BlueButtonStyle.normal.background = MakeTex(1, 1, new Color(0.2f, 0.3f, 0.5f));
            BlueButtonStyle.hover.background = MakeTex(1, 1, new Color(0.3f, 0.4f, 0.6f));
            BlueButtonStyle.active.background = MakeTex(1, 1, new Color(0.1f, 0.2f, 0.3f));
            BlueButtonStyle.alignment = TextAnchor.MiddleCenter;

            // ----- 7. 输入框样式 -----
            TextFieldStyle = new GUIStyle(GUI.skin.textField);
            TextFieldStyle.normal.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            TextFieldStyle.hover.background = MakeTex(1, 1, new Color(0.15f, 0.16f, 0.18f, 1f));
            TextFieldStyle.hover.textColor = Color.white;
            TextFieldStyle.focused.background = MakeTex(1, 1, new Color(0.18f, 0.20f, 0.22f, 1f));
            TextFieldStyle.normal.textColor = Color.white;
            TextFieldStyle.focused.textColor = Color.white;
            TextFieldStyle.border = new RectOffset(0, 0, 0, 0);
            TextFieldStyle.margin = new RectOffset(2, 2, 2, 2);
            TextFieldStyle.alignment = TextAnchor.MiddleLeft;

            // ----- 8. 纵向滚动条 -----
            ScrollbarStyle = new GUIStyle(GUI.skin.verticalScrollbar);
            ScrollbarStyle.normal.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            ScrollbarStyle.fixedWidth = 10f;
            ScrollbarStyle.border = new RectOffset(0, 0, 0, 0);

            ScrollbarThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            ScrollbarThumbStyle.normal.background = MakeTex(1, 1, new Color(0.3f, 0.31f, 0.33f, 1f));
            ScrollbarThumbStyle.hover.background = MakeTex(1, 1, new Color(0.4f, 0.41f, 0.43f, 1f));
            ScrollbarThumbStyle.active.background = MakeTex(1, 1, new Color(0.5f, 0.51f, 0.53f, 1f));
            ScrollbarThumbStyle.fixedWidth = 10f;
            ScrollbarThumbStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 9. 横向滚动条 -----
            HScrollbarStyle = new GUIStyle(GUI.skin.horizontalScrollbar);
            HScrollbarStyle.normal.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            HScrollbarStyle.fixedHeight = 10f;
            HScrollbarStyle.border = new RectOffset(0, 0, 0, 0);

            HScrollbarThumbStyle = new GUIStyle(GUI.skin.horizontalScrollbarThumb);
            HScrollbarThumbStyle.normal.background = MakeTex(1, 1, new Color(0.3f, 0.31f, 0.33f, 1f));
            HScrollbarThumbStyle.hover.background = MakeTex(1, 1, new Color(0.4f, 0.41f, 0.43f, 1f));
            HScrollbarThumbStyle.active.background = MakeTex(1, 1, new Color(0.5f, 0.51f, 0.53f, 1f));
            HScrollbarThumbStyle.fixedHeight = 10f;
            HScrollbarThumbStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 10. 选项卡 -----
            TabStyle = new GUIStyle(GUI.skin.button);
            TabStyle.fontSize = 13;
            TabStyle.fontStyle = FontStyle.Bold;
            TabStyle.alignment = TextAnchor.MiddleCenter;
            TabStyle.normal.background = MakeTex(1, 1, new Color(0.18f, 0.19f, 0.21f, 1f));
            TabStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f, 1f);
            TabStyle.hover.background = MakeTex(1, 1, new Color(0.25f, 0.26f, 0.28f, 1f));
            TabStyle.hover.textColor = Color.white;
            TabStyle.onNormal.background = MakeTex(1, 1, new Color(0.2f, 0.3f, 0.5f, 1f));
            TabStyle.onNormal.textColor = Color.white;
            TabStyle.onHover.background = MakeTex(1, 1, new Color(0.3f, 0.4f, 0.6f, 1f));
            TabStyle.onHover.textColor = Color.white;
            TabStyle.active.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            TabStyle.active.textColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            TabStyle.onActive.background = MakeTex(1, 1, new Color(0.1f, 0.2f, 0.3f, 1f));
            TabStyle.onActive.textColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            TabStyle.margin = new RectOffset(0, 0, 0, 0);
            TabStyle.padding = new RectOffset(5, 5, 5, 5);
            TabStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 11. 横向滑块 -----
            SliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            SliderStyle.normal.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            SliderStyle.fixedHeight = 8f;
            SliderStyle.margin = new RectOffset(2, 2, 8, 8);
            SliderStyle.border = new RectOffset(0, 0, 0, 0);

            SliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb);
            SliderThumbStyle.normal.background = MakeTex(1, 1, new Color(0.3f, 0.31f, 0.33f, 1f));
            SliderThumbStyle.hover.background = MakeTex(1, 1, new Color(0.4f, 0.41f, 0.43f, 1f));
            SliderThumbStyle.active.background = MakeTex(1, 1, new Color(0.35f, 0.5f, 0.75f, 1f));
            SliderThumbStyle.fixedWidth = 12f;
            SliderThumbStyle.fixedHeight = 14f;
            SliderThumbStyle.border = new RectOffset(0, 0, 0, 0);

            // ----- 12. 下拉表 -----
            DropdownStyle = new GUIStyle(NormalButtonStyle);
            DropdownStyle.normal.background = MakeTex(1, 1, new Color(0.18f, 0.19f, 0.21f, 1f));
            DropdownStyle.hover.background = MakeTex(1, 1, new Color(0.25f, 0.26f, 0.28f, 1f));
            DropdownStyle.active.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            DropdownStyle.alignment = TextAnchor.MiddleLeft;
            DropdownStyle.padding = new RectOffset(8, 8, 4, 4);
            DropdownStyle.border = new RectOffset(0, 0, 0, 0);

            DropdownItemStyle = new GUIStyle(NormalButtonStyle);
            DropdownItemStyle.normal.background = MakeTex(1, 1, new Color(0.16f, 0.17f, 0.19f, 1f));
            DropdownItemStyle.hover.background = MakeTex(1, 1, new Color(0.28f, 0.30f, 0.34f, 1f));
            DropdownItemStyle.active.background = MakeTex(1, 1, new Color(0.12f, 0.13f, 0.15f, 1f));
            DropdownItemStyle.alignment = TextAnchor.MiddleLeft;
            DropdownItemStyle.padding = new RectOffset(16, 8, 2, 2);
            DropdownItemStyle.border = new RectOffset(0, 0, 0, 0);

            DropdownItemSelectedStyle = new GUIStyle(DropdownItemStyle);
            DropdownItemSelectedStyle.normal.background = MakeTex(1, 1, new Color(0.2f, 0.3f, 0.5f, 1f));
            DropdownItemSelectedStyle.normal.textColor = Color.white;
            DropdownItemSelectedStyle.hover.background = MakeTex(1, 1, new Color(0.3f, 0.4f, 0.6f, 1f));
            DropdownItemSelectedStyle.hover.textColor = Color.white;

            // ----- 13. 按钮式开关 -----
            ToggleOnStyle = new GUIStyle(NormalButtonStyle);
            ToggleOnStyle.normal.background = MakeTex(1, 1, new Color(0.2f, 0.3f, 0.5f));
            ToggleOnStyle.hover.background = MakeTex(1, 1, new Color(0.3f, 0.4f, 0.6f));
            ToggleOnStyle.active.background = MakeTex(1, 1, new Color(0.1f, 0.2f, 0.3f));
            ToggleOnStyle.normal.textColor = Color.white;
            ToggleOnStyle.hover.textColor = Color.white;
            ToggleOnStyle.alignment = TextAnchor.MiddleCenter;
            ToggleOnStyle.fontStyle = FontStyle.Bold;

            ToggleOffStyle = new GUIStyle(NormalButtonStyle);
            ToggleOffStyle.normal.background = MakeTex(1, 1, new Color(0.22f, 0.23f, 0.25f));
            ToggleOffStyle.hover.background = MakeTex(1, 1, new Color(0.3f, 0.31f, 0.33f));
            ToggleOffStyle.active.background = MakeTex(1, 1, new Color(0.15f, 0.16f, 0.18f));
            ToggleOffStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f, 1f);
            ToggleOffStyle.hover.textColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            ToggleOffStyle.alignment = TextAnchor.MiddleCenter;

            // ----- 14. 表单标签 -----
            LabelStyle = new GUIStyle(GUI.skin.label);
            LabelStyle.normal.textColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            LabelStyle.alignment = TextAnchor.MiddleLeft;
            LabelStyle.padding = new RectOffset(4, 4, 0, 0);
            LabelStyle.margin = new RectOffset(0, 0, 2, 2);
            LabelStyle.wordWrap = false;

            ValueLabelStyle = new GUIStyle(LabelStyle);
            ValueLabelStyle.normal.textColor = Color.white;
            ValueLabelStyle.alignment = TextAnchor.MiddleRight;
            ValueLabelStyle.fontStyle = FontStyle.Bold;

            SectionTitleStyle = new GUIStyle(GUI.skin.label);
            SectionTitleStyle.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);
            SectionTitleStyle.alignment = TextAnchor.MiddleLeft;
            SectionTitleStyle.fontStyle = FontStyle.Bold;
            SectionTitleStyle.fontSize = 13;
            SectionTitleStyle.padding = new RectOffset(4, 4, 2, 4);

            _initialized = true;
        }

        /// <summary>
        /// 生成 1×1 纯色贴图。
        ///
        /// ⚠ hideFlags 必须设置，否则这些贴图会在 Resources.UnloadUnusedAssets() 时被回收，
        ///   表现为所有面板"还能点，但背景与按钮全部消失"。
        /// </summary>
        private static Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; ++i) pix[i] = col;

            Texture2D result = new Texture2D(width, height);
            result.hideFlags = HideFlags.HideAndDontSave;
            result.SetPixels(pix);
            result.Apply();
            return result;
        }
    }
}
