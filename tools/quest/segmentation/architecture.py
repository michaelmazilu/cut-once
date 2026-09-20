# Copyright (c) OpenMMLab. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
# Modifications: inference-only Torch adapters, pinned source-class extraction,
# static raw-head outputs, and fixed-count MatMul mask decoder. See NOTICE.md.
"""Pinned official inference bodies with a narrow no-mmcv CPU export adapter.

This is not a complete independent MMDetection installation. Preserve the
reference limitation in every result and do not label an export as headset proof.
"""
from __future__ import annotations

import ast
import hashlib
import math
from pathlib import Path

import torch
import torch.nn as nn
import torch.nn.functional as F

CHECKPOINT_SHA256 = 'ec670f7ee9e20bd7931e15f15b7016f7fe531baaab81f2e6153382d046111885'


class BaseModule(nn.Module):
    def __init__(self, init_cfg=None):
        super().__init__()


class ConvModule(nn.Module):
    """Only the Conv2d -> BN -> SiLU path used by the pinned eval model."""
    def __init__(self, in_channels, out_channels, kernel_size, stride=1, padding=0,
                 groups=1, conv_cfg=None, norm_cfg=None, act_cfg=None, **kwargs):
        super().__init__()
        assert conv_cfg is None and not kwargs
        assert norm_cfg is not None and norm_cfg['type'] in ('BN', 'SyncBN')
        assert act_cfg['type'] in ('SiLU', 'Swish')
        self.conv = nn.Conv2d(in_channels, out_channels, kernel_size, stride,
                              padding, groups=groups, bias=False)
        self.bn = nn.BatchNorm2d(out_channels, eps=norm_cfg.get('eps', 1e-5),
                                 momentum=norm_cfg.get('momentum', 0.1))
        self.activate = nn.SiLU(inplace=False)

    def forward(self, x):
        return self.activate(self.bn(self.conv(x)))


class DepthwiseSeparableConvModule(nn.Module):
    def __init__(self, in_channels, out_channels, kernel_size, stride=1, padding=0,
                 conv_cfg=None, norm_cfg=None, act_cfg=None):
        super().__init__()
        self.depthwise_conv = ConvModule(in_channels, in_channels, kernel_size,
                                         stride, padding, groups=in_channels,
                                         norm_cfg=norm_cfg, act_cfg=act_cfg)
        self.pointwise_conv = ConvModule(in_channels, out_channels, 1,
                                         norm_cfg=norm_cfg, act_cfg=act_cfg)

    def forward(self, x):
        return self.pointwise_conv(self.depthwise_conv(x))


def import_class(filename, name, namespace, methods=None, base=None):
    """Compile a selected official class, dropping registry decorators/imports.

    The source forward and constructor statements are kept unchanged. For the
    head, only graph construction/forward and decoder methods are needed; its
    general training parent is replaced by the explicit tiny-head adapter.
    """
    source = namespace['_source_root'] / 'reference' / filename
    tree = ast.parse(source.read_text())
    node = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == name)
    node.decorator_list = []
    if methods is not None:
        node.body = [n for n in node.body if isinstance(n, ast.FunctionDef) and n.name in methods]
    if base:
        node.bases = [ast.Name(id=base, ctx=ast.Load())]
    module = ast.Module(body=[ast.ImportFrom(module='__future__', names=[ast.alias(name='annotations')], level=0), node], type_ignores=[])
    exec(compile(ast.fix_missing_locations(module), str(source), 'exec'), namespace)
    return namespace[name]


class Priors:
    strides = [(8, 8), (16, 16), (32, 32)]

    def single_level_grid_priors(self, size, level_idx=0, device='cpu'):
        h, w = size
        y, x = torch.meshgrid(torch.arange(h, device=device), torch.arange(w, device=device), indexing='ij')
        return torch.stack((x, y), dim=-1).reshape(-1, 2).float() * self.strides[level_idx][0]


class TinyHeadAdapter(BaseModule):
    def __init__(self):
        super().__init__()
        self.in_channels = self.feat_channels = 96
        self.stacked_convs = 2
        self.share_conv = True
        self.with_objectness = False
        self.pred_kernel_size = 1
        self.conv_cfg = None
        self.norm_cfg = dict(type='SyncBN', requires_grad=True)
        self.act_cfg = dict(type='SiLU', inplace=True)
        self.num_dyconvs = 3
        self.num_prototypes = self.dyconv_channels = 8
        self.num_base_priors = 1
        self.cls_out_channels = 80
        self.prior_generator = Priors()
        self._init_layers()


