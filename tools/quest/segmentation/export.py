#!/usr/bin/env python3
"""Export and numerically validate a candidate; never modify production assets.

See NOTICE.md for Apache-2.0 source/model provenance and adaptation details.
Semantic failures are preserved in semantic-report.json without suppressing the
independent Unity import/numerical candidate package.
"""
from __future__ import annotations

import argparse
import importlib.metadata
import json
import platform
import sys
import time
from pathlib import Path

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import torch

from architecture import FixedMaskDecoder, make_model
from semantics import (BBOX_IOU, CONFIDENCE, MASK_IOU, MAX_INSTANCES, NMS_IOU, decoder_inputs,
                       evaluate_photo, preprocess, restore_masks, select_instances)
from sources import DIRECTORY, fetch_photos, fetch_sources, sha256

REPO = DIRECTORY.parents[2]
RAW_NAMES = ['class_logits', 'box_distances', 'mask_kernels', 'mask_features']
ATOL, RTOL = 0.01, 0.001
# Audited against the primary 2.6.1 documentation, not proof of actual import.
UNITY_OPS = set('Add Concat Constant Conv GlobalAveragePool HardSigmoid MaxPool Mul Relu Reshape Resize Shape Sigmoid Slice Transpose ConstantOfShape Div Equal Expand MatMul Split Sub Unsqueeze Where'.split())


def write_json(path: Path, payload) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, allow_nan=False) + '\n')


def tensor_record(output: Path, relative: str, name: str, value: np.ndarray) -> dict:
    value = np.asarray(value, dtype='<f4', order='C')
    if not np.isfinite(value).all():
        raise ValueError(f'Non-finite reference tensor: {name}')
    path = output / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value.tobytes(order='C'))
    return dict(name=name, file=relative, sha256=sha256(path), shape=list(value.shape),
                dtype='float32-le', byteCount=path.stat().st_size)


def compare(reference: np.ndarray, actual: np.ndarray) -> dict:
    if reference.shape != actual.shape:
        return dict(passed=False, reason='shape mismatch', expectedShape=list(reference.shape), actualShape=list(actual.shape))
    finite = bool(np.isfinite(reference).all() and np.isfinite(actual).all())
    if not finite:
        return dict(passed=False, finite=False, reason='non-finite values')
    delta = np.abs(reference.astype(np.float64) - actual.astype(np.float64))
    allowed = ATOL + RTOL * np.abs(reference)
    return dict(passed=bool(np.all(delta <= allowed)), finite=True,
                maximumAbsoluteError=float(delta.max()), meanAbsoluteError=float(delta.mean()),
                maximumRelativeError=float(np.max(delta / np.maximum(np.abs(reference), 1e-12))),
                mismatchCount=int(np.count_nonzero(delta > allowed)),
                worstFlatIndex=int(delta.argmax()), atol=ATOL, rtol=RTOL)


def normalize_attributes(path: Path) -> None:
    """Strip unused/default attributes documented unsupported by Unity 2.6.1."""
    model = onnx.load(path)
    for node in model.graph.node:
        kept = []
        for attribute in node.attribute:
            value = onnx.helper.get_attribute_value(attribute)
            if node.op_type == 'MaxPool' and attribute.name == 'ceil_mode' and value == 0:
                continue
            if node.op_type == 'MaxPool' and attribute.name == 'dilations' and all(x == 1 for x in value):
                continue
            if node.op_type == 'Resize' and attribute.name == 'cubic_coeff_a':
                mode = next(onnx.helper.get_attribute_value(a) for a in node.attribute if a.name == 'mode')
                if mode not in (b'nearest', b'linear'):
                    raise ValueError('Cannot discard cubic coefficient for cubic resize')
                continue
            kept.append(attribute)
        del node.attribute[:]
        node.attribute.extend(kept)
    onnx.checker.check_model(model)
    onnx.save(model, path)


def graph_record(path: Path, output: Path) -> dict:
    graph = onnx.load(path)
    onnx.checker.check_model(graph)
    operators = sorted({node.op_type for node in graph.graph.node})
    domains = sorted({node.domain for node in graph.graph.node} - {''})
    return dict(file=str(path.relative_to(output)), sha256=sha256(path), bytes=path.stat().st_size,
                opset=15, operators=operators, customDomains=domains,
                undocumentedOperatorNames=sorted(set(operators) - UNITY_OPS))


def worker(path: Path) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    return ort.InferenceSession(str(path), sess_options=options, providers=['CPUExecutionProvider'])


