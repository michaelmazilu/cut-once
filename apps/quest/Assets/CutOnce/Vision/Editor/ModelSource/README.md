# Pinned recognition-model source

This editor-only directory contains provenance and licensing for the explicit
FP32 recognition diagnostic candidate. The downloaded ONNX is not committed and
is never included in an Android player. Normal setup/check/build does not fetch
or replace any model.

`tools/quest/download-recognition-model.mjs` downloads the original YOLOv9t ONNX
from Meta's Passthrough Camera API sample at commit
`b48d61dd43ffafbd2e2de3ae63fa3719685391c3`. Its SHA-256 is
`189ed26d273423b02243ce7f0a7795acc7da8049f879f8b73a0684d25d1138fd`.
The exact URL, size, stable importer GUID and runtime target are recorded in
`tools/quest/fixtures/recognition-model.json`.

`CutOnce.Vision.Editor.ExportRecognitionModel.Run` reproduces Meta's output graph
(corner boxes, winning class, maximum score) without weight quantization. It
backs up the original bundled model under `Logs/cli/model-conversion`, writes the
candidate to the same runtime asset path, and verifies that the existing `.meta`
file was not changed. Conversion alone is not recognition or headset proof.

Meta's sample README explicitly licenses its `SentisInference/Model` files under
the upstream YOLO MIT license. That notice is reproduced in `MODEL-LICENSE.txt`.

- Source repository: https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples
- Model license statement: https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples#license
- Upstream model license: https://github.com/MultimediaTechLab/YOLO/blob/462b87edcaa795663ecb6450abf3e84b8d3aa823/LICENSE
