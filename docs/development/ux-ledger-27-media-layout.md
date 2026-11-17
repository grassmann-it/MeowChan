# UX-LEDGER-27 — Media page layout

## A — Selection grouping

Before: `N selected | Queue · Tag together · Selection as gallery · Export · More | Clear selection`.

After: `Queue · Tag together · Selection as gallery · Export · More | reason / slack | Clear selection · N selected`.

Commands lead as one non-wrapping group; clear and the polite live count dock together at the end. The building reason remains after commands in the fill area. The 32 dip control-height resource and counter ReserveText are unchanged. FitSelectionLine measures both groups, retaining its clear-shortening and overflow stages.

Recommendation: **align Tagging to this new actions-left / clear-and-counter-right contract** in a separate change. It currently puts count left, actions middle, clear right. This PR deliberately does not change Tagging.

Validation: affected desktop test project only; selection layout and multi-selection command filters: **16 passed, 0 failed, 0 skipped**. Layout assertions measure 760 / 1100 / 1440 dip widths, selection entry/exit grid position, 32 dip height, left/right grouping, polite count and reserved width. The headless fixture supplies the product control-height resource locally (its bare visual root has no application resource parent). No GUI smoke suite or native-input / theme / screenshot acceptance claimed.
