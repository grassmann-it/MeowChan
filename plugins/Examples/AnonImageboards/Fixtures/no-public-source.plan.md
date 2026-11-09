# Recheck gate for Anon Imageboards

This is a blocker fixture, not a source fixture. Before adding a route or a
synthetic parser, a fresh public probe must establish all of the following on a
single named host:

1. a public board URL and canonical board identity;
2. a board catalog response with honest completeness/pagination semantics;
3. a thread response with stable post IDs and field presence;
4. original-media URLs and a provider-owned media identity;
5. redirect behavior that stays on the verified host;
6. exact 404/410 behavior for an unknown board and thread; and
7. bounded handling for rate limits, challenges and non-200 responses.

Do not log in, accept cookies, solve a challenge, use an alternate domain, or
copy members-only content during the recheck. Historical software labels and
search/archive snippets are insufficient evidence.
