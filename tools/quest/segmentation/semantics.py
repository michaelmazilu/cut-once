"""Fixed-photo segmentation regression, not a dataset-wide accuracy evaluation."""
from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np
import torch
import torch.nn.functional as F
from pycocotools import mask as coco_mask

CONFIDENCE = 0.4
NMS_IOU = 0.5
BBOX_IOU = 0.4  # Existing recognition-regression contract.
MASK_IOU = 0.5  # New instance-mask regression gate.
MAX_INSTANCES = 8


def preprocess(path: Path, size: int) -> tuple[np.ndarray, dict]:
    """OpenCV uint8 BGR resize, top-left alignment, 114 bottom/right padding.

    The resize rounding/interpolation and normalization match the pinned
    MMDetection test pipeline. Nothing uses Pillow or RGB/YOLO preprocessing.
    """
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f'Could not decode photograph: {path}')
    height, width = image.shape[:2]
    scale = min(size / width, size / height)
    resized_width, resized_height = int(width * scale + .5), int(height * scale + .5)
    resized = cv2.resize(image, (resized_width, resized_height), interpolation=cv2.INTER_LINEAR)
    padded = np.full((size, size, 3), 114, dtype=np.uint8)
    padded[:resized_height, :resized_width] = resized
    normalized = (padded.astype(np.float32) - np.array([103.53, 116.28, 123.675], np.float32)) / np.array([57.375, 57.12, 58.395], np.float32)
    tensor = np.ascontiguousarray(normalized.transpose(2, 0, 1)[None], dtype=np.float32)
    metadata = dict(width=width, height=height, resizedWidth=resized_width,
                    resizedHeight=resized_height, inputSize=size,
                    scaleX=resized_width / width, scaleY=resized_height / height,
                    padLeft=0, padTop=0, padRight=size - resized_width,
                    padBottom=size - resized_height, origin='top-left',
                    channelOrder='BGR', layout='NCHW', interpolation='OpenCV INTER_LINEAR',
                    mean=[103.53, 116.28, 123.675], std=[57.375, 57.12, 58.395])
    return tensor, metadata


