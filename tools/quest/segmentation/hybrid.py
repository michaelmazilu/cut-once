#!/usr/bin/env python3
"""Experimental YOLO-authorized RTMDet mask estimation, never production promotion.

RTMDet retains its Apache-2.0 provenance in NOTICE.md. This separate architecture
does not change or relabel the failed standalone RTMDet recognition gate.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import re
import sys
import time

import numpy as np
import onnxruntime as ort
import torch

from export import prepare_output, validate_fixture_contract, write_json
from semantics import (MAX_INSTANCES, box_iou, decoder_inputs, evaluate_photo,
                       grid_priors, preprocess, restore_masks)
from sources import DIRECTORY, sha256

COCO_CLASSES = (
    'person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck',
    'boat', 'traffic light', 'fire hydrant', 'stop sign', 'parking meter', 'bench',
    'bird', 'cat', 'dog', 'horse', 'sheep', 'cow', 'elephant', 'bear', 'zebra',
    'giraffe', 'backpack', 'umbrella', 'handbag', 'tie', 'suitcase', 'frisbee',
    'skis', 'snowboard', 'sports ball', 'kite', 'baseball bat', 'baseball glove',
    'skateboard', 'surfboard', 'tennis racket', 'bottle', 'wine glass', 'cup',
    'fork', 'knife', 'spoon', 'bowl', 'banana', 'apple', 'sandwich', 'orange',
    'broccoli', 'carrot', 'hot dog', 'pizza', 'donut', 'cake', 'chair', 'couch',
    'potted plant', 'bed', 'dining table', 'toilet', 'tv', 'laptop', 'mouse',
    'remote', 'keyboard', 'cell phone', 'microwave', 'oven', 'toaster', 'sink',
    'refrigerator', 'book', 'clock', 'vase', 'scissors', 'teddy bear', 'hair drier',
    'toothbrush')
# Exact names emitted by the bundled labels plus YoloDetector.NormalizeClassName.
# These are explicit same-ID synonyms, not relabeling based on model predictions.
YOLO_CLASSES = tuple({3: 'motorbike', 4: 'aeroplane', 57: 'sofa',
                      62: 'TV / monitor'}.get(i, name) for i, name in enumerate(COCO_CLASSES))
RAW_NAMES = ('class_logits', 'box_distances', 'mask_kernels', 'mask_features')
JSON_LIMIT = 4 * 1024 * 1024
FILE_LIMIT = 64 * 1024 * 1024


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def finite_tree(value) -> None:
    if isinstance(value, float):
        require(math.isfinite(value), 'Non-finite JSON number')
    elif isinstance(value, dict):
        for item in value.values():
            finite_tree(item)
    elif isinstance(value, list):
        for item in value:
            finite_tree(item)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f'Duplicate JSON key: {key}')
        result[key] = value
    return result


def read_json(path: Path):
    require(path.is_file() and path.stat().st_size <= JSON_LIMIT, f'Missing/oversized JSON: {path}')
    value = json.loads(path.read_text(), object_pairs_hook=unique_object)
    finite_tree(value)
    return value


def verify_file(path: Path, expected: str, limit: int = FILE_LIMIT) -> Path:
    require(isinstance(expected, str) and re.fullmatch(r'[0-9a-f]{64}', expected) is not None,
            'Expected SHA256 must be supplied as 64 lowercase hexadecimal characters')
    require(path.is_file() and path.stat().st_size <= limit, f'Missing/oversized artifact: {path}')
    require(sha256(path) == expected, f'SHA256 mismatch: {path}')
    return path


def verified_record(root: Path, record: dict, limit: int = FILE_LIMIT) -> Path:
    relative = Path(record['file'])
    require(not relative.is_absolute() and '..' not in relative.parts, 'Artifact path must remain relative')
    target = (root / relative).resolve()
    require(target.is_relative_to(root.resolve()), 'Artifact symlink escapes candidate directory')
    return verify_file(target, record['sha256'], limit)


def finite_number(value, name: str) -> float:
    require(type(value) in (int, float) and math.isfinite(value), f'Invalid/non-finite {name}')
    return float(value)


def validate_detection(detection: dict, width: int, height: int, scanner_confidence: float) -> None:
    label = detection['classId']
    require(type(label) is int and 0 <= label < 80, 'Invalid YOLO class ID')
    require(detection['name'] == YOLO_CLASSES[label], 'YOLO class ID/name map mismatch')
    confidence = finite_number(detection['confidence'], 'YOLO confidence')
    require(0 <= confidence <= 1, 'YOLO confidence outside [0,1]')
    x, y, w, h = [finite_number(detection[k], k) for k in ('x', 'y', 'width', 'height')]
    require(w > 0 and h > 0 and x+w > 0 and y+h > 0 and x < width and y < height,
            'Invalid/non-intersecting YOLO box')
    require(detection['inputWidth'] == width and detection['inputHeight'] == height,
            'YOLO detection is not in original-image coordinates')
    require(detection['validBox'] is True, 'YOLO report contains invalid box')
    require(type(detection['acceptedByScanner']) is bool and
            detection['acceptedByScanner'] == (confidence >= scanner_confidence),
            'YOLO acceptedByScanner contradicts unchanged scanner confidence')


def validate_authority(report: dict, fixtures: dict, regression: dict) -> list[dict]:
    require(report['passed'] is True and report.get('error', '') == '', 'YOLO recognition prerequisite failed')
    threshold = finite_number(report['scannerConfidence'], 'scanner confidence')
    require(abs(threshold-.4) <= 1e-7 and report['nmsIoU'] == .5, 'Recognition thresholds changed')
    require(report['cornerBoxes'] is True, 'Unexpected production box convention')
    known = {f['fileName']: f for f in fixtures['fixtures']}
    images = {i['file_name']: i for i in regression['images']}
    photos = report['photos']
    names = [Path(photo['source']).name for photo in photos]
    require(len(photos) == 2 and len(set(names)) == 2 and set(names) == set(known),
            'Exactly the two fixed COCO photographs are required')
    for photo, name in zip(photos, names):
        image = images[name]
        require(photo['sha256'] == known[name]['sha256'], 'YOLO photo hash differs from pinned fixture')
        require(photo['width'] == image['width'] and photo['height'] == image['height'], 'Photo dimensions changed')
        require(photo['passed'] is True and photo['inferences'], 'YOLO photo prerequisite failed')
        inference = photo['inferences'][0]  # Never choose a later/better inference.
        require(inference['iteration'] == 1 and inference['passed'] is True and
                inference['eventDelivered'] is True and inference['timeoutDiscarded'] is False,
                'First YOLO inference did not pass/deliver')
        require(inference['eventInputWidth'] == photo['width'] and
                inference['eventInputHeight'] == photo['height'], 'YOLO event coordinate dimensions changed')
        detections = inference['detections']
        require(isinstance(detections, list) and len(detections) <= 1000, 'Invalid detection count')
        for detection in detections:
            validate_detection(detection, photo['width'], photo['height'], threshold)
        require(sum(d['acceptedByScanner'] for d in detections) <= MAX_INSTANCES,
                'More than eight accepted objects: reject rather than silently truncate')
    return photos


def validate_numerical(report: dict, expected_photos: set[str]) -> None:
    require(report['passed'] is True, 'RTMDet numerical prerequisite failed')
    cases = report['cases']
    require(len(cases) == 2 and {c['fileName'] for c in cases} == expected_photos,
            'Numerical prerequisite must cover both fixed photos')
    checks = list(report['randomInput'].values())
    require(set(report['randomInput']) == set(RAW_NAMES), 'Incomplete random numerical prerequisite')
    for case in cases:
        require(case['passed'] is True and set(case['raw']) == set(RAW_NAMES) and
                set(case['masks']) == {'groupedToMatmul', 'torchToOnnx'}, 'Incomplete/failed numerical case')
        checks.extend(case['raw'].values())
        checks.extend(case['masks'].values())
    for check in checks:
        require(check['passed'] is True and check['finite'] is True and check['mismatchCount'] == 0 and
                check['atol'] == .01 and check['rtol'] == .001, 'Numerical prerequisite or fixed tolerance failed')


def load_candidate(root: Path, expected_hash: str, fixtures: dict):
    manifest_path = verify_file(root / 'manifest.json', expected_hash, JSON_LIMIT)
    manifest = read_json(manifest_path)
    require(manifest['schemaVersion'] == 1 and manifest['candidateOnly'] is True and
            manifest['dtype'] == 'float32-le' and manifest['inputSize'] in (320, 640), 'Unexpected candidate contract')
    require(tuple(manifest['classes']) == COCO_CLASSES, 'Candidate COCO class map changed')
    expected = {f['fileName'] for f in fixtures['fixtures']}
    numerical = read_json(verified_record(root, manifest['reports']['numerical'], JSON_LIMIT))
    validate_numerical(numerical, expected)
    # Keep the original failed standalone result visible; do not gate on it or rewrite it.
    standalone = read_json(verified_record(root, manifest['reports']['semantic'], JSON_LIMIT))
    verified_record(root, manifest['reports']['provenance'], JSON_LIMIT)
    records = manifest['models']
    expected_names = {f'{kind}-coco-{int(Path(name).stem)}' for name in expected for kind in ('raw', 'masks8')}
    require(len(records) == 4 and {r['name'] for r in records} == expected_names, 'Missing/duplicate candidate cases')
    for record in records:
        fixture = next(f for f in fixtures['fixtures'] if f['fileName'] == record['sourcePhoto']['fileName'])
        require(record['sourcePhoto']['sha256'] == fixture['sha256'], 'Candidate source-photo hash changed')
        require(record['name'].endswith('-'+str(int(Path(fixture['fileName']).stem))), 'Candidate case/source mismatch')
        verified_record(root, record['onnx'])
    for kind in ('raw', 'masks8'):
        require(len({r['onnx']['sha256'] for r in records if r['name'].startswith(kind+'-')}) == 1,
                'Each photo must use the same graph, not a photo-specific model')
    return manifest, standalone


def validate_outputs(outputs: list[np.ndarray], size: int) -> None:
    anchors = sum((size//stride)**2 for stride in (8, 16, 32))
    shapes = [(1, anchors, 80), (1, anchors, 4), (1, anchors, 169), (1, 8, size//8, size//8)]
    require(len(outputs) == 4, 'Raw proposal output count changed')
    for value, shape in zip(outputs, shapes):
        require(value.shape == shape and value.dtype == np.float32 and np.isfinite(value).all(),
                'Malformed/non-finite proposal tensor')
    require((outputs[1] >= 0).all(), 'Negative proposal box distance')


def decode_boxes(outputs, metadata):
    priors = grid_priors(metadata['inputSize'])
    boxes = np.concatenate((priors[:, :2]-outputs[1][0, :, :2], priors[:, :2]+outputs[1][0, :, 2:]), axis=1)
    boxes[:, (0, 2)] = np.clip(boxes[:, (0, 2)], 0, metadata['resizedWidth'])
    boxes[:, (1, 3)] = np.clip(boxes[:, (1, 3)], 0, metadata['resizedHeight'])
    return boxes / np.array([metadata['scaleX'], metadata['scaleY'], metadata['scaleX'], metadata['scaleY']])


def associate(scores: np.ndarray, boxes: np.ndarray, detection: dict) -> dict:
    """No GT argument: winning class + IoU>=.5, descending score/anchor tie."""
    require(scores.ndim == 2 and scores.shape[1] == 80 and boxes.shape == (scores.shape[0], 4),
            'Proposal score/box shapes mismatch')
    require(np.isfinite(scores).all() and np.isfinite(boxes).all() and
            (scores >= 0).all() and (scores <= 1).all() and (boxes[:, 2:] >= boxes[:, :2]).all(),
            'Malformed/non-finite proposal scores or boxes')
    label = detection['classId']
    require(type(label) is int and 0 <= label < 80 and detection['name'] == YOLO_CLASSES[label],
            'YOLO class ID/name map mismatch')
    confidence = finite_number(detection['confidence'], 'YOLO confidence')
    require(.4 <= confidence <= 1 and detection['acceptedByScanner'] is True,
            'Mask proposal cannot authorize a rejected/subthreshold YOLO detection')
    x, y, w, h = [finite_number(detection[k], k) for k in ('x', 'y', 'width', 'height')]
    require(w > 0 and h > 0, 'Invalid YOLO dimensions')
    yolo_box = [x, y, x+w, y+h]
    winners = scores.argmax(axis=1)
    same_class = np.flatnonzero(winners == label)
    eligible = np.array([i for i in same_class if box_iou(boxes[i], yolo_box) >= .5], dtype=np.int64)
    eligible = eligible[np.lexsort((eligible, -scores[eligible, label]))]
    ranked = [dict(anchor=int(i), classScore=float(scores[i, label]), winningClassId=int(winners[i]),
                   winningClass=COCO_CLASSES[int(winners[i])], maximumClassProbability=float(scores[i].max()),
                   matchingBoxIou=box_iou(boxes[i], yolo_box), proposalBoxXyxy=boxes[i].tolist()) for i in eligible]
    return dict(classId=label, className=detection['name'], yoloConfidence=detection['confidence'],
                yoloBoxXyxy=yolo_box, sameWinningClassAnchorCount=int(same_class.size),
                eligibleAnchorCount=int(eligible.size), accepted=bool(ranked),
                maximumSameClassProbability=float(scores[:, label].max()) if len(scores) else 0.,
                rankedEligible=ranked, selection=ranked[0] if ranked else None,
                reason='' if ranked else 'No matching winning class with actual YOLO bbox IoU >= .5; no fallback')


def worker(path):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    return ort.InferenceSession(str(path), options, providers=['CPUExecutionProvider'])


def run(args, output: Path) -> dict:
    fixtures_path = DIRECTORY.parent / 'fixtures/recognition-coco.json'
    regression_path = DIRECTORY / 'coco-regression.json'
    fixtures, regression = read_json(fixtures_path), read_json(regression_path)
    validate_fixture_contract(fixtures, regression)
    authority = verify_file(args.yolo_report, args.yolo_report_sha256, JSON_LIMIT)
    yolo = read_json(authority)
    photos = validate_authority(yolo, fixtures, regression)
    root = args.candidate.resolve()
    manifest, standalone = load_candidate(root, args.candidate_manifest_sha256, fixtures)
    size = manifest['inputSize']
    torch.set_num_threads(4)
    records = {r['name']: r for r in manifest['models']}
    raw_path = verified_record(root, next(r['onnx'] for r in records.values() if r['name'].startswith('raw-')))
    decoder_path = verified_record(root, next(r['onnx'] for r in records.values() if r['name'].startswith('masks8-')))
    raw, decoder = worker(raw_path), worker(decoder_path)
    require([o.name for o in raw.get_outputs()] == list(RAW_NAMES), 'Unexpected raw graph output names/order')
    result = dict(passed=False, candidateOnly=True, exclusiveVisibleSurfaceValidated=False,
                  scope='Experimental two-model recorded-photo mask estimator; Linux only; no Unity/Quest integration or promotion.',
                  detectorAuthority=dict(file=str(authority), sha256=args.yolo_report_sha256,
                                         modelSha256=yolo['modelSha256'], confidence=.4, nmsIou=.5,
                                         selection='first inference; all scanner-accepted detections'),
                  candidateManifest=dict(file=str(root/'manifest.json'), sha256=args.candidate_manifest_sha256),
                  sourceReports=manifest['reports'], size=size,
                  originalStandaloneGateChanged=False, originalStandalonePassed=standalone['passed'],
                  associationPolicy=dict(winningClassMustMatch=True, minimumYoloProposalBoxIou=.5,
                      ranking='highest RTMDet class score; tie by ascending anchor index',
                      absoluteRtmdetConfidenceGate=None, groundTruthUsed=False, fallback=None,
                      oneToOneEnforced=False, maximumAcceptedObjectsPerPhoto=MAX_INSTANCES),
                  semanticGates=dict(minimumBboxIou=.4, minimumMaskIou=.5, allFourTargetsRequired=True),
                  annotationSource=dict(file=str(regression_path), sha256=sha256(regression_path), use='evaluation only'),
                  scriptSha256=sha256(Path(__file__)),
                  timingCaveat='Shared Linux CPU, four threads; excludes YOLO, model init, transfer and XR rendering. Not Quest performance.',
                  photos=[])
    for photo in photos:
        name = Path(photo['source']).name
        fixture = next(f for f in fixtures['fixtures'] if f['fileName'] == name)
        image_path = verify_file(args.photos / name, fixture['sha256'], 16*1024*1024)
        image, metadata = preprocess(image_path, size)
        recorded = records[f'raw-coco-{int(Path(name).stem)}']
        require(len(recorded['inputs']) == 1, 'Raw input count changed')
        tensor = recorded['inputs'][0]
        expected_bytes = 1*3*size*size*4
        tensor_path = verified_record(root, tensor)
        require(tensor['name'] == 'image_bgr_normalized' and tensor['shape'] == [1, 3, size, size] and
                tensor['dtype'] == 'float32-le' and tensor['byteCount'] == expected_bytes and
                tensor_path.stat().st_size == expected_bytes, 'Raw input tensor contract changed')
        expected_pixels = np.fromfile(tensor_path, dtype='<f4').reshape(1, 3, size, size)
        require(np.array_equal(image, expected_pixels) and metadata == recorded['preprocessing'],
                'Preprocessing differs from verified candidate')
        started = time.perf_counter()
        outputs = raw.run(None, {'image_bgr_normalized': image})
        raw_ms = (time.perf_counter()-started)*1000
        validate_outputs(outputs, size)
        scores = torch.sigmoid(torch.from_numpy(outputs[0][0])).numpy()
        boxes = decode_boxes(outputs, metadata)
        detections = [(i, d) for i, d in enumerate(photo['inferences'][0]['detections']) if d['acceptedByScanner']]
        started = time.perf_counter()
        associations = [dict(yoloIndex=i, **associate(scores, boxes, detection)) for i, detection in detections]
        association_ms = (time.perf_counter()-started)*1000
        selected = [dict(anchor=a['selection']['anchor'], classId=a['classId'], className=a['className'],
                         score=a['yoloConfidence'], boxXyxy=a['yoloBoxXyxy'], yoloIndex=a['yoloIndex'])
                    for a in associations if a['accepted']]
        require(len(selected) <= MAX_INSTANCES, 'Decoder capacity exceeded; no silent truncation')
        started = time.perf_counter()
        if selected:
            decoded = decoder.run(None, decoder_inputs(outputs, selected, size))
            require(len(decoded) == 1 and decoded[0].shape == (8, size//8, size//8) and
                    decoded[0].dtype == np.float32 and np.isfinite(decoded[0]).all(), 'Malformed/non-finite decoded masks')
            logits = decoded[0][:len(selected)]
            masks = restore_masks(logits, metadata)
        else:
            logits = np.empty((0, size//8, size//8), np.float32)
            masks = np.empty((0, metadata['height'], metadata['width']), bool)
        decode_ms = (time.perf_counter()-started)*1000
        info = next(i for i in regression['images'] if i['file_name'] == name)
        targets = [a for a in regression['annotations'] if a['image_id'] == info['id']]
        evaluation = evaluate_photo(info, targets, selected, masks)
        mask_path = output / (Path(name).stem+'-masks.npz')
        np.savez_compressed(mask_path, masks=masks, logits=logits,
                            yolo_indices=np.array([s['yoloIndex'] for s in selected]),
                            anchors=np.array([s['anchor'] for s in selected]))
        reused = {str(s['anchor']): [a['yoloIndex'] for a in selected if a['anchor'] == s['anchor']]
                  for s in selected if sum(a['anchor'] == s['anchor'] for a in selected) > 1}
        evaluation.update(associations=associations, allDetectionsAssociated=all(a['accepted'] for a in associations),
                          rejectedDetectionCount=sum(not a['accepted'] for a in associations), reusedAnchorAssignments=reused,
                          preprocessing=metadata, imageSha256=sha256(image_path),
                          timing=dict(rawInferenceMs=raw_ms, associationMs=association_ms, decodeAndRestoreMs=decode_ms,
                                      totalMaskEstimationMs=raw_ms+association_ms+decode_ms),
                          masks=dict(file=mask_path.name, sha256=sha256(mask_path), shape=list(masks.shape), dtype='bool',
                                     coordinateSpace='original image pixels; accepted associations only, no fallback'))
        result['photos'].append(evaluation)
        print(name, [(c['className'], round(c['maskIou'], 6), c['passed']) for c in evaluation['checks']], flush=True)
    result['semanticPassed'] = all(p['passed'] for p in result['photos'])
    result['allDetectionsAssociated'] = all(p['allDetectionsAssociated'] for p in result['photos'])
    result['passed'] = result['semanticPassed'] and result['allDetectionsAssociated']
    return result


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--yolo-report', type=Path, required=True)
    parser.add_argument('--yolo-report-sha256', required=True, help='Expected hash from the trusted captured run, not computed by this harness')
    parser.add_argument('--candidate', type=Path, required=True, help='Existing exported candidate directory')
    parser.add_argument('--candidate-manifest-sha256', required=True, help='Expected manifest hash from the trusted exporter handoff')
    parser.add_argument('--photos', type=Path, required=True, help='Directory containing exactly named pinned fixture JPEGs')
    parser.add_argument('--output', type=Path, required=True, help='Fresh output directory; inside repo only ignored apps/quest/Logs is allowed')
    args = parser.parse_args(argv)
    output = prepare_output(args.output)
    try:
        report = run(args, output)
    except Exception as error:
        write_json(output/'report.json', dict(passed=False, candidateOnly=True, exclusiveVisibleSurfaceValidated=False,
                                             scope='Experimental hybrid validation failure; no production promotion', error=str(error)))
        print(f'Hybrid validation failed: {error}', file=sys.stderr)
        return 1
    write_json(output/'report.json', report)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
