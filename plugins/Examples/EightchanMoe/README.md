# 8chan.moe source plugin

`meowchan.source.eightchanmoe` is a bundled `MeowChan · experimental · not
live-verified` SDK-only adapter for the documented LynxChan/Aleph-shaped contract
at `8chan.moe`. It supports the same bounded catalog/page/thread JSON shapes as
the observed LynxChan family, but current public requests are challenge-gated.

## Challenge boundary

The adapter accepts no browser context, cookies, login, PoW solver, JavaScript
execution, mirror or alternate host. If a bounded metadata response contains the
observed POWBlock/challenge markers, it returns `challenge_required`; challenge
HTML is never parsed as a successful catalog or thread. A 404 is only terminal for
the exact requested endpoint. Redirects and all other HTTP statuses remain
failures/deferred results under host control.

The declared transport host is exactly `8chan.moe`. No `8chan.se`, `8chan.cc`,
`redchannit.*` or other historical/uncertain alias is granted. Provider-supplied
original paths are accepted only below `/.media/`; thumbnails are not advertised.

## Evidence and limits

POWBlock prevented live metadata checks. `Fixtures/` contains synthetic
metadata, challenge, 404 and media HTTP examples.
