"""Prepare immutable, hash-verified DDI content for both release formats."""
import argparse
import base64
import hashlib
import json
import plistlib
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def valid_file(path, entry):
    return (path.is_file() and path.stat().st_size == entry['size']
            and hashlib.sha256(path.read_bytes()).hexdigest() == entry['sha256'])


def prepare(destination, download=True):
    manifest = json.loads((ROOT / 'config/ddi-bundles.json').read_text(encoding='utf-8'))
    for bundle in manifest['bundles']:
        directory = destination / bundle['directory']
        for entry in bundle['files']:
            target = directory / entry['name']
            if valid_file(target, entry):
                continue
            if not download:
                raise ValueError(f'Missing or corrupt bundled DDI: {target}')
            directory.mkdir(parents=True, exist_ok=True)
            upstream = f"PersonalizedImages/{bundle['directory']}/{entry['upstream_name']}"
            endpoint = (f"repos/{manifest['repository']}/contents/{upstream}"
                        f"?ref={manifest['revision']}")
            urls = [f"https://raw.githubusercontent.com/{manifest['repository']}/"
                    f"{manifest['revision']}/{upstream}", f'https://api.github.com/{endpoint}']
            with tempfile.NamedTemporaryFile(dir=directory, suffix='.part', delete=False) as output:
                partial = Path(output.name)
            try:
                errors = []
                for attempt in range(3):
                    for url in urls:
                        try:
                            if url.startswith('https://api.github.com/') and shutil.which('gh'):
                                # Request base64 JSON: Windows gh text conversion can
                                # corrupt binary raw responses redirected to a file.
                                result = subprocess.check_output(['gh', 'api', endpoint], timeout=120)
                                record = json.loads(result)
                                if record.get('encoding') != 'base64':
                                    result = subprocess.check_output(['gh', 'api',
                                        f"repos/{manifest['repository']}/git/blobs/{record['sha']}"], timeout=120)
                                    record = json.loads(result)
                                if record.get('encoding') != 'base64':
                                    raise ValueError('GitHub did not return base64 content')
                                partial.write_bytes(base64.b64decode(record['content']))
                            else:
                                request = urllib.request.Request(url, headers={
                                    'Accept': 'application/vnd.github.raw+json',
                                    'User-Agent': 'iPhoneMirror-DDI-build'})
                                with urllib.request.urlopen(request, timeout=30) as response, partial.open('wb') as output:
                                    received = 0
                                    while chunk := response.read(128 * 1024):
                                        received += len(chunk)
                                        if received > entry['size']:
                                            raise ValueError('DDI download exceeds pinned size')
                                        output.write(chunk)
                            if not valid_file(partial, entry):
                                raise ValueError('DDI size or SHA-256 mismatch')
                            partial.replace(target)
                            print(f"Prepared {bundle['directory']}/{entry['name']}", flush=True)
                            break
                        except (OSError, ValueError, subprocess.SubprocessError) as error:
                            errors.append(type(error).__name__)
                    if valid_file(target, entry):
                        break
                else:
                    raise RuntimeError(f"Could not prepare {target.name}: {', '.join(errors)}")
            finally:
                partial.unlink(missing_ok=True)
        build = plistlib.loads((directory / 'BuildManifest.plist').read_bytes())['ProductBuildVersion']
        if build != bundle['build']:
            raise ValueError(f'Unexpected DDI build: {build}')
    print('Verified both bundled DDI formats (8 pinned files).', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path, default=ROOT / 'tools/ddi')
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    prepare(args.destination, download=not args.verify_only)
