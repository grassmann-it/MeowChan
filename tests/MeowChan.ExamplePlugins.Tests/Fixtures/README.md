All six JSON files are synthetic test data authored for this repository on
2026-10-22. IDs, names, texts, filenames, hashes, media tokens and times are
invented. They contain no copied real posts or media. Field shapes come from the
bounded, separately documented observations under plugins/Examples/*/Evidence
and the explicitly identified archived LynxChan documentation.

The tests run the public ISourcePlugin entry points against a deterministic
IHostHttp stub and validate/roundtrip the resulting SDK DTOs. They test parser,
identity, presence, multiple-file ordering, validators, terminal/failure and
bounded-response semantics. They do not instantiate the product worker, core
registry, media queue or archive and cannot substitute for those E2E checks.
