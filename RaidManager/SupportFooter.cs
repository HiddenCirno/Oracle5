using Oracle.Utils;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 面板页脚支持区：左侧二维码 + 右侧支持文案，作为独立区块插在窗口最下方。
    ///
    /// 二维码不是贴图资源，而是把 37×37 的点阵硬编码进来，运行时逆向生成 Texture2D：
    ///   · 模块按整数倍(ModuleScale)展开写入像素，纹理 filterMode 设为 Point
    ///   · 绘制时按纹理原始尺寸 1:1 画，因此边缘是硬边，不会被插值糊掉
    ///   · 附带 2 个模块的白色静默区(quiet zone)，否则扫码器容易识别失败
    ///
    /// 使用方式：窗口高度 = 原高度 + GetHeight()，然后在布局末尾调用 DrawLayout()
    ///
    /// ══════════════ IL2CPP 移植要点 ══════════════
    ///
    /// 与 UIStyleManager 同理：运行时创建的 Texture2D 必须设 hideFlags，
    /// 否则会被 Resources.UnloadUnusedAssets() 回收，二维码会变成一片空白
    /// （本工程准星贴图曾因此消失）。
    /// </summary>
    public static class SupportFooter
    {
        // ==================== 二维码点阵（1 = 深色模块，从上到下书写） ====================
        // 37×37 = QR Version 5，无静默区，直接对应模块矩阵
        private static readonly string[] QrModules =
        {
            "1111111011101010011000111101001111111",
            "1000001011100000101111000100001000001",
            "1011101010110100111001000001101011101",
            "1011101011010010101010110101001011101",
            "1011101000000111101000110101001011101",
            "1000001010011110011111100110001000001",
            "1111111010101010101010101010101111111",
            "0000000001001010011101011011000000000",
            "1100111000010111011101000010100101111",
            "0101000001101001101000111101000110110",
            "0001101111111111001010010001101101010",
            "0010000010110101110001000011001111101",
            "1010101100101101010101000000111100101",
            "0101010011000111100001111111010011110",
            "1011011000100001111010010011100110000",
            "0001010011101011011011010010000111110",
            "0111001011010111010001100000101100101",
            "1110010011101001110010111111000011110",
            "0111111101011111011011010001101110000",
            "0001100100110101010011010010001000110",
            "0101001000101101110111100010001100111",
            "1010100111000111011000111001110010010",
            "1100001100100001011011010001000010100",
            "0100100110101011011101111011100110110",
            "1000011110110111000111100000101100111",
            "1010000000101001110001011111010111010",
            "0000001000011111001001011101010011000",
            "0000000000010101011101011011010110110",
            "1100011011101100110100100010111110100",
            "0000000011100111111001111101100011000",
            "1111111000100001011010010110101011100",
            "1000001010001011111011000001100010111",
            "1011101011110111010101100000111110111",
            "1011101000001001101000111110001100111",
            "1011101000011111111010111010101100100",
            "1000001011010101011111011010010100110",
            "1111111010001100010011100010000110111",
        };

        // ==================== 布局常量 ====================
        private const int ModuleCount = 37;      // 模块数
        private const int QuietZoneModules = 2;  // 静默区模块数
        private const int ModuleScale = 3;       // 像素放大倍数（整数倍，配合 Point 采样才是完美硬边）

        private const float QrTextGap = 14f;     // 二维码与文案的间距
        private const float BlockPadding = 8f;   // 区块内边距
        private const float BottomMargin = 8f;   // 区块底部留白

        /// <summary>二维码纹理边长（像素）</summary>
        private static float QrPixelSize => (ModuleCount + QuietZoneModules * 2) * ModuleScale;

        private static Texture2D _qrTexture;
        private static GUIStyle _hintStyle;
        private static string _hintText;

        /// <summary>
        /// 页脚区块需要的高度。调用方把它加到窗口原高度上，即可为页脚腾出新空间（而不是挤压内容）。
        /// </summary>
        public static float GetHeight()
        {
            return BlockPadding * 2f + QrPixelSize + BottomMargin + 12f;
        }

        /// <summary>
        /// 语言切换时刷新文案（未调用也无妨，绘制时会自动兜底刷新一次）
        /// </summary>
        public static void RefreshLocalizedCache()
        {
            _hintText = "text_support_footer_hint".i18n();
            _hintStyle = null;  // 字体可能随语言变化，重建
        }

        /// <summary>
        /// 在布局流末尾绘制页脚区块（会正常占用高度，不覆盖上方内容）。
        /// 调用前请确保窗口高度已加上 GetHeight()。
        /// </summary>
        public static void DrawLayout(float windowWidth)
        {
            EnsureTexture();

            if (string.IsNullOrEmpty(_hintText)) RefreshLocalizedCache();

            if (_hintStyle == null)
            {
                _hintStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    richText = true,
                    alignment = TextAnchor.MiddleLeft,
                    fontSize = 13,
                    padding = new RectOffset(0, 0, 0, 0)
                };
                _hintStyle.normal.textColor = new Color(0.86f, 0.86f, 0.9f, 1f);
            }

            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.Space(BlockPadding);

            float qrSize = QrPixelSize;
            // 文案宽度：扣掉两侧留白 + 二维码 + 间距，再留一点盒子 padding 余量
            float textWidth = Mathf.Max(80f, windowWidth - BlockPadding * 2f - qrSize - QrTextGap - 24f);

            GUILayout.BeginHorizontal();

            Rect qrRect = GUILayoutUtility.GetRect(qrSize, qrSize, GUILayout.Width(qrSize), GUILayout.Height(qrSize));
            // 1:1 绘制（rect 与纹理同尺寸），配合 Point 采样 → 硬边
            GUI.DrawTexture(qrRect, _qrTexture, ScaleMode.StretchToFill, false);

            GUILayout.Space(QrTextGap);

            Rect textRect = GUILayoutUtility.GetRect(textWidth, qrSize, GUILayout.Width(textWidth), GUILayout.Height(qrSize));
            GUI.Label(textRect, _hintText, _hintStyle);

            GUILayout.EndHorizontal();

            GUILayout.Space(BottomMargin);
            GUILayout.EndVertical();
        }

        /// <summary>
        /// 由点阵逆向生成二维码纹理：
        /// 每个模块铺成 ModuleScale×ModuleScale 的实心方块，四周再补白色静默区。
        /// </summary>
        private static void EnsureTexture()
        {
            // 伪 null 检测：被卸载的 Unity 对象同样视为需要重建
            if (_qrTexture != null) return;

            int modulesPerSide = ModuleCount + QuietZoneModules * 2;
            int size = modulesPerSide * ModuleScale;

            Color32 dark = new Color32(0, 0, 0, 255);
            Color32 light = new Color32(255, 255, 255, 255);
            Color32[] pixels = new Color32[size * size];

            // 整张先铺白（静默区也一并铺好）
            for (int i = 0; i < pixels.Length; i++) pixels[i] = light;

            for (int row = 0; row < QrModules.Length; row++)
            {
                string line = QrModules[row];

                // 注意：SetPixels32 的像素数组是「从左到右、从下到上」排列的，
                // 而点阵字符串是「从上到下」书写的 —— 行号必须翻转，否则画出来会上下镜像。
                int baseY = (ModuleCount - 1 - row + QuietZoneModules) * ModuleScale;

                for (int col = 0; col < line.Length; col++)
                {
                    if (line[col] != '1') continue;

                    int baseX = (col + QuietZoneModules) * ModuleScale;

                    for (int dy = 0; dy < ModuleScale; dy++)
                    {
                        int offset = (baseY + dy) * size + baseX;
                        for (int dx = 0; dx < ModuleScale; dx++)
                            pixels[offset + dx] = dark;
                    }
                }
            }

            _qrTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                // Point：放大时取最近像素，保留硬边缘（Bilinear 会糊成一团）
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                name = "OracleSupportQrCode",
                // ⚠ 必须设置：否则被 Resources.UnloadUnusedAssets() 回收后二维码变空白
                hideFlags = HideFlags.HideAndDontSave
            };
            _qrTexture.SetPixels32(pixels);
            _qrTexture.Apply(false, false);
        }
    }
}
