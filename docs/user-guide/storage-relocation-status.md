# Storage relocation status

MeowChan currently has two separate storage actions. This page explains what works now and what the accepted future relocation design does **not** yet provide.

## Changing the download location for future captures

You can change the download folder in **Settings → Downloads → Download path**.

The path preview is lexical: MeowChan expands the text to an absolute path, but does not access the folder or resolve links while you type. When you click **Check folder**, or save after changing the folder/free-space reserve, MeowChan checks that the selected folder already exists, is a directory, accepts a write, and currently has enough space for the configured reserve. The check creates one uniquely named `.meowchan-writecheck-*` file and deletes only that file. MeowChan does not create the folder. A successful check is only a momentary write/capacity observation, not reserved space or a guarantee for later downloads.

After saving:

- A newly admitted independent thread or board uses the new folder.
- Existing jobs, watches, retries, and threads discovered under an existing board keep their previously recorded destinations.
- No existing file is copied, moved, renamed, or deleted.
- Your profile, session, archive catalog, work state, favorites, and ratings stay in their existing locations.

Keep the old storage available while existing watches or the local archive still use it. If you want a clean split, choose an existing folder outside your current MeowChan media root. The Settings check does not prove that two spellings are different disks or safely related locations.

## Moving an existing library

There is not yet a supported user command for moving an existing media library to another disk or folder.

The accepted product direction is a media-root-first relocation: copy the selected media root, verify the copy, stop/drain the relevant writers, switch revision-bound references, and resume. The old copy remains as a recovery copy and is not deleted. This is not a full profile restore, selected-thread migration, or automatic cleanup.

That complete flow requires MeowChan to inventory historical media roots, manifests, archive/work/session references, shared files, physical aliases/mounts, capacity, and all writers, then journal the switch and recover from crashes. Until the supported flow ships, changing **Download path** only changes where new independent captures go.

Do not turn a manual copy into an unsupported migration:

- Do not edit SQLite databases, JSON stores, manifests, or lock files to rewrite paths.
- Do not copy the media directory and assume that changing Download path reconnects it.
- Do not replace the old path with a link/junction and expect relocation safety.
- Do not delete the old copy after copying files.
- Do not run MeowChan against a copied profile/database while the original profile is still in use.

A normal backup of your profile and media files is still useful, but it is a backup, not a relocation switch. For now, keep using the old root when you need the existing library and use the download-path setting only for genuinely new independent captures.

## Internal engineering preflight

Current development includes an internal, passive `StorageRelocationPreflight` component. It is not a Settings control or a user-facing move command.

The preflight only reads metadata for the proposed source/destination root and their ancestors. It can report invalid or overly long paths, the same root, one root nested inside the other, missing roots/parents, a file where a directory is required, or a symbolic-link/junction/reparse-point component. It does not read media contents, create directories, write probe files, copy data, change references, stop writers, or acquire ownership.

Every preflight result remains blocked: it has no all-writer proof and no permission to copy, verify, switch, or recover. Even its best path observation means only “no lexical or link hazard was observed.” It does not establish physical disk/mount identity, capacity, a complete reference inventory, or a safe time to switch. It is groundwork for the future relocation feature, not a status indicator that a manual move is safe.
