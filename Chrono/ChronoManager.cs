using EFT;
using EFT.Weather;
using Oracle.Data;
using Oracle.Utils;
using System;
using UnityEngine;
using static Oracle.Data.OracleInterface;

namespace Oracle.Chrono
{
    /// <summary>
    /// 一组环境参数的纯数据副本（面板编辑 / 快照还原都用它）。
    /// 纯托管结构，不涉及游戏类型，可原样使用。
    /// </summary>
    public class ChronoWeatherState
    {
        public bool Enabled;
        public float CloudDensity;
        public float Fog;
        public float Rain;
        public float LightningThunderProbability;
        public float Temperature;
        public float WindMagnitude;
        public WeatherDebug.Direction WindDirection;
        public Vector2 TopWindDirection;

        /// <summary>从游戏天气组件抓一份当前值</summary>
        public static ChronoWeatherState From(WeatherDebug wd)
        {
            ChronoWeatherState state = new ChronoWeatherState();
            if (wd == null) return state;

            state.Enabled = wd.isEnabled;
            state.CloudDensity = wd.CloudDensity;
            state.Fog = wd.Fog;
            state.Rain = wd.Rain;
            state.LightningThunderProbability = wd.LightningThunderProbability;
            state.Temperature = wd.Temperature;
            state.WindMagnitude = wd.WindMagnitude;
            state.WindDirection = wd.WindDirection;
            state.TopWindDirection = wd.TopWindDirection;

            return state;
        }

        /// <summary>写回游戏天气组件</summary>
        public void ApplyTo(WeatherDebug wd)
        {
            if (wd == null) return;

            // ⚠ 5.0 里 WeatherDebug 同时存在 isEnabled 与 Enabled 两个语义相同的属性
            //   （4.1 只有 Enabled；Enabled 很可能是包 isEnabled 的兼容属性）。
            //   两者都写，确保游戏侧无论读哪一个都能拿到我们的值。
            //
            //   Enabled 必须先设：WeatherController.WeatherCurve 就是靠它决定
            //   "用 WeatherDebug 这组调试值"还是"用服务器下发的后台天气曲线"——
            //   不置 true 的话，后面写再多参数游戏也一律无视
            //   （这正是"接管点了没反应"的原因）。
            wd.isEnabled = Enabled;
            wd.Enabled = Enabled;

            wd.CloudDensity = CloudDensity;
            wd.Fog = Fog;
            wd.Rain = Rain;
            wd.LightningThunderProbability = LightningThunderProbability;
            wd.Temperature = Temperature;
            wd.WindMagnitude = WindMagnitude;
            wd.WindDirection = WindDirection;
            wd.TopWindDirection = TopWindDirection;
        }

        public ChronoWeatherState Clone()
        {
            return (ChronoWeatherState)MemberwiseClone();
        }
    }

    /// <summary>
    /// 天气预设（数值对照 SamSWAT.TimeWeatherChanger 的十组预设，符号与量纲完全一致）
    /// </summary>
    public class ChronoWeatherPreset
    {
        public string NameKey;
        public float CloudDensity;
        public float Fog;
        public float Rain;
        public float Thunder;
        public float Temperature;
        public float WindMagnitude;

        /// <summary>0 表示每次随机一个风向</summary>
        public int WindDirection;
    }

    /// <summary>
    /// 晨昏线：战局时间与天气的接管。
    ///
    /// 实现依据（EFT 4.1 反编译源码，全部是 public 成员，无需反射）：
    ///   · 时间载体：GameWorld.GameDateTime（public 字段）
    ///     - Calculate() = StatedGameDateTime + 真实流逝 × TimeFactor × TimeFactorMod
    ///     - Reset(real, game, factor, force) 重新锚定；
    ///       **Locked 且 !force 时静默返回**（Lock() 由天气事件控制器在天气事件期间调用，
    ///       所以必须传 force:true）
    ///   · 天气载体：WeatherController.Instance，其 public 字段 WeatherDebug 持有全部可调参数
    ///
    /// ⚠ 5.0 移植差异：
    ///   · WeatherDebug.Enabled → 5.0 主用 isEnabled（两属性并存，ApplyTo 里都写）
    ///   · 时间控制与天气控制互不干扰：单纯把 Enabled 置位不会把 WeatherDebug.Date
    ///     里的陈旧时刻顶进 TOD_Sky（SetDebugDate 只在载入调试预设时才调用）
    /// </summary>
    public static class ChronoManager
    {
        // 进图时的原始环境（还原用）
        private static ChronoWeatherState _original;
        private static float _originalTimeFactor = -1f;

