using EFT.Weather;
using Oracle.Chrono;
using Oracle.Data;
using Oracle.Utils;
using System;
using UnityEngine;

namespace Oracle.RaidManager
{
    /// <summary>
    /// 晨昏线：时间 / 天气管理面板（创世引擎的最后一个页签）。
    ///
    /// 控件约定（与既有面板保持一致）：
    ///   · 开关一律用按钮表达（本项目没有 Toggle 与勾选框的样式）
    ///   · 滑块与下拉表是专门的样式，见 UIStyleManager
    ///   · 下拉表是"内联展开"：点一下把条目直接排在下面，不弹浮层
    ///     （IMGUI 里做浮层要处理裁剪与层级，内联展开最稳且和这套平色风格最搭）
    ///
    /// ⚠ 5.0 移植差异：
    ///   · 游戏世界引用改用 OracleGameState.CurrentGameWorld
    ///   · 其余逻辑与 4.1 一致（关键差异都在 ChronoManager 里）
    /// </summary>
    public class ChronoManagerGUI
    {
        public Vector2 _scrollPos;

        // 面板上待设定的目标时刻（注意不是"当前时刻"）
        private int _targetHour = 12;
        private int _targetMinute = 0;
        private bool _targetSeeded = false;

        // 下拉表展开态
        private bool _windDirExpanded = false;
        private bool _topWindExpanded = false;

        // 滑块皮肤的原样（换出去以后要能换回来）
        private GUIStyle _origSliderSkin;
        private GUIStyle _origSliderThumbSkin;

        // 时间流速预设
        private static readonly string[] SpeedLabels = { "1×", "7×", "30×", "60×" };
        private static readonly float[] SpeedValues = { 1f, 7f, 30f, 60f };

        // 高空气流：与 SamSWAT.TimeWeatherChanger 的 6 档完全一致
        private static readonly Vector2[] TopWindVectors =
        {
            new Vector2(0f, -1f),   // 下
            new Vector2(-1f, 0f),   // 左
            new Vector2(1f, 1f),    // 左上
            new Vector2(1f, 0f),    // 右
            new Vector2(0f, 1f),    // 上
            Vector2.zero            // 无
        };

        // 风向：WeatherDebug.Direction 是 East=1 起排的 8 向
        private static readonly WeatherDebug.Direction[] WindDirections =
        {
            WeatherDebug.Direction.East,
            WeatherDebug.Direction.North,
            WeatherDebug.Direction.West,
            WeatherDebug.Direction.South,
            WeatherDebug.Direction.SE,
            WeatherDebug.Direction.SW,
            WeatherDebug.Direction.NW,
            WeatherDebug.Direction.NE
        };

        private static readonly string[] WindDirectionKeys =
        {
            "text_chrono_winddir_east",
            "text_chrono_winddir_north",
            "text_chrono_winddir_west",
            "text_chrono_winddir_south",
            "text_chrono_winddir_se",
            "text_chrono_winddir_sw",
            "text_chrono_winddir_nw",
            "text_chrono_winddir_ne"
        };

        private static readonly string[] TopWindKeys =
        {
            "text_chrono_topwind_down",
            "text_chrono_topwind_left",
            "text_chrono_topwind_upleft",
            "text_chrono_topwind_right",
            "text_chrono_topwind_up",
            "text_chrono_topwind_none"
        };

