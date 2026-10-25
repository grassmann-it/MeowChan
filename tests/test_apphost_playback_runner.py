"""Runner contract/safety tests, not proof of product media playback."""
import importlib.util
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('playback_runner', ROOT / 'scripts/apphost-playback-smoke.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def metrics():
    clean = dict(HasPlayback=False, HasEngine=False, Disposed=True, ReleasedStreams=2, CleanupFailures=0, LoopEnabled=False)
    return dict(schemaVersion=1, completed=True, failure=None, fixtureSha256='AB', checks=[*runner.CHECKS, 'no-audio-track-controls'],
                samples=[dict(State='Playing', Time=300, DecodedFrames=3, DisplayedFrames=2, LoopEnabled=False, LoopRestarts=0, AudioTracks=0, AudioAvailable=False, Volume=-1),
                         dict(State='Playing', Time=600, DecodedFrames=7, DisplayedFrames=4),
                         dict(State='Playing', Time=300, LoopEnabled=True, LoopRestarts=1),
                         dict(State='Ended', Time=12000, LoopEnabled=False, LoopRestarts=1)],
                poster=dict(Generated=True, PersistentCacheHit=True, NonUniform=True, Width=1, Height=1, Sha256='00' * 32),
                imageCleanup=clean.copy(), closeCleanup=clean.copy(), shutdownCleanup=clean.copy())


class PlaybackRunnerTests(unittest.TestCase):
    def test_pinned_fixture_manifest(self):
        self.assertEqual(len(runner.default_fixtures()), 2)

    def test_requires_real_progress_and_cleanup(self):
        runner.validate_metrics(metrics(), 'ab')
        for field, value in [('Time', 350), ('DecodedFrames', 3), ('DisplayedFrames', 2), ('State', 'Paused')]:
            bad = metrics()
            bad['samples'][1][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                runner.validate_metrics(bad, 'ab')
        for name in ('imageCleanup', 'closeCleanup', 'shutdownCleanup'):
            for field, value in [('HasPlayback', True), ('ReleasedStreams', 0), ('CleanupFailures', 1), ('LoopEnabled', True)]:
                bad = metrics()
                bad[name][field] = value
                with self.subTest(name=name, field=field), self.assertRaises(ValueError):
                    runner.validate_metrics(bad, 'ab')

    def test_requires_native_repeat_progress_and_disabled_end(self):
        for index, field, value in [(0, 'LoopEnabled', True), (0, 'LoopRestarts', 1),
                                    (2, 'LoopRestarts', 0), (3, 'State', 'Playing')]:
            bad = metrics()
            bad['samples'][index][field] = value
            with self.subTest(index=index, field=field), self.assertRaises(ValueError):
                runner.validate_metrics(bad, 'ab')

    def test_silent_fixture_cannot_claim_native_mute_proof(self):
        bad = metrics()
        bad['checks'].append('mute-unmute-state')
        with self.assertRaisesRegex(ValueError, 'mute/unmute evidence'):
            runner.validate_metrics(bad, 'ab')
        for field, value in [('AudioTracks', 1), ('AudioAvailable', True)]:
            bad = metrics()
            bad['samples'][0][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'silent-track evidence'):
                runner.validate_metrics(bad, 'ab')

    def test_rejects_incomplete_or_wrong_fixture(self):
        for key, value in [('completed', False), ('failure', 'failed'), ('fixtureSha256', 'wrong'), ('checks', [])]:
            bad = metrics()
            bad[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                runner.validate_metrics(bad, 'ab')

    def test_requires_video_poster_cache_proof(self):
        evidence = metrics()
        evidence['checks'].remove('video-poster-cache')
        with self.assertRaisesRegex(ValueError, 'required playback checks'):
            runner.validate_metrics(evidence, 'ab')

    def test_requires_poster_generation_cache_and_bounded_dimensions(self):
        for key, value in [('Generated', False), ('PersistentCacheHit', False), ('NonUniform', False),
                           ('Width', 0), ('Width', 961), ('Height', 0), ('Height', 641)]:
            evidence = metrics()
            evidence['poster'][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                runner.validate_metrics(evidence, 'ab')

    def test_png_integrity(self):
        def chunk(kind, payload):
            return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
        png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b'\x00\x00\x00\x00')) + chunk(b'IEND', b'')
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'frame.png'
            path.write_bytes(png)
            self.assertEqual(runner.validate_png(path)['width'], 1)
            evidence = metrics()
            evidence['poster']['Sha256'] = runner.sha256(path).upper()
            self.assertEqual(runner.validate_poster(evidence, path)['width'], 1)
            evidence['poster']['Sha256'] = '00' * 32
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                runner.validate_poster(evidence, path)
            evidence['poster']['Sha256'] = runner.sha256(path)
            evidence['poster']['Width'] = 2
            with self.assertRaisesRegex(ValueError, 'dimensions mismatch'):
                runner.validate_poster(evidence, path)
            path.write_bytes(png[:-1] + b'X')
            with self.assertRaises(ValueError):
                runner.validate_png(path)

    @unittest.skipIf(os.name == 'nt', 'POSIX executable fixture')
    def test_timeout_cleans_owned_process_and_private_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            app = base / 'fake-apphost'
            app.write_text('#!' + sys.executable + '\nimport os,time\nfrom pathlib import Path\np=Path(os.environ["MEOWCHAN_PROFILE"])\nassert not p.exists()\nassert "OCHAN_PROFILE" not in os.environ\np.mkdir()\n(p/"apphost-playback-poster.png").write_bytes(b"retained-poster-evidence")\nprint(str(p),flush=True)\ntime.sleep(30)\n')
            app.chmod(0o755)
            fixture = ROOT / 'tests/Fixtures/gallery-playback-vp8.webm'
            result = runner.run_fixture(app, fixture, base / 'evidence', .2, False)
            self.assertFalse(result['passed'])
            self.assertTrue(result['timedOut'])
            self.assertEqual((base / 'evidence/poster.png').read_bytes(), b'retained-poster-evidence')
            profile = (base / 'evidence/output.log').read_text().strip()
            self.assertTrue(profile)
            self.assertFalse(Path(profile).parent.exists())


if __name__ == '__main__':
    unittest.main()
