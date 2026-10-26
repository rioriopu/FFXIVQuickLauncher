import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("mirror", Path(__file__).parents[1] / "mirror-dalamud-dependencies.py")
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)


def archive(files):
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w") as package:
        for name, content in files.items():
            package.writestr(name, content)
    return data.getvalue()


class MirrorTests(unittest.TestCase):
    def test_official_utf16_hash_json(self):
        self.assertEqual({"file": "hash"}, mirror.load_json(json.dumps({"file": "hash"}).encode("utf-16")))

    def test_runtime_corruption_does_not_replace_existing_version(self):
        with tempfile.TemporaryDirectory(prefix="estell-mirror-test-") as temporary:
            root = Path(temporary) / "public"
            target = root / "runtime" / "10.0.14"
            target.mkdir(parents=True)
            (target / "sentinel").write_text("previous")
            work = Path(temporary) / "work"
            work.mkdir()
            hashes = {"coreclr.dll": hashlib.md5(b"good").hexdigest()}
            with patch.object(mirror, "fetch", side_effect=lambda url: json.dumps(hashes).encode() if "/Hashes/" in url else archive({"coreclr.dll": b"bad"})):
                with self.assertRaises(ValueError):
                    mirror.sync_runtime(root, "10.0.14", work)
            self.assertEqual("previous", (target / "sentinel").read_text())

    def asset_case(self, corrupted):
        temporary = tempfile.TemporaryDirectory(prefix="estell-mirror-test-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name) / "public"
        work = Path(temporary.name) / "work"
        work.mkdir()
        (root / "assets").mkdir(parents=True)
        (root / "assets" / "meta.json").write_text("previous")
        metadata = {"version": 446, "packageUrl": "https://official.example/package.zip", "assets": [{"fileName": "font.bin", "url": "https://official.example/font.bin", "hash": hashlib.sha1(b"good").hexdigest()}]}
        responses = {f"{mirror.BASE}/Asset/Meta": json.dumps(metadata).encode(), metadata["packageUrl"]: archive({"font.bin": b"bad" if corrupted else b"good"})}
        return root, work, responses

    def test_corrupt_assets_do_not_switch_active_metadata(self):
        root, work, responses = self.asset_case(True)
        with patch.object(mirror, "fetch", side_effect=responses.__getitem__):
            with self.assertRaises(ValueError):
                mirror.sync_assets(root, "https://mirror.example", work)
        self.assertEqual("previous", (root / "assets" / "meta.json").read_text())

    def test_verified_assets_publish_package_before_metadata(self):
        root, work, responses = self.asset_case(False)
        with patch.object(mirror, "fetch", side_effect=responses.__getitem__):
            mirror.sync_assets(root, "https://mirror.example", work)
        metadata = json.loads((root / "assets" / "meta.json").read_bytes())
        self.assertEqual("https://official.example/package.zip", metadata["originalPackageUrl"])
        self.assertEqual("https://mirror.example/assets/446/package.zip", metadata["packageUrl"])
        self.assertTrue((root / "assets" / "446" / "package.zip").is_file())
        self.assertEqual(b"good", (root / "assets" / "446" / "files" / "font.bin").read_bytes())


if __name__ == "__main__":
    unittest.main()
