# Local media filters

This page describes the source/filter wave being integrated separately from the
1.0.0-alpha.2 release candidate. It is not a release announcement or a claim that
these filters have passed packaged or native-platform checks. The adjacent
plugin READMEs record their actual implementation limits and test evidence.

These filters help decide which newly acquired originals to keep. They run
locally through the existing plugin worker. The host downloads the original to
its controlled staging area first; a filter does not save that initial network
transfer. A matching result lets the host store the same verified bytes.
Existing archived files and posts are not changed or deleted by a filter.

| Filter | What it checks | Useful boundary |
| --- | --- | --- |
| [File rules](../../plugins/Examples/FileRulesFilter/README.md) | Inclusive minimum/maximum original size and an optional literal original-name condition | Uses the measured original size. Missing original name is a technical omission when a name condition is active. It does not guess the name from the URL. |
| [SHA-256 blocklist](../../plugins/Examples/HashBlocklistFilter/README.md) | Rejects originals whose measured SHA-256 is in the rule's explicit list | Exact file identity, not visual similarity. Changing or re-encoding the image changes its hash. |
| [File format and animation](../../plugins/Examples/AnimationFormatFilter/README.md) | JPEG, PNG/APNG, GIF and WebP containers; optionally one frame or multiple evidenced frames | Reads original bytes instead of trusting the extension/MIME. Container inspection does not prove that every pixel can be decoded. |
| [PNG metadata fields](../../plugins/Examples/PngMetadataFilter/README.md) | Presence or absence of the exact PNG text key, such as `parameters`, `workflow` or `prompt` | Case-sensitive key presence. An empty value counts as present. This does not read prompt contents, interpret a workflow or identify AI-generated images. |

## Choose one filter for a rule

Open **Rules**, choose the intended source, board or thread scope and select one
image filter. Review its rule parameters and save explicitly. Grant only the
required data access for that rule's scope; merely selecting a plugin does not
grant access. See the [filter and grant guide](filters-and-settings.md#give-only-the-intended-scope-access)
for the host's authorization model.

Each effective rule currently holds **one plugin filter**. These four additions
do not create a chain of several plugin filters. The host's ordinary media-type
and size conditions still apply before plugin evaluation. Keep this distinction
in mind when an original never reaches the selected filter.

Field labels/help supplied by these plugins are English. The application's own
controls, validation chrome and navigation follow its EN/DE setting; the current
SDK has no locale-specific plugin-description contract. Technical setting keys
and saved enum values stay stable across host-language changes.

## Read the result

A filter's **Match** is a rule decision; it becomes a saved original only after
the host successfully commits the verified bytes. **NoMatch** is an intentional
rule exclusion. **Unavailable**, **Error** and **Timeout** mean the filter could
not evaluate the input and remain technical omissions. They must not be read as
successful rejection or successful completion.

Unsupported formats, incomplete scans, corrupt relevant container structure,
missing original names and byte/block budgets have distinct reasons. A missing
PNG metadata field is reported only after an adequately complete scan. Text
values in compressed PNG metadata are not decompressed for a key-presence test,
so presence says nothing about their textual validity.

The existing staging, read-size, cancellation, revision and permission limits
remain in force. Revoking a grant or changing the effective rule invalidates an
older in-flight answer. The plugins do not request extra storage access, launch
external decoders or download classification models. Manual tags, automatic
tagging and filter chaining are separate proposals.
