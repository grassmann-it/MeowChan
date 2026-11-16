# Bounded plugin delivery / development-startup audit (2026-11-16)

Base: `fb375fcf9`. Inspection and focused tests only; no personal profile was
opened or changed. No desktop launch, solution build, CI or Danbooru change.

## Inventory and removal boundary

`packaging/PluginDelivery.targets` and `scripts/package-plugins.py` agree on
20 bundled assemblies and 17 editable source examples:

- Official projects: FourChan, EightKun, Danbooru (no editable copies shipped).
- Engine families: VichanFamily and JschanFamily.
- Distinct standalone deliveries: Kohlchan, Endchan, Dvach, SevenChan, BbwChan,
  CapybaraChan, EightchanMoe, Fchan, AnonImageboards and GuroCx. The last two are
  explicitly blocked scaffolds with no registered source routes, **not**
  implementations superseded by VichanFamily.
- Filters: ImageDimensionsFilter, FileRulesFilter, HashBlocklistFilter,
  AnimationFormatFilter and PngMetadataFilter.

The redundant single-site bundles are already absent from both inventories:

| Historical plugin suffix | Successor | Unchanged source ID |
| --- | --- | --- |
| `lainchan` | `meowchan.source.vichan` | `lainchan` |
| `leftypol` | `meowchan.source.vichan` | `leftypol` |
| `sushichan` | `meowchan.source.vichan` | `sushichan` |
| `uboachan` | `meowchan.source.vichan` | `uboachan` |
| `wizchan` | `meowchan.source.vichan` | `wizchan` |
| `zzzchan` | `meowchan.source.jschan` | `zzzchan` |

No further bundled entry can be removed as a proven family duplicate. Sharing
similar JSON fields or URL shapes is not evidence of equivalent parsing, routes,
host declarations or media semantics.

`Desktop/ChanSourceFamilies.cs` retains the historical plugin-to-source mapping.
`ChanSiteSwitches.MigrateFrom` takes over enabled choices once, without replacing
an explicit site choice. Source IDs, including the IDs used in thread/archive
identities, remain unchanged. `SupersededBundledPlugins` requires the successor
to cover every historical source, no present legacy candidate and no pin.
Retirement additionally requires completed switch takeover and retains an
immutable backup. Local legacy candidates/pins keep the ordinary conflict rules.
Source-integrity exceptions are owner-scoped: a legacy exception does not grant
an exception to the family automatically.

The intermediate delivery stage is rebuilt, but output delivery uses additive
`CopyToOutputDirectory=PreserveNewest`. An old build output could therefore
contain stale files even though the current inventory excludes them. This is a
possible separate cause, **not an observed condition in the user's output**.
Do not delete profile plugins, cache artifacts, pins or arbitrary output files
on that assumption. Automatic output pruning would need ownership evidence for
the exact generated files, protection for edited/unrecognized files, and a
separate explicit implementation decision.

## What the reported startup problem means

`PluginActivationProblems.ConfirmedTargetChanged` is emitted by
`PluginProfileStore.ApplyStartupCoreAsync` when an existing bundled repair binding
no longer matches the current target and no verified automatic update can advance
it. Its checks include target execution revision, slot, retained old provenance,
and, when present, the exact reviewed description fingerprint. This is not a
signature diagnostic. No failing profile/binding was inspected here, so which
check failed for the actual Vichan entry remains unproven.

The base already includes decision #130 in
`BundledCandidateMigration.CanAutomaticallyUpdateKnownBundle`:

- a unique current, verified shipped candidate at the established identity/path;
- no explicit pin, owner conflict or incompatible saved settings;
- an unchanged or structurally narrowing declaration, not added sources,
  hosts, routes, capabilities, filter access or other unknown changes;
- current delivery and imported-cache bytes reverified under the writer lease;
- serialized live permission authority, without concurrent rights changes;
- existing rights retained, not newly granted;
- accepted repair bindings advanced to the new exact executable/description.

This already covers ordinary in-place developer rebuilds and the established,
confirmed-pending rebuild loop. Relocation to a different worktree is deliberately
not the same authority. A plugin name or filename cannot attest provenance.
`--development` in the package verifier only allows framework-dependent helper
outputs; it is not a runtime trust switch.

