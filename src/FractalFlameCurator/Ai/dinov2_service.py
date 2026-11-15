"""Small JSON-lines worker for the optional DINOv2 preference scorer.

The WPF process remains responsible for the UI, file pipeline, ratings, and
atomic model activation. This worker owns PyTorch, the frozen DINOv2 ViT-B/14
backbone, and the trainable ordinal head. It intentionally refuses to score or
train on CPU: CPU rendering/manual curation remains available in the host app.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import tempfile
from pathlib import Path
from typing import Any


MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)
MODEL_NAME = "dinov2_vitb14"
GPU_BATCH_SIZE = 4


def _diagnostics() -> dict[str, Any]:
    try:
        import torch
    except Exception as exc:  # pragma: no cover - depends on local installation
        return {
            "TorchAvailable": False,
            "CudaAvailable": False,
            "GpuName": "Unavailable",
            "PyTorchVersion": "Unavailable",
            "ActiveDevice": "none",
            "AiReady": False,
            "Detail": f"PyTorch import failed: {exc}",
        }
    cuda = bool(torch.cuda.is_available())
    gpu = torch.cuda.get_device_name(0) if cuda else "Unavailable"
    return {
        "TorchAvailable": True,
        "CudaAvailable": cuda,
        "GpuName": gpu,
        "PyTorchVersion": str(torch.__version__),
        "ActiveDevice": "cuda:0" if cuda else "cpu (AI disabled)",
        "AiReady": cuda,
        "Detail": "DINOv2 AI scoring/training uses CUDA." if cuda else "CUDA is unavailable; AI scoring/training is disabled. Manual rendering and rating remain available.",
    }


class OrdinalHead:
    def __init__(self, torch: Any, dimension: int) -> None:
        import torch.nn as nn

        class Head(nn.Module):
            def __init__(self) -> None:
                super().__init__()
                self.latent = nn.Linear(dimension, 1)
                self.raw_spacing = nn.Parameter(torch.zeros(4))

            def forward(self, features: Any) -> Any:
                latent = self.latent(features)
                spacing = torch.nn.functional.softplus(self.raw_spacing)
                thresholds = torch.cumsum(spacing, dim=0)
                return latent - thresholds

        self.module = Head()


class Worker:
    def __init__(self, model_directory: str | None = None) -> None:
        import torch

        self.torch = torch
        info = _diagnostics()
        if not info["AiReady"]:
            raise RuntimeError(info["Detail"])
        self.device = torch.device("cuda:0")
        self.backbone = torch.hub.load("facebookresearch/dinov2", MODEL_NAME)
        self.backbone.to(self.device).eval()
        for parameter in self.backbone.parameters():
            parameter.requires_grad_(False)
        self.dimension = int(getattr(self.backbone, "embed_dim", 768))
        self.head: Any | None = None
        self.model_version: str | None = None
        if model_directory:
            self._load_latest(Path(model_directory))

    def _load_latest(self, model_directory: Path) -> None:
        candidates = sorted(model_directory.glob("*.pt"), key=lambda path: path.stat().st_mtime, reverse=True) if model_directory.exists() else []
        if not candidates:
            return
        payload = self.torch.load(candidates[0], map_location=self.device, weights_only=False)
        head = OrdinalHead(self.torch, int(payload.get("dimension", self.dimension))).module.to(self.device)
        head.load_state_dict(payload["head"])
        head.eval()
        self.head = head
        self.model_version = str(payload["model_version"])

    def _image_tensor(self, path: str) -> Any:
        from PIL import Image

        image = Image.open(path).convert("RGB")
        # DINOv2's standard 224px evaluation path: resize the short side to
        # 256px, center crop to 224px, then ImageNet normalize three RGB planes.
        scale = 256.0 / min(image.width, image.height)
        resized = image.resize((round(image.width * scale), round(image.height * scale)), Image.Resampling.BICUBIC)
        left = (resized.width - 256) // 2
        top = (resized.height - 256) // 2
        image = resized.crop((left + 16, top + 16, left + 240, top + 240))
        values = self.torch.tensor(list(image.getdata()), dtype=self.torch.float32, device=self.device)
        values = values.view(224, 224, 3).permute(2, 0, 1).div(255.0)
        mean = self.torch.tensor(MEAN, device=self.device).view(3, 1, 1)
        std = self.torch.tensor(STD, device=self.device).view(3, 1, 1)
        return values.sub(mean).div(std).unsqueeze(0)

    def _features(self, paths: list[str]) -> Any:
        with self.torch.inference_mode():
            batch = self.torch.cat([self._image_tensor(path) for path in paths])
            result = self.backbone.forward_features(batch)
            if isinstance(result, dict):
                result = result.get("x_norm_clstoken", result.get("x_norm_regtokens"))
                if result is None:
                    raise RuntimeError("DINOv2 did not return a class-token feature.")
            return result.float()

    def _ensure_head(self) -> Any:
        if self.head is None:
            self.head = OrdinalHead(self.torch, self.dimension).module.to(self.device)
        return self.head

    def _probabilities(self, features: Any) -> Any:
        head = self._ensure_head()
        with self.torch.inference_mode():
            return self.torch.sigmoid(head(features))

    def score(self, paths: list[str]) -> dict[str, Any]:
        if not self.model_version:
            raise RuntimeError("No trained preference model is active. Press Train Model first.")
        scores = []
        for start in range(0, len(paths), GPU_BATCH_SIZE):
            batch_paths = paths[start:start + GPU_BATCH_SIZE]
            probabilities = self._probabilities(self._features(batch_paths)).detach().cpu().tolist()
            for path, cumulative in zip(batch_paths, probabilities):
                expected = max(1.0, min(5.0, 1.0 + sum(max(0.0, min(1.0, value)) for value in cumulative)))
                scores.append({"Path": path, "ExpectedRating": expected, "Score": max(0.0, min(1.0, (expected - 1.0) / 4.0))})
        return {"ModelVersion": self.model_version, "Scores": scores}

    def train(self, request: dict[str, Any]) -> dict[str, Any]:
        import torch.nn.functional as functional

        train = request.get("train", [])
        all_entries = request.get("images", [])
        feature_cache: dict[str, Any] = {}
        for start in range(0, len(all_entries), GPU_BATCH_SIZE):
            entries = all_entries[start:start + GPU_BATCH_SIZE]
            features = self._features([entry["path"] for entry in entries]).detach()
            for index, entry in enumerate(entries):
                feature_cache[entry["path"]] = features[index:index + 1]
        head = OrdinalHead(self.torch, self.dimension).module.to(self.device)
        optimizer = self.torch.optim.AdamW(head.parameters(), lr=0.02, weight_decay=0.001)
        if train:
            train_features = self.torch.cat([feature_cache[entry["path"]] for entry in train])
            targets = self.torch.tensor([[1.0 if entry["rating"] >= threshold else 0.0 for threshold in range(2, 6)] for entry in train], device=self.device)
            for _ in range(80):
                optimizer.zero_grad(set_to_none=True)
                loss = functional.binary_cross_entropy_with_logits(head(train_features), targets)
                loss.backward()
                optimizer.step()
        self.head = head
        model_directory = Path(request["model_directory"])
        model_directory.mkdir(parents=True, exist_ok=True)
        version = f"dinov2-vitb14-ordinal-{self.torch.randint(0, 2**31, (1,)).item():010d}"
        model_path = model_directory / f"{version}.pt"
        descriptor, temporary_name = tempfile.mkstemp(prefix="ffc-model-", suffix=".tmp", dir=model_directory)
        os.close(descriptor)
        temporary = Path(temporary_name)
        try:
            self.torch.save({"model_version": version, "head": head.state_dict(), "dimension": self.dimension}, temporary)
            os.replace(temporary, model_path)
        finally:
            if temporary.exists():
                temporary.unlink()
        self.model_version = version
        return {"ModelVersion": version, "Metrics": self._metrics(request, feature_cache, head)}

    def _metrics(self, request: dict[str, Any], features: dict[str, Any], head: Any) -> dict[str, Any]:
        values = []
        for split_name in ("validation", "test"):
            entries = request.get(split_name, [])
            for entry in entries:
                with self.torch.inference_mode():
                    cumulative = self.torch.sigmoid(head(features[entry["path"]])).detach().cpu().tolist()[0]
                expected = max(1.0, min(5.0, 1.0 + sum(cumulative)))
                values.append((entry["rating"], expected, cumulative))
        reliable = bool(request.get("validation")) and bool(request.get("test")) and len(values) >= 4
        if not values:
            return {"OrdinalAccuracy": 0.0, "MeanAbsoluteRatingError": 0.0, "SpearmanCorrelation": 0.0, "RankCorrelation": 0.0, "CalibrationError": 0.0, "IsReliable": False, "Controls": self._control_metrics(request, head)}
        actual = [value[0] for value in values]
        predicted = [value[1] for value in values]
        rounded = [max(1, min(5, int(round(value)))) for value in predicted]
        ordinal_accuracy = sum(a == p for a, p in zip(actual, rounded)) / len(values)
        mae = sum(abs(a - p) for a, p in zip(actual, predicted)) / len(values)
        rank = _spearman(actual, predicted)
        calibration_errors = [
            abs(probability - (1.0 if rating >= threshold else 0.0))
            for rating, _, cumulative in values
            for threshold, probability in zip(range(2, 6), cumulative)
        ]
        calibration_error = sum(calibration_errors) / len(calibration_errors)
        controls = self._control_metrics(request, head)
        return {"OrdinalAccuracy": ordinal_accuracy, "MeanAbsoluteRatingError": mae, "SpearmanCorrelation": rank, "RankCorrelation": rank, "CalibrationError": calibration_error, "IsReliable": reliable, "Controls": controls}

    def _control_metrics(self, request: dict[str, Any], head: Any) -> list[dict[str, Any]]:
        controls = []
        for control in request.get("controls", []):
            with self.torch.inference_mode():
                cumulative = self.torch.sigmoid(head(self._features([control["path"]]))).detach().cpu().tolist()[0]
            expected = max(1.0, min(5.0, 1.0 + sum(cumulative)))
            controls.append({"Name": control["name"], "Score": max(0.0, min(1.0, (expected - 1.0) / 4.0)), "Interpretation": "Evaluation-only control; not included as a human training label."})
        return controls


def _spearman(actual: list[float], predicted: list[float]) -> float:
    if len(actual) < 2:
        return 0.0
    a = _average_ranks(actual)
    p = _average_ranks(predicted)
    a_mean = sum(a) / len(a)
    p_mean = sum(p) / len(p)
    numerator = sum((x - a_mean) * (y - p_mean) for x, y in zip(a, p))
    denominator = math.sqrt(sum((x - a_mean) ** 2 for x in a) * sum((y - p_mean) ** 2 for y in p))
    return numerator / denominator if denominator else 0.0


def _average_ranks(values: list[float]) -> list[float]:
    ordered = sorted(range(len(values)), key=values.__getitem__)
    ranks = [0.0] * len(values)
    start = 0
    while start < len(ordered):
        end = start + 1
        while end < len(ordered) and values[ordered[end]] == values[ordered[start]]:
            end += 1
        average = (start + end - 1) / 2.0
        for index in ordered[start:end]:
            ranks[index] = average
        start = end
    return ranks


def handle(request: dict[str, Any], worker: Worker | None) -> tuple[dict[str, Any], Worker | None]:
    command = request.get("command")
    if command == "diagnostics":
        return _diagnostics(), worker
    if command == "shutdown":
        return {"Ok": True}, worker
    if worker is None:
        worker = Worker(request.get("model_directory"))
    if command == "train":
        return worker.train(request), worker
    if command == "score":
        return worker.score(request.get("paths", [])), worker
    raise RuntimeError(f"Unknown command: {command}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", action="store_true")
    parser.parse_args()
    worker: Worker | None = None
    for line in sys.stdin:
        try:
            request = json.loads(line)
            response, worker = handle(request, worker)
            print(json.dumps(response), flush=True)
            if request.get("command") == "shutdown":
                break
        except Exception as exc:  # keep the JSON protocol alive for the next request
            print(json.dumps({"Error": str(exc)}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
