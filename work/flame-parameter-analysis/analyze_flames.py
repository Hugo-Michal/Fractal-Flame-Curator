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


TOOL_VERSION = "1.3.0"
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
GENERATOR_CONTROL_ORDER = (
    "Transform count", "Allowed symmetry", "Affine rotation", "Affine scale",
    "Affine shear", "Translation extent", "Transform balance",
    "Variations per transform", "Enabled variations", "Minimum variation share",
    "Post-transform chance", "Post rotation", "Post scale",
    "Post translation extent", "Allow final transforms / final-transform chance",
)


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
        self.variation_vectors: list[dict[str, Any]] = []

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


def parse_transform(
    state: AnalysisState,
    record: dict[str, Any],
    group: str,
    element: ET.Element,
    index: int | None,
    is_final: bool,
) -> tuple[int, bool]:
    prefix = "finalxform" if is_final else f"xform.{index:02d}"
    pooled = "finalxform[*]" if is_final else "xform[*]"
    scope = "finalxform" if is_final else "xform"
    category = "final transform" if is_final else "base transform"
    state.scope_totals[group][scope] += 1

    scalar_fields = (
        ("weight", "weight", numeric_spec(category, scope=scope, conditional=is_final, description="Transform selection weight", generator_setting="Transform balance")),
    )
    for attribute, name, spec in scalar_fields:
        value = parse_number(element.get(attribute))
        if value is not None:
            state.put(record, f"{prefix}.{name}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))
            state.observe(group, f"{pooled}.{name}", value, spec)

    coefficients = read_coefficients(element)
    if coefficients is None:
        raise ValueError(f"{prefix} has invalid affine coefficients")
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
    state.variation_vectors.append({
        "group": group,
        "source_id": record["source_id"],
        "transform_kind": "finalxform" if is_final else "xform",
        "transform_index": index or 0,
        "variation_count": variation_count,
        "variations": list(variations),
        "weights": list(variations.values()),
    })
    state.put(
        record, f"{prefix}.variation_count", variation_count,
        categorical_spec("variation", scope, is_final, generator_setting="Variations per transform", analyze=False),
    )
    state.observe(
        group, f"{pooled}.variation_count", variation_count,
        categorical_spec("variation", scope, is_final, "Number of variations on a transform", "Variations per transform"),
    )
    for attribute, raw_value in unknown_attributes.items():
        numeric_value = parse_number(raw_value)
        value: Any = numeric_value if numeric_value is not None else raw_value
        spec = numeric_spec("unrecognized XML attribute", scope=scope, conditional=is_final) if numeric_value is not None else categorical_spec("unrecognized XML attribute", scope, is_final)
        state.put(record, f"{prefix}.attribute.{attribute}", value, ParameterSpec(**{**spec.__dict__, "analyze": False}))

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

    return variation_count, has_post


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
    symmetry = flame.get("symmetry")
    if symmetry is not None:
        state.put_and_observe(
            record, group, "flame.symmetry", symmetry,
            categorical_spec("symmetry", description="Flame-level symmetry order", generator_setting="Allowed symmetry"),
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

    for index, element in enumerate(transform_elements, 1):
        parse_transform(state, record, group, element, index, False)
    for element in final_elements:
        parse_transform(state, record, group, element, None, True)
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
    profile_text = config.get("generator_profile_path")
    if profile_text:
        profile_path = Path(profile_text)
        if not profile_path.is_absolute():
            profile_path = (base / profile_path).resolve()
        if not profile_path.is_file():
            raise ValueError(f"Generator profile does not exist: {profile_path}")
        with profile_path.open("r", encoding="utf-8") as stream:
            profile = json.load(stream)
        profile_settings = profile.get("generator_settings")
        if not isinstance(profile_settings, dict):
            raise ValueError(f"Generator profile has no generator_settings object: {profile_path}")
        explicit_settings = config.get("expected_generator", {})
        for key in profile_settings.keys() & explicit_settings.keys():
            profile_value: Any = profile_settings[key]
            explicit_value: Any = explicit_settings[key]
            if key == "symmetry_types":
                profile_value = sorted(re.split(r"\s*[,|]\s*", str(profile_value).lower()))
                explicit_value = sorted(str(item).lower() for item in explicit_value) if isinstance(explicit_value, list) else sorted(re.split(r"\s*[,|]\s*", str(explicit_value).lower()))
            elif key == "enabled_variations":
                profile_value = sorted(str(item).lower() for item in profile_value)
                explicit_value = sorted(str(item).lower() for item in explicit_value)
            if profile_value != explicit_value:
                raise ValueError(f"expected_generator.{key} does not match the recorded generator profile")
        merged_settings = dict(profile_settings)
        merged_settings.update(explicit_settings)
        symmetry_types = merged_settings.get("symmetry_types")
        if isinstance(symmetry_types, str):
            merged_settings["symmetry_types"] = re.split(r"\s*[,|]\s*", symmetry_types.lower())
        config["expected_generator"] = merged_settings
        config["generator_profile_path"] = str(profile_path)
    names = [group["name"] for group in config["groups"]]
    if len(names) != len(set(names)):
        raise ValueError("Group names must be unique")
    reference = config.get("reference_group")
    if reference and reference not in names:
        raise ValueError(f"Unknown reference_group {reference}")
    report_groups = config.get("report_groups")
    if report_groups is not None:
        if not isinstance(report_groups, list) or not report_groups:
            raise ValueError("report_groups must be a non-empty list when supplied")
        unknown_report_groups = [group for group in report_groups if group not in names]
        if unknown_report_groups:
            raise ValueError(f"Unknown report group: {unknown_report_groups[0]}")
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
    target.variation_vectors.extend(source.variation_vectors)


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
    enabled_variations = [str(name).lower() for name in settings.get("enabled_variations", SUPPORTED_VARIATIONS)]
    variation_counts = Counter(
        variation
        for vector in state.variation_vectors
        if vector["group"] == reference and vector["transform_kind"] == "xform"
        for variation in vector["variations"]
    )
    if enabled_variations and variation_counts:
        expected_variations = {name: 1 / len(enabled_variations) for name in enabled_variations}
        statistic, p_value, max_error = chi_square_test(variation_counts, expected_variations)
        rows.append({
            "parameter": "variation occurrence", "reference_group": reference,
            "n": sum(variation_counts.values()), "expected_distribution": "categorical",
            "expected_definition": json.dumps(expected_variations, sort_keys=True),
            "test": "chi-square goodness-of-fit", "statistic": statistic,
            "p_value": p_value, "effect_size": max_error,
            "outside_expected_range": sum(count for name, count in variation_counts.items() if name not in expected_variations),
            "statistical_flag_p_lt_0_01": bool(math.isfinite(p_value) and p_value < 0.01),
            "practical_flag_effect_gt_0_02": bool(math.isfinite(max_error) and max_error > 0.02),
            "notes": "Equal occurrence share across enabled variation types; evaluated as one categorical selection distribution.",
        })
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


def write_variation_weight_vectors(output: Path, state: AnalysisState) -> None:
    fields = ["group", "source_id", "transform_kind", "transform_index", "variation_count"]
    for index in range(1, 6):
        fields.extend((f"variation_{index}", f"weight_{index}"))
    rows: list[dict[str, Any]] = []
    for vector in state.variation_vectors:
        row = {key: vector[key] for key in fields[:5]}
        for index, (variation, weight) in enumerate(zip(vector["variations"], vector["weights"]), 1):
            row[f"variation_{index}"] = variation
            row[f"weight_{index}"] = weight
        rows.append(row)
    write_dict_csv(output / "variation_weight_vectors.csv", rows, fields)


def make_variation_occurrence_rows(state: AnalysisState, config: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for group in [item["name"] for item in config["groups"]]:
        vectors = [
            vector for vector in state.variation_vectors
            if vector["group"] == group and vector["transform_kind"] == "xform"
        ]
        if not vectors:
            continue
        counts = Counter(
            variation
            for vector in vectors
            for variation in vector["variations"]
        )
        total_instances = sum(counts.values())
        for variation in SUPPORTED_VARIATIONS:
            count = counts[variation]
            rows.append({
                "group": group,
                "variation": variation,
                "base_transforms_with_variation": count,
                "total_base_transforms": len(vectors),
                "occurrence_rate_per_transform": count / len(vectors) if vectors else math.nan,
                "variation_instances": count,
                "total_variation_instances": total_instances,
                "share_of_variation_instances": count / total_instances if total_instances else math.nan,
            })
    reference = config.get("reference_group")
    reference_rates = {
        row["variation"]: row["occurrence_rate_per_transform"]
        for row in rows if row["group"] == reference
    }
    for row in rows:
        reference_rate = reference_rates.get(row["variation"], math.nan)
        row["reference_occurrence_rate"] = reference_rate
        row["difference_from_reference"] = (
            row["occurrence_rate_per_transform"] - reference_rate
            if math.isfinite(float(row["occurrence_rate_per_transform"])) and math.isfinite(float(reference_rate))
            else math.nan
        )
        row["ratio_to_reference"] = (
            row["occurrence_rate_per_transform"] / reference_rate
            if reference_rate and math.isfinite(float(row["occurrence_rate_per_transform"]))
            else math.nan
        )
    return rows


def make_variation_weight_by_name_rows(state: AnalysisState, config: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for group in [item["name"] for item in config["groups"]]:
        weights: defaultdict[str, list[float]] = defaultdict(list)
        for vector in state.variation_vectors:
            if vector["group"] != group or vector["transform_kind"] != "xform":
                continue
            for variation, weight in zip(vector["variations"], vector["weights"]):
                weights[variation].append(float(weight))
        if not weights:
            continue
        for variation in SUPPORTED_VARIATIONS:
            values = weights[variation]
            rows.append({
                "group": group,
                "variation": variation,
                "n": len(values),
                "mean_weight_when_present": statistics.fmean(values) if values else math.nan,
                "q05": quantile(values, 0.05) if values else math.nan,
                "median": quantile(values, 0.5) if values else math.nan,
                "q95": quantile(values, 0.95) if values else math.nan,
            })
    return rows


def summary_evidence(row: dict[str, Any], kind: str) -> str:
    if kind == "circular":
        if float(row["mean_resultant_length"]) < 0.05:
            return "direction broadly distributed"
        return f"center {format_number(row['circular_mean_degrees'])}°, circular spread {format_number(row['circular_variance'])}"
    if kind == "numeric":
        return f"median {format_number(row['median'])}; 90% interval {format_number(row['q05'])} to {format_number(row['q95'])}"
    return f"most common {row['mode']} ({format_number(100 * float(row['mode_probability']), 1)}%)"


def make_generator_control_findings(
    state: AnalysisState,
    config: dict[str, Any],
    summary_rows: list[dict[str, Any]],
    comparison_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    summaries = {(row["group"], row["parameter"]): row for row in summary_rows}
    minimum_target = max(30, int(config.get("comparison", {}).get("minimum_target_observations", 30)))
    rows: list[dict[str, Any]] = []
    for comparison in comparison_rows:
        parameter = comparison["parameter"]
        spec = state.specs[parameter]
        if not spec.generator_setting:
            continue
        reference = summaries.get((comparison["reference_group"], parameter))
        target = summaries.get((comparison["target_group"], parameter))
        if reference is None or target is None:
            continue
        target_n = int(comparison["target_n"])
        js = float(comparison["js_divergence_bits"])
        concentration = float(comparison["concentration_score"])
        if target_n < minimum_target:
            strength = "Insufficient sample"
            guidance = f"Collect at least {minimum_target} observations before changing this control."
        elif math.isfinite(concentration) and concentration >= 0.10 and math.isfinite(js) and js >= 0.02:
            strength = "Strong narrowing signal"
            if spec.kind == "numeric":
                guidance = (
                    f"Candidate uniform range: {format_number(target['q05'])} to {format_number(target['q95'])}. "
                    "Validate it in the next pass and retain a broad exploration reserve."
                )
            elif spec.kind == "circular":
                guidance = "The preferred cohort has directional concentration; inspect the chart before narrowing the angle range."
            else:
                guidance = f"Bias generation toward {target['mode']}; do not remove alternatives until the signal repeats in another pass."
        elif (math.isfinite(js) and js >= 0.02) or (math.isfinite(concentration) and concentration >= 0.05):
            strength = "Moderate shift"
            guidance = "Keep the current broad control for now and check whether this shift repeats in the next pass."
        else:
            strength = "No useful narrowing"
            guidance = "Keep the current generator setting broad."
        rows.append({
            "generator_setting": spec.generator_setting,
            "parameter": parameter,
            "measure": spec.description or parameter,
            "target_group": comparison["target_group"],
            "target_n": target_n,
            "reference_evidence": summary_evidence(reference, spec.kind),
            "target_evidence": summary_evidence(target, spec.kind),
            "signal": strength,
            "guidance": guidance,
            "js_divergence_bits": js,
            "concentration_score": concentration,
            "spread_ratio": comparison["circular_variance_ratio"] if spec.kind == "circular" else comparison["iqr_ratio"],
            "entropy_change_reference_minus_target": comparison["entropy_change_reference_minus_target"],
        })
    grouped: defaultdict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        grouped[(row["generator_setting"], row["target_group"])].append(row)
    aggregated: list[dict[str, Any]] = []
    signal_order = {"No useful narrowing": 0, "Moderate shift": 1, "Strong narrowing signal": 2}
    for (setting, target_group), items in grouped.items():
        target_n = min(int(item["target_n"]) for item in items)
        if all(item["signal"] == "Insufficient sample" for item in items):
            signal = "Insufficient sample"
            guidance = f"Collect at least {minimum_target} observations before changing this control."
        else:
            strongest = max(
                (item for item in items if item["signal"] != "Insufficient sample"),
                key=lambda item: signal_order[item["signal"]],
            )
            signal = strongest["signal"]
            guidance = strongest["guidance"]
            if signal == "Strong narrowing signal" and setting in {"Translation extent", "Post translation extent"}:
                extents = []
                for item in items:
                    target = summaries[(item["target_group"], item["parameter"])]
                    extents.extend((abs(float(target["q05"])), abs(float(target["q95"]))))
                guidance = (
                    f"Candidate symmetric extent: ±{format_number(max(extents))}. "
                    "Validate both axes in the next pass and retain a broad exploration reserve."
                )
        aggregated.append({
            "generator_setting": setting,
            "parameter": "; ".join(item["parameter"] for item in items),
            "measure": "; ".join(item["measure"] for item in items),
            "target_group": target_group,
            "target_n": target_n,
            "reference_evidence": " | ".join(f"{item['measure']}: {item['reference_evidence']}" for item in items),
            "target_evidence": " | ".join(f"{item['measure']}: {item['target_evidence']}" for item in items),
            "signal": signal,
            "guidance": guidance,
            "js_divergence_bits": max(float(item["js_divergence_bits"]) for item in items),
            "concentration_score": max(float(item["concentration_score"]) for item in items),
            "spread_ratio": min(float(item["spread_ratio"]) for item in items),
            "entropy_change_reference_minus_target": max(float(item["entropy_change_reference_minus_target"]) for item in items),
        })
    order = {name: index for index, name in enumerate(GENERATOR_CONTROL_ORDER)}
    return sorted(aggregated, key=lambda row: (order.get(row["generator_setting"], len(order)), natural_key(row["target_group"])))


def make_simplex_diagnostics(state: AnalysisState, config: dict[str, Any]) -> list[dict[str, Any]]:
    settings = config.get("expected_generator", {})
    minimum_share = float(settings.get("minimum_variation_share", 0.0))
    simplex_settings = config.get("simplex", {})
    two_bins = max(2, int(simplex_settings.get("two_variation_bins", 18)))
    simplex_grid = max(2, int(simplex_settings.get("simplex_grid_bins", 10)))
    rows: list[dict[str, Any]] = []
    groups = [group["name"] for group in config["groups"]]
    for group in groups:
        vectors = [
            vector for vector in state.variation_vectors
            if vector["group"] == group and vector["transform_kind"] == "xform"
        ]
        for variation_count in range(1, 6):
            selected = [vector for vector in vectors if vector["variation_count"] == variation_count]
            if not selected:
                continue
            weight_vectors = [[float(value) for value in vector["weights"]] for vector in selected]
            sum_error = max(abs(sum(values) - 1.0) for values in weight_vectors)
            floor_violations = sum(
                1 for values in weight_vectors
                if any(value < minimum_share - 1e-12 for value in values)
            )
            components = [value for values in weight_vectors for value in values]
            row: dict[str, Any] = {
                "group": group,
                "variation_count": variation_count,
                "n_vectors": len(weight_vectors),
                "minimum_share": minimum_share,
                "min_component": min(components),
                "max_component": max(components),
                "max_sum_error": sum_error,
                "floor_violations": floor_violations,
                "coverage_test": "",
                "statistic": math.nan,
                "p_value": math.nan,
                "occupied_cells": math.nan,
                "total_cells": math.nan,
                "occupancy_fraction": math.nan,
                "support_fraction": math.nan,
                "notes": "",
            }
            if variation_count == 1:
                row.update({
                    "coverage_test": "constant one-part mixture",
                    "occupied_cells": 1,
                    "total_cells": 1,
                    "occupancy_fraction": 1.0,
                    "support_fraction": 1.0 if abs(min(components) - 1.0) <= 1e-12 else 0.0,
                    "notes": "The only valid one-variation vector is [1].",
                })
            elif variation_count == 2:
                shares = [values[0] for values in weight_vectors]
                statistic, p_value, outside = ks_uniform_test(shares, minimum_share, 1 - minimum_share)
                occupied = {
                    min(two_bins - 1, max(0, int((value - minimum_share) / (1 - 2 * minimum_share) * two_bins)))
                    for value in shares
                } if minimum_share < 0.5 else set()
                row.update({
                    "coverage_test": "KS uniform first share",
                    "statistic": statistic,
                    "p_value": p_value,
                    "occupied_cells": len(occupied),
                    "total_cells": two_bins,
                    "occupancy_fraction": len(occupied) / two_bins,
                    "support_fraction": max(0.0, min(1.0, (max(shares) - min(shares)) / (1 - 2 * minimum_share))) if minimum_share < 0.5 else math.nan,
                    "notes": f"First component tested on [{minimum_share:g}, {1 - minimum_share:g}]; outside={outside}.",
                })
            else:
                scale = 1 - variation_count * minimum_share
                normalized = [
                    [(value - minimum_share) / scale for value in values]
                    for values in weight_vectors
                ] if scale > 0 else []
                if variation_count == 3 and normalized:
                    cells = set()
                    for values in normalized:
                        first = min(simplex_grid - 1, max(0, int(values[0] * simplex_grid)))
                        second = min(simplex_grid - 1, max(0, int(values[1] * simplex_grid)))
                        if first + second >= simplex_grid:
                            second = simplex_grid - 1 - first
                        cells.add((first, second))
                    total_cells = simplex_grid * (simplex_grid + 1) // 2
                    row.update({
                        "coverage_test": "triangular simplex occupancy",
                        "occupied_cells": len(cells),
                        "total_cells": total_cells,
                        "occupancy_fraction": len(cells) / total_cells,
                        "support_fraction": 1.0,
                        "notes": f"Barycentric grid {simplex_grid}x{simplex_grid}; inspect vectors for joint discrepancy.",
                    })
                else:
                    row.update({
                        "coverage_test": "simplex support invariant",
                        "support_fraction": 1.0,
                        "notes": "Retain complete vectors for higher-dimensional simplex analysis.",
                    })
            rows.append(row)
    return rows


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
        static = ' data-report-static="true"' if group == "all components" else ""
        parts.append(f'<g data-report-group="{html.escape(group, quote=True)}"{static}>')
        for bin_index, row in enumerate(sorted(rows_by_group[group], key=lambda item: item["bin_index"])):
            probability = row["probability"]
            bar_height = plot_height * probability / maximum
            x = left + bin_index * group_width + group_index * bar_width + group_width * 0.08
            y = top + plot_height - bar_height
            parts.append(f'<rect x="{x:.2f}" y="{y:.2f}" width="{bar_width:.2f}" height="{bar_height:.2f}" fill="{color}"><title>{html.escape(group)}: {probability:.4f} [{row["lower"]:.5g}, {row["upper"]:.5g}]</title></rect>')
        parts.append("</g>")
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
            parts.append(f'<g data-report-group="{html.escape(group, quote=True)}"><rect x="{left}" y="{y:.2f}" width="{bar_width:.2f}" height="{bar_height - 1:.2f}" fill="{color}"><title>{html.escape(group)}: {probability:.4f}</title></rect></g>')
    parts.append(f'<text x="{left + plot_width / 2:.1f}" y="{height - 5}" text-anchor="middle">Probability (maximum shown {maximum:.3f})</text>')
    parts.append("</svg>")
    return "".join(parts)


def svg_variation_occurrence(rows: list[dict[str, Any]], groups: list[str]) -> str:
    if not rows:
        return ""
    row_map = {(row["group"], row["variation"]): row for row in rows}
    width, row_height = 820, 25
    left, right, top, bottom = 165, 28, 36, 38
    height = top + len(SUPPORTED_VARIATIONS) * row_height + bottom
    plot_width = width - left - right
    maximum = max((float(row["occurrence_rate_per_transform"]) for row in rows if math.isfinite(float(row["occurrence_rate_per_transform"]))), default=0.0)
    maximum = max(0.01, maximum * 1.08)
    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="Variation occurrence rate by cohort">']
    for tick in range(5):
        rate = maximum * tick / 4
        x = left + plot_width * tick / 4
        parts.append(f'<line x1="{x:.1f}" y1="{top - 12}" x2="{x:.1f}" y2="{height - bottom}" stroke="#E2E8F0"/>')
        parts.append(f'<text x="{x:.1f}" y="{top - 18}" text-anchor="middle">{100 * rate:.1f}%</text>')
    for variation_index, variation in enumerate(SUPPORTED_VARIATIONS):
        y = top + variation_index * row_height + row_height / 2
        parts.append(f'<text x="{left - 10}" y="{y + 4:.1f}" text-anchor="end">{html.escape(variation)}</text>')
        parts.append(f'<line x1="{left}" y1="{y:.1f}" x2="{left + plot_width}" y2="{y:.1f}" stroke="#F1F5F9"/>')
    offset_step = min(3.0, 12 / max(1, len(groups) - 1)) if len(groups) > 1 else 0.0
    for group_index, group in enumerate(groups):
        color = GROUP_COLORS[group_index % len(GROUP_COLORS)]
        offset = (group_index - (len(groups) - 1) / 2) * offset_step
        parts.append(f'<g data-report-group="{html.escape(group, quote=True)}">')
        for variation_index, variation in enumerate(SUPPORTED_VARIATIONS):
            row = row_map.get((group, variation))
            if row is None or not math.isfinite(float(row["occurrence_rate_per_transform"])):
                continue
            rate = float(row["occurrence_rate_per_transform"])
            x = left + plot_width * rate / maximum
            y = top + variation_index * row_height + row_height / 2 + offset
            parts.append(
                f'<circle cx="{x:.2f}" cy="{y:.2f}" r="3.4" fill="{color}">'
                f'<title>{html.escape(group)} · {html.escape(variation)}: {100 * rate:.3f}% of base transforms '
                f'({row["base_transforms_with_variation"]}/{row["total_base_transforms"]})</title></circle>'
            )
        parts.append('</g>')
    parts.append(f'<text x="{left + plot_width / 2:.1f}" y="{height - 8}" text-anchor="middle">Base transforms containing the variation</text>')
    parts.append('</svg>')
    return "".join(parts)


def svg_simplex(vectors: Sequence[dict[str, Any]], minimum_share: float, maximum_points: int = 1200) -> str:
    selected = [
        vector for vector in vectors
        if vector["transform_kind"] == "xform" and vector["variation_count"] == 3
    ]
    if not selected or minimum_share >= 1 / 3:
        return ""
    stride = max(1, math.ceil(len(selected) / maximum_points))
    selected = selected[::stride]
    width, height = 780, 430
    left, bottom, side = 95, 365, 300
    height_side = side * math.sqrt(3) / 2
    points = [
        (left, bottom),
        (left + side, bottom),
        (left + side / 2, bottom - height_side),
    ]
    parts = [
        f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="Three-variation ternary simplex coverage">',
        f'<polygon points="{points[0][0]},{points[0][1]} {points[1][0]},{points[1][1]} {points[2][0]},{points[2][1]}" fill="#EEF4FF" stroke="#334155"/>',
        f'<text x="{points[0][0] - 12}" y="{points[0][1] + 18}">v1</text>',
        f'<text x="{points[1][0] - 8}" y="{points[1][1] + 18}">v2</text>',
        f'<text x="{points[2][0] - 8}" y="{points[2][1] - 12}">v3</text>',
    ]
    scale = 1 - 3 * minimum_share
    for vector in selected:
        values = [(float(value) - minimum_share) / scale for value in vector["weights"]]
        x = left + side * (values[1] + 0.5 * values[2])
        y = bottom - height_side * values[2]
        parts.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="2" fill="#D97706" fill-opacity="0.4"/>')
    parts.append(f'<text x="{width / 2:.1f}" y="{height - 8}" text-anchor="middle">normalized simplex coordinates; floor={minimum_share:g}; showing at most {maximum_points:,} points</text>')
    parts.append("</svg>")
    return "".join(parts)


def variation_weight_histogram(vectors: Sequence[dict[str, Any]], variation_count: int, minimum_share: float, bins: int = 18) -> str:
    selected = [
        vector for vector in vectors
        if vector["transform_kind"] == "xform" and vector["variation_count"] == variation_count
    ]
    values = [float(weight) for vector in selected for weight in vector["weights"]]
    if not values or variation_count <= 1:
        return ""
    lower = minimum_share
    upper = 1 - (variation_count - 1) * minimum_share
    if upper <= lower:
        return ""
    step = (upper - lower) / bins
    edges = [lower + step * index for index in range(bins)] + [upper]
    counts = histogram_counts(values, edges)
    total = len(values)
    rows = [
        {
            "group": "all components",
            "bin_index": index + 1,
            "lower": edges[index],
            "upper": edges[index + 1],
            "probability": count / total,
        }
        for index, count in enumerate(counts)
    ]
    return svg_numeric(f"{variation_count}-variation component weights", rows, ["all components"])


def variation_weight_tabs(
    vectors: Sequence[dict[str, Any]],
    simplex_rows: Sequence[dict[str, Any]],
    minimum_share: float,
) -> str:
    reference_groups = {vector["group"] for vector in vectors}
    panels: list[str] = []
    for variation_count in (1, 2, 3):
        selected = [
            vector for vector in vectors
            if vector["transform_kind"] == "xform" and vector["variation_count"] == variation_count
        ]
        diagnostic = next(
            (row for row in simplex_rows if row["group"] in reference_groups and row["variation_count"] == variation_count),
            None,
        )
        if diagnostic is None:
            body = "<p>No base-transform vectors with this variation count were found.</p>"
        elif variation_count == 1:
            body = (
                f"<p>{len(selected):,} transforms have one selected variation. The only valid normalized weight is exactly 1.0.</p>"
                f"<table><thead><tr><th>Vectors</th><th>Weight</th><th>Floor violations</th></tr></thead>"
                f"<tbody><tr><td>{len(selected):,}</td><td>1.0</td><td>{diagnostic['floor_violations']:,}</td></tr></tbody></table>"
            )
        elif variation_count == 2:
            chart = variation_weight_histogram(vectors, variation_count, minimum_share)
            body = (
                f"<p>{len(selected):,} two-variation transforms; the chart pools both components and uses the configured support interval.</p>"
                f"{chart}<table><thead><tr><th>Vectors</th><th>Observed range</th><th>KS D</th><th>p-value</th><th>Occupied bins</th></tr></thead>"
                f"<tbody><tr><td>{len(selected):,}</td><td>{format_number(diagnostic['min_component'])}–{format_number(diagnostic['max_component'])}</td>"
                f"<td>{format_number(diagnostic['statistic'])}</td><td>{format_number(diagnostic['p_value'])}</td>"
                f"<td>{diagnostic['occupied_cells']}/{diagnostic['total_cells']}</td></tr></tbody></table>"
            )
        else:
            chart = svg_simplex(vectors, minimum_share)
            body = (
                f"<p>{len(selected):,} three-variation transforms shown jointly on the valid simplex; no isolated component histogram is used.</p>"
                f"{chart}<table><thead><tr><th>Vectors</th><th>Observed range</th><th>Floor violations</th><th>Max sum error</th><th>Occupied cells</th></tr></thead>"
                f"<tbody><tr><td>{len(selected):,}</td><td>{format_number(diagnostic['min_component'])}–{format_number(diagnostic['max_component'])}</td>"
                f"<td>{diagnostic['floor_violations']:,}</td><td>{format_number(diagnostic['max_sum_error'])}</td>"
                f"<td>{diagnostic['occupied_cells']}/{diagnostic['total_cells']}</td></tr></tbody></table>"
            )
        hidden = "" if variation_count == 1 else " hidden"
        panels.append(
            f'<section id="variation-panel-{variation_count}" class="variation-panel" role="tabpanel" aria-labelledby="variation-tab-{variation_count}"{hidden}>'
            f'<h3>{variation_count} variation{("s" if variation_count != 1 else "")}</h3>{body}</section>'
        )
    buttons = "".join(
        f'<button type="button" class="variation-tab{" active" if variation_count == 1 else ""}" id="variation-tab-{variation_count}" role="tab" aria-selected="{"true" if variation_count == 1 else "false"}" aria-controls="variation-panel-{variation_count}" data-variation-tab="{variation_count}">{variation_count} variation{("s" if variation_count != 1 else "")}</button>'
        for variation_count in (1, 2, 3)
    )
    return f'<div class="variation-tabs" role="tablist" aria-label="Variation weight views">{buttons}</div>{"".join(panels)}'


def render_report(
    output: Path,
    config: dict[str, Any],
    state: AnalysisState,
    summary_rows: list[dict[str, Any]],
    histogram_rows: list[dict[str, Any]],
    category_rows: list[dict[str, Any]],
    comparison_rows: list[dict[str, Any]],
    uniformity_rows: list[dict[str, Any]],
    simplex_rows: list[dict[str, Any]],
    variation_occurrence_rows: list[dict[str, Any]],
    variation_weight_name_rows: list[dict[str, Any]],
    finding_rows: list[dict[str, Any]],
) -> None:
    groups = [group["name"] for group in config["groups"]]
    report_groups = config.get("report_groups") or groups
    parsed_counts = Counter(record["group"] for record in state.records)
    analysis_groups = [group for group in groups if parsed_counts[group] > 0]
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
    parameters_by_control: defaultdict[str, list[str]] = defaultdict(list)
    for parameter in sorted(summary_lookup, key=natural_key):
        spec = state.specs[parameter]
        if spec.generator_setting:
            parameters_by_control[spec.generator_setting].append(parameter)
    control_order = {name: index for index, name in enumerate(GENERATOR_CONTROL_ORDER)}
    parameter_sections: list[str] = []
    for control, parameters in sorted(parameters_by_control.items(), key=lambda item: (control_order.get(item[0], len(control_order)), item[0])):
        parameter_parts: list[str] = []
        search_fragments = [control]
        for parameter in parameters:
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
                summary_table_rows.append(
                    f'<tr data-report-table-group="{html.escape(row["group"], quote=True)}"><td>{html.escape(row["group"])}</td>'
                    f'<td>{row["n_observed"]:,}</td><td>{center}</td><td>{spread}</td></tr>'
                )
            search_fragments.extend((parameter, spec.category, spec.description))
            parameter_parts.append(
                f'<section class="control-measure"><h3>{html.escape(spec.description or parameter)}</h3>'
                f'<p class="technical"><code>{html.escape(parameter)}</code></p>{chart}'
                '<table><thead><tr><th>Group</th><th>n</th><th>Center</th><th>Spread / mode probability</th></tr></thead><tbody>'
                + "".join(summary_table_rows) + '</tbody></table></section>'
            )
        search_text = " ".join(search_fragments).lower()
        parameter_sections.append(
            f'<details class="parameter" data-search="{html.escape(search_text)}"><summary><span>{html.escape(control)}</span>'
            f'<small>{len(parameters)} measured distribution{("s" if len(parameters) != 1 else "")}</small></summary>'
            + "".join(parameter_parts) + '</details>'
        )

    inventory_rows = "".join(
        f'<tr><td>{html.escape(row["group"])}</td><td><code>{html.escape(row["folder"])}</code></td><td>{html.escape(row["extension"])}</td><td>{row["file_count"]:,}</td></tr>'
        for row in state.inventory
    )
    group_rows = "".join(
        f'<tr><td><span class="swatch" style="background:{GROUP_COLORS[index % len(GROUP_COLORS)]}"></span>{html.escape(group)}</td><td>{parsed_counts[group]:,}</td></tr>'
        for index, group in enumerate(groups)
    )
    group_controls = "".join(
        f'<label><input type="checkbox" class="report-group-toggle" value="{html.escape(group, quote=True)}"{" checked" if group in report_groups else ""}>'
        f'<span class="swatch" style="background:{GROUP_COLORS[groups.index(group) % len(GROUP_COLORS)]}"></span>{html.escape(group)}</label>'
        for group in analysis_groups
    )
    finding_table = "".join(
        f'<tr data-report-table-group="{html.escape(row["target_group"], quote=True)}"><td>{html.escape(row["generator_setting"])}</td>'
        f'<td>{html.escape(row["target_group"])}</td><td><small>{html.escape(row["measure"])}</small><br>{html.escape(row["target_evidence"])}</td>'
        f'<td><strong>{html.escape(row["signal"])}</strong><br><span class="muted">{html.escape(row["guidance"])}</span></td></tr>'
        for row in finding_rows
    ) or '<tr><td colspan="4">No reference-to-target findings were available.</td></tr>'
    occurrence_chart = svg_variation_occurrence(variation_occurrence_rows, analysis_groups)
    occurrence_table = "".join(
        f'<tr data-report-table-group="{html.escape(row["group"], quote=True)}"><td>{html.escape(row["variation"])}</td>'
        f'<td>{html.escape(row["group"])}</td><td>{format_number(100 * row["occurrence_rate_per_transform"], 2)}%</td>'
        f'<td>{format_number(100 * row["reference_occurrence_rate"], 2)}%</td>'
        f'<td>{format_number(100 * row["difference_from_reference"], 2)} pp</td></tr>'
        for row in variation_occurrence_rows
    )
    variation_name_table = "".join(
        f'<tr data-report-table-group="{html.escape(row["group"], quote=True)}"><td>{html.escape(row["variation"])}</td>'
        f'<td>{html.escape(row["group"])}</td><td>{row["n"]:,}</td><td>{format_number(row["mean_weight_when_present"])}</td>'
        f'<td>{format_number(row["q05"])}</td><td>{format_number(row["median"])}</td><td>{format_number(row["q95"])}</td></tr>'
        for row in variation_weight_name_rows if row["n"]
    ) or '<tr><td colspan="7">No named variation weights were available.</td></tr>'
    comparison_table = "".join(
        f'<tr><td>{html.escape(row["parameter"])}</td><td>{html.escape(row["target_group"])}</td><td>{format_number(row["concentration_score"])}</td><td>{format_number(row["js_divergence_bits"])}</td><td>{format_number(row["circular_variance_ratio"] if row["kind"] == "circular" else row["iqr_ratio"])}</td><td>{format_number(row["entropy_change_reference_minus_target"])}</td></tr>'
        for row in concentration_rows
    ) or '<tr><td colspan="6">No comparison distributions were available.</td></tr>'
    uniformity_table = "".join(
        f'<tr class="{"flag" if row["practical_flag_effect_gt_0_02"] else ""}"><td>{html.escape(row["parameter"])}</td><td>{html.escape(row["test"])}</td><td>{row["n"]:,}</td><td>{format_number(row["statistic"])}</td><td>{format_number(row["p_value"])}</td><td>{format_number(row["effect_size"])}</td><td>{"yes" if row["practical_flag_effect_gt_0_02"] else "no"}</td></tr>'
        for row in uniformity_rows
    ) or '<tr><td colspan="7">No expected-generator configuration was supplied.</td></tr>'
    reference_group = config.get("reference_group")
    reference_vectors = [vector for vector in state.variation_vectors if vector["group"] == reference_group]
    minimum_share = float(config.get("expected_generator", {}).get("minimum_variation_share", 0.0))
    variation_tabs = variation_weight_tabs(reference_vectors, simplex_rows, minimum_share)
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
.table-wrap{{overflow:auto;max-height:540px;border:1px solid var(--line);border-radius:8px}}.swatch{{display:inline-block;width:11px;height:11px;margin-right:7px;border-radius:2px;vertical-align:baseline}}.flag td{{background:#FFF7ED}}
input[type="search"]{{width:100%;padding:10px 12px;border:1px solid #AAB4C3;border-radius:6px;font:inherit;background:white}}.group-toolbar{{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}}.group-toolbar button{{border:1px solid #AAB4C3;border-radius:6px;background:white;padding:6px 10px;cursor:pointer}}.group-controls{{display:flex;flex-wrap:wrap;gap:10px 18px;margin:10px 0 20px}}.group-controls label{{white-space:nowrap;display:flex;align-items:center;gap:5px}}.group-controls input{{width:auto;margin:0}}details.parameter{{background:var(--panel);border:1px solid var(--line);border-radius:8px;margin:10px 0;padding:0 14px 14px}}
details.parameter summary{{cursor:pointer;padding:13px 0;display:flex;gap:12px;justify-content:space-between;font-weight:600}}.control-measure{{padding:4px 0 18px;border-top:1px solid var(--line)}}.control-measure h3{{margin:14px 0 0}}.technical{{margin-top:3px;color:var(--muted)}}svg{{display:block;width:100%;height:auto;max-width:900px;margin:8px 0 16px}}svg text{{font:11px system-ui,-apple-system,"Segoe UI",sans-serif;fill:#334155}}.variation-tabs{{display:flex;gap:8px;border-bottom:1px solid var(--line);margin-bottom:14px}}.variation-tab{{border:0;background:transparent;color:var(--muted);font:inherit;padding:10px 14px;cursor:pointer;border-bottom:3px solid transparent}}.variation-tab.active{{color:var(--ink);border-bottom-color:var(--accent);font-weight:600}}.variation-panel h3{{margin:14px 0 6px}}.variation-panel table{{margin:10px 0 16px}}
ul{{padding-left:22px}}@media(max-width:650px){{main{{padding:20px 12px}}details.parameter summary{{display:block}}th,td{{padding:6px}}}}
</style></head><body><main>
<h1>{title}</h1><p class="muted">Tool version {TOOL_VERSION}. Source workspaces were read only. Histograms are normalized probabilities; concentration scores describe narrowing, not quality.</p>
<div class="grid"><section class="panel"><h2>Parsed cohorts</h2><table><thead><tr><th>Group</th><th>Parsed flames</th></tr></thead><tbody>{group_rows}</tbody></table></section>
<section class="panel"><h2>Interpretation guardrail</h2><p>Univariate concentration can identify narrowed marginals, but interacting flame parameters may still form several joint modes. Do not configure the generator from concentration alone without validating joint structure and human target-style hit rate.</p></section></div>
<h2>Choose comparison groups</h2><p>These controls change every graph and cohort row in this report. They do not rerun or alter the analysis.</p><div class="group-toolbar"><button type="button" id="select-all-groups">Select all</button><button type="button" id="select-reference-group">Reference only</button><button type="button" id="select-rating-groups">Ratings only</button></div><div class="group-controls" id="group-controls">{group_controls}</div>
<h2>Generator-control findings</h2><p>This is screening guidance, not an automatic preset. A candidate range is the selected cohort’s 5th–95th percentile interpreted as a possible uniform generator range. Low-sample cohorts are deliberately prevented from producing configuration advice.</p><div class="table-wrap"><table><thead><tr><th>Generator control</th><th>Cohort</th><th>Observed selected distribution</th><th>Interpretation</th></tr></thead><tbody>{finding_table}</tbody></table></div>
<h2>Variation occurrence</h2><p>Each point is the percentage of base transforms in that cohort containing the named variation. This is one comparable distribution over variation types—not 38 true/false parameters. It directly informs the <em>Enabled variations</em> control; probability-weighted variation selection would require a future generator control.</p>{occurrence_chart}<details class="panel"><summary>Exact occurrence rates</summary><div class="table-wrap"><table><thead><tr><th>Variation</th><th>Group</th><th>Occurrence</th><th>Reference</th><th>Difference</th></tr></thead><tbody>{occurrence_table}</tbody></table></div></details>
<details class="panel"><summary>Variation weight by name</summary><p>Conditional weights are retained by variation name for future profile design. The current generator exposes a global minimum variation share, not a separate weight range per variation.</p><div class="table-wrap"><table><thead><tr><th>Variation</th><th>Group</th><th>n</th><th>Mean</th><th>q05</th><th>Median</th><th>q95</th></tr></thead><tbody>{variation_name_table}</tbody></table></div></details>
<h2>Generator controls</h2><p>Parameters are grouped by the setting visible in the random-generator window. Select cohorts above, then expand only the controls you want to compare.</p><label for="filter">Filter controls</label><input id="filter" type="search" placeholder="Search by generator control or measured distribution">
<div id="parameters">{''.join(parameter_sections)}</div>
<details class="panel"><summary>Advanced concentration metrics</summary><p><strong>JS divergence</strong> is zero when two distributions match and grows as their shapes differ. <strong>Spread ratio</strong> is selected spread divided by reference spread; below 1 means narrower. <strong>Entropy change</strong> is positive when categorical choices become more concentrated. These describe marginal distributions and do not prove a better style.</p><p>This ranking omits {excluded_concentration_count:,} comparisons below the sample guardrail; all rows remain in <code>concentration_comparisons.csv</code>.</p><div class="table-wrap"><table><thead><tr><th>Parameter</th><th>Target</th><th>Concentration</th><th>JS divergence</th><th>Spread ratio</th><th>Entropy change</th></tr></thead><tbody>{comparison_table}</tbody></table></div></details>
<details class="panel"><summary>Reference-generator validation statistics</summary><p><strong>p-value</strong> asks whether the reference differs detectably from its configured sampling rule; with 24,000 flames even tiny deviations can be detectable. <strong>Effect</strong> measures the size of the mismatch. The <strong>practical flag</strong> is yes when that mismatch exceeds 0.02, so it is the more useful first alert for generator validation.</p><div class="table-wrap"><table><thead><tr><th>Parameter</th><th>Test</th><th>n</th><th>Statistic</th><th>p-value</th><th>Effect</th><th>Practical flag</th></tr></thead><tbody>{uniformity_table}</tbody></table></div></details>
<details class="panel"><summary>Variation mixture geometry</summary><p>Weights are separated into one-, two-, and three-variation transforms because each has a different valid probability space.</p>{variation_tabs}</details>
<details class="panel"><summary>Folder inventory</summary><div class="table-wrap"><table><thead><tr><th>Group</th><th>Folder</th><th>Extension</th><th>Files</th></tr></thead><tbody>{inventory_rows}</tbody></table></div></details>
<h2>Warnings and exclusions</h2><ul>{warnings_html}</ul>
</main><script>
const input=document.getElementById('filter');const sections=[...document.querySelectorAll('.parameter')];input.addEventListener('input',()=>{{const q=input.value.trim().toLowerCase();sections.forEach(s=>s.hidden=q&&!s.dataset.search.includes(q));}});document.querySelectorAll('[data-variation-tab]').forEach(tab=>tab.addEventListener('click',()=>{{const selected=tab.dataset.variationTab;document.querySelectorAll('[data-variation-tab]').forEach(item=>{{const active=item===tab;item.classList.toggle('active',active);item.setAttribute('aria-selected',active?'true':'false');}});document.querySelectorAll('.variation-panel').forEach(panel=>{{panel.hidden=panel.id!==`variation-panel-${{selected}}`;}});}}));const toggles=[...document.querySelectorAll('.report-group-toggle')];const applyGroupVisibility=()=>{{const selected=new Set(toggles.filter(item=>item.checked).map(item=>item.value));document.querySelectorAll('[data-report-group]:not([data-report-static])').forEach(series=>{{series.style.display=selected.has(series.dataset.reportGroup)?'':'none';}});document.querySelectorAll('[data-report-table-group]').forEach(row=>{{row.style.display=selected.has(row.dataset.reportTableGroup)?'':'none';}});}};const chooseGroups=predicate=>{{toggles.forEach(item=>item.checked=predicate(item.value));applyGroupVisibility();}};toggles.forEach(item=>item.addEventListener('change',applyGroupVisibility));document.getElementById('select-all-groups').addEventListener('click',()=>chooseGroups(()=>true));document.getElementById('select-reference-group').addEventListener('click',()=>chooseGroups(value=>value==={json.dumps(reference_group)}));document.getElementById('select-rating-groups').addEventListener('click',()=>chooseGroups(value=>value!=={json.dumps(reference_group)}));applyGroupVisibility();
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
    simplex_rows = make_simplex_diagnostics(state, config)
    variation_occurrence_rows = make_variation_occurrence_rows(state, config)
    variation_weight_name_rows = make_variation_weight_by_name_rows(state, config)
    finding_rows = make_generator_control_findings(state, config, summary_rows, comparison_rows)
    matrix_rows, matrix_parameters = write_matrices(output, state, bool(config.get("write_transposed_matrix", True)))
    write_variation_weight_vectors(output, state)
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
    write_dict_csv(output / "simplex_coverage.csv", simplex_rows)
    write_dict_csv(output / "variation_occurrence.csv", variation_occurrence_rows)
    write_dict_csv(output / "variation_weight_by_name.csv", variation_weight_name_rows)
    write_dict_csv(output / "generator_control_findings.csv", finding_rows)
    render_report(
        output, config, state, summary_rows, histogram_rows, category_rows,
        comparison_rows, uniformity_rows, simplex_rows, variation_occurrence_rows,
        variation_weight_name_rows, finding_rows,
    )

    duplicate_ids = Counter(record["source_id"] for record in state.records)
    run_summary = {
        "tool_version": TOOL_VERSION,
        "config_path": str(config_path.resolve()),
        "generator_profile_path": config.get("generator_profile_path", ""),
        "output_directory": str(output.resolve()),
        "report_groups": config.get("report_groups") or [group["name"] for group in config["groups"]],
        "parsed_flames": len(state.records),
        "parsed_by_group": dict(sorted(Counter(record["group"] for record in state.records).items())),
        "matrix_rows": matrix_rows,
        "matrix_parameter_columns": matrix_parameters,
        "analyzed_parameters": len({row["parameter"] for row in summary_rows}),
        "variation_weight_vectors": len(state.variation_vectors),
        "variation_occurrence_rows": len(variation_occurrence_rows),
        "generator_control_findings": len(finding_rows),
        "simplex_diagnostics": simplex_rows,
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
