# Segmentation candidate provenance and modifications

Copyright (c) OpenMMLab. All rights reserved.

The official MMDetection source and RTMDet deployment path use Apache License
2.0. The original project notice is “Copyright 2018-2023 OpenMMLab. All rights
reserved.” Its complete license is retained without modification in
`LICENSE.Apache-2.0.txt` and copied into every export package.

Primary sources:

- [Pinned MMDetection project license](https://github.com/open-mmlab/mmdetection/blob/cfd5d3a985b0249de009b67d04f37263e11cdf3d/LICENSE).
- [Maintainer clarification of RTMDet deployment and trained-model licensing](https://github.com/open-mmlab/mmdetection/discussions/10006): RangiLyu explains the Apache-2.0 deployment path; hhaAndroid confirms the trained-model interpretation in the replies.
- [Official RTMDet model zoo](https://github.com/open-mmlab/mmdetection/blob/cfd5d3a985b0249de009b67d04f37263e11cdf3d/configs/rtmdet/README.md).

This is engineering provenance, not a legal guarantee about every use or about
rights in training photographs. The COCO regression photographs have separate
source/license links in `../fixtures/recognition-coco.json`; the regression
polygons retain COCO annotation provenance in `coco-regression.json`.

Export modifications: official inference class bodies are selected from pinned,
SHA256-verified source; registry decorators and package/training imports are not
used. Narrow PyTorch ConvModule, BatchNorm and base-module adapters replace mmcv
for inference only. The raw-head wrapper produces static tensors. An eight-mask
decoder replaces dynamic grouped convolutions with equivalent batched MatMul.
Unused/default ONNX attributes are removed without changing their semantics.
This is not a full independently installed MMDetection pipeline.

The official tiny checkpoint metadata contains an inherited small-model config.
Actual checkpoint tensor dimensions match the official tiny config, including
96-channel neck/head. The exporter verifies all state keys and shared-weight
aliases before loading; the embedded config is preserved as advisory provenance,
not used to construct a differently sized model.

No AGPL Ultralytics package, GPL YOLO segmentation implementation, package
upgrade, or production model replacement is part of this exporter.
