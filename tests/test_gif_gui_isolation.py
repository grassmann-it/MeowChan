"""Focused source contracts only: no build, GUI, or physical decoder proof."""
from pathlib import Path
import hashlib
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
SMOKE = ROOT / 'tests/MeowChan.DesktopSmoke'


def block(source, marker):
    start = source.index('{', source.index(marker))
    depth = 1
    end = start + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start + 1:end - 1]


class GifGuiIsolationContracts(unittest.TestCase):
    def setUp(self):
        self.source = (SMOKE / 'GifPlaybackChecks.cs').read_text()
        self.ownership = (SMOKE / 'GifOwnershipChecks.cs').read_text()

    def test_independent_helpers_and_unsplit_default(self):
        self.assertIn('RunGifPlaybackChecksAsync(MainWindow owner, GifJourney journey = GifJourney.All)', self.source)
        self.assertIn('RunAsync(MainWindow owner) => GalleryPlaybackFeatureChecks.RunGifPlaybackChecksAsync(owner);', self.source)
        for helper, journey in [('Playback', 'Playback'), ('Successors', 'Successors'),
                                ('VerifiedFailures', 'VerifiedFailures'), ('ReaderAssociation', 'ReaderAssociation')]:
            self.assertIn(f'Run{helper}Async(MainWindow owner) => GalleryPlaybackFeatureChecks.RunGifPlaybackChecksAsync(owner, GifJourney.{journey});', self.source)
        self.assertIn('RunAsync(MainWindow owner) => GalleryPlaybackFeatureChecks.RunGifOwnershipChecksAsync(owner);', self.ownership)
        run = block(self.ownership, 'internal static async Task RunGifOwnershipChecksAsync')
        self.assertNotIn('RunGifPlaybackChecksAsync', run)
        self.assertEqual(run.count('await CheckGifUnadoptedOwnershipAsync('), 1)
        self.assertEqual(run.count('await CheckGifFirstFrameOwnershipAsync('), 2)

    def test_exact_journey_call_coverage_no_duplicates(self):
        expected = {
            'Playback': ['CheckGifPixelsAndTransportAsync', 'CheckGifEncodedLoopsAsync', 'CheckGifSlideshowAsync'],
            'Successors': ['CheckGifVideoSuccessorAsync'],
            'VerifiedFailures': ['CheckGifFailureAsync', 'CheckGifFailureAsync'],
            'ReaderAssociation': ['CheckGifReaderAssociationAsync'],
        }
        run = block(self.source, 'internal static async Task RunGifPlaybackChecksAsync')
        calls = []
        for journey, names in expected.items():
            body = block(run, f'if (journey is GifJourney.All or GifJourney.{journey})')
            actual = re.findall(r'await (CheckGif\w+Async)\(', body)
            self.assertEqual(actual, names)
            calls += actual
        self.assertEqual(re.findall(r'await (CheckGif\w+Async)\(', run), calls)
        failures = block(run, 'if (journey is GifJourney.All or GifJourney.VerifiedFailures)')
        self.assertIn('OpenVerifiedReadAsync', failures)
        self.assertIn('catch (InvalidDataException)', failures)
        self.assertIn('Require(rejected,', failures)
        successors = block(run, 'if (journey is GifJourney.All or GifJourney.Successors)')
        self.assertIn('MEOWCHAN_VIDEO_FIXTURE', successors)
        self.assertIn('MEOWCHAN_MP4_FIXTURE', successors)
        self.assertIn('Require(allowed.Contains(sha)', successors)

    def test_disposable_desktop_and_fresh_workspace_protections(self):
        run = block(self.source, 'internal static async Task RunGifPlaybackChecksAsync')
        self.assertLess(run.index('PlatformNotSupportedException'), run.index('TemporaryWorkspacePaths.CreateDirectory'))
        self.assertIn('MEOWCHAN_DESKTOP_SMOKE_XVFB', run)
        self.assertIn('MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED', run)
        self.assertIn('Texts.SetLanguage(language)', run)
        self.assertIn('ShortcutService.Publish(owner.SessionHost.Config.ShortcutBindings)', run)
        self.assertIn('Directory.Delete(directory, recursive: true)', run)
        self.assertNotIn('catch (Exception', run)
        fixture = ROOT / 'tests/Fixtures/gif-playback/animated-loop.gif'
        self.assertEqual(hashlib.sha256(fixture.read_bytes()).hexdigest(),
                         '07ab9ab287a15e595a12797259548fa347c5a8c3bd9f5580b2b3e74b6093632d')


if __name__ == '__main__':
    unittest.main()
