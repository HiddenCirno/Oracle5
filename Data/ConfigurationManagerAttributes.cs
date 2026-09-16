namespace Oracle.Data
{
    /// <summary>
    /// ConfigurationManager 拓展（F12 菜单读取这些字段来定制显示）
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        /// <summary>
        /// 用于覆盖 F12 菜单中显示的配置项名称 (Key)
        /// </summary>
        public string DispName;

        /// <summary>
        /// 排序, 越大越靠上
        /// </summary>
        public int? Order;

        /// <summary>
        /// 高级选项开关
        /// </summary>
        public bool? IsAdvanced;
    }
}
