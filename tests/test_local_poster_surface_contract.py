#!/usr/bin/env python3
"""Static regression guards for the three local poster surfaces.

Run: python3 -B tests/test_local_poster_surface_contract.py -v
Reads source only: no .NET, GUI, file fixtures, network or native decoder. These
wiring/order checks do not replace the existing C# integrity and lifetime tests.
"""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]


def source(name):
    text = (ROOT / "Desktop" / name).read_text(encoding="utf-8")
    # Ignore standalone comments and formatting, not executable branches.
    return " ".join(re.sub(r"(?m)^\s*//.*$", "", text).split())


def section(text, start, end):
    return text[text.index(start):text.index(end, text.index(start) + len(start))]


class LocalPosterSurfaceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.archive_list = source("ArchiveListThumbnails.cs")
        cls.archive_tile = source("ArchiveMediaTileView.cs")
        cls.media = source("MediaTileThumbnails.cs")

    def assert_ordered(self, text, *fragments):
        position = 0
        for fragment in fragments:
            with self.subTest(fragment=fragment):
                found = text.find(fragment, position)
                self.assertGreaterEqual(found, 0, f"Missing/out-of-order: {fragment}")
                position = found + len(fragment)

    def test_archive_list_routes_video_before_the_image_only_cache(self):
        load = section(self.archive_list, "internal static async Task<Bitmap?> LoadAsync(",
                       "internal static Task<byte[]?> PosterPngAsync(")
        self.assertIn("internal const int MaximumWidth = 160, MaximumHeight = 120;", self.archive_list)
        self.assert_ordered(load,
            "token.ThrowIfCancellationRequested();",
            "if (Kind(row) == SavedMediaKind.Video)",
            "return await MediaTileThumbnails.LoadPosterAsync(row.Manifest, row.Asset, posterCacheDirectory, "
            "MaximumWidth, MaximumHeight, CacheKey(row), token)",
            "if (Kind(row) != SavedMediaKind.Image) return null;",
            "return await MediaTileThumbnails.KeepAsync(")

    def test_archive_tile_default_video_decoder_uses_the_shared_local_poster(self):
        self.assertIn('DecodeAsync(row, token, System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"))',
                      self.archive_tile)
        decode = section(self.archive_tile, "internal static Task<Bitmap?> DecodeAsync(",
                         "public ArchiveMediaTileView()")
        self.assertIn("UsesVideoPoster(row) ? MediaTileThumbnails.LoadPosterAsync(row.Manifest, row.Asset, "
                      "posterCacheDirectory, PosterWidth, PosterHeight, "
                      "MediaTileThumbnails.PosterKey(row.Asset, PosterWidth, PosterHeight), token) "
                      ": ArchivePage.DecodeThumbnailAsync(row, token);", decode)
        # A poster already carries a lease. The immediate acquire/Add path is not for videos or injected decoders.
        load = section(self.archive_tile, "private async Task LoadAsync()", "private void UpdateDescription()")
        self.assertIn("string? key = ReferenceEquals(decode, DefaultThumbnailDecoder) && !UsesVideoPoster(row) "
                      "? MediaTileThumbnails.ByteKey(row.Asset) : null;", load)
        self.assertIn("if (key != null && MediaTileThumbnails.Cache.TryAcquire(key)", load)
        self.assertIn("if (key != null && bitmap != null) bitmap = MediaTileThumbnails.Cache.Add(key, bitmap);", load)

    def test_library_video_and_manifest_video_both_use_verified_poster_loading(self):
        load = section(self.media, "internal static async Task<Bitmap?> LoadAsync(",
                       "private static async Task<Bitmap?> LoadLibraryPosterAsync(")
        poster_call = "return await LoadLibraryPosterAsync(tile, openManifest, posterCacheDirectory, token)"
        self.assert_ordered(load, "if (entry.Kind == SavedMediaKind.Video)", poster_call,
            "MediaAssetReference? asset = Matching(manifest, entry);", "if (asset == null) return null;",
            "if (SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) == SavedMediaKind.Video)",
            poster_call, "return await KeepAsync(")
        library = section(self.media, "private static async Task<Bitmap?> LoadLibraryPosterAsync(",
                          "internal static async Task<(byte[] Png, string AssetId)?> PosterPngAsync(")
        self.assert_ordered(library, "return await KeepVerifiedPosterAsync(Cache, key,",
            "await PosterPngAsync(tile, openManifest, posterCacheDirectory, cancellation)",
            "if (poster == null) return null;", "VideoPosterCache.DecodeBitmapAsync(")

    def test_shared_loader_keeps_the_verified_original_boundary(self):
        load = section(self.media, "internal static Task<Bitmap?> LoadPosterAsync(",
                       "internal static async Task<T?> KeepVerifiedPosterAsync<T>(")
        self.assertIn("KeepVerifiedPosterAsync(Cache, key, cancellation => "
                      "VideoPosterCache.GetPngAsync(manifest, asset, posterCacheDirectory, cancellation, Verified),", load)
        self.assertIn("VideoPosterCache.DecodeBitmapAsync(png, asset.AssetId, maximumWidth, maximumHeight, cancellation)", load)
        self.assertIn("VideoPosterCache.GetPngAsync(row.Manifest, row.Asset, posterCacheDirectory, token, "
                      "MediaTileThumbnails.Verified)", self.archive_list)

    def test_verification_and_cancellation_precede_every_warm_poster_lease(self):
        keep = section(self.media, "internal static async Task<T?> KeepVerifiedPosterAsync<T>(",
                       "internal static void Release(Bitmap bitmap)")
        self.assert_ordered(keep,
            "token.ThrowIfCancellationRequested();",
            "byte[]? png = await verifiedPoster(token)",
            "token.ThrowIfCancellationRequested();",
            "if (png == null) return null;",
            "if (key != null && cache.TryAcquire(key) is { } hit) return hit;",
            "T? preview = await decode(png, token)",
            "if (token.IsCancellationRequested) { preview?.Dispose(); token.ThrowIfCancellationRequested(); }",
            "return preview != null && key != null ? cache.Add(key, preview) : preview;")
        cached = section(self.media, "internal static Bitmap? TryGetCached(", "internal static string? ByteKey(")
        self.assertIn("tile.Entry.Kind == SavedMediaKind.Image &&", cached)

    def test_byte_identity_and_preview_size_remain_part_of_keys(self):
        self.assertIn('asset.Sha256.ToLowerInvariant() + ":" + asset.Length + size', self.media)
        self.assertIn('ByteKey(asset, $"@poster{width}x{height}")', self.media)
        self.assertIn('SavedMediaKind.Video => MediaTileThumbnails.ByteKey(row.Asset, "@poster160x120")', self.archive_list)
        self.assertIn('bytes + $"@poster{PosterWidth * 2}x{PosterHeight * 2}"', self.media)

    def test_grouped_occurrences_must_match_the_index_before_verification(self):
        self.assertIn("entry.Presence == MediaLibraryPresence.Present && entry.Directory != null && entry.AssetId != null",
                      self.media)
        self.assertIn(".Take(MaximumPosterCandidates)", self.media)
        poster = self.media[self.media.index("internal static async Task<(byte[] Png, string AssetId)?> PosterPngAsync("):]
        self.assert_ordered(poster, "token.ThrowIfCancellationRequested();",
            "await openManifest(entry.Directory!, token)",
            "catch (Exception exception) when (exception is not OperationCanceledException)",
            "if (Matching(manifest, entry) is not { } asset) continue;",
            "await VideoPosterCache.GetPngAsync(manifest, asset, posterCacheDirectory, token, Verified)")
        self.assertIn("manifest.FindById(entry.AssetId!) is { } asset && asset.Length == entry.Length "
                      "&& string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) ? asset : null;", poster)

    def test_recycled_tile_discards_late_leases_before_publishing(self):
        load = section(self.archive_tile, "private async Task LoadAsync()", "private void UpdateDescription()")
        self.assert_ordered(load, "int request = _request;", "await ThumbnailGate.WaitAsync(loading.Token);",
            "await Task.Run(() => decode(row, loading.Token), loading.Token)", "finally { ThumbnailGate.Release(); }",
            "if (request != _request || !_visible || !IsEffectivelyVisible || !ReferenceEquals(DataContext, row) "
            "|| loading.IsCancellationRequested)",
            "{ if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }",
            "_bitmap = bitmap;", "_image.Source = bitmap;")
        self.assertIn("finally { if (ReferenceEquals(_loading, loading)) _loading = null; }", load)

    def test_reset_cancels_generation_and_releases_the_displayed_lease(self):
        reset = self.archive_tile[self.archive_tile.index("private void Reset()") :]
        self.assert_ordered(reset, "++_request;", "_loading?.Cancel();", "_image.Source = null;",
            "if (_bitmap != null) MediaTileThumbnails.Release(_bitmap);", "_bitmap = null;")
        changed = section(self.archive_tile, "DataContextChanged +=", "EffectiveViewportChanged +=")
        detached = section(self.archive_tile, "DetachedFromVisualTree +=", "private void OnAncestorVisibilityChanged(")
        self.assertIn("Reset();", changed)
        self.assertIn("_visible = false; Reset();", detached)
        self.assertIn("if (sender is Visual { IsVisible: false }) { _visible = false; Reset(); }", self.archive_tile)
        self.assertIn("if (visible) _ = LoadAsync(); else Reset();", self.archive_tile)


if __name__ == "__main__":
    unittest.main()
