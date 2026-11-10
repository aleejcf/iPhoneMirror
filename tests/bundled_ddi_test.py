"""Keep release DDI pins aligned with the runtime and reject damaged payloads."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import ddi_support as ddi
import usb_touch_bridge as bridge

spec = importlib.util.spec_from_file_location('prepare_ddi', ROOT / 'scripts/prepare_ddi.py')
prepare_ddi = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare_ddi)


class BundledDdiTests(unittest.TestCase):
    def test_release_pins_match_both_runtime_formats(self):
        manifest = json.loads((ROOT / 'config/ddi-bundles.json').read_text())
        self.assertEqual(manifest['revision'], ddi.CRYPTEX_DDI_REVISION)
        self.assertEqual(manifest['revision'], bridge.PERSONALIZED_DDI_PINNED_REVISION)
        bundles = {bundle['directory']: bundle for bundle in manifest['bundles']}
        legacy = bundles['Xcode_iOS_DDI_Personalized']
        self.assertEqual(legacy['build'], bridge.PERSONALIZED_DDI_PINNED_BUILD_ID)
        self.assertEqual({entry['name']: (entry['upstream_name'], entry['size'], entry['sha256'])
                          for entry in legacy['files']},
                         {asset.local_name: (asset.upstream_name, asset.size, asset.sha256)
                          for asset in bridge.PERSONALIZED_DDI_ASSETS})
        cryptex = bundles['Xcode_iOS_DDI_Cryptex']
        self.assertEqual(cryptex['build'], ddi.CRYPTEX_DDI_BUILD)
        self.assertEqual({entry['name']: (entry['size'], entry['sha256'])
                          for entry in cryptex['files']}, ddi.CRYPTEX_DDI_FILES)

    def test_release_verification_rejects_absent_payload_without_downloading(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(prepare_ddi.urllib.request, 'urlopen') as network:
            with self.assertRaisesRegex(ValueError, 'Missing or corrupt bundled DDI'):
                prepare_ddi.prepare(Path(temp), download=False)
            network.assert_not_called()

    def test_runtime_finds_cryptex_beside_packaged_executable(self):
        with patch.object(ddi.sys, 'frozen', True, create=True), \
             patch.object(ddi.sys, 'executable', str(ROOT / 'work' / 'tools' / 'iUsbBridge.exe')):
            self.assertEqual(ddi.bundled_cryptex_directory(),
                ROOT / 'work' / 'tools' / 'ddi' / 'Xcode_iOS_DDI_Cryptex')


if __name__ == '__main__':
    unittest.main()