        public void DrawPanel()
        {
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            DrawStatusSection();
            GUILayout.Space(10);

            DrawTimeSection();
            GUILayout.Space(10);

            DrawWeatherSection();
            GUILayout.Space(10);

            DrawSceneSection();
            GUILayout.Space(10);

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// 时间节与天气节共用的可用性判定。返回 null 表示可用，否则返回要显示的提示文案键。
        ///
        /// 两种情况直接锁死控制器：
        /// · 不在战局里（没有 GameWorld / GameDateTime）；
        /// · 当前地区不是游戏认得的地图（MOD 图 / 自建图）—— 这类地图既没有官方天气曲线，
        ///   也不该被我们改写时间，索性连时间控制器一起禁用，避免留下半生效的状态。
        /// </summary>
        private static string GetBlockReasonKey()
        {
            var world = OracleGameState.CurrentGameWorld;
            if (world == null || world.GameDateTime == null)
            {
                return "text_chrono_no_world";
            }

            if (!ChronoManager.IsKnownLocation(ChronoManager.GetLocationId()))
            {
                return "text_chrono_unsupported_map";
            }

            return null;
        }

        // ==================== 当前状态 ====================

        private void DrawStatusSection()
        {
            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.Label("text_chrono_status_title".i18n(), UIStyleManager.SectionTitleStyle);

            DateTime? gameTime = ChronoManager.GetGameTime();

            DrawReadoutRow("text_chrono_status_clock", gameTime.HasValue ? gameTime.Value.ToString("HH:mm:ss") : "--:--:--");
            DrawReadoutRow("text_chrono_status_phase", ChronoManager.GetPhaseKey().i18n());

            float factor = ChronoManager.GetTimeFactor();
            DrawReadoutRow("text_chrono_status_speed", factor > 0f ? factor.ToString("0.0") + "×" : "—");

            // 走游戏语言包取官方译名，而不是直接甩内部地图名。
            // 读不到的（MOD 图）会退回内部 id，正好当排查线索用
            string mapId = ChronoManager.GetLocationId();
            DrawReadoutRow("text_chrono_status_map",
                string.IsNullOrEmpty(mapId) ? "—" : ChronoManager.GetLocationName(mapId));

            DrawReadoutRow(
                "text_chrono_status_transition",
                ChronoManager.Transitioning ? "text_chrono_state_running".i18n() : "text_chrono_state_idle".i18n());

            GUILayout.EndVertical();
        }

        // ==================== 时间控制 ====================

        private void DrawTimeSection()
        {
            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.Label("text_chrono_time_title".i18n(), UIStyleManager.SectionTitleStyle);

            string block = GetBlockReasonKey();
            if (block != null)
            {
                GUILayout.Label(block.i18n(), UIStyleManager.LabelStyle);
                GUILayout.EndVertical();
                return;
            }

            // 首次打开面板时把目标时刻对齐到当前战局时刻，避免一进来就是 12:00
            if (!_targetSeeded)
            {
                DateTime? now = ChronoManager.GetGameTime();
                if (now.HasValue)
                {
                    _targetHour = now.Value.Hour;
                    _targetMinute = now.Value.Minute;
                    _targetSeeded = true;
                }
            }

            SwapSliderSkin(true);

            GUILayout.BeginHorizontal();
            GUILayout.Label("text_chrono_hour".i18n(), UIStyleManager.LabelStyle, GUILayout.Width(96));
            _targetHour = Mathf.RoundToInt(GUILayout.HorizontalSlider(_targetHour, 0f, 23f));
            GUILayout.Label(_targetHour.ToString("00"), UIStyleManager.ValueLabelStyle, GUILayout.Width(64));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("text_chrono_minute".i18n(), UIStyleManager.LabelStyle, GUILayout.Width(96));
            _targetMinute = Mathf.RoundToInt(GUILayout.HorizontalSlider(_targetMinute, 0f, 59f));
            GUILayout.Label(_targetMinute.ToString("00"), UIStyleManager.ValueLabelStyle, GUILayout.Width(64));
            GUILayout.EndHorizontal();

            SwapSliderSkin(false);

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            bool smooth = ChronoCfg.SmoothDuration.Value > 0f;
            if (GUILayout.Button(
                    smooth ? "text_chrono_smooth_on".i18n() : "text_chrono_smooth_off".i18n(),
                    smooth ? UIStyleManager.ToggleOnStyle : UIStyleManager.ToggleOffStyle,
                    GUILayout.Height(24), GUILayout.Width(150)))
            {
                // 在"平滑"与"瞬跳"之间切换：直接改配置项，面板与设置页同步
                ChronoCfg.SmoothDuration.Value = smooth ? 0f : 2f;
            }

            if (GUILayout.Button("text_chrono_apply".i18n(), UIStyleManager.BlueButtonStyle, GUILayout.Height(24)))
            {
                ChronoManager.SetGameTime(_targetHour, _targetMinute, ChronoCfg.SmoothDuration.Value);
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("text_chrono_time_preset".i18n(), UIStyleManager.LabelStyle);
            DrawButtonGrid(
                new[]
                {
                    "text_chrono_timepreset_dawn".i18n(),
                    "text_chrono_timepreset_noon".i18n(),
                    "text_chrono_timepreset_dusk".i18n(),
                    "text_chrono_timepreset_midnight".i18n()
                },
                UIStyleManager.NormalButtonStyle,
                index =>
                {
                    int[] hours = { 5, 12, 19, 0 };
                    _targetHour = hours[index];
                    _targetMinute = 0;
                    ChronoManager.SetGameTime(hours[index], 0, ChronoCfg.SmoothDuration.Value);
                });

            GUILayout.Space(6);
            GUILayout.Label("text_chrono_speed".i18n(), UIStyleManager.LabelStyle);
            DrawButtonGrid(
                SpeedLabels,
                UIStyleManager.NormalButtonStyle,
                index => ChronoManager.SetTimeFactor(SpeedValues[index]));

            GUILayout.EndVertical();
        }

        // ==================== 天气控制 ====================

        private void DrawWeatherSection()
        {
            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.Label("text_chrono_weather_title".i18n(), UIStyleManager.SectionTitleStyle);

            string block = GetBlockReasonKey();
            if (block != null)
            {
                GUILayout.Label(block.i18n(), UIStyleManager.LabelStyle);
                GUILayout.EndVertical();
                return;
            }

            // 工厂没有名为 "Weather" 的对象，是纯室内封闭场景
            if (!ChronoManager.HasWeather)
            {
                GUILayout.Label("text_chrono_indoor_map".i18n(), UIStyleManager.LabelStyle);
                GUILayout.EndVertical();
                return;
            }

            // 本局首次打开时抓快照 + 把草稿播种成"当前实际天气"
            ChronoManager.EnsureInitialized();

            bool takeover = ChronoManager.Takeover;
            bool nextTakeover = DrawSwitchRow("text_chrono_takeover".i18n(), takeover);
            if (nextTakeover != takeover)
            {
                ChronoManager.SetTakeover(nextTakeover);
            }

            GUILayout.Space(4);

            ChronoWeatherState draft = ChronoManager.Draft;

            SwapSliderSkin(true);

            draft.CloudDensity = DrawSliderRow("text_chrono_cloud", draft.CloudDensity, -1f, 1f, "0.000");
            // 雾用指数标尺：实测可用区间只有 0.001(晴)~0.004(浓雾)，线性滑块几乎没法用
            draft.Fog = DrawLogSliderRow("text_chrono_fog", draft.Fog, ChronoManager.FogMin, ChronoManager.FogMax, "0.0000");
            draft.Rain = DrawSliderRow("text_chrono_rain", draft.Rain, 0f, 1f, "0.000");
            draft.LightningThunderProbability = DrawSliderRow("text_chrono_thunder", draft.LightningThunderProbability, 0f, 1f, "0.000");
            draft.Temperature = DrawSliderRow("text_chrono_temperature", draft.Temperature, -50f, 50f, "0.0");
            draft.WindMagnitude = DrawSliderRow("text_chrono_wind", draft.WindMagnitude, 0f, 1f, "0.000");

            SwapSliderSkin(false);

            // 风向下拉表
            int windIndex = Mathf.Clamp((int)draft.WindDirection - 1, 0, WindDirections.Length - 1);
            windIndex = DrawDropdownRow(
                "text_chrono_wind_dir".i18n(), windIndex, LocalizeKeys(WindDirectionKeys), ref _windDirExpanded);
            draft.WindDirection = WindDirections[windIndex];

            // 高空气流下拉表
            int topIndex = MatchTopWind(draft.TopWindDirection);
            topIndex = DrawDropdownRow(
                "text_chrono_top_wind".i18n(), topIndex, LocalizeKeys(TopWindKeys), ref _topWindExpanded);
            draft.TopWindDirection = TopWindVectors[topIndex];

            // 接管开着的时候草稿值每帧由 ChronoManager.Update 写进游戏，这里立刻生效一次手感更好
            if (takeover) ChronoManager.ApplyDraft();

            GUILayout.Space(6);
            GUILayout.Label("text_chrono_weather_preset".i18n(), UIStyleManager.LabelStyle);

            string[] presetLabels = new string[ChronoManager.WeatherPresets.Length];
            for (int i = 0; i < presetLabels.Length; i++)
            {
                presetLabels[i] = ChronoManager.WeatherPresets[i].NameKey.i18n();
            }

            DrawButtonGrid(
                presetLabels,
                UIStyleManager.NormalButtonStyle,
                index =>
                {
                    ChronoManager.ApplyPreset(ChronoManager.WeatherPresets[index]);
                    if (!ChronoManager.Takeover) ChronoManager.SetTakeover(true);
                    else ChronoManager.ApplyDraft();
                },
                4);

            GUILayout.EndVertical();
        }

        // ==================== 组合场景 ====================

        private void DrawSceneSection()
        {
            GUILayout.BeginVertical(UIStyleManager.BoxStyle);
            GUILayout.Label("text_chrono_scene_title".i18n(), UIStyleManager.SectionTitleStyle);

            DrawButtonGrid(
                new[]
                {
                    "text_chrono_scene_day".i18n(),
                    "text_chrono_scene_dusk".i18n(),
                    "text_chrono_scene_night".i18n(),
                    "text_chrono_scene_fog".i18n()
                },
                UIStyleManager.NormalButtonStyle,
                index =>
                {
                    float smooth = ChronoCfg.SmoothDuration.Value;

                    switch (index)
                    {
                        case 0: ChronoManager.ApplyScene(12, 0, FindPreset("text_chrono_preset_clear"), smooth); break;
                        case 1: ChronoManager.ApplyScene(19, 0, FindPreset("text_chrono_preset_hazy"), smooth); break;
                        case 2: ChronoManager.ApplyScene(0, 0, FindPreset("text_chrono_preset_rain"), smooth); break;
                        default: ChronoManager.ApplyScene(8, 0, FindPreset("text_chrono_preset_fog"), smooth); break;
                    }

                    _targetSeeded = false;   // 让时间滑块跟到新时刻上
                },
                2);

            GUILayout.Space(6);

            if (GUILayout.Button("text_chrono_restore".i18n(), UIStyleManager.RedButtonStyle, GUILayout.Height(26)))
            {
                ChronoManager.RestoreAll();
                _targetSeeded = false;
            }

            GUILayout.EndVertical();
        }

        // ==================== 控件封装 ====================

        /// <summary>
        /// 滑块只认 GUI.skin 上的那两个样式，画之前临时换一下（与滚动条同一套做法）。
        /// 首次切换时把原样式记下来，画完必须换回去，否则会污染其它面板。
        /// </summary>
        private void SwapSliderSkin(bool custom)
        {
            if (custom)
            {
                if (_origSliderSkin == null)
                {
                    _origSliderSkin = GUI.skin.horizontalSlider;
                    _origSliderThumbSkin = GUI.skin.horizontalSliderThumb;
                }

                GUI.skin.horizontalSlider = UIStyleManager.SliderStyle;
                GUI.skin.horizontalSliderThumb = UIStyleManager.SliderThumbStyle;
            }
            else if (_origSliderSkin != null)
            {
                GUI.skin.horizontalSlider = _origSliderSkin;
                GUI.skin.horizontalSliderThumb = _origSliderThumbSkin;
            }
        }

        /// <summary>只读行：左标签 + 右读数</summary>
        private static void DrawReadoutRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label.i18n(), UIStyleManager.LabelStyle, GUILayout.Width(110));
            GUILayout.Label(value, UIStyleManager.ValueLabelStyle);
            GUILayout.EndHorizontal();
        }

