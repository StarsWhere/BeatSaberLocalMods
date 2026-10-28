"""Regression checks for the native-caption audit's data and prose boundaries."""
from pathlib import Path
import argparse
import importlib.util
import json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-directory", type=Path, required=True)
    args = parser.parse_args()
    scripts = Path(__file__).resolve().parent.parent
    spec = importlib.util.spec_from_file_location("ui_audit", scripts / "audit_mod_ui_localization.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    cases = [
        ("RoomCalibration", "F8", False, True, False),
        ("SongChartVisualizer", "R: 0", False, True, False),
        ("PlaylistManager", "UserData/Playlists", False, True, False),
        ("BetterSongSearch", "ScoreSaber", False, True, False),
        ("Camera2", "Default", True, True, False),
        ("Camera2", "Settings", False, False, False),
        ("BetterSongSearch", "Jazz (12)", False, True, True),
        ("Camera2", "An unknown display warning", False, True, True),
        ("BetterSongSearch", "载入失败：unexpected response", False, True, True),
        ("Camera2", "Upload/Download", False, True, True),
        ("BeatSage", "j", False, True, False),
        ("BeatSage", "Backspace", False, True, True),
        ("BeatSage", "模型 · V2", False, True, False),
        ("BeatSage", "V2 Settings", False, True, True),
    ]
    rows = [dict(path=f"case/{i}", scope=scope, text=text, source=text,
                 protectedData=protected, scopeEnabled=enabled)
            for i, (scope, text, protected, enabled, _) in enumerate(cases)]
    rows.extend([
        dict(path="camera-title-data", scope="Camera2", text="镜头设置 | Default", source="Camera2 | Default", protectedData=False, dataSegments=["Default"]),
        dict(path="camera-title-caption", scope="Camera2", text="Camera2 | Camera2", source="Camera2 | Camera2", protectedData=False, dataSegments=["Camera2"]),
        dict(path="config-name-data", scope="HitScoreVisualizer", text="当前加载的配置：HitScoreVisualizerConfig (default)", source="Currently Loaded Config : HitScoreVisualizerConfig (default)", protectedData=False, dataSegments=["HitScoreVisualizerConfig (default)"]),
        dict(path="config-caption", scope="HitScoreVisualizer", text="Currently Loaded Config : HitScoreVisualizerConfig (default)", source="Currently Loaded Config : HitScoreVisualizerConfig (default)", protectedData=False, dataSegments=["HitScoreVisualizerConfig (default)"]),
        dict(path="mod-credit-data", scope="BetterSongList", text="BetterSongList v0.4.3，作者：Kinsi55", source="BetterSongList v0.4.3 by Kinsi55", protectedData=False, dataSegments=["Kinsi55"]),
    ])
    args.build_directory.mkdir(parents=True, exist_ok=True)
    fixture = args.build_directory / "audit-boundaries.json"
    fixture.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
    report = module.audit([fixture], scripts.parent / "localization")
    actual = {finding["path"] for finding in report["findings"]}
    expected = {f"case/{i}" for i, case in enumerate(cases) if case[-1]}
    expected.add("camera-title-caption")
    expected.add("config-caption")
    if actual != expected:
        raise AssertionError(f"Caption audit mismatch: {actual} != {expected}")
    print(f"UI audit boundary checks passed: {len(rows)}")
    focused = module.audit([fixture], scripts.parent / "localization", ["BeatSage"])
    assert {row["path"] for row in focused["findings"]} == {"case/11", "case/13"}
    assert focused["includedScopes"] == ["BeatSage"] and focused["outOfScopeTexts"] == len(rows) - 4
    assert module.audit([fixture], scripts.parent / "localization", ["AbsentScope"])["scannedTexts"] == 0
    print("Focused UI audit boundary checks passed: 3")
    resource = args.build_directory / "resource-probe.bsml"
    resource.write_text("<vertical><text text='Enabled'/><text text='Settings'/></vertical>", encoding="utf-8")
    inventory = args.build_directory / "resource-inventory.json"
    inventory.write_text(json.dumps({"plugins": [{"assembly": "DensityReducer", "resources": [{
        "name": "resource-probe.bsml", "path": resource.name}]}]}), encoding="utf-8")
    probe = args.build_directory / "resource-observations.json"
    probe.write_text(json.dumps([
        dict(scope="DensityReducer", source="Enabled", text="启用", protectedData=False),
        dict(scope="DensityReducer", source="Settings", text="Settings", protectedData=True),
        dict(scope="DensityReducer", source="Settings", text="Settings", protectedData=False, scopeEnabled=False),
    ]), encoding="utf-8")
    observed = module.resource_caption_coverage(inventory, [probe], scripts.parent / "localization")["resources"][0]
    assert observed["status"] == "partial" and observed["observedCaptions"] == ["Enabled"]
    assert observed["unobservedCaptions"] == ["Settings"]
    print("UI resource caption boundary checks passed: 3")


if __name__ == "__main__":
    main()