        // 本局的初始化标记：快照是否已抓 / 草稿是否已播种
        // 分开两个标记是为了让"先点天气预设、后点接管"也不会把预设冲掉
        private static bool _initialized;
        private static bool _draftSeeded;

        // 平滑过渡状态
        private static bool _transitioning;
        private static DateTime _transitionFrom;
        private static DateTime _transitionTo;
        private static float _transitionElapsed;
        private static float _transitionFactor = 7f;

        // 世界切换检测
        private static GameWorld _lastWorld;

        /// <summary>面板正在编辑的草稿值（接管开启时逐帧写入游戏）</summary>
        public static ChronoWeatherState Draft { get; private set; } = new ChronoWeatherState();

        /// <summary>是否已接管天气</summary>
        public static bool Takeover { get; private set; }

        /// <summary>
        /// 天色平滑过渡时长（秒）。0 = 立即切换。
        ///
        /// ⚠ 这是**战局内的实时状态**，不是配置项 —— 由 F8 面板的开关直接改写。
        ///   原先它挂在 ChronoCfg 上，导致「面板改设置」要绕一圈配置文件，
        ///   而且改完会写盘、跨战局残留。战局管理的所有功能都应当是实时的、
        ///   不落盘的，所以统一收敛到内存变量。
        /// </summary>
        public static float SmoothDuration = 2f;

        /// <summary>是否正在做天色平滑过渡</summary>
        public static bool Transitioning => _transitioning;

        // ==================== 环境访问 ====================

        private static GameWorld World => OracleGameState.CurrentGameWorld;

        // ==================== System.DateTime ↔ Il2CppSystem.DateTime 转换桥 ====================
        //
        // ⚠ 5.0 的 GameDateTime.Calculate() 返回的是 **Il2CppSystem.DateTime**，
        //   Reset() 也接收它 —— 这是 Il2CPP 侧的 BCL 镜像类型，
        //   与托管的 System.DateTime **不能隐式互换**（本工程反复踩到的类型隔离）。
        //
        //   两者都是 struct 且都有 Ticks / UtcNow，所以互转只需要走 Ticks。
        //   策略：**所有时间算术都留在托管的 System.DateTime 上做**
        //   （插值、构造、格式化用起来更顺手），只在读写游戏的两端转换一次。
        private static DateTime ToManaged(Il2CppSystem.DateTime dt)
        {
            return new DateTime(dt.Ticks, DateTimeKind.Utc);
        }

        private static Il2CppSystem.DateTime ToIl2Cpp(DateTime dt)
        {
            return new Il2CppSystem.DateTime(dt.Ticks, Il2CppSystem.DateTimeKind.Utc);
        }

        /// <summary>
        /// 取天气组件。工厂 / 藏身处没有它（全室内封闭场景），返回 null。
        ///
        /// 走 Instance 而不是 GameObject.Find("Weather")：控制器在 Awake 里挂上自己、
        /// 在 OnDestroy 里清空，生命周期可靠且白拿一份缓存；它又是 UnityEngine.Object，
        /// 对象销毁后 Unity 的 == 重载会让判空依旧成立，换图时不需要手动失效。
        /// </summary>
        public static WeatherController GetWeather()
        {
            return WeatherController.Instance;
        }

        /// <summary>当前地图是否有可用天气（工厂 / 藏身处为 false）</summary>
        public static bool HasWeather => GetWeather() != null;

        // ==================== 地区 ====================

        /// <summary>当前战局的地区 id（战局外为 null）</summary>
        public static string GetLocationId()
        {
            return World?.LocationId;
        }

        /// <summary>
        /// 当前地区是不是游戏认得的（官方）地图。
        ///
        /// 语言包里地区名的键就是裸的地区 id（"Sandbox" / "bigmap" / "factory4_day"），
        /// 没有前后缀。MOD 地图不会往官方语言包里塞 key，所以这里返回 false。
        ///
        /// ⚠ 不能拿 LocalizedValue 的返回值判断：键缺失时它会把键原样吐回来，
        ///   看起来"有值"其实是没查到。
        /// </summary>
        public static bool IsKnownLocation(string locationId)
        {
            return !string.IsNullOrEmpty(locationId)
                && LocalizationManager.Instance.HasValidLocalization(locationId);
        }

