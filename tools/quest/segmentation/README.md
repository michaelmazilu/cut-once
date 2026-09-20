# Segmentation candidate export and regression checks

This Linux-side tool produces an **experimental RTMDet-Ins-tiny candidate** for
Unity importer tests. It does not change the production detector, Unity packages,
scenes or settings. Generated weights, downloaded source/photos and numerical
tensors stay in ignored `apps/quest/Logs/` directories, never in Git.

## Run

Use Python 3.12 in an isolated environment. The pinned `torch==2.6.0+cpu` requirement
uses the official CPU wheel index and does not install CUDA. These dependencies
belong on the Linux export worker, **not on the Mac Unity runner**.

```sh
python3.12 -m venv apps/quest/Logs/cli/segmentation-venv
apps/quest/Logs/cli/segmentation-venv/bin/python -m pip install -r tools/quest/segmentation/requirements.txt
apps/quest/Logs/cli/segmentation-venv/bin/python -m unittest discover -s tools/quest/segmentation/tests -v
apps/quest/Logs/cli/segmentation-venv/bin/python tools/quest/segmentation/export.py --size 320 --output apps/quest/Logs/cli/segmentation-candidate
```

`--size` defaults to 320; 640 is an explicit comparison option. Preserve each
resolution's evidence in a separate output directory. `--output` defaults to the
directory shown above. Source/checkpoint/photo cache defaults to its sibling
`segmentation-cache`; `--cache` can choose another dedicated location. A corrupt
existing cache entry fails closed instead of silently being trusted or replaced.
Every official source file, model checkpoint and photo is checked by SHA256.
Outputs must be fresh/empty directories; reruns cannot mix new files with older
evidence. Direct dependencies are version-pinned, not a byte-reproducible complete
environment lock. The provenance report records all installed distribution versions.

The export exits nonzero for acquisition/export/numerical failures. A **semantic
regression failure does not suppress the numerical candidate package**: it stays
in `semantic-report.json` as `passed:false`. The consuming workflow must reject
that result separately after preserving independent Unity compatibility evidence.
Never lower thresholds or omit a photograph to make a run pass.

## Package contract

`manifest.json` has `schemaVersion:1`, `candidateOnly:true`, `dtype:"float32-le"`
and four `models` records: raw detector and eight-mask decoder for each of the two
unchanged COCO photographs. Two ONNX files are shared by those records.

Each record contains `name`, `onnx:{file,sha256}`, `inputs` and `outputs`. Each
tensor entry has `name`, `file`, `sha256`, `shape`, `dtype` and `byteCount`. All paths
are relative to the manifest. `.f32` files are C-contiguous raw IEEE754 float32,
little-endian, with **no header**. Expected outputs come from the official PyTorch
source-forward body; mask expected outputs come from the official grouped-conv
implementation, not the exported decoder comparing against itself.
`reports.semantic`, `reports.numerical` and `reports.provenance` each bind a report
sidecar's relative `file` and `sha256`. A workflow gate must verify those hashes
against the pinned manifest before trusting any `passed` value.

For 320 input:

| Graph | Inputs | Outputs |
| --- | --- | --- |
| Raw | `image_bgr_normalized [1,3,320,320]` | `class_logits [1,2100,80]`, `box_distances [1,2100,4]`, `mask_kernels [1,2100,169]`, `mask_features [1,8,40,40]` |
| Masks8 | `mask_features [1,8,40,40]`, `kernels [8,169]`, `priors_xyss [8,4]` | `mask_logits [8,40,40]` |

At 640, the raw anchor count is 8400 and mask spatial dimensions are 80×80.
All axes are static; batch size is one. The decoder always has eight slots and
each fixture record states `validCount`; padded slots are ignored semantically.

Other outputs:

- `numerical-report.json`: source-Torch versus ONNX Runtime, decoder equivalence,
  deterministic random-input check, operator audit, and CPU diagnostic timings.
- `semantic-report.json`: all four fixed bottle/table bbox and mask IoUs, misses,
  confidence scores and all selected detections.
- `provenance.json`: source lock, source/photo/annotation/script hashes, package
  versions, model identity, licensing links and reference limitations.
- `NOTICE.md`, `LICENSE.Apache-2.0.txt`: source/model provenance and notices.
- `checkpoint-config-advisory.py`: original checkpoint metadata, **not executable
  configuration or architecture authority**; see the tiny/small metadata caveat
  in the notice.

## Preprocessing and mask interpretation

Images are decoded as uint8 **BGR** using OpenCV, resized with `INTER_LINEAR` and
MMCV-compatible `int(dimension*scale + .5)` rounding. Place the resized photograph
at the input's top-left and pad only bottom/right with 114. Normalize pixel units
by mean `[103.53,116.28,123.675]` and std `[57.375,57.12,58.395]`, then NCHW. This is
not the current YOLO preprocessing. Exact per-photo scales and padding are saved.

Grid priors have offset zero at strides 8,16,32. Box distances are already in input
pixels: `(x-left,y-top,x+right,y+bottom)`. Filter all class scores at **0.4** and use
class-aware NMS at **0.5**. Keep the eight highest scoring survivors globally;
bottle/table targets get no preferred slots. This is an explicit candidate budget,
not the original model-zoo threshold/max-detection settings.

Each selected 169-value kernel splits into weights `[80,64,8]`, then biases
`[8,8,1]`. Relative coordinates are
`(selectedPriorXY - stride8MaskGridXY)/(selectedPriorStride*8)`; concatenate them
before eight shared feature channels. Apply 10→8/ReLU, 8→8/ReLU, 8→1 layers. The
exported decoder expresses this with ordinary batched MatMul, not dynamic-group
convolution. Outputs are raw mask logits. Bilinearly upsample to input dimensions,
remove recorded padding, restore exact original dimensions, then threshold at
logit zero (probability 0.5). No rectangular mask fill is used.

## What these tests establish—and do not establish

The same two pre-existing COCO photos are mandatory; hashes and expected boxes
come from `../fixtures/recognition-coco.json`. `coco-regression.json` preserves
their four original COCO segmentation polygons and annotation IDs, with the full
annotation-file hash. Ground-truth masks are decoded using pycocotools, not a
hand-drawn approximation. Each expected object requires both **bbox IoU≥0.4 and
mask IoU≥0.5**. Missing expected objects are failures. This is a small regression
gate, **not COCO AP, general object-recognition accuracy or headset evidence**.

Numerical tolerance is fixed at `abs(error) <= .01 + .001*abs(reference)`. The
candidate graphs use opset15 and documented Unity Inference2.6.1 operator names;
that audit is **not proof of Unity import or execution**. Mac-side CPU and
GPUCompute comparison is the next independent gate, without PyTorch on the Mac.
Linux CPU timings do not estimate Quest GPU speed or satisfy the 72fps budget.

The reference uses pinned official inference class bodies with narrow Torch
ConvModule/BatchNorm adapters rather than a complete installed MMDetection stack.
Weights load strictly, shared-state aliases are checked, and the mask decoder is
compared to the original grouped-convolution implementation. Licensing and
modification details are in [NOTICE.md](NOTICE.md).

Production integration still needs RGB timestamp/pose/intrinsics/sensor-crop
metadata, per-eye world-point-to-captured-RGB projection, tracking and stale-mask
rejection, moving-object/disocclusion handling, and real Quest memory/frame-time
measurements. A passing pair of photographs cannot prove clean wrapping of every
object or reconstruction of its unseen backside.