def grid_priors(size: int) -> np.ndarray:
    levels = []
    for stride in (8, 16, 32):
        y, x = np.meshgrid(np.arange(size // stride), np.arange(size // stride), indexing='ij')
        xy = np.stack((x, y), axis=-1).reshape(-1, 2).astype(np.float32) * stride
        levels.append(np.concatenate((xy, np.full_like(xy, stride)), axis=1))
    return np.concatenate(levels)


def box_iou(a, b) -> float:
    a, b = np.asarray(a), np.asarray(b)
    intersection = np.maximum(np.minimum(a[2:], b[2:]) - np.maximum(a[:2], b[:2]), 0).prod()
    union = np.maximum(a[2:] - a[:2], 0).prod() + np.maximum(b[2:] - b[:2], 0).prod() - intersection
    return float(intersection / union) if union > 0 else 0.0


def select_instances(outputs: list[np.ndarray], metadata: dict, class_names: list[str]) -> list[dict]:
    """Apply fixed .4 confidence and class-aware .5 NMS to all class scores.

    Selection is global score order, never target-class aware. The eight-instance
    limit is the explicit candidate decoder budget and may cause a regression to
    fail; expected objects are not granted preferred slots.
    """
    logits, distances, _, _ = outputs
    scores = torch.sigmoid(torch.from_numpy(logits[0])).numpy()
    priors = grid_priors(metadata['inputSize'])
    boxes = np.concatenate((priors[:, :2] - distances[0, :, :2],
                            priors[:, :2] + distances[0, :, 2:]), axis=1)
    boxes[:, (0, 2)] = np.clip(boxes[:, (0, 2)], 0, metadata['resizedWidth'])
    boxes[:, (1, 3)] = np.clip(boxes[:, (1, 3)], 0, metadata['resizedHeight'])
    indices, labels = np.nonzero(scores >= CONFIDENCE)
    order = np.argsort(-scores[indices, labels], kind='stable')
    selected = []
    for position in order:
        index, label = int(indices[position]), int(labels[position])
        box = boxes[index]
        if box[2] <= box[0] or box[3] <= box[1]:
            continue
        if any(item['classId'] == label and box_iou(box, item['inputBoxXyxy']) > NMS_IOU for item in selected):
            continue
        original_box = box / np.array([metadata['scaleX'], metadata['scaleY'], metadata['scaleX'], metadata['scaleY']])
        selected.append(dict(anchor=index, classId=label, className=class_names[label],
                             score=float(scores[index, label]), inputBoxXyxy=box.tolist(),
                             boxXyxy=original_box.tolist()))
        if len(selected) == MAX_INSTANCES:
            break
    return selected


def decoder_inputs(outputs: list[np.ndarray], selected: list[dict], size: int) -> dict[str, np.ndarray]:
    priors = grid_priors(size)
    kernels = np.zeros((MAX_INSTANCES, 169), dtype=np.float32)
    selected_priors = np.full((MAX_INSTANCES, 4), 8, dtype=np.float32)
    selected_priors[:, :2] = 0
    for slot, item in enumerate(selected):
        kernels[slot] = outputs[2][0, item['anchor']]
        selected_priors[slot] = priors[item['anchor']]
    return dict(mask_features=np.ascontiguousarray(outputs[3]), kernels=kernels,
                priors_xyss=selected_priors)


def restore_masks(mask_logits: np.ndarray, metadata: dict) -> np.ndarray:
    """Invert the recorded resize/pad transform before thresholding logits at0.

    A selected mask is first upsampled to the full model input. Remove bottom/right
    padding, then resize the remaining rectangle to the exact source dimensions.
    Per-axis recorded scales avoid rounding/aspect-ratio drift on odd heights.
    """
    tensor = torch.from_numpy(mask_logits)[None]
    full = F.interpolate(tensor, size=(metadata['inputSize'], metadata['inputSize']),
                         mode='bilinear', align_corners=False)
    full = full[..., :metadata['resizedHeight'], :metadata['resizedWidth']]
    original = F.interpolate(full, size=(metadata['height'], metadata['width']),
                             mode='bilinear', align_corners=False)
    return original[0].numpy() > 0


def polygon_mask(annotation: dict, height: int, width: int) -> np.ndarray:
    if annotation['iscrowd'] != 0 or not isinstance(annotation['segmentation'], list):
        raise ValueError('Pinned regression expects non-crowd COCO polygons')
    rles = coco_mask.frPyObjects(annotation['segmentation'], height, width)
    return coco_mask.decode(coco_mask.merge(rles)).astype(bool)


def evaluate_photo(image: dict, annotations: list[dict], selected: list[dict], masks: np.ndarray) -> dict:
    checks = []
    for annotation in annotations:
        x, y, width, height = annotation['bbox']
        expected_box = [x, y, x + width, y + height]
        eligible = [(slot, item) for slot, item in enumerate(selected) if item['className'] == annotation['class_name']]
        eligible.sort(key=lambda pair: box_iou(pair[1]['boxXyxy'], expected_box), reverse=True)
        gt = polygon_mask(annotation, image['height'], image['width'])
        check = dict(annotationId=annotation['id'], className=annotation['class_name'],
                     expectedBoxXyxy=expected_box, groundTruthMaskPixels=int(gt.sum()),
                     minimumBoxIou=BBOX_IOU, minimumMaskIou=MASK_IOU,
                     bboxIou=0.0, maskIou=0.0, score=0.0, found=False, passed=False)
        if eligible:
            slot, prediction = eligible[0]
            predicted = masks[slot]
            union = np.logical_or(gt, predicted).sum()
            mask_iou = float(np.logical_and(gt, predicted).sum() / union) if union else 0.0
            bbox_iou = box_iou(prediction['boxXyxy'], expected_box)
            check.update(found=True, score=prediction['score'], predictionSlot=slot,
                         predictedBoxXyxy=prediction['boxXyxy'], predictedMaskPixels=int(predicted.sum()),
                         bboxIou=bbox_iou, maskIou=mask_iou,
                         passed=bbox_iou >= BBOX_IOU and mask_iou >= MASK_IOU)
        checks.append(check)
    return dict(fileName=image['file_name'], imageId=image['id'], checks=checks,
                selected=selected, passed=bool(checks) and all(check['passed'] for check in checks))
