# Package-only libmeowchan consumer

This .NET 10 console application references the local `libmeowchan` NuGet package. It has no project reference to either the library source or the desktop application.

The example registers its own provider through the public API, serves deterministic bytes from an injected `HttpMessageHandler`, and lets the real registry, queue and media store perform the download. It serializes the typed `ThreadSnapshot` from `Registry.GetSnapshot()` as JSON and checks the saved bytes, media totals and actual queue counts. A second session with fresh provider, queue and store instances reuses the committed file; the handler must receive exactly one media request across both sessions. Both sessions shut down before their temporary output is deleted.

The example also initializes the packaged SQLite catalog, captures a synthetic post, disposes the writer and profile lease, and reads the post through the independent archive API, searches its indexed text and resolves a local quote/backlink and exports an independent HTML folder containing the captured post and verified saved medium. This checks native SQLite resolution from the actual package graph.

The custom provider freezes one complete `MediaProviderMetadata` observation before enqueueing: media/variant, original URL and filename, expected size, MD5 and SHA-256. It computes the public `MediaRevisionKeys` acquisition key and post/position attachment key, and supplies the matching `AttachmentRevisionReference` through `DownloadItem`. The real queue/store publishes the bytes and exact attachment binding together. A later independent `ArchiveCatalog.CaptureAsync` uses the same raw metadata and attachment key. The gallery checks that this is an exact, strictly accepted binding to the committed asset, then reads its verified bytes. Work-state checks retain SHA, acquisition, references and committed asset identity across both sessions.

New captures receive host revision keys even if a caller omits the key. Merely capturing a matching media ID/size/MD5 after an earlier unbound download does not bind the new observation to those bytes. Custom library providers should pass the observed acquisition and references before enqueueing; the manifest commit establishes their association. No source permission is needed for this fixture's matching provider values, and a historical binding never grants permission for another revision. The second session reuses the exact verified binding without another request.

A historical unchanged consumer binary passed with its original Core package, then failed its gallery attachment-association check after only the Core DLL was replaced. Constructor and method signatures were still callable; this is a behavior incompatibility, not a successful old-binary compatibility check. Merely recompiling the old source does not add the missing association: update the source to pass the metadata/acquisition/reference flow above and then rebuild against the current package. This affects newly captured observations after an unbound download; it does not reclassify genuinely old stored null-key attachments or authorize an unsafe fallback. The library remains explicitly unstable in 1.x and provides no binary compatibility promise.

The package query probe also compiles the preserved positional `ArchiveThreadQuery` constructor, deconstruction and `with` expression, then exercises the public multi-source/board `ArchiveSearchSpec`, strict JSON v1 round-trip, calendar-date resolution, all three named `BySpec` reader methods and query-to-collection export. It checks defensive copies, frozen date bounds, cancellation, disposal and representative validation failures from the actual package. Work-state membership remains the caller's responsibility. The probe prints a `queryApi: PASS` record only after these checks complete.

The first-poll completion in `DemoThread` is specific to this fixture. Waiting for queue idle immediately after `AddAsync` would race the asynchronous first poll, so the sample waits for that poll to enqueue its medium first.

Run the repository's isolated package verifier:

```sh
dotnet pack libmeowchan/libmeowchan.csproj -c Release -o artifacts/packages
python3 scripts/test-local-package.py --feed artifacts/packages --consumer samples/LibMeowChan.Consumer/LibMeowChan.Consumer.csproj
```

Use a fresh feed containing one candidate. The verifier selects its version, applies `-p:LibMeowChanVersion`, restores from a fresh NuGet cache with `libmeowchan` mapped exclusively to the local source, and checks that the resolved graph contains no desktop dependencies. Without an override, the sample uses the repository's product version from `Directory.Build.props`. The verifier also checks the restored package's SHA-256 against the candidate before running the sample. External dependency packages may be downloaded from nuget.org during restore; the running example performs no external network requests.

This sample remains outside `MeowChan.sln` so ordinary source builds do not depend on a previously packed library.
