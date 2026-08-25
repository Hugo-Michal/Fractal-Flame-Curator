import csv
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path


TOOL_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOL_DIR))

import analyze_flames as analysis  # noqa: E402


def write_flame(path: Path, seed: int, variation: str = "linear", with_post: bool = False) -> None:
    angle = math.radians(30)
    scale = 0.5
    shear = 0.1
    a = math.cos(angle) * scale
    c = math.sin(angle) * scale
    b = -math.sin(angle) * scale + shear
    d = math.cos(angle) * scale
    post = ' post="0.848528137423857 0.848528137423857 -0.848528137423857 0.848528137423857 0.12 -0.08"' if with_post else ""
    xml = f'''<flames>
  <flame name="test_{seed}" version="Apophysis 7X" seed="{seed}" size="2048 2048" center="0 0" scale="100" rotate="0" symmetry="1" oversample="1" filter="0.5" quality="4.76837158203125" background="1 1 1" brightness="1" gamma="1" gamma_threshold="0.01" vibrancy="1" hue_rotation="0">
    <xform weight="0.8" color="0" symmetry="0.3" coefs="{a} {b} {c} {d} 0.2 -0.3" {variation}="1"{post} />
    <xform weight="1.1" color="1" symmetry="0.3" coefs="0.4 -0.2 0.2 0.4 -0.1 0.1" sinusoidal="0.4" swirl="0.6" />
    <palette count="2" format="RGB">FFFFFF000000</palette>
  </flame>
</flames>
'''
    path.write_text(xml, encoding="utf-8")


def expected_generator() -> dict:
    return {
        "minimum_transform_count": 2,
        "maximum_transform_count": 2,
        "symmetry_types": ["rotational"],
        "symmetry_chance": 0.4,
        "symmetry_orders": [2, 3],
        "minimum_affine_rotation_degrees": -180.0,
        "maximum_affine_rotation_degrees": 180.0,
        "minimum_affine_scale": 0.35,
        "maximum_affine_scale": 0.85,
        "minimum_affine_shear": -0.25,
        "maximum_affine_shear": 0.25,
        "translation_extent": 0.75,
        "transform_selection_balance": 0.35,
        "minimum_variation_count": 1,
        "maximum_variation_count": 2,
        "enabled_variations": ["linear", "sinusoidal", "swirl"],
        "variation_blend_dominance": 0.5,
        "post_transform_chance": 0.42,
        "minimum_post_rotation_degrees": -180.0,
        "maximum_post_rotation_degrees": 180.0,
        "minimum_post_scale": 0.75,
        "maximum_post_scale": 1.25,
        "post_translation_extent": 0.18,
        "allow_final_transforms": False,
        "final_transform_chance": 0.15,
    }


