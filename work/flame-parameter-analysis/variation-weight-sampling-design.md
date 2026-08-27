# System design: coverage-first variation-mixture sampling

**Status:** implemented in v1.3.0; this document records the design rationale
and the acceptance criteria used by the current generator and analysis tool.

## Decision being addressed

The current normalized-raw sampler is not retained. It excludes large regions
of the meaningful relative-mixture space: with two selected variations, it
cannot produce a ratio close to `0.05 / 0.95`; with three or more variations,
it concentrates too strongly around equal blends.

The replacement must generate broad, balanced coverage of the valid *relative*
weight space whenever a transform contains two or more variations.

## What “full coverage” means

The valid weights for `k` selected variations are not a rectangular space. They
obey:

```text
wi >= minimumShare
sum(wi) = 1
```

This is a line for two variations, a triangle for three, a tetrahedron for
four, and a higher-dimensional simplex thereafter. No finite batch can visit
every point in a continuous space. The precise practical target is therefore:

1. **Full support:** every valid ratio above a deliberate minimum share can be
   generated; no artificial central-only range remains.
2. **Balanced finite-batch coverage:** early samples are dispersed across that
   line, triangle, or simplex rather than clustering by ordinary independent
   randomness.
3. **No variation-name bias:** a variation receives every region of the
   mixture space equally over a session.

## Proposed replacement: `SpaceFillingSimplexSampler`

Use this sampler for every transform with two or more selected variations.
There is no legacy sampling mode or compatibility switch.

### Setting

```text
MinimumVariationShare = 0.05     # proposed initial value
```

The setting is non-negative and must not exceed `1 / MaximumVariationCount`.
With the present maximum of three, `0.05` is valid. It makes a two-variation
blend cover the range from almost `0.05 / 0.95` through balanced blends to
almost `0.95 / 0.05`.

For `k` variations, the largest possible share is:

```text
1 - (k - 1) * MinimumVariationShare
```

Thus a three-variation blend with a `0.05` floor can approach
`0.90 / 0.05 / 0.05`. Exact zeros are deliberately excluded: a zero-weight
variation is semantically indistinguishable from not selecting that variation.

### Sampling rule

For a transform with `k` selected variation names:

1. If `k = 1`, assign the only variation weight `1.0`. This is the only valid
   one-part mixture and is not a sampling defect.
2. For `k >= 2`, obtain the next `(k - 1)` dimensional low-discrepancy point
   from that session's coverage sequence for `k` variations.
3. Sort those coordinates and take consecutive differences between `0`, the
   sorted coordinates, and `1`. The result is a uniformly distributed point
   `q1 ... qk` on the `k`-part simplex.
4. Apply the floor:

   ```text
   wi = MinimumVariationShare + (1 - k * MinimumVariationShare) * qi
   ```

5. Deterministically shuffle the `wi` values with the candidate's seeded
   random source before matching them to the already randomly selected
   variation names.

The final weights are finite, positive, sum to one, and represent the actual
visual proportions used by the renderer.

### Why this covers the desired ratios

For two variations, the simplex is a line. The sampler's first coordinate is
uniformly dispersed over `[0, 1]`, so each final share is uniformly dispersed
over `[0.05, 0.95]`. It includes highly asymmetric blends as often as balanced
regions over a complete coverage cycle.

For three or more variations, no independent flat histogram per weight column
is possible because every component is coupled to all other components by the
sum constraint. The neutral analogue is **uniform coverage of the full
triangle/tetrahedron/simplex**. This samples dominant, balanced, and mixed
regions without treating any feasible mixture as structurally privileged.

## Finite-batch coverage sequence

Independent random simplex draws have the right probability law but can leave
gaps in a finite experiment. Use a seed-scrambled low-discrepancy sequence for
the simplex coordinates instead:

```text
SessionVariationCoverage
  ├─ one deterministic counter for k = 2
  ├─ one deterministic counter for k = 3
  ├─ one deterministic counter for k = 4
  └─ one deterministic counter for k = 5
       └─ scrambled Halton / radical-inverse coordinates
            └─ sorted-cut transformation into the simplex
```

- For `k = 2`, the base-2 radical-inverse sequence fills the interval by
  repeatedly subdividing its largest gaps.
- For `k = 3` through `5`, use the first `k - 1` coprime Halton bases, scramble
  each dimension once from the session seed, then transform them to the
  simplex using the sorted-cut rule.
- The session seed changes the ordering and phase; it does not change the
  uniform coverage target.

This is quasi-random sampling: it is still diverse, but has much lower
clustering than independent random draws. It is the appropriate mechanism for
the stated goal of covering a batch rather than merely covering the space in
expectation.

