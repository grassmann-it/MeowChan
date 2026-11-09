# Anon Imageboards source adapter (blocked)

`MeowChan.Plugin.AnonImageboards` is a compile-safe SDK-only placeholder for the
MeowChan 1.0.3 experimental-source goal. It intentionally registers **no source
routes** and performs no HTTP request.

## Why this is blocked

The public entrance observed on 2026-11-09 was:

- `https://anonimageboards.com/` → HTTP 302 to
  `https://anonpremium.com/site-offline-members-only/`;
- `https://boards.anonimages.com/` → the same members-only destination;
- `https://anonimageboard.com/` → the public entrance before the same redirect;
- `https://anonimageboards.org/` → Cloudflare 522 in the probe.

That proves neither a current public board route nor a catalog, thread, media or
provider-verified 404 contract. A historical Tinyboard/vichan classification is
not enough to construct an adapter. The plugin therefore does not guess a
canonical route, follow a replacement host, create credentials/settings, reuse
cookies, or label an invented fixture as working source support.

`AnonImageboards.cs` follows the existing blocked-plugin contract: description is
inert, the source list is empty, and calls return a precise
`public_entrance_unavailable` failure without network access. It is not an
activatable source until a new public probe establishes the complete contract.

## Fixtures and recheck gate

`Fixtures/` contains only negative availability evidence and a recheck plan:

- `redirect-members-only.http` records the public redirect boundary without
  copying members-only content;
- `old-domain-unavailable.http` records the separate unavailable-domain case;
- `no-public-source.plan.md` lists the evidence required before implementation.

No synthetic Tinyboard/vichan catalog or thread fixture is included because that
would falsely claim a current public Anon Imageboards contract. This worker's
fixtures are not live availability, access, package or platform acceptance
proof.
