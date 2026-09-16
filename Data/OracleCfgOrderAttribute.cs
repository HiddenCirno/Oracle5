using System;

namespace Oracle.Data
{
    /// <summary>
    /// 配置项排序标签
    /// </summary>
    public class OracleCfgOrderAttribute : Attribute
    {
        public int Order { get; }

        public OracleCfgOrderAttribute(int order)
        {
            Order = order;
        }
    }
}