def make_model(source_root: Path):
    ns = dict(_source_root=source_root, torch=torch, nn=nn, F=F, math=math, BaseModule=BaseModule,
              ConvModule=ConvModule, DepthwiseSeparableConvModule=DepthwiseSeparableConvModule,
              TinyHeadAdapter=TinyHeadAdapter, _BatchNorm=nn.modules.batchnorm._BatchNorm,
              digit_version=lambda value: tuple(int(x) for x in value.split('+')[0].split('.')[:3]))
    for filename, names in [('se_layer.py', ['ChannelAttention']),
                            ('csp_layer.py', ['DarknetBottleneck', 'CSPNeXtBlock', 'CSPLayer']),
                            ('csp_darknet.py', ['SPPBottleneck']),
                            ('cspnext.py', ['CSPNeXt']),
                            ('cspnext_pafpn.py', ['CSPNeXtPAFPN']),
                            ('rtmdet_ins_head.py', ['MaskFeatModule'])]:
        for name in names:
            import_class(filename, name, ns)
    decoder_reference = import_class('rtmdet_ins_head.py', 'RTMDetInsHead', ns,
                                     methods=['parse_dynamic_params', '_mask_predict_by_feat_single'], base='BaseModule')
    head_class = import_class('rtmdet_ins_head.py', 'RTMDetInsSepBNHead', ns,
                              methods=['_init_layers', 'forward'], base='TinyHeadAdapter')
    head_class.parse_dynamic_params = decoder_reference.parse_dynamic_params
    head_class._mask_predict_by_feat_single = decoder_reference._mask_predict_by_feat_single

    class RawHeadModel(nn.Module):
        def __init__(self):
            super().__init__()
            self.backbone = ns['CSPNeXt'](deepen_factor=.167, widen_factor=.375,
                                         norm_cfg=dict(type='SyncBN'), act_cfg=dict(type='SiLU'))
            self.neck = ns['CSPNeXtPAFPN']([96, 192, 384], 96, num_csp_blocks=1,
                                           norm_cfg=dict(type='SyncBN'), act_cfg=dict(type='SiLU'))
            self.bbox_head = head_class()

        def forward(self, image):
            cls, reg, kernel, mask = self.bbox_head(self.neck(self.backbone(image)))
            flatten = lambda xs: torch.cat([x.flatten(2).transpose(1, 2) for x in xs], dim=1)
            return flatten(cls), flatten(reg), flatten(kernel), mask

    model = RawHeadModel().eval()
    path = source_root / 'rtmdet-ins-tiny.pth'
    assert hashlib.sha256(path.read_bytes()).hexdigest() == CHECKPOINT_SHA256
    checkpoint = torch.load(path, map_location='cpu', weights_only=True)
    aliases = {}
    for name, tensor in model.state_dict().items():
        key = tensor.data_ptr()
        if key in aliases:
            assert torch.equal(checkpoint['state_dict'][name], checkpoint['state_dict'][aliases[key]]), \
                f'Official checkpoint disagrees with source weight sharing: {name}, {aliases[key]}'
        else:
            aliases[key] = name
    model.checked_state_alias_count = len(model.state_dict()) - len(aliases)
    model.load_state_dict(checkpoint['state_dict'], strict=True)
    return model, checkpoint['meta']


class FixedMaskDecoder(nn.Module):
    """Eight selected instances, no variable-group convolutions/custom ONNX ops."""
    def __init__(self, size=80, count=8):
        super().__init__()
        self.count, self.size = count, size
        self.register_buffer('coord', Priors().single_level_grid_priors((size, size)).T[None])

    def forward(self, mask_features, kernels, priors):
        relative = (priors[:, :2, None] - self.coord) / (priors[:, 2:3, None] * 8)
        shared = mask_features.reshape(1, 8, self.size * self.size).expand(self.count, -1, -1)
        x = torch.cat((relative, shared), dim=1)
        parts = torch.split(kernels, [80, 64, 8, 8, 8, 1], dim=1)
        w0, w1, w2, b0, b1, b2 = parts
        x = F.relu(torch.matmul(w0.reshape(self.count, 8, 10), x) + b0[:, :, None])
        x = F.relu(torch.matmul(w1.reshape(self.count, 8, 8), x) + b1[:, :, None])
        x = torch.matmul(w2.reshape(self.count, 1, 8), x) + b2[:, :, None]
        return x.reshape(self.count, self.size, self.size)
