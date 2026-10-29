# Isolated1.0.x upgrade fixtures

`prepare.py OUTPUT --version 1.0.0|1.0.1` exports the exact local release tag,
builds its real production code with a test-only seed overlay and creates a fresh
owned profile. Run serially under the repository build lock. No private profiles
or live HTTP are used. A receipt records old binary/source identity and selections.
A failed generation is not an upgrade proof; keep the attempt and use a fresh output.

With a successful fixture, run `Release102UpgradeTests` with
`MEOWCHAN_UPGRADE_1X_FIXTURE` set to its absolute output directory. The test is
one-use and fails if a new installation loses selected candidates' availability;
it does not silently migrate identities. Published-package/native update coverage
remains separate from this old-source fixture.