        /// <summary>
        /// 地区显示名：官方译名优先，读不到就退回内部 id
        /// （顺带在面板上暴露出来，方便排查是哪个 MOD 图）
        /// </summary>
        public static string GetLocationName(string locationId)
        {
            if (string.IsNullOrEmpty(locationId)) return null;

            return LocalizationManager.Instance.HasValidLocalization(locationId)
                ? LocalizationManager.Instance.LocalizedValue(locationId)
                : locationId;
        }

        /// <summary>
        /// 检测进出战局 / 换图，切换时把接管状态与快照全部清空，避免跨局残留
        /// </summary>
        public static void SyncWorld()
        {
            GameWorld world = World;

            if (ReferenceEquals(world, _lastWorld)) return;

            _lastWorld = world;
            _original = null;
            _originalTimeFactor = -1f;
            _initialized = false;
            _draftSeeded = false;
            Takeover = false;
            StopTransition();
            Draft = new ChronoWeatherState();
        }

        // ==================== 时间 ====================

        /// <summary>当前战局时刻</summary>
        public static DateTime? GetGameTime()
        {
            GameDateTime gdt = World?.GameDateTime;
            if (gdt == null) return null;

            return ToManaged(gdt.Calculate());
        }

        /// <summary>当前时间流速（1 = 现实速度，7 = 游戏默认）</summary>
        public static float GetTimeFactor()
        {
            GameDateTime gdt = World?.GameDateTime;
            return (gdt != null) ? gdt.TimeFactor : 0f;
        }

        /// <summary>
        /// 修改时间流速。
        ///
        /// 必须连同 Calculate() 的当前值一起重新锚定 —— 只改 TimeFactor 的话，
        /// Calculate() 会把**已经流逝**的那段时间也按新系数重算，时钟当场跳变。
        /// </summary>
        public static void SetTimeFactor(float factor)
        {
            GameDateTime gdt = World?.GameDateTime;
            if (gdt == null) return;

            if (factor <= 0f) factor = 1f;

            DateTime current = ToManaged(gdt.Calculate());
            // force: 天气事件期间 GameDateTime 会被 Lock()，不加 force 这里会静默失效
            // ⚠ 两端都要转回 Il2CppSystem.DateTime —— 托管与 IL2CPP 的 DateTime 不可互换
            gdt.Reset(Il2CppSystem.DateTime.UtcNow, ToIl2Cpp(current), factor, true);
        }

        /// <summary>
        /// 设定战局时刻（只改时分秒，日期保持当前值）
        /// </summary>
        /// <param name="smoothSeconds">平滑过渡时长；&lt;= 0 表示瞬间跳变</param>
        public static void SetGameTime(int hour, int minute, float smoothSeconds)
        {
            GameDateTime gdt = World?.GameDateTime;
            if (gdt == null) return;

            DateTime current = ToManaged(gdt.Calculate());
            DateTime target = new DateTime(
                current.Year, current.Month, current.Day,
                Mathf.Clamp(hour, 0, 23), Mathf.Clamp(minute, 0, 59), 0,
                DateTimeKind.Utc);

            if (smoothSeconds <= 0f)
            {
                StopTransition();
                gdt.Reset(Il2CppSystem.DateTime.UtcNow, ToIl2Cpp(target), gdt.TimeFactor, true);
                return;
            }

            // 过渡期间把流速压到 0（时钟冻结在插值点上），结束时再还原原流速
            _transitionFrom = current;
            _transitionTo = target;
            _transitionElapsed = 0f;
            _transitionFactor = (gdt.TimeFactor > 0f) ? gdt.TimeFactor : 1f;
            _transitioning = true;
        }

        /// <summary>当前所处时段（用于面板上那个"时段"读数）</summary>
        public static string GetPhaseKey()
        {
            DateTime? now = GetGameTime();
            if (now == null) return "text_chrono_phase_unknown";

            int hour = now.Value.Hour;

            if (hour >= 5 && hour < 8) return "text_chrono_phase_dawn";
            if (hour >= 8 && hour < 18) return "text_chrono_phase_day";
            if (hour >= 18 && hour < 21) return "text_chrono_phase_dusk";

            return "text_chrono_phase_night";
        }

