# Counter edge-case results

Each row covers English + German at 760, 1099 and 1100 dip client width (six rendered cells).

| Selected count | Exact count | No wrap | Maximum grid-rectangle delta (dip) | Full accessible Clear name |
|---:|---|---|---:|---|
| 1 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 9 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 10 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 99 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 100 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 999 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 1000 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 1099 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |
| 1100 | Pass 6/6 | Pass 6/6 | 0 | Pass 6/6 |

The Media geometry assertions passed (54/54), and the entire header/grid check completed with an empty failures.txt. **The overall native harness did not pass:** it recorded one initial GLX initialization error because llvmpipe is blacklisted, then successfully used the software renderer. The strict harness verdict is retained unchanged: `SMOKE FAILED: exit=0, checks=True, closed=True, errors=1`. No second GUI invocation was run.

The shortened label was observed at both 1099/1100-dip widths in English, and at all three widths in German. In every case its accessible name remained the full phrase.

## Captures

| Client width (dip) | 1099 selected | 1100 selected |
|---:|---|---|
| 760 | [English](en-counter-1099-width-760.png) · [German](de-counter-1099-width-760.png) | [English](en-counter-1100-width-760.png) · [German](de-counter-1100-width-760.png) |
| 1099 | [English](en-counter-1099-width-1099.png) · [German](de-counter-1099-width-1099.png) | [English](en-counter-1100-width-1099.png) · [German](de-counter-1100-width-1099.png) |
| 1100 | [English](en-counter-1099-width-1100.png) · [German](de-counter-1099-width-1100.png) | [English](en-counter-1100-width-1100.png) · [German](de-counter-1100-width-1100.png) |
