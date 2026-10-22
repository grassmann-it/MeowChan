# Official source parity fixtures

Synthetic public-API-shaped data, assembled 2026-10-22 from the existing repository
cases in `tests/MeowChan.Tests/ProviderHttpIntegrationTests.cs`,
`ContentRuntimeTests.cs`, and `ContentRevisionTests.cs` at baseline `dc053f8`.
No live requests, site posts, cookies or private data were collected.

`thread.json` combines the existing 4chan capture and 8kun `extra_files` cases
with HTML entity decoding, explicit null/empty fields, omitted fields, text-only
posts, a deleted primary file and deleted extra attachment. `catalog.json`
exercises multiple pages and a title-less entry. HTTP304/404/403/206, archive,
8kun lock, deferred responses and malformed bodies are provided directly by
bounded in-memory transports in the test suite. These are parity evidence,
not evidence of live site availability.