        private static void StopTransition()
        {
            _transitioning = false;
            _transitionElapsed = 0f;
        }

        private static void UpdateTransition()
        {
            GameDateTime gdt = World?.GameDateTime;
            if (gdt == null)
            {
                StopTransition();
                return;
            }

            float duration = SmoothDuration;
            if (duration <= 0.01f) duration = 0.01f;

            _transitionElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_transitionElapsed / duration);

            // smoothstep 缓动，避免匀速推进那种生硬的"天色平移"感
            float eased = t * t * (3f - 2f * t);
            long ticks = _transitionFrom.Ticks
                       + (long)((_transitionTo.Ticks - _transitionFrom.Ticks) * (double)eased);

            // 流速置 0 = 时钟冻结在插值点上
            gdt.Reset(Il2CppSystem.DateTime.UtcNow, ToIl2Cpp(new DateTime(ticks, DateTimeKind.Utc)), 0f, true);

            if (t >= 1f)
            {
                gdt.Reset(Il2CppSystem.DateTime.UtcNow, ToIl2Cpp(_transitionTo), _transitionFactor, true);
                StopTransition();
            }
        }

        // ==================== 天气 ====================

        /// <summary>
        /// 本局的首次初始化：抓一份原始环境快照，并把草稿播种成"当前实际天气"。
        /// 面板在画滑块之前调用，所以用户看到的就是眼下真实生效的值。
        ///
        /// 两个标记分开判断：快照只抓一次；草稿只在没播种过时填充，
        /// 这样"先点预设再点接管"也不会把预设冲掉。
        /// </summary>
        public static void EnsureInitialized()
        {
            WeatherController wc = GetWeather();
            if (wc == null) return;   // 工厂 / 藏身处：等进到有天气的地图再说

            if (!_initialized)
            {
                _original = ChronoWeatherState.From(wc.WeatherDebug);
                _originalTimeFactor = GetTimeFactor();
                _initialized = true;
            }

            if (!_draftSeeded)
            {
                Draft = _original.Clone();
                _draftSeeded = true;
            }
        }

        /// <summary>面板上那个"天气接管"开关</summary>
        public static void SetTakeover(bool on)
        {
            if (on)
            {
                EnsureInitialized();

                // 接管 = 打开天气调试开关。不置 true 的话后面写再多参数游戏也一律无视
                Draft.Enabled = true;
                Takeover = true;
                ApplyDraft();
            }
            else
            {
                // 交还控制权：把开关关掉，游戏立刻回到自己的天气曲线，
                // 剩下那几个参数留在原地不影响任何东西
                Takeover = false;
                Draft.Enabled = false;
                ApplyDraft();
            }
        }

        /// <summary>把草稿值写进游戏</summary>
        public static void ApplyDraft()
        {
            WeatherController wc = GetWeather();
            if (wc == null) return;

            Draft.ApplyTo(wc.WeatherDebug);
        }

        /// <summary>只还原天气参数（草稿也一并退回原始值，面板要跟着动）</summary>
        public static void RestoreWeather()
        {
            WeatherController wc = GetWeather();
            if (wc == null || _original == null) return;

            Draft = _original.Clone();
            Draft.ApplyTo(wc.WeatherDebug);
            _draftSeeded = true;
        }

        /// <summary>还原整个环境：天气 + 时间流速</summary>
        public static void RestoreAll()
        {
            Takeover = false;
            RestoreWeather();

            if (_originalTimeFactor > 0f) SetTimeFactor(_originalTimeFactor);
        }

        // ==================== 每帧驱动 ====================

        /// <summary>
        /// 由 RaidManagerGUI 通过 OracleEvent.OnUpdate 驱动。
        /// 只在接管 / 过渡期间做事，平时是一次引用比较就返回。
        ///
        /// ⚠ 必须放在"是否在战局内"的判定【之前】调用 ——
        ///   出局时也需要它跑一次 SyncWorld 把接管状态清干净。
        /// </summary>
        public static void Update()
        {
            SyncWorld();

            if (_transitioning) UpdateTransition();
            if (Takeover) ApplyDraft();
        }