class FlameParameterAnalysisTests(unittest.TestCase):
    def test_reconstructs_generator_facing_affine_values(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "flame_000001_seed_42_run_test.flame"
            write_flame(path, 42, with_post=True)
            state = analysis.AnalysisState()
            record = analysis.parse_flame_file(state, "reference", path)

            self.assertAlmostEqual(30.0, record["xform.01.derived.rotation_degrees"], places=10)
            self.assertAlmostEqual(0.5, record["xform.01.derived.scale"], places=10)
            self.assertAlmostEqual(0.1, record["xform.01.derived.shear"], places=10)
            self.assertAlmostEqual(0.2, record["xform.01.derived.translation_x"], places=10)
            self.assertAlmostEqual(-0.3, record["xform.01.derived.translation_y"], places=10)
            self.assertAlmostEqual(-45.0, record["xform.01.post.derived.rotation_degrees"], places=8)
            self.assertAlmostEqual(1.2, record["xform.01.post.derived.scale"], places=8)
            self.assertEqual(2, record["genome.transform_count"])
            self.assertTrue(record["genome.variation.linear.present"])
            self.assertFalse(record["genome.finalxform_present"])

    def test_run_writes_auditable_matrices_statistics_and_report(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            reference = root / "reference"
            selected = root / "selected"
            inventory = root / "images"
            empty_inventory = root / "empty"
            reference.mkdir()
            selected.mkdir()
            inventory.mkdir()
            empty_inventory.mkdir()
            for index, variation in enumerate(("linear", "sinusoidal", "swirl", "linear"), 1):
                name = f"flame_{index:06d}_seed_{index}_run_current.flame"
                write_flame(reference / name, index, variation, with_post=index % 2 == 0)
                if index <= 2:
                    write_flame(selected / name, index, variation, with_post=index % 2 == 0)
            (inventory / "reference.jpg").write_bytes(b"image")
            output = root / "output"
            config = {
                "title": "Test report",
                "output_directory": str(output),
                "write_transposed_matrix": True,
                "histogram": {"minimum_bins": 3, "maximum_bins": 8},
                "groups": [
                    {"name": "reference", "folders": [str(reference)], "include_run_ids": ["current"]},
                    {"name": "selected", "folders": [str(selected)], "include_run_ids": ["current"]},
                    {"name": "images", "folders": [str(inventory)], "analyze": False},
                    {"name": "empty", "folders": [str(empty_inventory)], "analyze": False},
                ],
                "reference_group": "reference",
                "comparison_groups": ["selected"],
                "expected_generator": expected_generator(),
            }
            config_path = root / "config.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")

            summary = analysis.run_analysis(config_path)

            self.assertEqual(6, summary["parsed_flames"])
            self.assertEqual({"reference": 4, "selected": 2}, summary["parsed_by_group"])
            expected_files = (
                "flame_parameter_matrix.csv", "flame_parameter_matrix_transposed.csv",
                "folder_inventory.csv", "flame_manifest.csv", "parameter_catalog.csv",
                "parameter_summary.csv", "numeric_histograms.csv",
                "categorical_probabilities.csv", "concentration_comparisons.csv",
                "uniformity_tests.csv", "distribution_profiles.json", "report.html", "run_summary.json",
            )
            for name in expected_files:
                self.assertTrue((output / name).is_file(), name)

            with (output / "flame_parameter_matrix.csv").open(encoding="utf-8-sig", newline="") as stream:
                matrix = list(csv.DictReader(stream))
            self.assertEqual(6, len(matrix))
            self.assertIn("xform.01.derived.scale", matrix[0])

            with (output / "categorical_probabilities.csv").open(encoding="utf-8-sig", newline="") as stream:
                probabilities = list(csv.DictReader(stream))
            linear_presence = [
                float(row["probability"])
                for row in probabilities
                if row["parameter"] == "xform[*].variation.linear.present"
                and row["group"] == "reference"
            ]
            self.assertAlmostEqual(1.0, sum(linear_presence), places=12)
            profiles = json.loads((output / "distribution_profiles.json").read_text(encoding="utf-8"))
            transform_count = profiles["groups"]["reference"]["parameters"]["genome.transform_count"]
            self.assertEqual("categorical", transform_count["kind"])
            self.assertAlmostEqual(1.0, sum(item["probability"] for item in transform_count["probabilities"]))
            with (output / "folder_inventory.csv").open(encoding="utf-8-sig", newline="") as stream:
                inventory_rows = list(csv.DictReader(stream))
            self.assertTrue(any(
                row["group"] == "empty" and row["extension"] == "<empty-folder>" and row["file_count"] == "0"
                for row in inventory_rows
            ))
            report = (output / "report.html").read_text(encoding="utf-8")
            self.assertIn("Test report", report)
            self.assertIn("Largest measured concentrations", report)

    def test_entropy_uses_the_full_bin_or_category_support(self) -> None:
        self.assertAlmostEqual(0.5, analysis.normalized_entropy([0.5, 0.5, 0.0, 0.0]))
        self.assertEqual(0.0, analysis.normalized_entropy([1.0, 0.0]))

    def test_circular_statistics_do_not_split_a_boundary_cluster(self) -> None:
        mean, resultant_length, variance = analysis.circular_statistics_degrees([179.0, -179.0])
        self.assertAlmostEqual(180.0, abs(mean), places=10)
        self.assertGreater(resultant_length, 0.99)
        self.assertLess(variance, 0.001)

    def test_enforced_expected_count_rejects_an_incomplete_reference(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            reference = root / "reference"
            reference.mkdir()
            write_flame(reference / "flame_000001_seed_1_run_current.flame", 1)
            config = {
                "output_directory": str(root / "output"),
                "enforce_expected_counts": True,
                "groups": [{
                    "name": "reference",
                    "folders": [str(reference)],
                    "expected_flame_count": 2,
                }],
            }
            config_path = root / "config.json"
            config_path.write_text(json.dumps(config), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "expected 2 parsed flames, found 1"):
                analysis.run_analysis(config_path)


if __name__ == "__main__":
    unittest.main()
