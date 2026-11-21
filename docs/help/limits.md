# Limits: what MeowChan does not do

A short list of things that look similar but are not the same, so a result is not
read as more than it is. Status of MeowChan 1.1.1.

## Search and sources

- **Several tags are one search, not several searches.** A search with two tags asks
  the source once for posts that carry both. It does not combine the results of two
  separate searches.
- **A Danbooru search is limited.** At most two tags plus one optional `rating:` term,
  20 results per page and at most 5 pages. MeowChan sends the terms exactly as typed
  and adds no rating filter and no extra search terms.
- **Loaded pages are not the whole source.** An empty page or the last allowed page
  only means that nothing more was loaded here. It does not say that the source has
  nothing else. Narrow the tags, or create a watcher to follow new posts.
- **Sources are read-only.** MeowChan reads from sources. It does not post, edit,
  vote or sign in at a provider.

## Files and duplicates

- **A file name is not a content comparison.** Two files with the same name can differ,
  and different names can hide the same bytes. Imports compare content by SHA-256.
- **Import is not sync.** Importing copies (or links) files once. Later changes to the
  originals are not followed, and your originals are never moved or changed.
- **A read-only media folder stays read-only.** Tags, favorites and ratings live in
  your profile, not in the files of the folder.

## Offline

- **Offline viewing is not offline editing.** What is saved locally can be read, filtered
  and tagged without a connection. Anything that needs the source (searching, loading
  originals, checking a watcher) waits until you are connected.

## Where each limit is set

Size and count limits are settings under **Settings > Storage and limits**, with a
default, a minimum and a maximum. Fixed ceilings of a source (such as the Danbooru page
limits above) are not settings. See the
[source capability matrix](../plugins/source-capabilities.md) for what each source
supports.
