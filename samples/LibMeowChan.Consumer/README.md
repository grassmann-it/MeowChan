# Package-only libmeowchan consumer

This .NET 10 console application references the local `libmeowchan` NuGet package. It has no project reference to either the library source or the desktop application.

The example registers its own provider through the public API, serves deterministic bytes from an injected `HttpMessageHandler`, and lets the real registry, queue and media store perform the download. It serializes the typed `ThreadSnapshot` from `Registry.GetSnapshot()` as JSON and checks the saved bytes, media totals and actual queue counts. A second session with fresh provider, queue and store instances reuses the committed file; the handler must receive exactly one media request across both sessions. Both sessions shut down before their temporary output is deleted.

The example also initializes the packaged SQLite catalog, captures a synthetic post, disposes the writer and profile lease, and reads the post through the independent archive API, searches its indexed text and resolves a local quote/backlink and exports an independent HTML folder containing the captured post and verified saved medium. This checks native SQLite resolution from the actual package graph.

The first-poll completion in `DemoThread` is specific to this fixture. Waiting for queue idle immediately after `AddAsync` would race the asynchronous first poll, so the sample waits for that poll to enqueue its medium first.

Run the repository's isolated package verifier:

```sh
dotnet pack libmeowchan/libmeowchan.csproj -c Release -o artifacts/packages
python3 scripts/test-local-package.py --feed artifacts/packages --consumer samples/LibMeowChan.Consumer/LibMeowChan.Consumer.csproj
```

Use a fresh feed containing one candidate. The verifier selects its version, applies `-p:LibMeowChanVersion`, restores from a fresh NuGet cache with `libmeowchan` mapped exclusively to the local source, and checks that the resolved graph contains no desktop dependencies. Without an override, the sample uses the repository's product version from `Directory.Build.props`. The verifier also checks the restored package's SHA-256 against the candidate before running the sample. External dependency packages may be downloaded from nuget.org during restore; the running example performs no external network requests.

This sample remains outside `MeowChan.sln` so ordinary source builds do not depend on a previously packed library.
