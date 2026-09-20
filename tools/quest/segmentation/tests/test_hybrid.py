"""Experimental selector/input-integrity tests; not headset or mask accuracy proof."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(DIRECTORY))

from hybrid import (COCO_CLASSES, YOLO_CLASSES, associate, load_candidate, read_json,
                    validate_authority, validate_detection, validate_numerical,
                    validate_outputs, verified_record, verify_file)


def detection(label=39, confidence=.9):
    return dict(classId=label, name=YOLO_CLASSES[label], confidence=confidence,
                x=0., y=0., width=10., height=10., inputWidth=100, inputHeight=100,
                validBox=True, acceptedByScanner=confidence >= .4)


def fixture_contract():
    names = ['000000160012.jpg', '000000146489.jpg']
    fixtures = dict(fixtures=[dict(fileName=name, sha256=letter*64) for name, letter in zip(names, 'ab')])
    regression = dict(images=[dict(file_name=name, width=100, height=100) for name in names])
    report = dict(passed=True, error='', scannerConfidence=.4000000059604645, nmsIoU=.5, cornerBoxes=True, photos=[])
    for fixture in fixtures['fixtures']:
        inference = dict(iteration=1, passed=True, eventDelivered=True, timeoutDiscarded=False,
                         eventInputWidth=100, eventInputHeight=100, detections=[detection()])
        report['photos'].append(dict(source='/captured/'+fixture['fileName'], sha256=fixture['sha256'],
                                      width=100, height=100, passed=True, inferences=[inference]))
    return fixtures, regression, report


def numerical_report():
    check = dict(passed=True, finite=True, mismatchCount=0, atol=.01, rtol=.001)
    raw = {name: copy.deepcopy(check) for name in ('class_logits', 'box_distances', 'mask_kernels', 'mask_features')}
    return dict(passed=True, randomInput=copy.deepcopy(raw), cases=[
        dict(fileName=name, passed=True, raw=copy.deepcopy(raw),
             masks={name: copy.deepcopy(check) for name in ('groupedToMatmul', 'torchToOnnx')})
        for name in ('000000160012.jpg', '000000146489.jpg')])


class SelectorTests(unittest.TestCase):
    def setUp(self):
        self.boxes = np.array([[0., 0., 10., 10.]]*4)
        self.scores = np.zeros((4, 80), dtype=np.float32)
        self.item = detection()

    def test_winning_class_and_geometry_are_both_required(self):
        self.scores[0, 39], self.scores[0, 40] = .9, .95
        self.scores[1, 39] = .99
        self.boxes[1] = [30, 30, 40, 40]
        self.scores[2, 39] = .2
        result = associate(self.scores, self.boxes, self.item)
        self.assertEqual(2, result['selection']['anchor'])
        self.assertEqual(1, result['eligibleAnchorCount'])
        self.assertLess(result['selection']['classScore'], .4)  # Proposal, not recognition threshold.

    def test_highest_score_then_lowest_anchor_not_best_geometry(self):
        self.scores[:, 39] = [.7, .8, .8, .6]
        self.boxes[1] = [0, 0, 10, 20]  # Exactly .5 IoU; anchor2 has perfect IoU.
        result = associate(self.scores, self.boxes, self.item)
        self.assertEqual(1, result['selection']['anchor'])
        self.assertEqual(.5, result['selection']['matchingBoxIou'])
        self.assertEqual([1, 2, 0, 3], [x['anchor'] for x in result['rankedEligible']])

    def test_no_match_rejects_without_fallback(self):
        self.scores[:, 40] = .9
        result = associate(self.scores, self.boxes, self.item)
        self.assertFalse(result['accepted'])
        self.assertIsNone(result['selection'])
        self.assertEqual([], result['rankedEligible'])

    def test_non_target_class_uses_same_policy_and_preserves_authority_name(self):
        self.scores[2, 57] = .25
        result = associate(self.scores, self.boxes, detection(57))
        self.assertEqual('sofa', result['className'])
        self.assertEqual('couch', result['selection']['winningClass'])
        self.assertEqual(57, result['selection']['winningClassId'])

    def test_all_eighty_class_ids_have_explicit_unchanged_mapping(self):
        for label in range(80):
            scores = np.zeros((1, 80), np.float32)
            scores[0, label] = .01
            self.assertTrue(associate(scores, self.boxes[:1], detection(label))['accepted'])
        self.assertEqual(('motorbike', 'aeroplane', 'TV / monitor'),
                         (YOLO_CLASSES[3], YOLO_CLASSES[4], YOLO_CLASSES[62]))

    def test_wrong_class_name_cannot_authorize_mask(self):
        self.item['name'] = 'dining table'
        with self.assertRaisesRegex(ValueError, 'map mismatch'):
            associate(self.scores, self.boxes, self.item)

    def test_nonfinite_out_of_range_and_inverted_proposals_rejected(self):
        for value in (np.nan, np.inf, -np.inf, -.1, 1.1):
            scores = self.scores.copy()
            scores[0, 39] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                associate(scores, self.boxes, self.item)
        for box in ([0, 0, np.nan, 10], [5, 0, 1, 10]):
            boxes = self.boxes.copy()
            boxes[0] = box
            with self.subTest(box=box), self.assertRaises(ValueError):
                associate(self.scores, boxes, self.item)

    def test_malformed_raw_outputs_rejected(self):
        outputs = [np.zeros(shape, np.float32) for shape in
                   ((1, 2100, 80), (1, 2100, 4), (1, 2100, 169), (1, 8, 40, 40))]
        validate_outputs(outputs, 320)
        for index in range(4):
            malformed = [x.copy() for x in outputs]
            malformed[index].flat[0] = np.nan
            with self.assertRaisesRegex(ValueError, 'proposal tensor'):
                validate_outputs(malformed, 320)
        outputs[1].flat[0] = -1
        with self.assertRaisesRegex(ValueError, 'Negative'):
            validate_outputs(outputs, 320)


class AuthorityTests(unittest.TestCase):
    def setUp(self):
        self.fixtures, self.regression, self.report = fixture_contract()

    def validate(self):
        return validate_authority(self.report, self.fixtures, self.regression)

    def test_exact_photos_and_first_inference_are_preserved(self):
        self.assertIs(self.validate(), self.report['photos'])
        self.report['photos'][0]['inferences'][0]['passed'] = False
        self.report['photos'][0]['inferences'].append(dict(passed=True))
        with self.assertRaisesRegex(ValueError, 'First YOLO'):
            self.validate()

    def test_nine_accepted_objects_fail_not_truncate(self):
        self.report['photos'][0]['inferences'][0]['detections'] = [detection(i) for i in range(9)]
        with self.assertRaisesRegex(ValueError, 'eight accepted'):
            self.validate()

    def test_failed_authority_and_changed_thresholds_rejected(self):
        for key, value in [('passed', False), ('scannerConfidence', .39), ('nmsIoU', .6)]:
            report = copy.deepcopy(self.report)
            report[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_authority(report, self.fixtures, self.regression)

    def test_missing_duplicate_and_wrong_hash_photo_rejected(self):
        for photos in (self.report['photos'][:1], [self.report['photos'][0]]*2):
            changed = copy.deepcopy(self.report)
            changed['photos'] = photos
            with self.assertRaises(ValueError):
                validate_authority(changed, self.fixtures, self.regression)
        self.report['photos'][0]['sha256'] = 'c'*64
        with self.assertRaisesRegex(ValueError, 'photo hash'):
            self.validate()

    def test_bad_detections_rejected_instead_of_silently_filtering(self):
        for key, value in [('x', np.nan), ('y', np.inf), ('width', -1), ('confidence', 1.1),
                           ('confidence', np.nan), ('height', np.inf),
                           ('validBox', False), ('inputWidth', 640), ('acceptedByScanner', False),
                           ('classId', True), ('name', 'table')]:
            item = detection()
            item[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_detection(item, 100, 100, .4)

    def test_legitimate_negative_edge_coordinates_preserved(self):
        item = detection()
        item['x'] = -.04
        validate_detection(item, 100, 100, .4)
        self.assertEqual(-.04, item['x'])

    def test_subthreshold_detection_not_misrepresented_as_accepted(self):
        item = detection(confidence=.39)
        validate_detection(item, 100, 100, .4)
        self.assertFalse(item['acceptedByScanner'])


class IntegrityTests(unittest.TestCase):
    def test_external_hash_is_required_not_self_computed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'input.json'
            path.write_text('{}')
            good = hashlib.sha256(b'{}').hexdigest()
            self.assertEqual(path, verify_file(path, good))
            for wrong in ('', '0'*64, good.upper()):
                with self.subTest(wrong=wrong), self.assertRaises(ValueError):
                    verify_file(path, wrong)

    def test_json_duplicate_and_nonfinite_numbers_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'report.json'
            for text in ('{"passed":false,"passed":true}', '{"x":NaN}', '{"x":Infinity}', '{"x":1e309}'):
                path.write_text(text)
                with self.subTest(text=text), self.assertRaises(ValueError):
                    read_json(path)

    def test_artifact_traversal_and_symlink_escape_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            root = parent/'candidate'
            root.mkdir()
            outside = parent/'outside'
            outside.write_text('x')
            digest = hashlib.sha256(b'x').hexdigest()
            (root/'escape').symlink_to(outside)
            for relative in ('../outside', str(outside), 'escape'):
                with self.subTest(relative=relative), self.assertRaises(ValueError):
                    verified_record(root, dict(file=relative, sha256=digest))

    def test_numerical_failure_or_relaxed_tolerance_rejected(self):
        report = numerical_report()
        photos = {'000000160012.jpg', '000000146489.jpg'}
        validate_numerical(report, photos)
        for key, value in [('passed', False), ('finite', False), ('atol', .1), ('mismatchCount', 1)]:
            changed = copy.deepcopy(report)
            changed['cases'][0]['raw']['class_logits'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_numerical(changed, photos)

    def test_pinned_candidate_requires_exact_class_map_and_numerical_sidecar(self):
        fixtures, _, _ = fixture_contract()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def record(name, value):
                raw = json.dumps(value).encode()
                (root/name).write_bytes(raw)
                return dict(file=name, sha256=hashlib.sha256(raw).hexdigest())
            reports = dict(numerical=record('numeric.json', numerical_report()),
                           semantic=record('semantic.json', dict(passed=False)),
                           provenance=record('provenance.json', {}))
            raw_graph, masks_graph = record('raw.onnx', {}), record('masks.onnx', [])
            models = [dict(name=f'{kind}-coco-{int(Path(f["fileName"]).stem)}',
                           sourcePhoto=dict(fileName=f['fileName'], sha256=f['sha256']), onnx=graph)
                      for f in fixtures['fixtures'] for kind, graph in [('raw', raw_graph), ('masks8', masks_graph)]]
            manifest = dict(schemaVersion=1, candidateOnly=True, dtype='float32-le', inputSize=320,
                            classes=list(COCO_CLASSES), models=models, reports=reports)
            pinned = record('manifest.json', manifest)
            _, standalone = load_candidate(root, pinned['sha256'], fixtures)
            self.assertFalse(standalone['passed'])  # Original standalone failure is preserved, not relabeled.
            manifest['classes'][0], manifest['classes'][1] = manifest['classes'][1], manifest['classes'][0]
            pinned = record('manifest.json', manifest)
            with self.assertRaisesRegex(ValueError, 'class map'):
                load_candidate(root, pinned['sha256'], fixtures)
            manifest['classes'] = list(COCO_CLASSES)
            pinned = record('manifest.json', manifest)
            (root/'numeric.json').write_text('{"passed":true}')
            with self.assertRaisesRegex(ValueError, 'SHA256 mismatch'):
                load_candidate(root, pinned['sha256'], fixtures)


if __name__ == '__main__':
    unittest.main()
