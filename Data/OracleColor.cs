using Oracle.Utils;
using UnityEngine;

namespace Oracle.Data
{
    /// <summary>
    /// 二次封装的自定义颜色结构, 同时具备字符串和 Unity Color 的隐式转换。
    ///
    /// IL2CPP 移植说明：这是纯托管结构体，不涉及游戏类型，可原样使用。
    /// 去掉了旧版的 ToArgb()（它依赖 Color32 显式转换，且只服务于已删除的原生叠加层）。
    /// </summary>
    public readonly struct OracleColor
    {
        public readonly string HexColor;
        public readonly string HexColorNoHash;
        public readonly Color UnityColor;

        public OracleColor(string hex)
        {
            //防御
            if (string.IsNullOrEmpty(hex))
            {
                HexColor = "#FF00FF";
                HexColorNoHash = "FF00FF";
                UnityColor = Color.magenta;
                return;
            }

            HexColor = hex.StartsWith("#") ? hex.ToUpper() : $"#{hex}".ToUpper();
            HexColorNoHash = HexColor.Substring(1);

            if (ColorUtility.TryParseHtmlString(HexColor, out Color parsedColor))
            {
                UnityColor = parsedColor;
            }
            else
            {
                OracleLog.ErrorOnce($"bad_color_{hex}", $"[Oracle] 解析颜色失败，无效的代码: {hex}");
                UnityColor = Color.magenta;
            }
        }

        //隐式转换
        public static implicit operator Color(OracleColor oc) => oc.UnityColor;

        public static implicit operator string(OracleColor oc) => oc.HexColor;

        //覆盖ToString为文本拼接提供兼容
        public override string ToString() => HexColor;

        /// <summary>
        /// 打包成 ARGB 32 位整数（0xAARRGGBB）。
        ///
        /// 供叠加层原语层使用：GDI 的 AlphaBlend 与软件光栅器都按这个字节序读像素。
        /// 与 OnGUI 的富文本路径（用 HexColor 字符串）互不影响。
        /// </summary>
        public uint ToArgb()
        {
            Color32 c = UnityColor;
            return ((uint)c.a << 24) | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
        }
    }
}
