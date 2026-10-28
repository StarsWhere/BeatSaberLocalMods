# BeatSaberLocalMods

面向 Windows x64、Beat Saber **1.45.2_28848** 的 11 个自制模组。

| 模组 | 版本 | 功能 |
| --- | --- | --- |
| LocalModUI | 0.1.0 | 统一模组导航与设置入口 |
| AdvancedSongFilters | 0.3.0 | 歌曲筛选、排序与个人预设 |
| BeatSage | 0.2.2 | 从本地音乐生成谱面 |
| VoiceInput | 0.1.1 | 键盘语音输入，支持腾讯云和百度识别 |
| NoteMotionControl | 0.4.0 | 练习预设、音符密度、速度与反应时间设置 |
| ActivityEnergyCounter | 0.2.1 | 根据头显与控制器运动估算活动能量 |
| RoomCalibration | 0.4.1 | 房间位置和朝向校准 |
| ModUiChinese | 1.1.7 | 模组界面简体中文翻译 |
| SongDuplicateCleaner | 0.1.0 | 预览并清理完全相同的重复谱面 |
| Camera2UrpFix | 0.3.1 | Camera2 的 URP、XR 与配置交互适配 |
| UnityShutdownFix | 0.1.0 | Unity 退出时的辅助功能清理修复 |

## 安装

从 [Releases](https://github.com/StarsWhere/BeatSaberLocalMods/releases) 下载合集，或选择单独模组包。关闭游戏，将包内目录合并到游戏根目录；安装单独模组时一并安装其 manifest 声明的依赖，使用统一设置入口的模组需要 LocalModUI。

依赖由本地游戏安装提供，包括 BSIPA、BSML、SiraUtil、SongCore，以及部分模组需要的 BetterSongList、CustomJSONData、Counters+ 和 Camera2，具体版本范围见各模组的 manifest。目标依赖环境包含本地移植版本，普通官方依赖组合的兼容性需另行确认。发布包保留所需通用运行库，不分发游戏程序集或上述第三方模组。

## 构建

准备 Windows x64、PowerShell 7、.NET 8 SDK 或更新版本、Python 3，以及带有兼容依赖 DLL 的 Beat Saber 1.45.2_28848 安装。在仓库根目录运行：

```powershell
pwsh -File scripts/build_release.ps1 -GameRoot 'C:\Games\Beat Saber'
```

`scripts/` 包含各模组源码、共享组件及单独构建入口；`localization/` 保存翻译数据。临时构建输出位于 `.tmp/`，发布输出位于 `releases/`，包括 11 个单独模组包、合集、项目源码包、TagLibSharp 对应源码包和 SHA256 校验文件。此发布流程执行构建与包内容校验；游戏内加载和运行行为需在目标依赖环境验证。

## 许可

项目原创代码采用 [MIT](LICENSE)。第三方组件保留各自许可，完整声明与许可文本见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和 [licenses/](licenses/)。Beat Sage 随附的 TagLibSharp 2.3.0 采用 LGPL-2.1-only，同一 Release 提供其完整对应源码包 `TagLibSharp-2.3.0-source.zip`。