## Safe result now and choices before a trust-policy patch

Implemented only an inexpensive regression asserting that the staging task's
assembly/source allowlists agree with the graph/verifier inventory, including
multiplicity. Existing inventory tests already exclude the retired single-site
bundles. No production delivery or trust policy changed.

1. **Recommended first: preserve the current policy.** Obtain a read-only,
   redacted comparison of the failing binding and freshly staged candidate:
   selected/target IDs and paths, slot, execution/description hashes, presence,
   pending/last-good basis, pin, proof and exact reason automatic update failed.
   If unchanged/narrowing in-place code is rejected, fix that exact eligibility
   defect with a reproducing synthetic test, rather than adding broad trust.
2. **Developer-default exception, if still wanted:** approve its precise scope
   before implementation. Use a host build-time developer-only gate plus a
   fresh staging inventory binding the allowed bundled slot, plugin identity,
   SDK and actual file hashes. VichanFamily is under `plugins/Examples`, so
   “official” must mean the host's bundled inventory, not the directory name or
   a plugin's claimed ID. Limit the exception to established, unique, unpinned
   in-place unchanged/narrowing updates; keep relocation and declaration
   widening subject to review. A marker in a writable directory alone is not a
   signature or sufficient trust anchor. No SDK bypass, foreign-signature
   acceptance, grant creation, pin override, local-plugin trust or release-path
   change. Cross-worktree trust or automatic widening would be separate policy
   expansions, not implicit consequences of `dotnet run` convenience.

The SDK/toolchain mismatch of the separately diagnosed pinned Danbooru worker
remains independent; nothing here updates, unpins or reauthorizes it.

## Focused validation

- `python3 -m unittest discover -s tests -p test_plugin_packaging.py -v`: 25 passed.
- Filtered `MeowChan.PluginHost.Configuration.Tests`: 33 passed, covering
  known-delivery rebuilds, binding/pending loops, Vichan-like narrowing,
  worktree relocation, widening and superseded-source ownership.
- A second filtered host configuration run: 46 passed, covering integrity,
  stale provenance, explicit pins and SDK/declaration boundaries.

Only the host configuration test project and its core/SDK dependencies were
built; the application/plugin delivery graph and full solution were not built.
Existing XML-doc/xUnit analyzer warnings were observed; no lockfile edits were
needed.

## Read-only dotnet-run diagnosis (2026-11-16)

The active profile was inspected without writing, resetting or unpinning it.
The old `meowchan.source.vichan` record has one current bundled candidate at the
same manifest path, no pin, and an exact-description repair binding whose old
execution is the retained last-good build and whose pending target is an earlier
rebuild. The current startup observation is a later execution of that same
candidate. The old `69c088a47` implementation rejected this because its automatic
known-bundle predicate treated the retained `lainchan`/`wizchan` source-integrity
exceptions as inherited rights. With the automatic path disabled, the binding
comparison fell through to `PluginActivationProblems.ConfirmedTargetChanged`.
This is a repair-policy boundary, **not** a signature failure.

The integration base's #130 implementation in
`MeowChan.PluginHost/Configuration/BundledCandidateMigration.cs` is the narrow
fix: for an established, unpinned, uniquely proven candidate at the exact
shipped manifest path it can carry existing rights across an unchanged or
narrowing in-place rebuild; it still re-verifies the manifest/cache bytes under
the writer lease and rejects relocation, widening, SDK/API changes, foreign
provenance and user/profile plugins. This should be exercised by rebuilding the
integration base, not by adding a `dotnet run` blanket-trust switch.

For the pinned `meowchan.official.danbooru` record, the active cached build is
SDK-incompatible (`SourcePostId` is absent) while the current staged binding
passes the SDK probe. The pin correctly prevents automatic replacement. The
Booru recovery action therefore opens the existing exact-build review and, for
an unchanged same-slot bundled rebuild, offers an explicit current-build
selection that clears the pin only when confirmed. It never changes a pin during
startup, bypasses SDK validation, grants, signatures or user plugins. A separate
read-only worker probe also completed a current Danbooru `cat` search with HTTP
200 and 20 results; this validates that staged snapshot only, not arbitrary user
builds or every live provider condition.
