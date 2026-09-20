"""Small deterministic tests; none of these fixtures are visual/hardware proof."""
from __future__ import annotations

import hashlib
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(DIRECTORY))

from export import compare, prepare_output, safe_output, tensor_record, validate_fixture_contract
from semantics import (BBOX_IOU, CONFIDENCE, MASK_IOU, MAX_INSTANCES, NMS_IOU,
                       decoder_inputs, evaluate_photo, grid_priors, preprocess,
                       restore_masks, select_instances)
from sources import verified_download


class PreprocessingTests(unittest.TestCase):
    def test_opencv_bgr_normalization_and_bottom_right_padding(self):
        source = np.empty((461, 640, 3), dtype=np.uint8)
        source[:] = [10, 20, 30]
        with patch('semantics.cv2.imread', return_value=source):
            tensor, meta = preprocess(Path('not-read.jpg'), 320)
        self.assertEqual((1, 3, 320, 320), tensor.shape)
        self.assertEqual(231, meta['resizedHeight'])
        self.assertEqual(89, meta['padBottom'])
        self.assertEqual(0, meta['padTop'])
        expected = (np.array([10, 20, 30], np.float32) - [103.53, 116.28, 123.675]) / [57.375, 57.12, 58.395]
        np.testing.assert_allclose(tensor[0, :, 0, 0], expected, rtol=1e-6)
        pad = (np.array([114, 114, 114], np.float32) - [103.53, 116.28, 123.675]) / [57.375, 57.12, 58.395]
        np.testing.assert_allclose(tensor[0, :, 231, 0], pad, rtol=1e-6)
        self.assertTrue(tensor.flags.c_contiguous)

    def test_grid_origin_and_scale_boundaries(self):
        priors = grid_priors(320)
        self.assertEqual((2100, 4), priors.shape)
        np.testing.assert_array_equal(priors[0], [0, 0, 8, 8])
        np.testing.assert_array_equal(priors[1], [8, 0, 8, 8])
        np.testing.assert_array_equal(priors[1600], [0, 0, 16, 16])
        np.testing.assert_array_equal(priors[2000], [0, 0, 32, 32])

    def test_mask_restore_ignores_padding_and_returns_original_dimensions(self):
        metadata = dict(inputSize=32, resizedHeight=16, resizedWidth=32, height=8, width=16)
        logits = np.full((8, 4, 4), -10, dtype=np.float32)
        logits[:, :2] = 10
        masks = restore_masks(logits, metadata)
        self.assertEqual((8, 8, 16), masks.shape)
        self.assertTrue(masks.all())


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.metadata = dict(inputSize=320, resizedWidth=320, resizedHeight=320, scaleX=1, scaleY=1)
        self.names = [str(i) for i in range(80)]
        self.outputs = [np.full((1, 2100, 80), -100, np.float32),
                        np.zeros((1, 2100, 4), np.float32),
                        np.zeros((1, 2100, 169), np.float32),
                        np.zeros((1, 8, 40, 40), np.float32)]

    def detection(self, index, label, confidence):
        self.outputs[0][0, index, label] = np.log(confidence / (1 - confidence))
        x, y = grid_priors(320)[index, :2]
        self.outputs[1][0, index] = [x, y, 100 - x, 100 - y]

    def test_thresholds_are_explicit_and_unchanged(self):
        self.assertEqual((.4, .5, .4, .5, 8), (CONFIDENCE, NMS_IOU, BBOX_IOU, MASK_IOU, MAX_INSTANCES))

    def test_class_aware_nms_and_confidence(self):
        self.detection(0, 0, .9)
        self.detection(1, 0, .8)
        self.detection(2, 1, .7)
        self.detection(3, 2, .399)
        selected = select_instances(self.outputs, self.metadata, self.names)
        self.assertEqual([0, 1], [item['classId'] for item in selected])
        self.assertEqual([0, 2], [item['anchor'] for item in selected])

    def test_eight_slots_are_score_order_not_target_class_preference(self):
        for label in range(9):
            self.detection(label, label, .95 - label * .05)
        selected = select_instances(self.outputs, self.metadata, self.names)
        self.assertEqual(list(range(8)), [item['classId'] for item in selected])

    def test_empty_selection_decoder_padding_is_finite_and_ignored(self):
        feed = decoder_inputs(self.outputs, [], 320)
        self.assertEqual((8, 169), feed['kernels'].shape)
        self.assertFalse(feed['kernels'].any())
        self.assertTrue((feed['priors_xyss'][:, 2:] > 0).all())

    def test_missing_semantic_object_is_failure(self):
        annotation = dict(id=1, class_name='bottle', bbox=[1, 1, 2, 2], iscrowd=0,
                          segmentation=[[1, 1, 3, 1, 3, 3, 1, 3]])
        result = evaluate_photo(dict(id=1, file_name='unit', width=4, height=4), [annotation], [],
                                np.zeros((8, 4, 4), bool))
        self.assertFalse(result['passed'])
        self.assertFalse(result['checks'][0]['found'])
        self.assertEqual(0, result['checks'][0]['maskIou'])


class ArtifactTests(unittest.TestCase):
    def test_fp32_tensor_layout_checksum_and_shape(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            record = tensor_record(root, 'tensors/test.f32', 'test', np.array([[1, -2], [3.5, 0]], np.float64))
            raw = (root / record['file']).read_bytes()
        self.assertEqual(struct.pack('<4f', 1, -2, 3.5, 0), raw)
        self.assertEqual([2, 2], record['shape'])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), record['sha256'])

    def test_numerical_gate_fails_without_tolerance_tuning(self):
        reference = np.zeros((2,), np.float32)
        self.assertTrue(compare(reference, np.array([0, .009], np.float32))['passed'])
        self.assertFalse(compare(reference, np.array([0, .011], np.float32))['passed'])
        self.assertFalse(compare(reference, np.array([0, np.nan], np.float32))['passed'])

    def test_existing_corrupt_cache_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'cached'
            path.write_bytes(b'bad')
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                verified_download('https://example.invalid/no-request', hashlib.sha256(b'good').hexdigest(), path)

    def test_no_generated_artifacts_in_tracked_tree(self):
        with self.assertRaises(ValueError):
            safe_output(DIRECTORY / 'generated')

    def test_reusing_nonempty_output_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            prepare_output(root)
            (root / 'manifest.json').write_text('{}')
            with self.assertRaisesRegex(ValueError, 'fresh/empty'):
                prepare_output(root)

    def test_same_four_existing_detector_expectations(self):
        fixtures = json.loads((DIRECTORY.parent / 'fixtures/recognition-coco.json').read_text())
        regression = json.loads((DIRECTORY / 'coco-regression.json').read_text())
        validate_fixture_contract(fixtures, regression)
        self.assertEqual(4, len(regression['annotations']))
        regression['annotations'][0]['bbox'][0] += 1
        with self.assertRaises(ValueError):
            validate_fixture_contract(fixtures, regression)


if __name__ == '__main__':
    unittest.main()