        /// <summary>滑块行：左标签 + 滑块 + 右读数</summary>
        private static float DrawSliderRow(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label.i18n(), UIStyleManager.LabelStyle, GUILayout.Width(96));
            float result = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.Label(result.ToString(format), UIStyleManager.ValueLabelStyle, GUILayout.Width(64));
            GUILayout.EndHorizontal();
            return result;
        }

        /// <summary>
        /// 指数标尺滑块：位置 t(0~1) 与取值之间走 min*(max/min)^t。
        /// 只为雾准备 —— 它的可用区间（0.001~0.004）紧贴下限，
        /// 线性滑块上挤在开头 1% 的位置，根本拖不准。
        /// </summary>
        private static float DrawLogSliderRow(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label.i18n(), UIStyleManager.LabelStyle, GUILayout.Width(96));

            float span = Mathf.Log(max / min);

            // 值 -> 位置：先夹到下限（低于 0.001 在游戏里也是同一个效果）
            float t = Mathf.Clamp01(Mathf.Log(Mathf.Max(value, min) / min) / span);
            t = GUILayout.HorizontalSlider(t, 0f, 1f);

            float result = min * Mathf.Pow(max / min, t);

            GUILayout.Label(result.ToString(format), UIStyleManager.ValueLabelStyle, GUILayout.Width(64));
            GUILayout.EndHorizontal();
            return result;
        }

        /// <summary>
        /// 按钮式开关。**返回值就是"当前应该处于的状态"**（点了就翻转，没点就原样），
        /// 调用方拿它跟旧值比一下决定要不要落状态，别反过来用 —— 早期版本这里
        /// 返回的是"是否被点击"，配上 `if (返回值) 反转()` 的写法，
        /// 会让开关在打开后的下一帧立刻自己关掉。
        /// </summary>
        private static bool DrawSwitchRow(string label, bool value, float width = 130f)
        {
            bool next = value;

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UIStyleManager.LabelStyle, GUILayout.Width(96));

            if (GUILayout.Button(
                    value ? "text_enable".i18n() : "text_disable".i18n(),
                    value ? UIStyleManager.ToggleOnStyle : UIStyleManager.ToggleOffStyle,
                    GUILayout.Width(width), GUILayout.Height(22)))
            {
                next = !value;
            }

            GUILayout.EndHorizontal();
            return next;
        }

        /// <summary>内联下拉表。收起态是一个按钮，展开后把选项直接排在下方。</summary>
        private static int DrawDropdownRow(string label, int selected, string[] options, ref bool expanded, float width = 200f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, UIStyleManager.LabelStyle, GUILayout.Width(96));

            string caption = (selected >= 0 && selected < options.Length) ? options[selected] : "-";

            // 用 ASCII 的 > / v 而不是 ▲▼：游戏内字体不一定带那两个字形
            if (GUILayout.Button((expanded ? "v  " : ">  ") + caption, UIStyleManager.DropdownStyle,
                    GUILayout.Width(width), GUILayout.Height(22)))
            {
                expanded = !expanded;
            }

            GUILayout.EndHorizontal();

            if (!expanded) return selected;

            for (int i = 0; i < options.Length; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(100f);

                bool isSelected = (i == selected);
                if (GUILayout.Button(
                        options[i],
                        isSelected ? UIStyleManager.DropdownItemSelectedStyle : UIStyleManager.DropdownItemStyle,
                        GUILayout.Width(width), GUILayout.Height(20)))
                {
                    selected = i;
                    expanded = false;
                }

                GUILayout.EndHorizontal();
            }

            return selected;
        }

        /// <summary>按钮网格：每行 perRow 个</summary>
        private static void DrawButtonGrid(string[] labels, GUIStyle style, Action<int> onClick, int perRow = 4, float height = 24f)
        {
            if (labels == null || labels.Length == 0) return;

            for (int i = 0; i < labels.Length; i++)
            {
                if (i % perRow == 0) GUILayout.BeginHorizontal();

                if (GUILayout.Button(labels[i], style, GUILayout.Height(height)))
                {
                    onClick?.Invoke(i);
                }

                if (i % perRow == perRow - 1 || i == labels.Length - 1) GUILayout.EndHorizontal();
            }
        }

        /// <summary>把一组本地化键翻成当前语言</summary>
        private static string[] LocalizeKeys(string[] keys)
        {
            string[] result = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) result[i] = keys[i].i18n();
            return result;
        }

        /// <summary>把当前高空气流值匹配回档位下标</summary>
        private static int MatchTopWind(Vector2 value)
        {
            for (int i = 0; i < TopWindVectors.Length; i++)
            {
                if ((TopWindVectors[i] - value).sqrMagnitude < 0.01f) return i;
            }
            return TopWindVectors.Length - 1;
        }

        /// <summary>按本地化键找预设</summary>
        private static ChronoWeatherPreset FindPreset(string nameKey)
        {
            for (int i = 0; i < ChronoManager.WeatherPresets.Length; i++)
            {
                if (ChronoManager.WeatherPresets[i].NameKey == nameKey) return ChronoManager.WeatherPresets[i];
            }
            return null;
        }
    }
}