def model_manifest_record(output: Path, case: str, kind: str, graph: dict,
                          inputs: dict[str, np.ndarray], outputs: dict[str, np.ndarray], **extra) -> dict:
    return dict(name=f'{kind}-coco-{case}', onnx=dict(file=graph['file'], sha256=graph['sha256']),
                inputs=[tensor_record(output, f'tensors/{case}/{kind}-input-{name}.f32', name, value) for name, value in inputs.items()],
                outputs=[tensor_record(output, f'tensors/{case}/{kind}-output-{name}.f32', name, value) for name, value in outputs.items()],
                **extra)


def validate_fixture_contract(fixtures: dict, regression: dict) -> None:
    known = {image['file_name']: image for image in regression['images']}
    if {fixture['fileName'] for fixture in fixtures['fixtures']} != set(known):
        raise ValueError('Regression must use exactly the existing detector photographs')
    for fixture in fixtures['fixtures']:
        image = known[fixture['fileName']]
        targets = [a for a in regression['annotations'] if a['image_id'] == image['id']]
        expected = {(e['className'], e['x'], e['y'], e['width'], e['height']) for e in fixture['expectations']}
        actual = {(a['class_name'], *a['bbox']) for a in targets}
        if actual != expected:
            raise ValueError('Regression annotations differ from the existing detector expectations')


def safe_output(path: Path) -> Path:
    path = path.resolve()
    # A CLI typo must never put generated weights/tensors inside a tracked Assets
    # or tools tree. Other explicitly chosen directories outside this repo are OK.
    if path == REPO or (REPO in path.parents and not path.is_relative_to(REPO / 'apps/quest/Logs')):
        raise ValueError('Inside this repository, --output must be under ignored apps/quest/Logs')
    if path == Path('/') or path == Path.home():
        raise ValueError('Choose a dedicated output directory')
    return path


def prepare_output(path: Path) -> Path:
    path = safe_output(path)
    if path.exists() and (not path.is_dir() or any(path.iterdir())):
        raise ValueError('Output must be a fresh/empty dedicated directory; preserve previous evidence and choose a new --output')
    path.mkdir(parents=True, exist_ok=True)
    return path


