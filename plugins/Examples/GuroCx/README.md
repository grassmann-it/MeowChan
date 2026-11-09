# Guro.cx source adapter (blocked)

`MeowChan.Plugin.GuroCx` is a compile-safe, SDK-only placeholder for the 1.0.3
experimental-source set. It deliberately registers **no source** and performs no
network request.

## Precise technical block

The bounded public Kokonotsuba contract that was verified for the current host is:

- thread listing: `/{board}/koko.php?mode=module&load=postApi&pageName=threads&page={n}`;
- thread reads: the same query endpoint with `pageName=thread&thread_uid={id}&page={n}`;
- media URLs are present in rendered response HTML, not in attachment metadata;
- `boards.guro.cx` redirects to `boards.guro.st` and the final host is a separate
  transport observation.

The current MeowChan SDK requires a path-only canonical route for every declared
thread-reading source and validates every returned snapshot URL against that route.
The proven API does not provide a path-only browser thread route, while the old
`/res/{id}.html` shape was explicitly not verified as the current normal link. Adding
that route, deriving media paths from `file_id`, or treating a redirect entry as a
second source would invent behavior. The adapter therefore returns a bounded
`technical_block` result and does not declare `ThreadRead`, `BoardCatalog`, media,
or a host grant.

A future implementation needs one of the following before it can honestly register:

1. a verified current browser thread path that fits the SDK route contract; or
2. an accepted SDK route-contract extension for query templates, plus a deterministic
   attachment-to-rendered-HTML association contract.

No endpoint, mirror, cookie, credential, or media fallback is included. Fixtures are
not wired because the existing example-plugin fixture contract lives in the separate
`tests/MeowChan.ExamplePlugins.Tests` project, outside this worker's permitted paths;
synthetic fixtures here would not be executed by that contract.
