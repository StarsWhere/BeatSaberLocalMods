"""Find untranslated captions in native runtime text snapshots, without editing user data."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

TAGS = re.compile(r"<[^>]+>")
WORDS = re.compile(r"[A-Za-z][A-Za-z0-9+_-]*")
# Scientific units, counter abbreviations, ranks and native toggle symbols.
SYMBOLS = frozenset("s ms m cm mm kg kJ kcal MET NJS NPS BPM FPS HP PP Hz dB SS S A B C D E F R G H X Y Z I O x".split())
FILE_OR_URL = re.compile(
    r"https?://\S+|[A-Za-z]:[/\\][^\s<>]+|"
    r"\b(?:UserData|Beat\ Saber_Data|CustomLevels|CustomWIPLevels|Plugins|Libs|Playlists|CustomSabers|CustomAvatars|localization)"
    r"(?:[/\\]+[A-Za-z0-9_.-]+)+\b|"
    r"\b[A-Za-z0-9_.-]+(?:[/\\]+[A-Za-z0-9_.-]+)*\.(?:json|ini|cfg|dll|bsml|png|jpg|zip)\b"
)
PLACEHOLDERS = re.compile(r"\{[0-9]+(?:,[^}:]+)?(?::[^}]+)?\}")


def resource_caption_coverage(inventory_path: Path, paths: list[Path], localization: Path) -> dict:
    """Compare installed resources with visible captions, excluding synthetic contracts.

    Shared captions can occur in several resources; this proves caption observation,
    not that a resource's controller or every conditional branch was instantiated.
    """
    spec = importlib.util.spec_from_file_location("resource_catalog", Path(__file__).parent / "mod_ui_chinese/generate_catalog.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    catalog, _ = module.collect(localization)
    normalize = lambda value: re.sub(r"\s+", " ", TAGS.sub("", value)).strip().casefold()
    observed = {}
    for path in paths:
        rows = json.loads(path.read_text(encoding="utf-8-sig"))
        for row in rows:
            if row.get("protectedData") or not row.get("localizationEnabled", True) or not row.get("scopeEnabled", True) or row.get("kind") in {"caption-contract", "native-dialog-arguments"}:
                continue
            values = observed.setdefault(row["scope"], set())
            values.update(normalize(row.get(key) or "") for key in ["source", "text"])
    inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))
    resources = []
    for plugin in inventory["plugins"]:
        scope = plugin["assembly"]
        targets = dict(catalog.get("Common", {}), **catalog.get(scope, {}))
        for resource in plugin["resources"]:
            source = inventory_path.parent / resource["path"]
            row = {"scope": scope, "resource": resource["name"], "captionCount": 0,
                   "observedCaptions": [], "unobservedCaptions": []}
            if scope == "BSML" and re.search(r"(?:\.|-)(?:test|tests)\.bsml$", resource["name"], re.I):
                row["status"] = "development-sample"
            else:
                try:
                    content = re.sub(r"<\?xml.*?\?>", "", source.read_text(encoding="utf-8-sig"))
                    xml = ET.fromstring("<audit-root>" + content + "</audit-root>")
                    captions = sorted({value for element in xml.iter() for key, value in element.attrib.items()
                                       if key in {"text", "hover-hint", "tab-name", "placeholder"}
                                       and value.strip() and not value.startswith(("~", "#"))
                                       and re.search("[A-Za-z]", value)})
                    row["captionCount"] = len(captions)
                    for caption in captions:
                        found = normalize(caption) in observed.get(scope, set()) or normalize(targets.get(caption, caption)) in observed.get(scope, set())
                        row["observedCaptions" if found else "unobservedCaptions"].append(caption)
                    row["status"] = "no-literal-captions" if not captions else "observed" if not row["unobservedCaptions"] else "partial" if row["observedCaptions"] else "unobserved"
                except (OSError, ET.ParseError) as error:
                    row.update(status="inspection-error", error=str(error))
            resources.append(row)
    return {"schemaVersion": 1, "kind": "installed-resource-caption-observation",
            "resources": resources,
            "unobservedResources": sum(row["status"] == "unobserved" for row in resources),
            "note": "Caption observation only. Conditional errors and shared templates require their own controller/branch evidence."}


def declared_latin(text: str, scopes: list[dict[str, str]], tokens: list[str]) -> bool:
    """Retain Latin literals explicitly present in the chosen Chinese captions."""
    for table in scopes:
        for target in table.values():
            plain = TAGS.sub("", target)
            literal_tokens = set(WORDS.findall(PLACEHOLDERS.sub("", plain)))
            if not literal_tokens or not set(tokens) <= literal_tokens:
                continue
            parts = PLACEHOLDERS.split(plain)
            pattern = ".*?".join(re.escape(part) for part in parts)
            if re.fullmatch(pattern, text, re.DOTALL):
                return True
    return False


def audit(paths: list[Path], localization: Path, scopes: list[str] | None = None) -> dict:
    spec = importlib.util.spec_from_file_location("caption_catalog", Path(__file__).parent / "mod_ui_chinese/generate_catalog.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    catalog, _ = module.collect(localization)
    owners: dict[str, list[str]] = {}
    for scope, entries in catalog.items():
        for source, target in entries.items():
            if source != target:
                owners.setdefault(TAGS.sub("", source).strip().casefold(), []).append(scope)
    findings = []
    seen = set()
    scanned = 0
    excluded = 0
    included_scopes = set(scopes) if scopes else None
    for path in paths:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(data, list):
            raise ValueError(f"Not a native UI text snapshot: {path}")
        for item in data:
            if not isinstance(item, dict) or not {"text", "source", "scope", "path", "protectedData"} <= item.keys():
                raise ValueError(f"Invalid UI snapshot entry: {path}")
            if included_scopes is not None and item["scope"] not in included_scopes:
                excluded += 1
                continue
            scanned += 1
            if item["protectedData"] or not item["text"] or not item.get("localizationEnabled", True) or not item.get("scopeEnabled", True):
                continue
            plain = TAGS.sub("", item["text"])
            if re.fullmatch(r"[A-Za-z]", plain.strip()):
                continue
            if re.fullmatch(r"F(?:[1-9]|1[0-9]|2[0-4])", plain.strip()):
                continue
            if re.fullmatch(r":[A-Za-z0-9_+\-]+:", plain.strip()):
                continue
            caption = plain
            segments = item.get("dataSegments", [])
            if not isinstance(segments, list) or not all(isinstance(value, str) for value in segments):
                raise ValueError(f"Invalid caption data segments: {path}")
            for segment in sorted((value for value in segments if value), key=len, reverse=True):
                start = caption.rfind(segment)
                if start >= 0:
                    caption = caption[:start] + caption[start + len(segment):]
            prose = PLACEHOLDERS.sub("", FILE_OR_URL.sub("", caption))
            remaining = [word for word in WORDS.findall(prose)
                         if word not in SYMBOLS and not re.fullmatch(r"[Vv][0-9]+", word)]
            if not remaining:
                continue
            if declared_latin(plain, [catalog.get(item["scope"], {}), catalog.get("Common", {})], remaining):
                continue
            key = (item["scope"], item["source"], item["text"], item["path"])
            if key in seen:
                continue
            seen.add(key)
            possible = sorted(set(owners.get(plain.strip().casefold(), [])))
            findings.append({
                "snapshot": str(path), "scope": item["scope"], "path": item["path"],
                "source": item["source"], "text": item["text"], "englishTokens": remaining,
                "catalogOwners": possible,
                "reason": "unapplied-caption" if possible else "untranslated-prose",
            })
    return {"schemaVersion": 1, "snapshots": len(paths), "scannedTexts": scanned,
            "includedScopes": sorted(included_scopes) if included_scopes is not None else None,
            "outOfScopeTexts": excluded,
            "findings": findings}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshots", type=Path, nargs="+", help="Chinese/default-language native Capture JSON files (exclude intentional English captures)")
    parser.add_argument("--localization", type=Path, default=Path(__file__).resolve().parent.parent / "localization")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--fail-on-findings", action="store_true")
    parser.add_argument("--inventory", type=Path, help="Also report installed resource captions not observed in actual UI snapshots")
    parser.add_argument("--scope", action="append", help="Audit only this owner scope; repeat for a focused UI run")
    args = parser.parse_args()
    report = audit(args.snapshots, args.localization, args.scope)
    if args.inventory:
        report["resourceCaptionCoverage"] = resource_caption_coverage(args.inventory, args.snapshots, args.localization)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Localization audit: {report['snapshots']} snapshots, {report['scannedTexts']} texts, {len(report['findings'])} findings; {args.output}")
    return 1 if args.fail_on_findings and report["findings"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
