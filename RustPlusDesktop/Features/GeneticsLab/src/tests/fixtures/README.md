# Scanner capture fixtures

The 17 checked-in `.rgba.gz` files are gzip-compressed RGBA pixel buffers. Their
dimensions and assertions live in the corresponding tests. These files are mandatory:
missing, corrupt, or incorrectly sized published captures must fail their tests.

Upstream's plant reader tests also reference the following ten captures that were
never included in the public repository or its introducing commit:

- `plant-planter-hemp-ui10-12`
- `plant-planter-orchid-ui10-13`
- `plant-planter-wheat-ui10-14`
- `plant-planter-green-berry-ui10-15`
- `plant-planter-blue-berry-ui10-16`
- `plant-planter-rose-ui07-22`
- `plant-planter-hemp-ui07-23`
- `plant-planter-red-berry-ui07-24`
- `plant-planter-red-berry-ui07-25`
- `plant-planter-sunflower-ui07-26`

The default `npm test` runs all available real captures and deterministic tests.
It prints this missing inventory and explicitly skips only the 12 cases requiring
these unpublished files. This does **not** verify those UI-scale/terrain regressions.
Each case automatically runs if its original capture is supplied. Existing available
inventory-title offset coverage runs independently of the unavailable planter case.

For release validation that requires the complete upstream capture coverage, set
`BETTERRUSTPLUS_REQUIRE_CAPTURE_FIXTURES=1` before running `npm test`. Missing optional
captures then fail collection with their names. Obtain the original files from their
author; do not generate images and label them as real game captures.

`plantNameReaderSynthetic.test.ts` uses explicitly generated pixels to check rejection
and geometry behavior independently. It supplements, rather than replaces, real game
captures and does not establish real-world OCR accuracy.
