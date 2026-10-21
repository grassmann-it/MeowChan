# Bounded release probes

Run these only after the release source is committed and a serial build/live-check slot is available. Neither command modifies an existing profile or earlier evidence. Keep at least 2 GiB free plus 512 MiB working headroom. Existing dotnet applications should remain untouched.

From the repository root, with a full frozen commit and new output paths:

```sh
python3 scripts/verify-library-release.py --commit FULL_COMMIT --output artifacts/NEW-library
python3 scripts/verify-live-release.py --commit FULL_COMMIT --source artifacts/NEW-library/source --output artifacts/NEW-live
```

The library command creates a detached checkout, performs a locked Release restore and pack with fresh producer caches, then reuses `test-local-package.py` for a separate fresh consumer cache. That consumer checks the exact nupkg hash, forbids desktop dependencies and exercises public archive/download APIs, including received-byte counters and media restore. Evidence and source checkout remain available under the output directory. It does not publish a package.

The live command requires that exact clean detached checkout. It builds these small console probes in a separate artifact directory. It fetches one catalog per provider (4chan `/g/`, 8kun `/tech/`; 30 seconds and 2 MiB maximum each), chooses one thread whose catalog reports one small JPEG/PNG original, and admits only that thread. There is no fallback/retry selection if the catalog has no eligible thread. Each provider has at most eight HTTP attempts and one media request; originals are restricted to 200,000 bytes by media rules. A changed/removed thread can fail the check; report that evidence rather than broadening the budget. Initial capture checks verified media, local indexed search, and HTML export without omissions. A separate process reopens the same isolated profile with every HTTP attempt blocked and requires zero attempted requests. No browser screenshots or personal profiles are read.

`result.json`, command logs, selected catalog responses, per-provider results, profiles, exports, and fresh-process evidence are retained. These probes establish Linux library behavior against real providers. They do not establish native desktop/apphost or five-platform release acceptance.
