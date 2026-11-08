# W32b3 — Schema 11: attachment-field presence

This document records the additive archive-schema change used by the current
native media-transfer work. It is a storage contract only. It does not by
itself open a new transfer format, a new execution path, or a release gate.

## Purpose

The archive already stored attachment metadata rows and field-presence flags.
The flags distinguish an absent field from a present field, but without the new
marker a present field with no attachment rows cannot distinguish:

- an explicit `attachments: null`; or
- an explicit empty `attachments: []`.

A reader therefore had to project every post without attachment rows as an
empty collection. Schema 11 adds the missing presence bit so a valid native
import can preserve the distinction between explicit `null` and explicit
empty without manufacturing attachment rows.

## Additive schema change

Schema 11 adds one column to `archive_posts`:

```sql
attachments_were_null INTEGER NOT NULL DEFAULT 0
    CHECK(attachments_were_null IN (0,1))
```

The column is deliberately on the post row, rather than in a side table or a
bit-packed field. Post content and its attachment-presence observation are
then written and validated in the same catalog transaction.

The archive header uses `PRAGMA user_version = 11` after the target schema has
been validated. The existing schema-10 media-transfer tables and their version
contracts remain unchanged by this increment.

## Migration and lifecycle

The catalog lifecycle reaches schema 10 before applying this increment. That
is true for both a fresh catalog and an older catalog that is being upgraded.
The schema-11 migration then:

1. requires the caller's active transaction and checks cancellation;
2. accepts schema 11 as an already-migrated, validation-only case;
3. accepts schema 10 as the only migration source;
4. validates the complete schema-10 shape before changing `archive_posts`;
5. executes the additive `ALTER TABLE ... ADD COLUMN` with the default marker;
6. validates the resulting column, constraint, and readable projection; and
7. lets `ArchiveCatalog` validate the complete target schema and stamp version
   11 before committing the outer transaction.

No internal transaction or commit is started by the migration helper. A failed
DDL operation, validation error, or cancellation remains part of the caller's
transaction and must roll back together with the version stamp. Versions other
than 10 and 11 are rejected by this migration helper; normal catalog upgrade
steps must bring older catalogs through their existing schema-10 path first.

For pre-11 read-only/schema-only compatibility projections, the presence
expression is the constant `0`. Such a projection does not repair or silently
upgrade a catalog. A normal catalog opened through `ArchiveCatalog` reaches
schema 11 before normal thread reads are used.

## Validation rules

The schema validator accepts versions through 11 and invokes the presence
validator for version 11. The presence validator requires:

- exactly one visible `archive_posts.attachments_were_null` column;
- SQLite type `INTEGER`;
- `NOT NULL`;
- default value `0`;
- no primary-key role; and
- the exact `CHECK(attachments_were_null IN (0,1))` constraint in the table
  definition.

It also executes a zero-row read of the new column. A missing, duplicated,
wrongly typed, nullable, differently defaulted, or differently constrained
column is invalid. Migration is validation-first; it is not a repair pass for
an already malformed schema.

## Null versus empty semantics

The marker is interpreted together with `PostFieldPresence.Attachments` and
the attachment rows for the same post:

| Source/persisted shape | `Attachments` presence bit | `attachments_were_null` | Attachment rows | Read result |
| --- | ---: | ---: | ---: | --- |
| field absent (legacy/partial scalar post) | 0 | 0 | none | field remains absent; the legacy view projects an empty list |
| explicit `attachments: null` | 1 | 1 | none | `Attachments == null` |
| explicit `attachments: []` | 1 | 0 | none | non-null empty collection |
| one or more attachment records | 1 | 0 | present | non-null collection containing the records |

The reader requires an integer marker of `0` or `1`; another storage class or
value is invalid. A marker value of `1` is valid only when the attachment field is present and
there are no attachment rows. Any other combination is corrupt and must be
rejected by the reader. Marker `0` is the legacy/non-null shape and can coexist
with zero or more attachment rows.

Writers update the marker after the post row and attachment changes. A post
with a present, null attachment field is marked `1` only when no attachment
rows remain for that post. A non-null collection, including an empty one, is
marked `0`. Inserting an attachment row clears the null marker.

This is an observation-preservation rule, not a delete rule. In particular, a
scalar-only merge may carry a null attachment observation while already-known
attachment rows exist; it must retain those rows and must not mark the post as
null. `ArchiveCaptureMerge` likewise keeps explicit null when the merged
attachment set is empty, while never interpreting null as permission to erase
known attachment metadata.

The native v2 content writer carries the wire-level null state into the
persisted `CapturedPost`. It does not infer original bindings from this bit,
create synthetic posts, or turn derived/evidence representations into current
original attachment rows.

## Legacy behavior and compatibility

Schema-10 and older catalogs have no persisted presence bit. During the normal
upgrade, the additive column's default makes every existing post marker `0`.
Consequently, legacy posts with no attachment rows retain their previous
non-null/empty projection; the migration does not invent knowledge that the
old database never stored.

Existing legacy readers and v1 mutation paths remain format/version-bound.
The schema-11 presence bit does not make a v2 operation readable through a v1
reader, and it does not grant a v1 apply path permission to consume v2 rows.
The version-aware persisted/recovery readers remain responsible for validating
versioned media-transfer operations and receipts. `RequirePersistedMediaSchema`
accepts exactly schema 10 or 11 and continues to reject every other archive
version. This reader compatibility does not make the v2 writer schema-agnostic:
the v2 writer additionally requires schema 11 and validates its attachment-
presence column, so it rejects a schema-10 catalog before attempting a write.
Runtime verification is recorded separately against the exact W32b3 freeze;
the guard alone is not recovery or release acceptance.

## Deliberate scope exclusions

This increment does **not** include or authorize:

- v2 merge;
- Desktop-v4 Apply or private/multithread Apply;
- inventory-aware export or a new exporter gate;
- automatic recovery, repair, or cleanup of interrupted operations;
- a second importer/coordinator or a new public import API;
- filesystem reconstruction from the presence marker; or
- release/publication/platform acceptance.

The current native v2 slice remains limited to its separately specified
existing-coordinator, public single-thread import path. Schema 11 only ensures
that its post attachment-field observation can survive persistence and later
reading without conflating `null` with `[]`.

## Verification status

This contract documents the current implementation shape. It does not claim
that tests, builds, recovery runs, or release acceptance have passed. Those
results must be recorded separately against the exact integrated source
freeze.
