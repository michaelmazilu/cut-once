"""Bounded, checksum-verified public downloads; generated files stay in cache."""
from __future__ import annotations

import hashlib
import json
import os
import tempfile
from pathlib import Path
from urllib.request import urlopen

DIRECTORY = Path(__file__).resolve().parent


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def verified_download(url: str, expected_sha256: str, target: Path,
                      expected_bytes: int | None = None) -> Path:
    if not url.startswith('https://'):
        raise ValueError('Only pinned HTTPS sources are accepted')
    if len(expected_sha256) != 64:
        raise ValueError('Missing source SHA256')
    if target.exists():
        if sha256(target) != expected_sha256:
            raise ValueError(f'Existing cache checksum mismatch: {target}')
        if expected_bytes is not None and target.stat().st_size != expected_bytes:
            raise ValueError(f'Existing cache size mismatch: {target}')
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=target.parent, prefix='.download-', delete=False) as output:
            temporary = Path(output.name)
            with urlopen(url, timeout=30) as response:
                count = 0
                for block in iter(lambda: response.read(1024 * 1024), b''):
                    count += len(block)
                    if count > (expected_bytes if expected_bytes is not None else 32 * 1024 * 1024):
                        raise ValueError(f'Download exceeds pinned/bounded size: {target.name}')
                    output.write(block)
        if sha256(temporary) != expected_sha256:
            raise ValueError(f'Download checksum mismatch: {target.name}')
        if expected_bytes is not None and temporary.stat().st_size != expected_bytes:
            raise ValueError(f'Download size mismatch: {target.name}')
        os.replace(temporary, target)
        temporary = None
        return target
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def fetch_sources(cache: Path) -> dict:
    lock = json.loads((DIRECTORY / 'sources.lock.json').read_text())
    for relative, entry in lock['sources'].items():
        path = Path(relative)
        if path.is_absolute() or '..' in path.parts:
            raise ValueError('Invalid source lock path')
        verified_download(entry['url'], entry['sha256'], cache / path, entry['bytes'])
    return lock


def fetch_photos(cache: Path, manifest: dict) -> dict[str, Path]:
    paths = {}
    for fixture in manifest['fixtures']:
        name = fixture['fileName']
        if Path(name).name != name:
            raise ValueError('Invalid fixture filename')
        paths[name] = verified_download(fixture['url'], fixture['sha256'], cache / 'photos' / name)
    return paths