## Required pipeline change for determinism

The existing pipeline creates genomes in parallel render workers. A shared
per-`k` coverage counter there would depend on worker timing, breaking the
fixed-seed reproducibility invariant.

Generate each genome sequentially in the existing producer, where candidates
already have stable sequence numbers. Enqueue a bounded `RenderJob` containing
the finished validated genome; render workers continue to perform only CPU
rendering and archival work.

```text
Producer, in candidate-sequence order
  -> SessionVariationCoverage
  -> FlameGenerator.Generate(seed, settings, coverage context)
  -> bounded queue of RenderJob(sequence, genome)
  -> parallel render workers
```

This preserves bounded memory, makes the coverage counters deterministic, and
keeps rendering parallel and cancellable.

For direct, one-off calls to `FlameGenerator.Generate`, derive a deterministic
coverage index from the supplied seed. That retains repeatability outside a
render session, while finite-batch coverage is guaranteed only for a managed
session with its sequence context.

## UI and settings changes

- Remove `Variation blend dominance`; its old meaning is superseded.
- Add `Minimum variation share`, defaulting to the proposed `0.05`.
- Explain that multi-variation transforms now use broad simplex coverage and
  that one-variation transforms necessarily have a weight of one.
- Record a `variation_weight_sampler_version` and the applied minimum share in
  each future session's generator-profile metadata. Historical flames remain
  valid source files but are clearly associated with the former algorithm.

## Analysis changes

The focused report must analyze weights conditional on variation count:

| Selected variation count | Correct diagnostic |
|---:|---|
| 1 | Report the count only; the weight is exactly 1 by definition. |
| 2 | Histogram and goodness-of-fit over `[minimumShare, 1 - minimumShare]`; use one share per transform, because the second is its complement. |
| 3 | Ternary coverage plot plus triangular-cell occupancy/discrepancy. Do not call an individual marginal histogram “uniform.” |
| 4–5 | Simplex-cell occupancy and marginal quantiles; retain raw vectors for reproducible recomputation. |

Variation-name selection remains a separate categorical distribution. Its
occurrence analysis must not be conflated with the relative-weight sampler.

## Alternatives rejected

| Alternative | Why it is rejected |
|---|---|
| Existing independent raw weights followed by normalization | Artificially restricts ratios and favours equal blends. |
| Unnormalized storage of the same raw values | The renderer normalizes the total, so rendered geometry would remain unchanged. |
| Independent uniform final weights | Invalid for `k >= 3`; independent values cannot also sum to one. |
| IID uniform-simplex draws | Correct in expectation but does not meet the finite-batch coverage goal as well as the proposed low-discrepancy sequence. |
| Discrete lattice vertices only | Covers a grid but creates visible quantization and misses continuous interior blends. |
| Removing renderer normalization | Changes flame semantics rather than solving relative-mixture coverage. |

## Acceptance criteria

1. The former normalized-raw algorithm and `VariationBlendDominance` are no
   longer used by new generation.
2. For every `k >= 2`, every generated vector is finite, positive, sums to one
   within floating-point tolerance, and respects the configured floor.
3. With `k = 2` and a floor of `0.05`, a session visibly covers every interval
   across `0.05–0.95`, including values near both extremes.
4. With `k = 3`, generated vectors occupy the full valid triangle rather than
   clustering around its centroid; the report demonstrates this with cell
   coverage, not a misleading one-dimensional histogram.
5. Across a session, no variation name has a systematic advantage in assigned
   share because the simplex vector is shuffled before name assignment.
6. The same session seed, candidate limit, generator settings, and worker
   count produce the same genomes and pixels.
7. Render queues remain bounded, and pause/stop behavior remains unchanged.
8. New session metadata records the sampler version and minimum share.

## Implementation order after approval

1. Introduce the sampler, setting, validation, and focused unit tests.
2. Move deterministic genome generation into the render producer and preserve
   the bounded render queue.
3. Add the generator-window control and remove the obsolete dominance slider.
4. Add generator-profile metadata to each session.
5. Update the analysis tool for conditional simplex diagnostics.
6. Render a new fixed-seed reference batch and compare its coverage with Pass
   1 before using it for DINOv2-guided adaptation.

## Historical approval record

Approve or adjust these concrete choices before implementation:

1. Replace the old sampler completely; do not retain a legacy UI or generation
   mode.
2. Use `MinimumVariationShare = 0.05` for the first new reference batch.
3. Use the proposed low-discrepancy, seed-scrambled simplex coverage sequence
   rather than independent random simplex draws.
4. Make sequential genome generation in the producer acceptable in exchange
   for deterministic batch coverage while retaining parallel rendering.
