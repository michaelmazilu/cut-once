# Editor-only segmentation candidate imports

`SegmentationProof.Run` imports checksum-verified, content-addressed candidate ONNX files here only when explicitly invoked in an isolated batch Editor. Generated `.onnx` and `.onnx.meta` files are ignored; they are not production resources and must not be committed or added to a build scene.

The proof compares every raw head and fixed-eight-mask decoder output against the pinned artifact's official Torch reference tensors on requested CPU and GPUCompute backends. It does not change production recognition, validate headset performance, or demonstrate live object wrapping. The complete report is written to `Logs/cli/segmentation-proof/report.json`.

Run with graphics enabled and without `-quit`; Editor updates advance scheduling and asynchronous readback. Tolerances remain fixed at absolute `0.01` plus relative `0.001 * abs(reference)` per element. A failed or unsupported backend is a failed gate, not a skipped pass.