def run(args) -> int:
    output = safe_output(args.output)
    cache = safe_output(args.cache or output.parent / 'segmentation-cache')
    if cache == output or cache.is_relative_to(output):
        raise ValueError('Keep source/checkpoint/photo cache outside the candidate artifact directory')
    output = prepare_output(output)
    source_lock = fetch_sources(cache)
    fixtures_path = DIRECTORY.parent / 'fixtures/recognition-coco.json'
    fixtures = json.loads(fixtures_path.read_text())
    regression_path = DIRECTORY / 'coco-regression.json'
    regression = json.loads(regression_path.read_text())
    validate_fixture_contract(fixtures, regression)
    photos = fetch_photos(cache, fixtures)
    torch.set_num_threads(4)
    torch.manual_seed(1234)
    model, metadata = make_model(cache)
    class_names = list(metadata['dataset_meta'].get('classes', metadata['dataset_meta'].get('CLASSES')))
    if len(class_names) != 80:
        raise ValueError('Expected the official 80 COCO classes')
    first_image, _ = preprocess(photos[fixtures['fixtures'][0]['fileName']], args.size)
    raw_path = output / f'models/rtmdet-ins-tiny-raw-{args.size}.onnx'
    mask_path = output / f'models/rtmdet-ins-tiny-masks8-{args.size}.onnx'
    raw_path.parent.mkdir(parents=True, exist_ok=True)
    with torch.inference_mode():
        torch.onnx.export(model, torch.from_numpy(first_image), str(raw_path),
                          input_names=['image_bgr_normalized'], output_names=RAW_NAMES,
                          opset_version=15, dynamo=False, do_constant_folding=True)
    decoder = FixedMaskDecoder(args.size // 8, MAX_INSTANCES).eval()
    dummy = (torch.zeros(1, 8, args.size // 8, args.size // 8),
             torch.zeros(MAX_INSTANCES, 169), torch.full((MAX_INSTANCES, 4), 8.0))
    torch.onnx.export(decoder, dummy, str(mask_path),
                      input_names=['mask_features', 'kernels', 'priors_xyss'], output_names=['mask_logits'],
                      opset_version=15, dynamo=False, do_constant_folding=True)
    normalize_attributes(raw_path)
    normalize_attributes(mask_path)
    raw_graph, mask_graph = graph_record(raw_path, output), graph_record(mask_path, output)
    raw_worker, mask_worker = worker(raw_path), worker(mask_path)
    reference_description = 'Pinned official MMDetection backbone/neck/head forward class bodies with narrow inference-only Torch ConvModule/BatchNorm adapters; not a full independently installed MMDetection pipeline.'
    manifest = dict(schemaVersion=1, dtype='float32-le', candidateOnly=True, models=[],
                    scope='Static candidate importer/numerical fixtures; not production, simulator, or headset evidence.',
                    reference=reference_description, atol=ATOL, rtol=RTOL,
                    inputSize=args.size, classes=class_names)
    numerical = dict(passed=True, candidateOnly=True, reference=reference_description,
                     unityImportTested=False, questExecutionTested=False, cases=[],
                     graphs=[raw_graph, mask_graph])
    semantic = dict(passed=False, scope=regression['scope'], candidateOnly=True,
                    confidence=CONFIDENCE, nmsIou=NMS_IOU, minimumBboxIou=BBOX_IOU,
                    minimumMaskIou=MASK_IOU, maximumInstances=MAX_INSTANCES,
                    segmentationThreshold=0.5, photos=[],
                    annotationSource=regression['annotationSource'],
                    annotationFileSha256=regression['annotationFileSha256'],
                    annotationSubsetSha256=sha256(regression_path),
                    maskAssembly='Bilinear input-size upsample; crop recorded bottom/right padding; restore exact original dimensions; logit>0. No bbox-shaped mask fill.')
    known_images = {image['file_name']: image for image in regression['images']}
    for fixture in fixtures['fixtures']:
        filename = fixture['fileName']
        image_info = known_images[filename]
        case = str(image_info['id'])
        image, transform = preprocess(photos[filename], args.size)
        if (transform['width'], transform['height']) != (image_info['width'], image_info['height']):
            raise ValueError('Pinned photograph dimensions disagree with annotation metadata')
        with torch.inference_mode():
            reference = [x.detach().numpy() for x in model(torch.from_numpy(image))]
        begin = time.perf_counter()
        actual = raw_worker.run(None, {'image_bgr_normalized': image})
        raw_ms = (time.perf_counter() - begin) * 1000
        raw_checks = {name: compare(ref, value) for name, ref, value in zip(RAW_NAMES, reference, actual)}
        manifest['models'].append(model_manifest_record(output, case, 'raw', raw_graph,
            {'image_bgr_normalized': image}, dict(zip(RAW_NAMES, reference)),
            preprocessing=transform, sourcePhoto=dict(fileName=filename, sha256=fixture['sha256'])))
        # Unity decoder test inputs are exact frozen Torch outputs/selection;
        # expected masks are the official grouped-convolution implementation.
        reference_selection = select_instances(reference, transform, class_names)
        reference_feed = decoder_inputs(reference, reference_selection, args.size)
        with torch.inference_mode():
            grouped = model.bbox_head._mask_predict_by_feat_single(
                torch.from_numpy(reference_feed['mask_features']),
                torch.from_numpy(reference_feed['kernels']),
                torch.from_numpy(reference_feed['priors_xyss'])).numpy()
            matrix = decoder(*(torch.from_numpy(reference_feed[name]) for name in ('mask_features', 'kernels', 'priors_xyss'))).numpy()
        imported_masks = mask_worker.run(None, reference_feed)[0]
        decoder_checks = dict(groupedToMatmul=compare(grouped, matrix), torchToOnnx=compare(grouped, imported_masks))
        manifest['models'].append(model_manifest_record(output, case, 'masks8', mask_graph,
            reference_feed, {'mask_logits': grouped}, validCount=len(reference_selection),
            selected=reference_selection, sourcePhoto=dict(fileName=filename, sha256=fixture['sha256'])))
        case_passed = all(check['passed'] for check in [*raw_checks.values(), *decoder_checks.values()])
        numerical['cases'].append(dict(fileName=filename, raw=raw_checks, masks=decoder_checks,
                                       passed=case_passed, linuxCpuRawMilliseconds=raw_ms,
                                       timingCaveat='Shared Linux CPU, 4 threads, cold fixture run; NOT Quest performance'))
        numerical['passed'] &= case_passed
        # Semantic result comes from the ONNX outputs all the way through: no
        # expected-class preference, GT input or Torch-output substitution.
        try:
            selection = select_instances(actual, transform, class_names)
            feed = decoder_inputs(actual, selection, args.size)
            logits = mask_worker.run(None, feed)[0]
            masks = restore_masks(logits, transform)
            targets = [a for a in regression['annotations'] if a['image_id'] == image_info['id']]
            photo_result = evaluate_photo(image_info, targets, selection, masks)
            photo_result['preprocessing'] = transform
            semantic['photos'].append(photo_result)
        except Exception as error:
            semantic['photos'].append(dict(fileName=filename, imageId=image_info['id'], passed=False,
                                            error=f'{type(error).__name__}: {error}'))
        print(f'{filename}: numerical={case_passed}, semantic={semantic["photos"][-1]["passed"]}', flush=True)
    semantic['passed'] = len(semantic['photos']) == 2 and all(photo['passed'] for photo in semantic['photos'])
    # Independently vary pixels to catch an accidental photo-specialized trace.
    random_input = np.random.default_rng(1234).standard_normal(first_image.shape).astype(np.float32)
    with torch.inference_mode():
        random_reference = [x.numpy() for x in model(torch.from_numpy(random_input))]
    random_actual = raw_worker.run(None, {'image_bgr_normalized': random_input})
    numerical['randomInput'] = {name: compare(ref, value) for name, ref, value in zip(RAW_NAMES, random_reference, random_actual)}
    numerical['passed'] &= all(check['passed'] for check in numerical['randomInput'].values())
    numerical['passed'] &= all(not graph['customDomains'] and not graph['undocumentedOperatorNames'] for graph in (raw_graph, mask_graph))
    scripts = ['export.py', 'architecture.py', 'semantics.py', 'sources.py', 'sources.lock.json', 'requirements.txt', 'coco-regression.json']
    provenance = dict(candidateOnly=True, sourceLock=source_lock,
                      scriptSha256={name: sha256(DIRECTORY / name) for name in scripts},
                      photoSources=fixtures, fixtureManifestSha256=sha256(fixtures_path),
                      annotationSubset=regression, strictCheckpointKeys=len(model.state_dict()),
                      identicalAliasedCheckpointEntries=model.checked_state_alias_count,
                      uniqueParameters=sum(p.numel() for p in model.parameters()),
                      versions=dict(python=platform.python_version(), torch=torch.__version__, onnx=onnx.__version__,
                                    onnxruntime=ort.__version__, numpy=np.__version__, opencv=cv2.__version__),
                      installedDistributions=dict(sorted((distribution.metadata['Name'], distribution.version)
                                                        for distribution in importlib.metadata.distributions())),
                      modelLicense='Apache-2.0, per official project and maintainer clarification; see NOTICE.md',
                      modelLicenseClarification='https://github.com/open-mmlab/mmdetection/discussions/10006',
                      reference=reference_description,
                      metadataCaveat='Tiny checkpoint embeds inherited small-model config; actual tensor dimensions and official tiny config determine the strictly checked architecture.',
                      operatorDocumentation='https://docs.unity3d.com/Packages/com.unity.ai.inference@2.6/manual/supported-operators.html')
    (output / 'checkpoint-config-advisory.py').write_text(metadata['cfg'])
    for name in ('NOTICE.md', 'LICENSE.Apache-2.0.txt'):
        (output / name).write_bytes((DIRECTORY / name).read_bytes())
    write_json(output / 'numerical-report.json', numerical)
    write_json(output / 'semantic-report.json', semantic)
    write_json(output / 'provenance.json', provenance)
    manifest['reports'] = {key: dict(file=filename, sha256=sha256(output / filename))
                           for key, filename in [('numerical', 'numerical-report.json'),
                                                 ('semantic', 'semantic-report.json'),
                                                 ('provenance', 'provenance.json')]}
    write_json(output / 'manifest.json', manifest)
    print(f'Candidate: {output}\nManifest SHA256: {sha256(output / "manifest.json")}\n'
          f'Numerical passed: {numerical["passed"]}; semantic regression passed: {semantic["passed"]}', flush=True)
    return 0 if numerical['passed'] else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=REPO / 'apps/quest/Logs/cli/segmentation-candidate')
    parser.add_argument('--cache', type=Path, help='Verified source/checkpoint/photo cache outside the candidate directory')
    parser.add_argument('--size', type=int, choices=(320, 640), default=320)
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 12):
        parser.error('Use the isolated Python 3.12 environment documented in README.md')
    if torch.__version__ != '2.6.0+cpu' or torch.version.cuda is not None:
        parser.error('Use pinned torch==2.6.0+cpu; CUDA/unpinned Torch is not the candidate export environment')
    return run(args)


if __name__ == '__main__':
    raise SystemExit(main())
