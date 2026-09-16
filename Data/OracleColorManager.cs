namespace Oracle.Data
{
    /// <summary>
    /// 自定义色板（纯托管，无游戏依赖）
    /// </summary>
    public static class OracleColorManager
    {
        public static readonly OracleColor EnemySafe = new OracleColor("#00FF00");
        public static readonly OracleColor EnemySafeDestroy = new OracleColor("#0000FF");
        public static readonly OracleColor EnemyWarning = new OracleColor("#FFFF00");
        public static readonly OracleColor EnemyWarningDestroy = new OracleColor("#FF0000");
        public static readonly OracleColor EnemyDangerous = new OracleColor("#FF0000");
        public static readonly OracleColor EnemyDangerousDestroy = new OracleColor("#590000");
        public static readonly OracleColor EnemyPartDestroy = new OracleColor("#FF00FF");

        public static readonly OracleColor HealthBarBG = new OracleColor("#333333");
        public static readonly OracleColor HealthBarFull = new OracleColor("#00FF00");
        public static readonly OracleColor HealthBarHalf = new OracleColor("#FFFF00");
        public static readonly OracleColor HealthBarQuarter = new OracleColor("#FF0000");

        public static readonly OracleColor AimbotCircle = new OracleColor("#FF0000");

        public static readonly OracleColor Tripwire = new OracleColor("#FF0000");

        public static readonly OracleColor Corpse = new OracleColor("#800000");

        public static readonly OracleColor LootCircle = new OracleColor("#FFFFFF");

        public static readonly OracleColor Distance = new OracleColor("#FFFF00");

        public static readonly OracleColor LootTier0 = new OracleColor("#FFFFFF");
        public static readonly OracleColor LootTier1 = new OracleColor("#00AA00");
        public static readonly OracleColor LootTier2 = new OracleColor("#00A0FF");
        public static readonly OracleColor LootTier3 = new OracleColor("#AA00AA");
        public static readonly OracleColor LootTier4 = new OracleColor("#FFAA00");
        public static readonly OracleColor LootTier5 = new OracleColor("#AA0000");
        public static readonly OracleColor LootTier6 = new OracleColor("#FF55FF");
        public static readonly OracleColor LootTierX = new OracleColor("#808080");
        public static readonly OracleColor LootTierEX = new OracleColor("#DC143C");

        public static readonly OracleColor TextGray = new OracleColor("#808080");

        public static readonly OracleColor ManagerGUIBackground = new OracleColor("#FFFFFF");

        public static readonly OracleColor PlayerLevel = new OracleColor("#7FFF00");
        public static readonly OracleColor PMCUSEC = new OracleColor("#007CFF");
        public static readonly OracleColor PMCBEAR = new OracleColor("#FF8C00");
        public static readonly OracleColor AllyPlayer = new OracleColor("#66CCFF");
        public static readonly OracleColor Scav = new OracleColor("#FFFF8B");
        public static readonly OracleColor Boss = new OracleColor("#CE0000");
        public static readonly OracleColor Sniper = new OracleColor("#00FA9A");
        public static readonly OracleColor Raider = new OracleColor("#7300A6");
        public static readonly OracleColor Follower = new OracleColor("#FF2DE9");
        public static readonly OracleColor Sectant = new OracleColor("#ADFF2F");
        public static readonly OracleColor Santa = new OracleColor("#00FFFF");
        public static readonly OracleColor BTR = new OracleColor("#228B22");
        public static readonly OracleColor BlackDiv = new OracleColor("#DC143C"); //WTT compat
        public static readonly OracleColor Event = new OracleColor("#818ef2");   //BloodHound, etc
    }
}
