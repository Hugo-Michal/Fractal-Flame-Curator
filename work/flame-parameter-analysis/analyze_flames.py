#!/usr/bin/env python3
"""Extract and compare Apophysis flame-genome parameter distributions.

The tool is intentionally read-only with respect to source workspaces. It writes
CSV/JSON/HTML artifacts only to the configured output directory.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import html
import json
import math
import re
import statistics
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence


TOOL_VERSION = "1.0.0"
SCORE_PREFIX = re.compile(r"^\d{6}__")
RUN_SUFFIX = re.compile(r"_run_([^_]+)$", re.IGNORECASE)
SEQUENCE_PREFIX = re.compile(r"^flame_(\d+)_", re.IGNORECASE)
SEED_TOKEN = re.compile(r"_seed_(-?\d+)(?:_|$)", re.IGNORECASE)
SUPPORTED_VARIATIONS = (
    "linear", "sinusoidal", "spherical", "swirl", "horseshoe", "polar",
    "handkerchief", "heart", "disc", "spiral", "hyperbolic", "diamond",
    "ex", "julia", "bent", "waves", "fisheye", "popcorn", "exponential",
    "power", "cosine", "rings", "fan", "blob", "pdj", "perspective",
    "noise", "julian", "juliascope", "curl", "rectangles", "tangent",
    "cross", "rays", "secant", "twintrian", "blur", "radial_blur",
)
RESERVED_TRANSFORM_ATTRIBUTES = {
    "weight", "color", "symmetry", "coefs", "a", "b", "c", "d", "e", "f", "post"
}
MATRIX_METADATA_COLUMNS = (
    "group", "source_id", "run_id", "sequence", "seed_from_filename",
    "source_path", "source_sha256", "flame_name", "flame_version", "xml_seed",
)
GROUP_COLORS = ("#2155A3", "#D97706", "#16803C", "#7C3AED", "#C0264A", "#087E8B")


@dataclass(frozen=True)
class ParameterSpec:
    category: str
    kind: str
    unit: str = ""
    scope: str = "flame"
    conditional: bool = False
    description: str = ""
    generator_setting: str = ""
    analyze: bool = True


class AnalysisState:
    def __init__(self) -> None:
        self.records: list[dict[str, Any]] = []
        self.specs: dict[str, ParameterSpec] = {}
        self.observations: dict[str, dict[str, list[Any]]] = defaultdict(lambda: defaultdict(list))
        self.scope_totals: dict[str, Counter[str]] = defaultdict(Counter)
        self.manifest: list[dict[str, Any]] = []
        self.inventory: list[dict[str, Any]] = []
        self.warnings: list[str] = []

    def register(self, key: str, spec: ParameterSpec) -> None:
        existing = self.specs.get(key)
        if existing is not None and existing != spec:
            raise ValueError(f"Conflicting parameter specification for {key}")
        self.specs[key] = spec

    def put(self, record: dict[str, Any], key: str, value: Any, spec: ParameterSpec) -> None:
        self.register(key, spec)
        record[key] = value

    def observe(self, group: str, key: str, value: Any, spec: ParameterSpec) -> None:
        self.register(key, spec)
        if spec.analyze and value is not None:
            self.observations[group][key].append(value)

    def put_and_observe(
        self, record: dict[str, Any], group: str, key: str, value: Any, spec: ParameterSpec
    ) -> None:
        self.put(record, key, value, spec)
        self.observe(group, key, value, spec)


def stable_source_id(path: Path) -> str:
    return SCORE_PREFIX.sub("", path.stem)


def source_tokens(source_id: str) -> tuple[str, str, str]:
    run_match = RUN_SUFFIX.search(source_id)
    sequence_match = SEQUENCE_PREFIX.search(source_id)
    seed_match = SEED_TOKEN.search(source_id)
    return (
        run_match.group(1) if run_match else "",
        sequence_match.group(1) if sequence_match else "",
        seed_match.group(1) if seed_match else "",
    )


def parse_number(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        number = float(value)
    except ValueError:
        return None
    return number if math.isfinite(number) else None


def parse_vector(value: str | None, length: int) -> list[float] | None:
    if not value:
        return None
    parts = value.replace("\t", " ").replace("\r", " ").replace("\n", " ").split()
    if len(parts) != length:
        return None
    values = [parse_number(part) for part in parts]
    return [float(item) for item in values] if all(item is not None for item in values) else None


def read_coefficients(element: ET.Element) -> list[float] | None:
    coefficients = parse_vector(element.get("coefs"), 6)
    if coefficients is not None:
        return coefficients
    defaults = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    values: list[float] = []
    for name, fallback in zip("abcdef", defaults):
        value = parse_number(element.get(name))
        values.append(fallback if value is None else value)
    return values


def derived_affine(coefficients: Sequence[float]) -> tuple[float, float, float, float, float]:
    a, b, c, _d, e, f = coefficients
    scale = math.hypot(a, c)
    rotation = math.degrees(math.atan2(c, a))
    shear = b + c
    return rotation, scale, shear, e, f


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def natural_key(value: str) -> list[Any]:
    return [int(part) if part.isdigit() else part.lower() for part in re.split(r"(\d+)", value)]


def numeric_spec(
    category: str,
    unit: str = "",
    scope: str = "flame",
    conditional: bool = False,
    description: str = "",
    generator_setting: str = "",
    analyze: bool = True,
) -> ParameterSpec:
    return ParameterSpec(category, "numeric", unit, scope, conditional, description, generator_setting, analyze)


def circular_spec(
    category: str,
    unit: str = "degrees",
    scope: str = "flame",
    conditional: bool = False,
    description: str = "",
    generator_setting: str = "",
    analyze: bool = True,
) -> ParameterSpec:
    return ParameterSpec(category, "circular", unit, scope, conditional, description, generator_setting, analyze)


def categorical_spec(
    category: str,
    scope: str = "flame",
    conditional: bool = False,
    description: str = "",
    generator_setting: str = "",
    analyze: bool = True,
) -> ParameterSpec:
    return ParameterSpec(category, "categorical", "", scope, conditional, description, generator_setting, analyze)


def boolean_spec(
    category: str,
    scope: str = "flame",
    description: str = "",
    generator_setting: str = "",
    analyze: bool = True,
) -> ParameterSpec:
    return ParameterSpec(category, "boolean", "", scope, False, description, generator_setting, analyze)


def add_root_parameter(
    state: AnalysisState,
    record: dict[str, Any],
    group: str,
    key: str,
    raw_value: str | None,
    spec: ParameterSpec,
) -> None:
    if spec.kind in {"numeric", "circular"}:
        value: Any = parse_number(raw_value)
    else:
        value = raw_value
    if value is not None:
        state.put_and_observe(record, group, key, value, spec)


def parse_palette(state: AnalysisState, record: dict[str, Any], group: str, palette: ET.Element | None) -> None:
    if palette is None:
        state.warnings.append(f"{record['source_path']}: no palette element")
        return
    state.scope_totals[group]["palette"] += 1
    count = parse_number(palette.get("count"))
    if count is not None:
        state.put_and_observe(
            record, group, "palette.count", count,
            numeric_spec("palette", "colors", "palette", description="Declared palette color count"),
        )
    palette_format = palette.get("format", "")
    state.put_and_observe(
        record, group, "palette.format", palette_format,
        categorical_spec("palette", "palette", description="Serialized palette format"),
    )
    raw = "".join((palette.text or "").split()).upper()
    if not raw:
        state.warnings.append(f"{record['source_path']}: empty palette")
        return
    state.put_and_observe(
        record, group, "palette.sha256", hashlib.sha256(raw.encode("ascii", "replace")).hexdigest(),
        categorical_spec("palette", "palette", description="Hash identifying the complete palette"),
    )
    if len(raw) % 6 != 0 or not re.fullmatch(r"[0-9A-F]+", raw):
        state.warnings.append(f"{record['source_path']}: palette is not complete RGB hex")
        return
    colors = [(int(raw[i:i + 2], 16), int(raw[i + 2:i + 4], 16), int(raw[i + 4:i + 6], 16)) for i in range(0, len(raw), 6)]
    state.put_and_observe(
        record, group, "palette.unique_color_count", len(set(colors)),
        numeric_spec("palette", "colors", "palette", description="Number of distinct RGB colors"),
    )
    channels = {"red": [c[0] for c in colors], "green": [c[1] for c in colors], "blue": [c[2] for c in colors]}
    luminance = [0.2126 * r + 0.7152 * g + 0.0722 * b for r, g, b in colors]
    channels["luminance"] = luminance
    for name, values in channels.items():
        for suffix, value in (
            ("mean", statistics.fmean(values)),
            ("std", statistics.pstdev(values) if len(values) > 1 else 0.0),
            ("min", min(values)),
            ("max", max(values)),
        ):
            state.put_and_observe(
                record, group, f"palette.{name}.{suffix}", value,
                numeric_spec("palette", "0-255", "palette", description=f"Palette {name} {suffix}"),
            )


def parse_transform(
    state: AnalysisState,
    record: dict[str, Any],
    group: str,
    element: ET.Element,
    index: int | None,
    is_final: bool,
) -> tuple[set[str], int, bool]:
    prefix = "finalxform" if is_final else f"xform.{index:02d}"
    pooled = "finalxform[*]" if is_final else "xform[*]"
    scope = "finalxform" if is_final else "xform"
    category = "final transform" if is_final else "base transform"
    state.scope_totals[group][scope] += 1

    scalar_fields = (
        ("weight", "weight", numeric_spec(category, scope=scope, conditional=is_final, description="Transform selection weight", generator_setting="Transform balance")),
        ("color", "color", numeric_spec(category, scope=scope, conditional=is_final, description="Palette position assigned to the transform")),
        ("symmetry", "symmetry", numeric_spec(category, scope=scope, conditional=is_final, description="Legacy per-transform symmetry field")),
    )
    for attribute, name, spec in scalar_fields:
        value = parse_number(element.get(attribute))
        if value is not None:
            state.put(record, f"{prefix}.{name}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))
            state.observe(group, f"{pooled}.{name}", value, spec)

    coefficients = read_coefficients(element)
    if coefficients is None:
        raise ValueError(f"{prefix} has invalid affine coefficients")
    coefficient_names = ("a", "b", "c", "d", "e", "f")
    for name, value in zip(coefficient_names, coefficients):
        state.put(
            record, f"{prefix}.affine.{name}", value,
            numeric_spec(category, scope=scope, conditional=is_final, description="Serialized affine coefficient", analyze=False),
        )
        state.observe(
            group, f"{pooled}.affine.{name}", value,
            numeric_spec(category, scope=scope, conditional=is_final, description="Serialized affine coefficient"),
        )
    rotation, scale, shear, translate_x, translate_y = derived_affine(coefficients)
    derived_values = (
        ("rotation_degrees", rotation, "degrees", "Affine rotation", "Affine rotation reconstructed from coefficients", True),
        ("scale", scale, "ratio", "Affine scale", "Affine scale reconstructed from coefficients", False),
        ("shear", shear, "coefficient", "Affine shear", "Affine shear reconstructed using the current generator convention B + C", False),
        ("translation_x", translate_x, "coordinate", "Translation extent", "Affine translation on the x axis", False),
        ("translation_y", translate_y, "coordinate", "Translation extent", "Affine translation on the y axis", False),
    )
    for name, value, unit, setting, description, is_circular in derived_values:
        spec_factory = circular_spec if is_circular else numeric_spec
        spec = spec_factory("generator-facing affine", unit, scope, is_final, description, setting)
        state.put(record, f"{prefix}.derived.{name}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))
        state.observe(group, f"{pooled}.derived.{name}", value, spec)

    variations: dict[str, float] = {}
    unknown_attributes: dict[str, str] = {}
    for attribute, raw_value in element.attrib.items():
        normalized = attribute[4:] if attribute.lower().startswith("var_") else attribute
        if normalized.lower() in SUPPORTED_VARIATIONS:
            value = parse_number(raw_value)
            if value is not None:
                variations[normalized.lower()] = value
        elif attribute.lower() not in RESERVED_TRANSFORM_ATTRIBUTES:
            unknown_attributes[attribute] = raw_value

    variation_count = len(variations)
    state.put(
        record, f"{prefix}.variation_count", variation_count,
        categorical_spec("variation", scope, is_final, generator_setting="Variations per transform", analyze=False),
    )
    state.observe(
        group, f"{pooled}.variation_count", variation_count,
        categorical_spec("variation", scope, is_final, "Number of variations on a transform", "Variations per transform"),
    )
    for variation in SUPPORTED_VARIATIONS:
        present = variation in variations
        state.observe(
            group, f"{pooled}.variation.{variation}.present", present,
            boolean_spec("variation occurrence", scope, f"Whether {variation} occurs on a transform", "Enabled variation types"),
        )
        if present:
            value = variations[variation]
            state.put(
                record, f"{prefix}.variation.{variation}.weight", value,
                numeric_spec("variation weight", "proportion", f"variation:{variation}", True, analyze=False),
            )
            state.scope_totals[group][f"variation:{variation}"] += 1
            state.scope_totals[group]["variation_instance"] += 1
            state.observe(
                group, f"{pooled}.variation.{variation}.weight", value,
                numeric_spec("variation weight", "proportion", f"variation:{variation}", True, f"Conditional weight of {variation}", "Variation blend dominance"),
            )
            state.observe(
                group, f"{pooled}.variation.name", variation,
                categorical_spec("variation occurrence", "variation_instance", True, "Selected variation names", "Enabled variation types"),
            )
            state.observe(
                group, f"{pooled}.variation.weight", value,
                numeric_spec("variation weight", "proportion", "variation_instance", True, "All selected variation weights", "Variation blend dominance"),
            )

    for attribute, raw_value in unknown_attributes.items():
        numeric_value = parse_number(raw_value)
        value: Any = numeric_value if numeric_value is not None else raw_value
        spec = numeric_spec("unrecognized XML attribute", scope=scope, conditional=is_final) if numeric_value is not None else categorical_spec("unrecognized XML attribute", scope, is_final)
        state.put(record, f"{prefix}.attribute.{attribute}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))
        state.observe(group, f"{pooled}.attribute.{attribute}", value, spec)

    post = parse_vector(element.get("post"), 6)
    has_post = post is not None
    state.put(
        record, f"{prefix}.post.present", has_post,
        boolean_spec("post transform", scope, "Whether a transform has a post transform", "Post-transform chance", analyze=False),
    )
    state.observe(
        group, f"{pooled}.post.present", has_post,
        boolean_spec("post transform", scope, "Whether a transform has a post transform", "Post-transform chance"),
    )
    if has_post and post is not None:
        post_scope = "final_post" if is_final else "post"
        state.scope_totals[group][post_scope] += 1
        for name, value in zip(coefficient_names, post):
            state.put(
                record, f"{prefix}.post.{name}", value,
                numeric_spec("post transform", scope=post_scope, conditional=True, analyze=False),
            )
            state.observe(
                group, f"{pooled}.post.{name}", value,
                numeric_spec("post transform", scope=post_scope, conditional=True, description="Serialized post-transform coefficient"),
            )
        post_rotation, post_scale, _post_shear, post_x, post_y = derived_affine(post)
        for name, value, unit, setting, description, is_circular in (
            ("rotation_degrees", post_rotation, "degrees", "Post rotation", "Post-transform rotation reconstructed from coefficients", True),
            ("scale", post_scale, "ratio", "Post scale", "Post-transform scale reconstructed from coefficients", False),
            ("translation_x", post_x, "coordinate", "Post translation extent", "Post-transform translation on the x axis", False),
            ("translation_y", post_y, "coordinate", "Post translation extent", "Post-transform translation on the y axis", False),
        ):
            spec_factory = circular_spec if is_circular else numeric_spec
            spec = spec_factory("generator-facing post transform", unit, post_scope, True, description, setting)
            state.put(record, f"{prefix}.post.derived.{name}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))
            state.observe(group, f"{pooled}.post.derived.{name}", value, spec)

    return set(variations), variation_count, has_post


def parse_flame_file(state: AnalysisState, group: str, path: Path) -> dict[str, Any]:
    source_id = stable_source_id(path)
    run_id, sequence, filename_seed = source_tokens(source_id)
    record: dict[str, Any] = {
        "group": group,
        "source_id": source_id,
        "run_id": run_id,
        "sequence": sequence,
        "seed_from_filename": filename_seed,
        "source_path": str(path.resolve()),
        "source_sha256": file_sha256(path),
    }
    root = ET.parse(path).getroot()
    flame = root if root.tag.rsplit("}", 1)[-1] == "flame" else next(
        (child for child in root if child.tag.rsplit("}", 1)[-1] == "flame"), None
    )
    if flame is None:
        raise ValueError("document has no flame element")
    state.scope_totals[group]["flame"] += 1
    record["flame_name"] = flame.get("name", "")
    record["flame_version"] = flame.get("version", "")
    record["xml_seed"] = flame.get("seed", "")

    for key, attribute, spec in (
        ("flame.scale", "scale", numeric_spec("camera", "coordinate scale", description="Camera scale")),
        ("flame.rotate", "rotate", numeric_spec("camera", "degrees", description="Camera rotation")),
        ("flame.symmetry", "symmetry", categorical_spec("symmetry", description="Flame-level symmetry order", generator_setting="Allowed symmetry")),
        ("flame.oversample", "oversample", numeric_spec("render settings", "multiplier", description="Render oversampling")),
        ("flame.filter", "filter", numeric_spec("render settings", "radius", description="Filter radius")),
        ("flame.quality", "quality", numeric_spec("render settings", "samples/pixel", description="Serialized Apophysis sample density")),
        ("flame.brightness", "brightness", numeric_spec("tone settings", "ratio")),
        ("flame.gamma", "gamma", numeric_spec("tone settings", "ratio")),
        ("flame.gamma_threshold", "gamma_threshold", numeric_spec("tone settings", "ratio")),
        ("flame.vibrancy", "vibrancy", numeric_spec("tone settings", "ratio")),
        ("flame.hue_rotation", "hue_rotation", numeric_spec("tone settings", "turns")),
    ):
        add_root_parameter(state, record, group, key, flame.get(attribute), spec)

    for attribute, names, category, unit in (
        ("size", ("width", "height"), "render settings", "pixels"),
        ("center", ("x", "y"), "camera", "coordinate"),
        ("background", ("red", "green", "blue"), "tone settings", "normalized channel"),
    ):
        vector = parse_vector(flame.get(attribute), len(names))
        if vector is None:
            state.warnings.append(f"{path}: invalid or missing {attribute} vector")
            continue
        for name, value in zip(names, vector):
            state.put_and_observe(
                record, group, f"flame.{attribute}.{name}", value,
                numeric_spec(category, unit, description=f"{attribute} {name}"),
            )

    transform_elements = [child for child in flame if child.tag.rsplit("}", 1)[-1] == "xform"]
    final_elements = [child for child in flame if child.tag.rsplit("}", 1)[-1] == "finalxform"]
    state.put_and_observe(
        record, group, "genome.transform_count", len(transform_elements),
        categorical_spec("genome structure", description="Number of base transforms", generator_setting="Transform count"),
    )
    state.put_and_observe(
        record, group, "genome.finalxform_present", bool(final_elements),
        boolean_spec("genome structure", description="Whether a final transform is present", generator_setting="Allow final transforms / final-transform chance"),
    )

    all_variations: set[str] = set()
    total_variations = 0
    post_count = 0
    for index, element in enumerate(transform_elements, 1):
        variations, count, has_post = parse_transform(state, record, group, element, index, False)
        all_variations.update(variations)
        total_variations += count
        post_count += int(has_post)
    for element in final_elements:
        variations, count, has_post = parse_transform(state, record, group, element, None, True)
        all_variations.update(variations)
        total_variations += count
        post_count += int(has_post)

    for key, value, description in (
        ("genome.post_transform_count", post_count, "Number of base/final transforms with a post transform"),
        ("genome.total_variation_count", total_variations, "Total selected variation instances"),
        ("genome.unique_variation_count", len(all_variations), "Distinct variation types used by the flame"),
    ):
        state.put_and_observe(record, group, key, value, numeric_spec("genome structure", "count", description=description))
    for variation in SUPPORTED_VARIATIONS:
        state.put_and_observe(
            record, group, f"genome.variation.{variation}.present", variation in all_variations,
            boolean_spec("flame variation occurrence", description=f"Whether the flame uses {variation}", generator_setting="Enabled variation types"),
        )

    parse_palette(state, record, group, next((child for child in flame if child.tag.rsplit("}", 1)[-1] == "palette"), None))
    return record


def load_config(path: Path, output_override: str | None) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as stream:
        config = json.load(stream)
    if not isinstance(config.get("groups"), list) or not config["groups"]:
        raise ValueError("Configuration must contain a non-empty groups array")
    base = path.parent.resolve()
    for group in config["groups"]:
        if not group.get("name") or not group.get("folders"):
            raise ValueError("Every group needs a name and at least one folder")
        group["folders"] = [str((base / folder).resolve()) if not Path(folder).is_absolute() else str(Path(folder).resolve()) for folder in group["folders"]]
    output = output_override or config.get("output_directory", "outputs/latest")
    config["output_directory"] = str((base / output).resolve()) if not Path(output).is_absolute() else str(Path(output).resolve())
    names = [group["name"] for group in config["groups"]]
    if len(names) != len(set(names)):
        raise ValueError("Group names must be unique")
    reference = config.get("reference_group")
    if reference and reference not in names:
        raise ValueError(f"Unknown reference_group {reference}")
    for group in config.get("comparison_groups", []):
        if group not in names:
            raise ValueError(f"Unknown comparison group {group}")
    return config


def group_includes(group: dict[str, Any], source_id: str, run_id: str) -> bool:
    include_runs = {str(item) for item in group.get("include_run_ids", [])}
    exclude_runs = {str(item) for item in group.get("exclude_run_ids", [])}
    if include_runs and run_id not in include_runs:
        return False
    if run_id in exclude_runs:
        return False
    include_regex = group.get("include_source_id_regex")
    exclude_regex = group.get("exclude_source_id_regex")
    if include_regex and re.search(include_regex, source_id) is None:
        return False
    if exclude_regex and re.search(exclude_regex, source_id) is not None:
        return False
    return True


def merge_parsed_state(target: AnalysisState, source: AnalysisState) -> None:
    for key, spec in source.specs.items():
        target.register(key, spec)
    for group, parameters in source.observations.items():
        for parameter, values in parameters.items():
            target.observations[group][parameter].extend(values)
    for group, totals in source.scope_totals.items():
        target.scope_totals[group].update(totals)
    target.warnings.extend(source.warnings)


def scan_inputs(state: AnalysisState, config: dict[str, Any]) -> None:
    for group in config["groups"]:
        name = group["name"]
        recursive = bool(group.get("recursive", False))
        records_before_group = len(state.records)
        parsed_source_ids: set[str] = set()
        for folder_text in group["folders"]:
            folder = Path(folder_text)
            if not folder.exists():
                state.inventory.append({"group": name, "folder": str(folder), "extension": "<missing-folder>", "file_count": 0})
                state.warnings.append(f"Group {name}: folder does not exist: {folder}")
                continue
            files = sorted((path for path in (folder.rglob("*") if recursive else folder.iterdir()) if path.is_file()), key=lambda item: natural_key(str(item)))
            extension_counts = Counter(path.suffix.lower() or "<no-extension>" for path in files)
            if extension_counts:
                for extension, count in sorted(extension_counts.items()):
                    state.inventory.append({"group": name, "folder": str(folder.resolve()), "extension": extension, "file_count": count})
            else:
                state.inventory.append({"group": name, "folder": str(folder.resolve()), "extension": "<empty-folder>", "file_count": 0})
            if not group.get("analyze", True):
                continue
            flame_files = [path for path in files if path.suffix.lower() == ".flame"]
            for position, path in enumerate(flame_files, 1):
                source_id = stable_source_id(path)
                run_id, sequence, filename_seed = source_tokens(source_id)
                manifest = {
                    "group": name,
                    "source_id": source_id,
                    "run_id": run_id,
                    "sequence": sequence,
                    "seed_from_filename": filename_seed,
                    "source_path": str(path.resolve()),
                    "status": "filtered_out",
                    "error": "",
                }
                if not group_includes(group, source_id, run_id):
                    state.manifest.append(manifest)
                    continue
                if source_id in parsed_source_ids:
                    manifest["status"] = "duplicate_skipped"
                    manifest["error"] = "The stable source ID was already parsed in this group."
                    state.manifest.append(manifest)
                    state.warnings.append(f"Group {name}: duplicate source ID skipped: {source_id}")
                    continue
                try:
                    file_state = AnalysisState()
                    record = parse_flame_file(file_state, name, path)
                    merge_parsed_state(state, file_state)
                    state.records.append(record)
                    parsed_source_ids.add(source_id)
                    manifest["status"] = "parsed"
                except (ET.ParseError, OSError, ValueError) as error:
                    manifest["status"] = "error"
                    manifest["error"] = str(error)
                    state.warnings.append(f"{path}: {error}")
                state.manifest.append(manifest)
                if position % 1000 == 0:
                    print(f"[{name}] inspected {position:,}/{len(flame_files):,} .flame files", file=sys.stderr)
        expected_count = group.get("expected_flame_count")
        parsed_count = len(state.records) - records_before_group
        if expected_count is not None and parsed_count != int(expected_count):
            message = f"Group {name}: expected {int(expected_count):,} parsed flames, found {parsed_count:,}."
            if config.get("enforce_expected_counts", False):
                raise ValueError(message)
            state.warnings.append(message)


def quantile(values: Sequence[float], probability: float) -> float:
    if not values:
        return math.nan
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def normalized_entropy(probabilities: Iterable[float]) -> float:
    values = list(probabilities)
    if len(values) <= 1:
        return 0.0
    entropy = -sum(value * math.log2(value) for value in values if value > 0)
    return entropy / math.log2(len(values))


def circular_statistics_degrees(values: Sequence[float]) -> tuple[float, float, float]:
    """Return circular mean, mean resultant length, and circular variance."""
    if not values:
        return math.nan, math.nan, math.nan
    radians = [math.radians(value) for value in values]
    mean_sine = statistics.fmean(math.sin(value) for value in radians)
    mean_cosine = statistics.fmean(math.cos(value) for value in radians)
    resultant_length = math.hypot(mean_sine, mean_cosine)
    mean_degrees = math.degrees(math.atan2(mean_sine, mean_cosine))
    return mean_degrees, resultant_length, 1 - resultant_length


def histogram_edges(values: Sequence[float], minimum_bins: int, maximum_bins: int) -> list[float]:
    finite = [value for value in values if math.isfinite(value)]
    if not finite:
        return []
    lower, upper = min(finite), max(finite)
    if lower == upper:
        padding = max(0.5, abs(lower) * 0.01)
        return [lower - padding, upper + padding]
    q1, q3 = quantile(finite, 0.25), quantile(finite, 0.75)
    iqr = q3 - q1
    width = 2 * iqr / (len(finite) ** (1 / 3)) if iqr > 0 else 0
    bins = math.ceil((upper - lower) / width) if width > 0 else math.ceil(math.sqrt(len(finite)))
    bins = max(minimum_bins, min(maximum_bins, bins))
    step = (upper - lower) / bins
    return [lower + step * index for index in range(bins)] + [upper]


def histogram_counts(values: Sequence[float], edges: Sequence[float]) -> list[int]:
    if len(edges) < 2:
        return []
    counts = [0] * (len(edges) - 1)
    lower, upper = edges[0], edges[-1]
    span = upper - lower
    for value in values:
        if value < lower or value > upper or not math.isfinite(value):
            continue
        index = len(counts) - 1 if value == upper else int((value - lower) / span * len(counts))
        counts[max(0, min(len(counts) - 1, index))] += 1
    return counts


def categorical_value(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def jensen_shannon(left: Sequence[float], right: Sequence[float]) -> float:
    midpoint = [(a + b) / 2 for a, b in zip(left, right)]
    def divergence(values: Sequence[float]) -> float:
        return sum(value * math.log2(value / middle) for value, middle in zip(values, midpoint) if value > 0 and middle > 0)
    return 0.5 * divergence(left) + 0.5 * divergence(right)


def ks_two_sample(left: Sequence[float], right: Sequence[float]) -> tuple[float, float]:
    if not left or not right:
        return math.nan, math.nan
    a, b = sorted(left), sorted(right)
    i = j = 0
    distance = 0.0
    while i < len(a) or j < len(b):
        if j >= len(b) or (i < len(a) and a[i] <= b[j]):
            value = a[i]
        else:
            value = b[j]
        while i < len(a) and a[i] <= value:
            i += 1
        while j < len(b) and b[j] <= value:
            j += 1
        distance = max(distance, abs(i / len(a) - j / len(b)))
    effective = len(a) * len(b) / (len(a) + len(b))
    p_value = kolmogorov_pvalue(distance, effective)
    return distance, p_value


def kolmogorov_pvalue(distance: float, effective_n: float) -> float:
    if not math.isfinite(distance) or effective_n <= 0:
        return math.nan
    root = math.sqrt(effective_n)
    lam = (root + 0.12 + 0.11 / root) * distance
    total = 0.0
    for k in range(1, 101):
        term = 2 * ((-1) ** (k - 1)) * math.exp(-2 * k * k * lam * lam)
        total += term
        if abs(term) < 1e-12:
            break
    return min(1.0, max(0.0, total))


def regularized_gamma_q(a: float, x: float) -> float:
    if x < 0 or a <= 0:
        return math.nan
    if x == 0:
        return 1.0
    epsilon = 3e-14
    tiny = 1e-300
    gln = math.lgamma(a)
    if x < a + 1:
        ap = a
        term = total = 1 / a
        for _ in range(1000):
            ap += 1
            term *= x / ap
            total += term
            if abs(term) < abs(total) * epsilon:
                break
        p_value = total * math.exp(-x + a * math.log(x) - gln)
        return max(0.0, min(1.0, 1 - p_value))
    b = x + 1 - a
    c = 1 / tiny
    d = 1 / b
    h = d
    for index in range(1, 1000):
        an = -index * (index - a)
        b += 2
        d = an * d + b
        if abs(d) < tiny:
            d = tiny
        c = b + an / c
        if abs(c) < tiny:
            c = tiny
        d = 1 / d
        delta = d * c
        h *= delta
        if abs(delta - 1) < epsilon:
            break
    return max(0.0, min(1.0, math.exp(-x + a * math.log(x) - gln) * h))


def chi_square_test(observed: Counter[str], expected: dict[str, float]) -> tuple[float, float, float]:
    total = sum(observed.values())
    if total == 0 or len(expected) < 2:
        return math.nan, math.nan, math.nan
    statistic = 0.0
    max_error = 0.0
    for category, probability in expected.items():
        expected_count = total * probability
        actual = observed.get(category, 0)
        if expected_count > 0:
            statistic += (actual - expected_count) ** 2 / expected_count
        elif actual > 0:
            statistic = math.inf
        max_error = max(max_error, abs(actual / total - probability))
    unexpected = sum(count for category, count in observed.items() if category not in expected)
    if unexpected:
        statistic = math.inf
        max_error = max(max_error, unexpected / total)
    p_value = 0.0 if math.isinf(statistic) else regularized_gamma_q((len(expected) - 1) / 2, statistic / 2)
    return statistic, p_value, max_error


def ks_uniform_test(values: Sequence[float], lower: float, upper: float) -> tuple[float, float, int]:
    if not values or upper <= lower:
        return math.nan, math.nan, 0
    normalized = sorted((value - lower) / (upper - lower) for value in values)
    outside = sum(value < 0 or value > 1 for value in normalized)
    clipped = [min(1.0, max(0.0, value)) for value in normalized]
    count = len(clipped)
    d_plus = max((index + 1) / count - value for index, value in enumerate(clipped))
    d_minus = max(value - index / count for index, value in enumerate(clipped))
    distance = max(d_plus, d_minus, outside / count)
    return distance, kolmogorov_pvalue(distance, count), outside


def make_distributions(
    state: AnalysisState, config: dict[str, Any]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    groups = [group["name"] for group in config["groups"]]
    histogram_config = config.get("histogram", {})
    minimum_bins = int(histogram_config.get("minimum_bins", 5))
    maximum_bins = int(histogram_config.get("maximum_bins", 40))
    summary_rows: list[dict[str, Any]] = []
    histogram_rows: list[dict[str, Any]] = []
    category_rows: list[dict[str, Any]] = []

    for parameter, spec in sorted(state.specs.items(), key=lambda item: natural_key(item[0])):
        if not spec.analyze:
            continue
        values_by_group = {group: state.observations[group].get(parameter, []) for group in groups}
        if not any(values_by_group.values()):
            continue
        if spec.kind in {"numeric", "circular"}:
            finite_by_group = {group: [float(value) for value in values if math.isfinite(float(value))] for group, values in values_by_group.items()}
            edge_values = [value for values in finite_by_group.values() for value in values]
            edges = histogram_edges(edge_values, minimum_bins, maximum_bins)
            for group, values in finite_by_group.items():
                total = len(values) if spec.conditional else state.scope_totals[group][spec.scope]
                observed = len(values)
                counts = histogram_counts(values, edges)
                probabilities = [count / observed for count in counts] if observed else [0.0] * len(counts)
                q05 = quantile(values, 0.05) if values else math.nan
                q25 = quantile(values, 0.25) if values else math.nan
                median = quantile(values, 0.5) if values else math.nan
                q75 = quantile(values, 0.75) if values else math.nan
                q95 = quantile(values, 0.95) if values else math.nan
                entropy = normalized_entropy(probabilities)
                circular_mean, resultant_length, circular_variance = (
                    circular_statistics_degrees(values)
                    if spec.kind == "circular"
                    else (math.nan, math.nan, math.nan)
                )
                summary_rows.append({
                    "parameter": parameter, "category": spec.category, "kind": spec.kind, "unit": spec.unit,
                    "scope": spec.scope, "group": group, "n_total_units": total, "n_observed": observed,
                    "n_missing": max(0, total - observed), "presence_rate": observed / total if total else math.nan,
                    "unique_values": len(set(values)), "mean": statistics.fmean(values) if values else math.nan,
                    "sample_std": statistics.stdev(values) if len(values) > 1 else (0.0 if values else math.nan),
                    "min": min(values) if values else math.nan, "q05": q05, "q25": q25, "median": median,
                    "q75": q75, "q95": q95, "max": max(values) if values else math.nan,
                    "iqr": q75 - q25 if values else math.nan, "mode": "", "mode_probability": math.nan,
                    "circular_mean_degrees": circular_mean, "mean_resultant_length": resultant_length,
                    "circular_variance": circular_variance,
                    "normalized_entropy": entropy, "concentration": 1 - entropy,
                })
                for index, count in enumerate(counts):
                    histogram_rows.append({
                        "parameter": parameter, "category": spec.category, "unit": spec.unit, "group": group,
                        "bin_index": index + 1, "lower": edges[index], "upper": edges[index + 1],
                        "count": count, "probability": probabilities[index],
                    })
        else:
            all_categories = sorted({categorical_value(value) for values in values_by_group.values() for value in values}, key=natural_key)
            for group, values in values_by_group.items():
                counts = Counter(categorical_value(value) for value in values)
                observed = len(values)
                total = observed if spec.conditional else state.scope_totals[group][spec.scope]
                probabilities = [counts.get(category, 0) / observed if observed else 0.0 for category in all_categories]
                mode, mode_count = (counts.most_common(1)[0] if counts else ("", 0))
                entropy = normalized_entropy(probabilities)
                summary_rows.append({
                    "parameter": parameter, "category": spec.category, "kind": spec.kind, "unit": spec.unit,
                    "scope": spec.scope, "group": group, "n_total_units": total, "n_observed": observed,
                    "n_missing": max(0, total - observed), "presence_rate": observed / total if total else math.nan,
                    "unique_values": len(counts), "mean": math.nan, "sample_std": math.nan, "min": math.nan,
                    "q05": math.nan, "q25": math.nan, "median": math.nan, "q75": math.nan, "q95": math.nan,
                    "max": math.nan, "iqr": math.nan, "mode": mode,
                    "mode_probability": mode_count / observed if observed else math.nan,
                    "circular_mean_degrees": math.nan, "mean_resultant_length": math.nan,
                    "circular_variance": math.nan,
                    "normalized_entropy": entropy, "concentration": 1 - entropy,
                })
                for category, probability in zip(all_categories, probabilities):
                    category_rows.append({
                        "parameter": parameter, "category": spec.category, "group": group,
                        "value": category, "count": counts.get(category, 0), "probability": probability,
                    })
    return summary_rows, histogram_rows, category_rows


def make_comparisons(
    state: AnalysisState,
    config: dict[str, Any],
    histogram_rows: list[dict[str, Any]],
    category_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    reference = config.get("reference_group")
    if not reference:
        return []
    targets = config.get("comparison_groups") or [group["name"] for group in config["groups"] if group["name"] != reference]
    comparison_settings = config.get("comparison", {})
    minimum_reference_n = int(comparison_settings.get("minimum_reference_observations", 100))
    minimum_target_n = int(comparison_settings.get("minimum_target_observations", 30))
    hist_lookup: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    category_lookup: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in histogram_rows:
        hist_lookup[(row["parameter"], row["group"])].append(row)
    for row in category_rows:
        category_lookup[(row["parameter"], row["group"])].append(row)
    rows: list[dict[str, Any]] = []
    for parameter, spec in sorted(state.specs.items(), key=lambda item: natural_key(item[0])):
        if not spec.analyze:
            continue
        reference_values = state.observations[reference].get(parameter, [])
        if not reference_values:
            continue
        for target in targets:
            target_values = state.observations[target].get(parameter, [])
            if not target_values:
                continue
            row: dict[str, Any] = {
                "parameter": parameter, "category": spec.category, "kind": spec.kind, "unit": spec.unit,
                "reference_group": reference, "target_group": target,
                "reference_n": len(reference_values), "target_n": len(target_values),
                "js_divergence_bits": math.nan, "total_variation_distance": math.nan,
                "ks_statistic": math.nan, "ks_p_value": math.nan, "standardized_mean_difference": math.nan,
                "std_ratio": math.nan, "iqr_ratio": math.nan, "entropy_change_reference_minus_target": math.nan,
                "reference_circular_variance": math.nan, "target_circular_variance": math.nan,
                "circular_variance_ratio": math.nan, "circular_mean_difference_degrees": math.nan,
                "concentration_score": math.nan, "rank_eligible": False, "eligibility_note": "",
            }
            if spec.kind in {"numeric", "circular"}:
                left = [float(value) for value in reference_values]
                right = [float(value) for value in target_values]
                left_std = statistics.stdev(left) if len(left) > 1 else 0.0
                right_std = statistics.stdev(right) if len(right) > 1 else 0.0
                pooled_denominator = math.sqrt((left_std ** 2 + right_std ** 2) / 2)
                left_iqr = quantile(left, 0.75) - quantile(left, 0.25)
                right_iqr = quantile(right, 0.75) - quantile(right, 0.25)
                ref_hist = sorted(hist_lookup.get((parameter, reference), []), key=lambda item: item["bin_index"])
                target_hist = sorted(hist_lookup.get((parameter, target), []), key=lambda item: item["bin_index"])
                left_prob = [item["probability"] for item in ref_hist]
                right_prob = [item["probability"] for item in target_hist]
                ks_stat, ks_p = ks_two_sample(left, right)
                left_entropy = normalized_entropy(left_prob)
                right_entropy = normalized_entropy(right_prob)
                numeric_update = {
                    "js_divergence_bits": jensen_shannon(left_prob, right_prob) if left_prob and len(left_prob) == len(right_prob) else math.nan,
                    "entropy_change_reference_minus_target": left_entropy - right_entropy,
                }
                if spec.kind == "circular":
                    left_mean, _left_resultant, left_variance = circular_statistics_degrees(left)
                    right_mean, _right_resultant, right_variance = circular_statistics_degrees(right)
                    variance_ratio = right_variance / left_variance if left_variance else math.nan
                    numeric_update.update({
                        "reference_circular_variance": left_variance,
                        "target_circular_variance": right_variance,
                        "circular_variance_ratio": variance_ratio,
                        "circular_mean_difference_degrees": ((right_mean - left_mean + 180) % 360) - 180,
                        "concentration_score": 1 - min(1.0, variance_ratio) if math.isfinite(variance_ratio) else math.nan,
                    })
                else:
                    numeric_update.update({
                        "ks_statistic": ks_stat, "ks_p_value": ks_p,
                        "standardized_mean_difference": (statistics.fmean(right) - statistics.fmean(left)) / pooled_denominator if pooled_denominator else 0.0,
                        "std_ratio": right_std / left_std if left_std else math.nan,
                        "iqr_ratio": right_iqr / left_iqr if left_iqr else math.nan,
                        "concentration_score": 1 - min(1.0, right_iqr / left_iqr) if left_iqr else math.nan,
                    })
                row.update(numeric_update)
            else:
                ref_rows = category_lookup.get((parameter, reference), [])
                target_rows = category_lookup.get((parameter, target), [])
                categories = sorted({item["value"] for item in ref_rows + target_rows}, key=natural_key)
                ref_map = {item["value"]: item["probability"] for item in ref_rows}
                target_map = {item["value"]: item["probability"] for item in target_rows}
                left_prob = [ref_map.get(category, 0.0) for category in categories]
                right_prob = [target_map.get(category, 0.0) for category in categories]
                left_entropy = normalized_entropy(left_prob)
                right_entropy = normalized_entropy(right_prob)
                row.update({
                    "js_divergence_bits": jensen_shannon(left_prob, right_prob),
                    "total_variation_distance": 0.5 * sum(abs(a - b) for a, b in zip(left_prob, right_prob)),
                    "entropy_change_reference_minus_target": left_entropy - right_entropy,
                    "concentration_score": 1 - min(1.0, right_entropy / left_entropy) if left_entropy else math.nan,
                })
            row["rank_eligible"] = len(reference_values) >= minimum_reference_n and len(target_values) >= minimum_target_n
            if not row["rank_eligible"]:
                row["eligibility_note"] = (
                    f"Headline ranking requires reference_n >= {minimum_reference_n} and "
                    f"target_n >= {minimum_target_n}."
                )
            rows.append(row)
    return rows


def expected_symmetry_distribution(settings: dict[str, Any]) -> dict[str, float]:
    allowed = [str(item).lower() for item in settings.get("symmetry_types", [])]
    chance = float(settings.get("symmetry_chance", 0.4)) if allowed else 0.0
    distribution: defaultdict[str, float] = defaultdict(float)
    distribution["1"] += 1 - chance
    if not allowed:
        return dict(distribution)
    type_probability = chance / len(allowed)
    orders = [int(item) for item in settings.get("symmetry_orders", [2, 3])]
    for symmetry_type in allowed:
        if symmetry_type == "reflection":
            distribution["-1"] += type_probability
        elif symmetry_type == "dihedral":
            for order in orders:
                distribution[str(-order)] += type_probability / len(orders)
        elif symmetry_type == "rotational":
            for order in orders:
                distribution[str(order)] += type_probability / len(orders)
    return dict(distribution)


def make_uniformity_tests(state: AnalysisState, config: dict[str, Any]) -> list[dict[str, Any]]:
    reference = config.get("reference_group")
    settings = config.get("expected_generator")
    if not reference or not settings:
        return []
    expected: list[tuple[str, str, Any, str]] = []
    minimum_transform = int(settings["minimum_transform_count"])
    maximum_transform = int(settings["maximum_transform_count"])
    expected.append(("genome.transform_count", "categorical", {str(value): 1 / (maximum_transform - minimum_transform + 1) for value in range(minimum_transform, maximum_transform + 1)}, "Discrete uniform transform count"))
    expected.append(("flame.symmetry", "categorical", expected_symmetry_distribution(settings), "Configured symmetry mixture"))
    for key, minimum_key, maximum_key, label in (
        ("xform[*].derived.rotation_degrees", "minimum_affine_rotation_degrees", "maximum_affine_rotation_degrees", "Uniform affine rotation"),
        ("xform[*].derived.scale", "minimum_affine_scale", "maximum_affine_scale", "Uniform affine scale"),
        ("xform[*].derived.shear", "minimum_affine_shear", "maximum_affine_shear", "Uniform affine shear"),
    ):
        expected.append((key, "uniform", (float(settings[minimum_key]), float(settings[maximum_key])), label))
    translation = float(settings["translation_extent"])
    for axis in ("x", "y"):
        expected.append((f"xform[*].derived.translation_{axis}", "uniform", (-translation, translation), "Uniform affine translation"))
    balance = float(settings["transform_selection_balance"])
    if balance == 0:
        expected.append(("xform[*].weight", "constant", 1.0, "Equal transform weight"))
    else:
        expected.append(("xform[*].weight", "uniform", (max(0.01, 1 - balance), 1 + balance), "Uniform transform-selection weight"))
    minimum_variation = int(settings["minimum_variation_count"])
    maximum_variation = int(settings["maximum_variation_count"])
    expected.append(("xform[*].variation_count", "categorical", {str(value): 1 / (maximum_variation - minimum_variation + 1) for value in range(minimum_variation, maximum_variation + 1)}, "Discrete uniform variation count"))
    enabled = [str(item).lower() for item in settings.get("enabled_variations", SUPPORTED_VARIATIONS)]
    expected.append(("xform[*].variation.name", "categorical", {value: 1 / len(enabled) for value in enabled}, "Equal marginal variation-choice probability"))
    post_chance = float(settings["post_transform_chance"])
    expected.append(("xform[*].post.present", "categorical", {"false": 1 - post_chance, "true": post_chance}, "Configured post-transform Bernoulli probability"))
    for key, minimum_key, maximum_key, label in (
        ("xform[*].post.derived.rotation_degrees", "minimum_post_rotation_degrees", "maximum_post_rotation_degrees", "Uniform post rotation conditional on presence"),
        ("xform[*].post.derived.scale", "minimum_post_scale", "maximum_post_scale", "Uniform post scale conditional on presence"),
    ):
        expected.append((key, "uniform", (float(settings[minimum_key]), float(settings[maximum_key])), label))
    post_translation = float(settings["post_translation_extent"])
    for axis in ("x", "y"):
        expected.append((f"xform[*].post.derived.translation_{axis}", "uniform", (-post_translation, post_translation), "Uniform post translation conditional on presence"))
    final_probability = float(settings["final_transform_chance"]) if settings.get("allow_final_transforms", False) else 0.0
    expected.append(("genome.finalxform_present", "categorical", {"false": 1 - final_probability, "true": final_probability}, "Configured final-transform probability"))

    rows: list[dict[str, Any]] = []
    for parameter, distribution, definition, note in expected:
        values = state.observations[reference].get(parameter, [])
        if not values:
            continue
        row = {
            "parameter": parameter, "reference_group": reference, "n": len(values),
            "expected_distribution": distribution, "expected_definition": json.dumps(definition, sort_keys=True),
            "test": "", "statistic": math.nan, "p_value": math.nan, "effect_size": math.nan,
            "outside_expected_range": 0, "statistical_flag_p_lt_0_01": False,
            "practical_flag_effect_gt_0_02": False, "notes": note,
        }
        if distribution == "uniform":
            lower, upper = definition
            statistic, p_value, outside = ks_uniform_test([float(value) for value in values], lower, upper)
            row.update({"test": "one-sample Kolmogorov-Smirnov", "statistic": statistic, "p_value": p_value, "effect_size": statistic, "outside_expected_range": outside})
        elif distribution == "categorical":
            observed = Counter(categorical_value(value) for value in values)
            statistic, p_value, max_error = chi_square_test(observed, definition)
            row.update({"test": "chi-square goodness-of-fit", "statistic": statistic, "p_value": p_value, "effect_size": max_error})
        else:
            target = float(definition)
            max_error = max(abs(float(value) - target) for value in values)
            row.update({"test": "constant-value check", "statistic": max_error, "p_value": 1.0 if max_error <= 1e-12 else 0.0, "effect_size": max_error})
        row["statistical_flag_p_lt_0_01"] = bool(math.isfinite(row["p_value"]) and row["p_value"] < 0.01)
        row["practical_flag_effect_gt_0_02"] = bool(math.isfinite(row["effect_size"]) and row["effect_size"] > 0.02)
        rows.append(row)
    return rows


def csv_value(value: Any) -> Any:
    if isinstance(value, float):
        if math.isnan(value):
            return ""
        if math.isinf(value):
            return "inf" if value > 0 else "-inf"
        return format(value, ".17g")
    if isinstance(value, bool):
        return "true" if value else "false"
    return value


def write_dict_csv(path: Path, rows: Sequence[dict[str, Any]], fieldnames: Sequence[str] | None = None) -> None:
    if fieldnames is None:
        fieldnames = list(rows[0].keys()) if rows else []
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow({key: csv_value(row.get(key, "")) for key in fieldnames})


def write_matrices(output: Path, state: AnalysisState, write_transposed: bool) -> tuple[int, int]:
    parameter_columns = sorted(
        {key for record in state.records for key in record if key not in MATRIX_METADATA_COLUMNS},
        key=natural_key,
    )
    columns = list(MATRIX_METADATA_COLUMNS) + parameter_columns
    matrix_path = output / "flame_parameter_matrix.csv"
    with matrix_path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(columns)
        for record in state.records:
            writer.writerow([csv_value(record.get(column, "")) for column in columns])
    if write_transposed:
        transpose_path = output / "flame_parameter_matrix_transposed.csv"
        identifiers = [f"{record['group']}::{record['source_id']}" for record in state.records]
        with transpose_path.open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(["parameter"] + identifiers)
            for parameter in parameter_columns:
                writer.writerow([parameter] + [csv_value(record.get(parameter, "")) for record in state.records])
    return len(state.records), len(parameter_columns)


def finite_or_none(value: Any) -> Any:
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def write_distribution_profiles(
    output: Path,
    config: dict[str, Any],
    state: AnalysisState,
    summary_rows: list[dict[str, Any]],
    histogram_rows: list[dict[str, Any]],
    category_rows: list[dict[str, Any]],
) -> None:
    """Write a sampling-oriented JSON representation of every marginal."""
    summaries = {(row["group"], row["parameter"]): row for row in summary_rows}
    histograms: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    categories: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in histogram_rows:
        histograms[(row["group"], row["parameter"])].append(row)
    for row in category_rows:
        categories[(row["group"], row["parameter"])].append(row)

    groups: dict[str, dict[str, Any]] = {}
    for group in [item["name"] for item in config["groups"]]:
        parameters: dict[str, Any] = {}
        for parameter, spec in sorted(state.specs.items(), key=lambda item: natural_key(item[0])):
            summary = summaries.get((group, parameter))
            if not spec.analyze or summary is None:
                continue
            profile: dict[str, Any] = {
                "kind": spec.kind,
                "category": spec.category,
                "unit": spec.unit,
                "scope": spec.scope,
                "conditional": spec.conditional,
                "generator_setting": spec.generator_setting,
                "n_total_units": summary["n_total_units"],
                "n_observed": summary["n_observed"],
                "presence_rate": finite_or_none(summary["presence_rate"]),
                "summary": {
                    key: finite_or_none(summary[key])
                    for key in (
                        "mean", "sample_std", "min", "q05", "q25", "median", "q75", "q95", "max",
                        "iqr", "mode", "mode_probability", "circular_mean_degrees",
                        "mean_resultant_length", "circular_variance", "normalized_entropy", "concentration",
                    )
                },
            }
            if spec.kind in {"numeric", "circular"}:
                profile["histogram"] = [
                    {
                        "lower": row["lower"],
                        "upper": row["upper"],
                        "count": row["count"],
                        "probability": row["probability"],
                    }
                    for row in sorted(histograms[(group, parameter)], key=lambda item: item["bin_index"])
                ]
            else:
                profile["probabilities"] = [
                    {"value": row["value"], "count": row["count"], "probability": row["probability"]}
                    for row in sorted(categories[(group, parameter)], key=lambda item: natural_key(item["value"]))
                ]
            parameters[parameter] = profile
        groups[group] = {"parameters": parameters}

    document = {
        "tool_version": TOOL_VERSION,
        "reference_group": config.get("reference_group"),
        "groups": groups,
        "guardrail": (
            "These are univariate empirical marginals. A future generator must preserve joint structure, "
            "validity constraints, and an explicit broad-prior exploration component."
        ),
    }
    (output / "distribution_profiles.json").write_text(
        json.dumps(document, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8"
    )


def format_number(value: Any, digits: int = 4) -> str:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return "—"
    if not math.isfinite(number):
        return "—"
    if number != 0 and (abs(number) >= 10000 or abs(number) < 0.001):
        return f"{number:.3e}"
    return f"{number:.{digits}f}".rstrip("0").rstrip(".")


def svg_numeric(parameter: str, rows: list[dict[str, Any]], groups: list[str]) -> str:
    if not rows:
        return ""
    rows_by_group: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        rows_by_group[row["group"]].append(row)
    ordered_groups = [group for group in groups if rows_by_group.get(group)]
    bins = sorted(rows_by_group[ordered_groups[0]], key=lambda item: item["bin_index"])
    width, height = 780, 230
    left, right, top, bottom = 58, 18, 22, 48
    plot_width, plot_height = width - left - right, height - top - bottom
    maximum = max((row["probability"] for row in rows), default=1.0) or 1.0
    group_width = plot_width / max(1, len(bins))
    bar_width = max(1.0, group_width / max(1, len(ordered_groups)) * 0.82)
    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="Histogram for {html.escape(parameter)}">', f'<line x1="{left}" y1="{top + plot_height}" x2="{left + plot_width}" y2="{top + plot_height}" stroke="#334155"/>', f'<line x1="{left}" y1="{top}" x2="{left}" y2="{top + plot_height}" stroke="#334155"/>']
    for tick in range(5):
        probability = maximum * tick / 4
        y = top + plot_height - plot_height * tick / 4
        parts.append(f'<line x1="{left}" y1="{y:.1f}" x2="{left + plot_width}" y2="{y:.1f}" stroke="#E2E8F0"/>')
        parts.append(f'<text x="{left - 7}" y="{y + 4:.1f}" text-anchor="end">{probability:.2f}</text>')
    for group_index, group in enumerate(ordered_groups):
        color = GROUP_COLORS[group_index % len(GROUP_COLORS)]
        for bin_index, row in enumerate(sorted(rows_by_group[group], key=lambda item: item["bin_index"])):
            probability = row["probability"]
            bar_height = plot_height * probability / maximum
            x = left + bin_index * group_width + group_index * bar_width + group_width * 0.08
            y = top + plot_height - bar_height
            parts.append(f'<rect x="{x:.2f}" y="{y:.2f}" width="{bar_width:.2f}" height="{bar_height:.2f}" fill="{color}"><title>{html.escape(group)}: {probability:.4f} [{row["lower"]:.5g}, {row["upper"]:.5g}]</title></rect>')
    for position, index in ((left, 0), (left + plot_width / 2, len(bins) // 2), (left + plot_width, len(bins) - 1)):
        value = bins[index]["lower"] if index < len(bins) - 1 else bins[index]["upper"]
        anchor = "start" if position == left else ("end" if position == left + plot_width else "middle")
        parts.append(f'<text x="{position:.1f}" y="{height - 20}" text-anchor="{anchor}">{value:.5g}</text>')
    parts.append(f'<text x="{left + plot_width / 2:.1f}" y="{height - 3}" text-anchor="middle">Parameter value</text>')
    parts.append(f'<text transform="translate(14 {top + plot_height / 2:.1f}) rotate(-90)" text-anchor="middle">Probability</text>')
    parts.append("</svg>")
    return "".join(parts)


def svg_categorical(parameter: str, rows: list[dict[str, Any]], groups: list[str]) -> str:
    if not rows:
        return ""
    categories = sorted({row["value"] for row in rows}, key=natural_key)
    rows_map = {(row["group"], row["value"]): row for row in rows}
    ordered_groups = [group for group in groups if any((group, category) in rows_map for category in categories)]
    width = 780
    row_height = max(24, 16 * len(ordered_groups) + 8)
    height = 45 + row_height * len(categories)
    left, right, top = 190, 25, 20
    plot_width = width - left - right
    maximum = max((row["probability"] for row in rows), default=1.0) or 1.0
    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="Category probabilities for {html.escape(parameter)}">']
    for index, category in enumerate(categories):
        y0 = top + index * row_height
        parts.append(f'<text x="{left - 8}" y="{y0 + row_height / 2 + 4:.1f}" text-anchor="end">{html.escape(category)}</text>')
        for group_index, group in enumerate(ordered_groups):
            row = rows_map.get((group, category))
            probability = row["probability"] if row else 0.0
            bar_height = max(4, (row_height - 6) / max(1, len(ordered_groups)))
            y = y0 + 3 + group_index * bar_height
            bar_width = plot_width * probability / maximum
            color = GROUP_COLORS[group_index % len(GROUP_COLORS)]
            parts.append(f'<rect x="{left}" y="{y:.2f}" width="{bar_width:.2f}" height="{bar_height - 1:.2f}" fill="{color}"><title>{html.escape(group)}: {probability:.4f}</title></rect>')
    parts.append(f'<text x="{left + plot_width / 2:.1f}" y="{height - 5}" text-anchor="middle">Probability (maximum shown {maximum:.3f})</text>')
    parts.append("</svg>")
    return "".join(parts)


def render_report(
    output: Path,
    config: dict[str, Any],
    state: AnalysisState,
    summary_rows: list[dict[str, Any]],
    histogram_rows: list[dict[str, Any]],
    category_rows: list[dict[str, Any]],
    comparison_rows: list[dict[str, Any]],
    uniformity_rows: list[dict[str, Any]],
) -> None:
    groups = [group["name"] for group in config["groups"]]
    parsed_counts = Counter(record["group"] for record in state.records)
    hist_lookup: dict[str, list[dict[str, Any]]] = defaultdict(list)
    category_lookup: dict[str, list[dict[str, Any]]] = defaultdict(list)
    summary_lookup: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in histogram_rows:
        hist_lookup[row["parameter"]].append(row)
    for row in category_rows:
        category_lookup[row["parameter"]].append(row)
    for row in summary_rows:
        summary_lookup[row["parameter"]].append(row)

    concentration_rows = sorted(
        [
            row for row in comparison_rows
            if row.get("rank_eligible") and math.isfinite(float(row.get("concentration_score", math.nan)))
        ],
        key=lambda row: float(row["concentration_score"]), reverse=True,
    )[:30]
    excluded_concentration_count = sum(
        1
        for row in comparison_rows
        if not row.get("rank_eligible") and math.isfinite(float(row.get("concentration_score", math.nan)))
    )
    parameter_sections: list[str] = []
    for parameter in sorted(summary_lookup, key=natural_key):
        spec = state.specs[parameter]
        chart = svg_numeric(parameter, hist_lookup[parameter], groups) if spec.kind in {"numeric", "circular"} else svg_categorical(parameter, category_lookup[parameter], groups)
        summary_table_rows = []
        for row in summary_lookup[parameter]:
            if spec.kind == "circular":
                center = (
                    format_number(row["circular_mean_degrees"])
                    if row["mean_resultant_length"] >= 0.05
                    else "— (near-uniform)"
                )
                spread = format_number(row["circular_variance"])
            elif spec.kind == "numeric":
                center = format_number(row["median"])
                spread = format_number(row["iqr"])
            else:
                center = html.escape(str(row["mode"] or "—"))
                spread = format_number(row["mode_probability"])
            summary_table_rows.append(f'<tr><td>{html.escape(row["group"])}</td><td>{row["n_observed"]:,}</td><td>{center}</td><td>{spread}</td><td>{format_number(row["normalized_entropy"])}</td></tr>')
        search_text = f"{parameter} {spec.category} {spec.generator_setting} {spec.description}".lower()
        parameter_sections.append(
            f'<details class="parameter" data-search="{html.escape(search_text)}"><summary><span>{html.escape(parameter)}</span><small>{html.escape(spec.category)} · {html.escape(spec.generator_setting or "observed parameter")}</small></summary>'
            f'<p>{html.escape(spec.description or "No additional description.")}</p>{chart}'
            '<table><thead><tr><th>Group</th><th>n</th><th>Center</th><th>Spread / mode probability</th><th>Normalized entropy</th></tr></thead><tbody>'
            + "".join(summary_table_rows) + '</tbody></table></details>'
        )

    inventory_rows = "".join(
        f'<tr><td>{html.escape(row["group"])}</td><td><code>{html.escape(row["folder"])}</code></td><td>{html.escape(row["extension"])}</td><td>{row["file_count"]:,}</td></tr>'
        for row in state.inventory
    )
    group_rows = "".join(
        f'<tr><td><span class="swatch" style="background:{GROUP_COLORS[index % len(GROUP_COLORS)]}"></span>{html.escape(group)}</td><td>{parsed_counts[group]:,}</td></tr>'
        for index, group in enumerate(groups)
    )
    comparison_table = "".join(
        f'<tr><td>{html.escape(row["parameter"])}</td><td>{html.escape(row["target_group"])}</td><td>{format_number(row["concentration_score"])}</td><td>{format_number(row["js_divergence_bits"])}</td><td>{format_number(row["circular_variance_ratio"] if row["kind"] == "circular" else row["iqr_ratio"])}</td><td>{format_number(row["entropy_change_reference_minus_target"])}</td></tr>'
        for row in concentration_rows
    ) or '<tr><td colspan="6">No comparison distributions were available.</td></tr>'
    uniformity_table = "".join(
        f'<tr class="{"flag" if row["practical_flag_effect_gt_0_02"] else ""}"><td>{html.escape(row["parameter"])}</td><td>{html.escape(row["test"])}</td><td>{row["n"]:,}</td><td>{format_number(row["statistic"])}</td><td>{format_number(row["p_value"])}</td><td>{format_number(row["effect_size"])}</td><td>{"yes" if row["practical_flag_effect_gt_0_02"] else "no"}</td></tr>'
        for row in uniformity_rows
    ) or '<tr><td colspan="7">No expected-generator configuration was supplied.</td></tr>'
    warnings_html = "".join(f'<li>{html.escape(warning)}</li>' for warning in state.warnings[:100]) or '<li>No parse warnings.</li>'
    title = html.escape(config.get("title", "Flame parameter-space report"))
    document = f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{title}</title><style>
:root{{--ink:#172033;--muted:#5B6475;--line:#D8DEE9;--paper:#F6F8FB;--panel:#FFFFFF;--accent:#2155A3;--warn:#9A3412}}
*{{box-sizing:border-box}}body{{margin:0;background:var(--paper);color:var(--ink);font:15px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}}
main{{max-width:1180px;margin:auto;padding:32px 24px 64px}}h1{{font-size:28px;margin:0 0 6px}}h2{{font-size:20px;margin:32px 0 12px}}p{{max-width:90ch}}.muted,small{{color:var(--muted)}}
.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:16px}}.panel{{background:var(--panel);border:1px solid var(--line);padding:16px;border-radius:8px}}
table{{width:100%;border-collapse:collapse;background:var(--panel)}}th,td{{padding:8px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}}th{{font-weight:600;background:#EEF2F7;position:sticky;top:0}}code{{font-size:12px;overflow-wrap:anywhere}}
.table-wrap{{overflow:auto;max-height:540px;border:1px solid var(--line);border-radius:8px}}.swatch{{display:inline-block;width:11px;height:11px;margin-right:7px;border-radius:2px}}.flag td{{background:#FFF7ED}}
input{{width:100%;padding:10px 12px;border:1px solid #AAB4C3;border-radius:6px;font:inherit;background:white}}details.parameter{{background:var(--panel);border:1px solid var(--line);border-radius:8px;margin:10px 0;padding:0 14px 14px}}
details.parameter summary{{cursor:pointer;padding:13px 0;display:flex;gap:12px;justify-content:space-between;font-weight:600}}details.parameter p{{color:var(--muted)}}svg{{display:block;width:100%;height:auto;max-width:900px;margin:8px 0 16px}}svg text{{font:11px system-ui,-apple-system,"Segoe UI",sans-serif;fill:#334155}}
ul{{padding-left:22px}}@media(max-width:650px){{main{{padding:20px 12px}}details.parameter summary{{display:block}}th,td{{padding:6px}}}}
</style></head><body><main>
<h1>{title}</h1><p class="muted">Tool version {TOOL_VERSION}. Source workspaces were read only. Histograms are normalized probabilities; concentration scores describe narrowing, not quality.</p>
<div class="grid"><section class="panel"><h2>Parsed cohorts</h2><table><thead><tr><th>Group</th><th>Parsed flames</th></tr></thead><tbody>{group_rows}</tbody></table></section>
<section class="panel"><h2>Interpretation guardrail</h2><p>Univariate concentration can identify narrowed marginals, but interacting flame parameters may still form several joint modes. Do not configure the generator from concentration alone without validating joint structure and human target-style hit rate.</p></section></div>
<h2>Folder inventory</h2><div class="table-wrap"><table><thead><tr><th>Group</th><th>Folder</th><th>Extension</th><th>Files</th></tr></thead><tbody>{inventory_rows}</tbody></table></div>
<h2>Largest measured concentrations</h2><p>Positive scores mean the target distribution is narrower than the reference on the selected marginal. Jensen–Shannon divergence measures distribution change. Numeric spread uses IQR; circular parameters use circular variance. This headline ranking omits {excluded_concentration_count:,} comparisons below the configured sample-size guardrail; all remain available in <code>concentration_comparisons.csv</code>.</p><div class="table-wrap"><table><thead><tr><th>Parameter</th><th>Target</th><th>Concentration</th><th>JS divergence</th><th>Spread ratio</th><th>Entropy change</th></tr></thead><tbody>{comparison_table}</tbody></table></div>
<h2>Reference-generator distribution checks</h2><p>At this sample size, tiny deviations can have small p-values. The practical flag uses an effect threshold of 0.02; inspect both effect size and the chart.</p><div class="table-wrap"><table><thead><tr><th>Parameter</th><th>Test</th><th>n</th><th>Statistic</th><th>p-value</th><th>Effect</th><th>Practical flag</th></tr></thead><tbody>{uniformity_table}</tbody></table></div>
<h2>Parameter distributions</h2><label for="filter">Filter parameters</label><input id="filter" type="search" placeholder="Search by parameter, category, or generator setting">
<div id="parameters">{''.join(parameter_sections)}</div>
<h2>Warnings and exclusions</h2><ul>{warnings_html}</ul>
</main><script>
const input=document.getElementById('filter');const sections=[...document.querySelectorAll('.parameter')];input.addEventListener('input',()=>{{const q=input.value.trim().toLowerCase();sections.forEach(s=>s.hidden=q&&!s.dataset.search.includes(q));}});
</script></body></html>'''
    (output / "report.html").write_text(document, encoding="utf-8")


def run_analysis(config_path: Path, output_override: str | None = None) -> dict[str, Any]:
    config = load_config(config_path.resolve(), output_override)
    output = Path(config["output_directory"])
    output.mkdir(parents=True, exist_ok=True)
    state = AnalysisState()
    scan_inputs(state, config)
    summary_rows, histogram_rows, category_rows = make_distributions(state, config)
    comparison_rows = make_comparisons(state, config, histogram_rows, category_rows)
    uniformity_rows = make_uniformity_tests(state, config)
    matrix_rows, matrix_parameters = write_matrices(output, state, bool(config.get("write_transposed_matrix", True)))
    write_distribution_profiles(output, config, state, summary_rows, histogram_rows, category_rows)

    catalog_rows = [
        {"parameter": key, **spec.__dict__}
        for key, spec in sorted(state.specs.items(), key=lambda item: natural_key(item[0]))
    ]
    write_dict_csv(output / "folder_inventory.csv", state.inventory, ("group", "folder", "extension", "file_count"))
    write_dict_csv(output / "flame_manifest.csv", state.manifest, ("group", "source_id", "run_id", "sequence", "seed_from_filename", "source_path", "status", "error"))
    write_dict_csv(output / "parameter_catalog.csv", catalog_rows)
    write_dict_csv(output / "parameter_summary.csv", summary_rows)
    write_dict_csv(output / "numeric_histograms.csv", histogram_rows)
    write_dict_csv(output / "categorical_probabilities.csv", category_rows)
    write_dict_csv(output / "concentration_comparisons.csv", comparison_rows)
    write_dict_csv(output / "uniformity_tests.csv", uniformity_rows)
    render_report(output, config, state, summary_rows, histogram_rows, category_rows, comparison_rows, uniformity_rows)

    duplicate_ids = Counter(record["source_id"] for record in state.records)
    run_summary = {
        "tool_version": TOOL_VERSION,
        "config_path": str(config_path.resolve()),
        "output_directory": str(output.resolve()),
        "parsed_flames": len(state.records),
        "parsed_by_group": dict(sorted(Counter(record["group"] for record in state.records).items())),
        "matrix_rows": matrix_rows,
        "matrix_parameter_columns": matrix_parameters,
        "analyzed_parameters": len({row["parameter"] for row in summary_rows}),
        "manifest_status": dict(sorted(Counter(row["status"] for row in state.manifest).items())),
        "duplicate_source_ids_across_groups": sum(1 for count in duplicate_ids.values() if count > 1),
        "warning_count": len(state.warnings),
        "warnings": state.warnings,
    }
    (output / "run_summary.json").write_text(json.dumps(run_summary, indent=2, ensure_ascii=False), encoding="utf-8")
    return run_summary


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path, help="Path to an analysis JSON configuration")
    parser.add_argument("--output", help="Override the configured output directory")
    args = parser.parse_args(argv)
    try:
        summary = run_analysis(args.config, args.output)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
