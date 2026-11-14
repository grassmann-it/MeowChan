The JSON files are synthetic test data authored for this repository on
2026-10-22. IDs, names, texts, filenames, hashes, media tokens and times are
invented. They contain no copied real posts or media. Field shapes come from the
bounded, separately documented observations under plugins/Examples/*/Evidence
and the explicitly identified archived LynxChan documentation.

The tests run the public ISourcePlugin entry points against a deterministic
IHostHttp stub and validate/roundtrip the resulting SDK DTOs. They test parser,
identity, presence, multiple-file ordering, validators, terminal/failure and
bounded-response semantics. They do not instantiate the product worker, core
registry, media queue or archive and cannot substitute for those E2E checks.

`uboachan-thread.json` and `uboachan-catalog.json` (synthetic, 2026-11-14) give the
Uboachan site of the vichan family the same per-site thread/catalog fixture pair as
the other family sites; they exercise its MD5/SHA-1 digest field, empty original
names and the `ext: "deleted"` slot marker.
