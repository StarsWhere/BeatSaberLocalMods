using System;
using System.Collections.Generic;
using ModUiChinese;

internal static class TranslationEngineTests
{
    static int checks;
    static void Equal(string actual, string expected)
    {
        checks++;
        if (actual != expected) throw new Exception("Expected [" + expected + "], got [" + actual + "]");
    }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    }
    static void Run()
    {
        var tables = new Dictionary<string, Dictionary<string, string>> {
            {"Common", new Dictionary<string,string> {
                {"Default", "默认"}, {"PB: ", "个人最佳： "}, {" Settings", " 设置"},
                {"{0} cameras", "{0} 个镜头"}, {"Ready {0:F1} seconds", "{0:F1} 秒后就绪"},
                {"Map {0} has {1} notes", "谱面 {0} 有 {1} 个音符"}
            }},
            {"AdvancedSongFilters", new Dictionary<string,string> {{"NJS", "NJS"}, {"BPM", "BPM"}, {"NPS", "NPS"}}},
            {"Search", new Dictionary<string,string> {{"Rank", "排名"}, {"NJS", "音符跳跃速度"}, {"BPM", "每分钟节拍数"}}},
            {"Counters+", new Dictionary<string,string> {{"Rank", "评级"}, {"Missed", "漏击"}, {" Settings", " 设置"}}}
        };
        var engine = new TranslationEngine(tables);
        Equal(engine.Translate("DEFAULT", "Common"), "默认");
        Equal(engine.Translate("  Default\n", "Common"), "  默认\n");
        Equal(engine.Translate("<b>Default</b>", "Common"), "<b>默认</b>");
        Equal(engine.Translate("<", "Common"), "<");
        Equal(engine.Translate("S", "Common"), "S");
        Equal(engine.Translate("2 cameras", "Common"), "2 个镜头");
        Equal(engine.Translate("Ready 3.5 seconds", "Common"), "3.5 秒后就绪");
        Equal(engine.Translate("Map Escaping the Ruins has 200 notes", "Common"), "谱面 Escaping the Ruins 有 200 个音符");
        Equal(engine.Translate("PB: 97.50%", "Counters+"), "个人最佳： 97.50%");
        Equal(engine.Translate("Missed Settings", "Counters+"), "漏击 设置");
        Equal(engine.Translate("12. Oct 26 - 13:15", "BetterSongSearch"), "2026年10月12日 - 13:15");
        Equal(engine.Translate("Rank", "Search"), "排名");
        Equal(engine.Translate("Rank", "Counters+"), "评级");
        Equal(engine.Translate("Some song that is not a caption", "Common"), "Some song that is not a caption");
        Equal(engine.Translate("NJS", "AdvancedSongFilters"), "NJS");
        Equal(engine.Translate("BPM", "AdvancedSongFilters"), "BPM");
        Equal(engine.Translate("NJS", "Search"), "音符跳跃速度");
        Equal(engine.Translate("NJS", "UnregisteredMod"), "NJS");
        Equal(engine.Translate("BPM", "UnregisteredMod"), "BPM");
        var disabled = new TranslationEngine(tables, new[] { "Counters+" });
        Equal(disabled.Translate("Default", "Counters+"), "Default");
        Equal(disabled.Translate("Missed", "Counters+"), "Missed");
        Equal(disabled.Translate("Rank", "Search"), "排名");
        var noCommon = new TranslationEngine(tables, new[] { "Common" });
        Equal(noCommon.Translate("Default", "Search"), "Default");
        Equal(noCommon.Translate("Rank", "Search"), "排名");
        var off = new TranslationEngine(tables, null, false);
        Equal(off.Translate("Default", "Common"), "Default");
        Equal(off.Translate("12. Oct 26 - 13:15", "BetterSongSearch"), "12. Oct 26 - 13:15");
        var installed = new TranslationEngine(GeneratedCatalog.Create());
        var pluginScopes = new[] {
            new KeyValuePair<string, string>("BS Utils", "BS_Utils"),
            new KeyValuePair<string, string>("Custom Plugin", "CustomScope"),
            new KeyValuePair<string, string>("Untranslated Plugin", "UntranslatedAssembly")
        };
        var visibleScopes = TranslationEngine.InstalledScopes(pluginScopes, new[] { "BS_Utils", "CustomScope", "NotInstalled" });
        Equal(String.Join(",", visibleScopes), "BS_Utils,Common,CustomScope,GameFeedback");
        var disabledUtils = new TranslationEngine(GeneratedCatalog.Create(), new[] { "BS_Utils" });
        Equal(disabledUtils.Translate("A basic library for beat saber mods to use.", "BS_Utils"), "A basic library for beat saber mods to use.");
        Equal(installed.Translate("Session: 2.0 kJ · estimated", "ActivityEnergyCounter"),
            "本次：2.0 kJ · 估算");
        Equal(installed.Translate("Today total: 2.0 kJ · 01:01", "ActivityEnergyCounter"),
            "今日总耗能：2.0 kJ · 01:01");
        Equal(installed.Translate("2.0 kcal · estimated", "ActivityEnergyCounter"), "2.0 kcal · 估算");
        Equal(installed.Translate("2.0 kJ · estimated · 01:01", "ActivityEnergyCounter"), "2.0 kJ · 估算 · 01:01");
        Equal(installed.Translate("Session: 2.0 kJ · estimated\nSong: 1.0 kJ\nToday: 3.0 kJ", "ActivityEnergyCounter"),
            "本次：2.0 kJ · 估算\n本曲：1.0 kJ\n今日：3.0 kJ");
        Equal(installed.Translate("Song total: 1.0 kcal · estimated · 00:30\nSession total: 2.0 kcal · 01:00", "ActivityEnergyCounter"),
            "本曲总耗能：1.0 kcal · 估算 · 00:30\n本次总耗能：2.0 kcal · 01:00");
        Equal(installed.Translate("Song: 1.0 kcal · estimated · 00:30   |   Session: 2.0 kcal · 01:00   |   Today: 3.0 kcal · 01:30", "ActivityEnergyCounter"),
            "本曲：1.0 kcal · 估算 · 00:30   |   本次：2.0 kcal · 01:00   |   今日：3.0 kcal · 01:30");
        Equal(installed.Translate("1.0 kcal · estimated   |   2.0 kcal   |   3.0 kcal", "ActivityEnergyCounter"),
            "1.0 kcal · 估算   |   2.0 kcal   |   3.0 kcal");
        Equal(installed.Translate("Retained 200/400 · 50.0%\nPeak density 8.0 → 4.0 NPS\nMax score 1000 → 500", "NoteMotionControl"),
            "保留 200/400 · 50.0%\n峰值密度 8.0 → 4.0 NPS\n最高分 1000 → 500");
        Equal(installed.Translate("PlaylistManager Settings", "PlaylistManager"), "PlaylistManager 设置");
        Equal(installed.Translate("More Playlists", "MorePlaylists"), "更多歌单");
        Equal(installed.Translate("⏬ Playlist + Songs", "MorePlaylists"), "⏬ 歌单 + 歌曲");
        Equal(installed.Translate("Default [3/5 downloaded]", "PlaylistManager"), "Default [已下载 3/5]");
        Equal(installed.Translate("Settings [Download Queued]", "PlaylistManager"), "Settings [等待下载]");
        Equal(installed.Translate("Song successfully added to Default", "PlaylistManager"), "歌曲已成功添加到歌单：Default");
        Equal(installed.Translate("user:Default", "MorePlaylists"), "用户：Default");
        Equal(installed.Translate("Default, IncludeEmpty, CuratedOnly", "MorePlaylists"), "Default，包含空歌单，仅精选");
        Equal(installed.Translate("Playlist download failed: Playlist download timed out after 90 seconds.\nTry again.", "MorePlaylists"), "歌单下载失败：歌单下载在 90 秒后超时。\n请重试。");
        Equal(installed.Translate("Playlist download failed: https://example.invalid/Default\nTry again.", "MorePlaylists"), "歌单下载失败：https://example.invalid/Default\n请重试。");
        Equal(installed.Translate("1608 songs loaded\n11 playlists loaded", "SongCore"), "已加载 1608 首歌曲\n已加载 11 个歌单");
        Equal(installed.Translate("1 song deleted\n11 playlists loaded", "SongCore"), "已删除 1 首歌曲\n已加载 11 个歌单");
        Equal(installed.Translate("You're about to reach the peak difficulty in <color=#ffa500ff>3.0</color> seconds!", "SongChartVisualizer"),
            "你将在 <color=#ffa500ff>3.0</color> 秒后达到最高难度！");
        string submission = "<size=115%><color=#f03030>Score Submission Disabled By</color></size>\nNoteMotionControl\n<size=80%><color=#999999>Note speed settings changed native movement parameters.</color></size>";
        string translatedSubmission = "<size=115%><color=#f03030>以下模组已禁用成绩提交</color></size>\n音符训练控制\n<size=80%><color=#999999>音符速度设置已生效。</color></size>";
        Equal(installed.Translate(submission, "SiraUtil"), translatedSubmission);
        var disabledSubmission = new TranslationEngine(GeneratedCatalog.Create(), new[] { "SiraUtil" });
        Equal(disabledSubmission.Translate(submission, "SiraUtil"), submission);
        var disabledMotion = new TranslationEngine(GeneratedCatalog.Create(), new[] { "NoteMotionControl" });
        Equal(installed.Translate("Note Training Control", "NoteMotionControl"), "音符训练控制");
        Equal(installed.Translate("Configure Note Training Control", "NoteMotionControl"), "音符训练控制");
        Equal(installed.Translate("Note Training Control", "ModUiChinese"), "音符训练控制");
        Equal(disabledMotion.Translate("Note Training Control", "NoteMotionControl"), "Note Training Control");
        Equal(disabledMotion.Translate(submission, "SiraUtil"), translatedSubmission.Replace("音符训练控制", "NoteMotionControl").Replace("音符速度设置已生效。", "Note speed settings changed native movement parameters."));
        string authoredMotionNotice = "Author-controlled note motion was preserved; movement controls were not applied to this chart.";
        Equal(installed.Translate(authoredMotionNotice, "NoteMotionControl"), "已保留谱面作者的音符运动，本谱面未应用运动设置。");
        Equal(disabledMotion.Translate(authoredMotionNotice, "NoteMotionControl"), authoredMotionNotice);
        Equal(installed.Translate("<size=75%>SongChartVisualizer</size>", "SongChartVisualizer"), "<size=75%>歌曲难度曲线</size>");
        var disabledChart = new TranslationEngine(GeneratedCatalog.Create(), new[] { "SongChartVisualizer" });
        Equal(disabledChart.Translate("<size=75%>SongChartVisualizer</size>", "SongChartVisualizer"), "<size=75%>SongChartVisualizer</size>");
        Equal(installed.Translate("Miss", "GameFeedback"), "漏击");
        Equal(installed.Translate("Positionable, assigned to 2 Scenes", "Camera2"), "可定位，已分配 2 个场景");
        Equal(installed.Translate("Jazz (12)", "BetterSongSearch"), "爵士乐（12）");
        Equal(installed.Translate("BS_Utils", "ModUiChinese"), "基础工具");
        Equal(installed.Translate("<color=#6dd9ff>› Any</color>", "AdvancedSongFilters"), "<color=#6dd9ff>› 不限</color>");
        Equal(installed.Translate("Camera2 | Default", "Camera2"), "镜头设置 | Default");
        Equal(installed.Translate("13 M", "BetterSongList"), "13 个月");
        var disabledCamera = new TranslationEngine(GeneratedCatalog.Create(), new[] { "Camera2" });
        Equal(disabledCamera.Translate("Positionable, assigned to 2 Scenes", "Camera2"), "Positionable, assigned to 2 Scenes");
        var disabledFeedback = new TranslationEngine(GeneratedCatalog.Create(), new[] { "GameFeedback" });
        Equal(disabledFeedback.Translate("Miss", "GameFeedback"), "Miss");
        bool rejected = false;
        try { TranslationEngine.ValidateTemplate("Value {0}", "值 {0} }"); }
        catch (FormatException) { rejected = true; }
        checks++;
        if (!rejected) throw new Exception("Malformed custom template was accepted");
        Console.WriteLine("Translation boundary checks passed: " + checks);
    }
}
