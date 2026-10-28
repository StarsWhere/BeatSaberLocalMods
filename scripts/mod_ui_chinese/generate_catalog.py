"""Validate the translation source and generate a compiled caption catalog."""
from __future__ import annotations
import argparse
import hashlib
import html
import json
import re
from pathlib import Path

PLACEHOLDERS = re.compile(r"\{[^{}]+\}")
TAGS = re.compile(r"<[^>]+>")
ALIASES = {"CountersPlus": "Counters+"}

def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate translation key: {key!r}")
        result[key] = value
    return result

def read(path):
    value = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)
    if not isinstance(value, dict):
        raise ValueError(f"Expected dictionary: {path}")
    return value

def validate(source, target, path):
    if not isinstance(source, str) or not source or not isinstance(target, str) or not target.strip():
        raise ValueError(f"Empty or invalid translation: {path}: {source!r}")
    if sorted(PLACEHOLDERS.findall(source)) != sorted(PLACEHOLDERS.findall(target)):
        raise ValueError(f"Changed placeholders: {path}: {source!r}")
    if TAGS.findall(source) != TAGS.findall(target):
        raise ValueError(f"Changed rich text tags: {path}: {source!r}")
    if re.fullmatch(r"[A-Za-z]", source) and source != target:
        raise ValueError(f"Single letter/rank must be retained: {path}: {source!r}")

def collect(directory):
    catalogs = {}
    hashes = {}
    base = directory / "ui.json"
    common = {key: value for key, value in read(base).items() if re.search(r"[\u3400-\u9fff]", value)}
    hashes[str(base.name)] = hashlib.sha256(base.read_bytes()).hexdigest()
    for path in sorted((directory / "mods").glob("*.json")):
        values = read(path)
        for source, target in values.items():
            validate(source, target, path.name)
        scope = ALIASES.get(path.stem, path.stem)
        rendered = {}
        for source, target in values.items():
            source, target = html.unescape(source), html.unescape(target)
            if source in rendered and rendered[source] != target:
                raise ValueError(f"Conflicting rendered caption: {path}: {source!r}")
            rendered[source] = target
        hashes["mods/" + path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
        if scope == "Common":
            common.update(rendered)
        else:
            catalogs[scope] = rendered
    catalogs["Common"] = common
    return catalogs, hashes

def generate(directory, output):
    catalog, hashes = collect(directory)
    output.mkdir(parents=True, exist_ok=True)
    lines = ["using System;", "using System.Collections.Generic;", "namespace ModUiChinese {", "internal static class GeneratedCatalog {", "public static Dictionary<string, Dictionary<string,string>> Create() {", "var catalog = new Dictionary<string, Dictionary<string,string>>(StringComparer.Ordinal);"]
    for scope, table in sorted(catalog.items()):
        lines.append("catalog.Add(" + json.dumps(scope, ensure_ascii=False) + ", new Dictionary<string,string>(StringComparer.Ordinal) {")
        for key, value in sorted(table.items()):
            lines.append("{ " + json.dumps(key, ensure_ascii=False) + ", " + json.dumps(value, ensure_ascii=False) + " },")
        lines.append("});")
    lines.extend(["return catalog;", "}", "public static bool HasScope(string name) {", "switch(name) {"])
    for scope in sorted(catalog):
        lines.append("case " + json.dumps(scope) + ": return true;")
    lines.extend(["default: return false;", "}", "}", "}", "}"])
    (output / "GeneratedCatalog.cs").write_text("\n".join(lines), encoding="utf-8-sig")
    metadata = {"formatVersion": 1, "locale": "zh-CN", "translationSources": hashes, "scopeCounts": {k: len(v) for k, v in catalog.items()}}
    (output / "catalog-provenance.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(metadata["scopeCounts"], ensure_ascii=False))

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--localization-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    generate(args.localization_dir, args.output_dir)