        // ==================== 预设表 ====================

        /// <summary>
        /// 天气预设。除雾以外的参数沿用 SamSWAT.TimeWeatherChanger 的原值。
        ///
        /// 【雾的标尺是单独校准过的】原版那套雾值（0.004 晴 / 0.02 薄雾 / 0.1 浓雾）
        /// 搬过来会明显偏浓，实测本机可用区间只有 0.001~0.004：
        ///     0.001 = 晴（也是硬下限）
        ///     0.002 = 薄雾
        ///     0.004 = 浓雾
        /// 所以下面全部雾值按实测重新标定，其余字段一律未动。
        /// </summary>
        public static readonly ChronoWeatherPreset[] WeatherPresets =
        {
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_clear",      CloudDensity = -0.7f,   Fog = 0.001f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_partly",     CloudDensity = -0.2f,   Fog = 0.001f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_overcast",   CloudDensity = 0f,      Fog = 0.0015f, Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_fullcloud",  CloudDensity = 1f,      Fog = 0.002f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_windy",      CloudDensity = -0.7f,   Fog = 0.001f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0.4f,  WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_hazy",       CloudDensity = 0.2f,    Fog = 0.002f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_fog",        CloudDensity = -0.4f,   Fog = 0.004f,  Rain = 0f,   Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_drizzle",    CloudDensity = -0.1f,   Fog = 0.0015f, Rain = 0.5f, Thunder = 0f,   Temperature = 22f, WindMagnitude = 0f,    WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_rain",       CloudDensity = 0.05f,   Fog = 0.002f,  Rain = 1f,   Thunder = 0.3f, Temperature = 22f, WindMagnitude = 0.15f, WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_thunder",    CloudDensity = 1f,      Fog = 0.0015f, Rain = 0f,   Thunder = 0.8f, Temperature = 22f, WindMagnitude = 0.4f,  WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_storm",      CloudDensity = 1f,      Fog = 0.002f,  Rain = 0.8f, Thunder = 0.5f, Temperature = 22f, WindMagnitude = 0.6f,  WindDirection = 0 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_defaultevn", CloudDensity = -0.3f,   Fog = 0.0015f, Rain = 0f,   Thunder = 0f,   Temperature = 20f, WindMagnitude = 0.75f, WindDirection = 7 },
            new ChronoWeatherPreset { NameKey = "text_chrono_preset_bsg",        CloudDensity = -0.371f, Fog = 0.0025f, Rain = 0f,   Thunder = 0f,   Temperature = 0f,  WindMagnitude = 0.125f, WindDirection = 8 }
        };

        /// <summary>雾滑块标尺下限 —— 也是游戏的硬下限</summary>
        public const float FogMin = 0.001f;

        /// <summary>雾滑块标尺上限 —— WeatherDebug.Fog 的 [Range(0.001f, 0.255f)]</summary>
        public const float FogMax = 0.255f;

        /// <summary>把预设套用进草稿</summary>
        public static void ApplyPreset(ChronoWeatherPreset preset)
        {
            if (preset == null) return;

            // 预设本身就是一份完整参数，标记为已播种，避免随后的 EnsureInitialized 把它冲掉
            _draftSeeded = true;

            Draft.Enabled = true;
            Draft.CloudDensity = preset.CloudDensity;
            Draft.Fog = preset.Fog;
            Draft.Rain = preset.Rain;
            Draft.LightningThunderProbability = preset.Thunder;
            Draft.Temperature = preset.Temperature;
            Draft.WindMagnitude = preset.WindMagnitude;

            // 原实现是 Random.Range(1, 8)：0 表示随机风向，避免每次都是同一个朝向
            Draft.WindDirection = (preset.WindDirection == 0)
                ? (WeatherDebug.Direction)UnityEngine.Random.Range(1, 9)
                : (WeatherDebug.Direction)preset.WindDirection;
        }

        /// <summary>组合场景：时间 + 天气一次性打包</summary>
        public static void ApplyScene(int hour, int minute, ChronoWeatherPreset preset, float smoothSeconds)
        {
            SetGameTime(hour, minute, smoothSeconds);

            if (preset == null) return;

            ApplyPreset(preset);

            if (!Takeover) SetTakeover(true);
            else ApplyDraft();
        }
    }

}
