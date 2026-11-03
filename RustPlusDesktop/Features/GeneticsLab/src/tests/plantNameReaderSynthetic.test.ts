import { describe, expect, it } from 'vitest';
import { plantNameRect, readPlantName, searchPlantTitle } from '../services/scanner/plantNameReader.ts';
import { GLYPH_GRID_HEIGHT, GLYPH_GRID_WIDTH, GLYPH_TEMPLATES } from '../services/livestock/glyphTemplateData.ts';
import type { RasterImage } from '../services/scanner/scannerTypes.ts';

function background(width: number, height: number, rgb: readonly number[]): RasterImage {
  const data = new Uint8ClampedArray(width * height * 4);
  for (let i = 0; i < width * height; i++) data.set([...rgb, 255], i * 4);
  return { data, width, height };
}

// Render the known reference font into a complete raster to exercise title placement,
// contrast masking and search-band recovery together. This tests integration/geometry,
// not independent font-recognition accuracy (the real captures cover that).
function drawHemp(image: RasterImage, x0: number, y0: number, scale: number, rgb = [240, 240, 240]) {
  const height = 7 * scale;
  let offset = 0;
  for (const char of 'HEMP') {
    const glyph = GLYPH_TEMPLATES.find(g => g.char === char)!;
    const width = Math.round(height * glyph.aspect);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const gx = Math.min(GLYPH_GRID_WIDTH - 1, Math.floor(x / width * GLYPH_GRID_WIDTH));
      const gy = Math.min(GLYPH_GRID_HEIGHT - 1, Math.floor(y / height * GLYPH_GRID_HEIGHT));
      if (Number(glyph.grid[gy * GLYPH_GRID_WIDTH + gx]) < 5) continue;
      const p = ((y0 + y) * image.width + x0 + offset + x) * 4;
      image.data.set([...rgb, 255], p);
    }
    offset += width + Math.round(height * 0.15);
  }
}
describe('plant title reading on explicitly synthetic pixels', () => {
  for (const scale of [2, 3]) {
    it(`reads simple generated HEMP at ${7 * scale}px capital height`, () => {
      const image = background(600, 300, [45, 45, 45]);
      const row = { x: 120, y: 220, pitch: 52 * scale / 2 };
      const rect = plantNameRect(row, 'planter');
      drawHemp(image, Math.ceil(rect.x0 + 5), Math.ceil(rect.y0 + 8), scale);
      expect(readPlantName(image, row, 'planter')?.plant).toBe('hemp');
    });
  }

  it('recovers an off-band generated title without reporting a direct-band match', () => {
    const image = background(400, 240, [45, 45, 45]);
    const row = { x: 100, y: 150, pitch: 52 };
    const rect = plantNameRect(row, 'planter');
    drawHemp(image, Math.ceil(rect.x0 + 5), Math.ceil(rect.y0 + 8), 2);
    const shifted = { ...row, y: row.y + 0.6 * row.pitch };
    expect(readPlantName(image, shifted, 'planter')).toBeNull();
    expect(searchPlantTitle(image, shifted, 'planter').read.plant).toBe('hemp');
  });

  it('rejects equally bright saturated markings rather than reading them as a title', () => {
    const image = background(400, 200, [45, 45, 45]);
    const row = { x: 100, y: 150, pitch: 52 };
    const rect = plantNameRect(row, 'planter');
    drawHemp(image, Math.ceil(rect.x0 + 5), Math.ceil(rect.y0 + 8), 2, [255, 160, 40]);
    expect(readPlantName(image, row, 'planter')).toBeNull();
    expect(searchPlantTitle(image, row, 'planter').read.plant).toBeNull();
  });

  it('rejects flat bright terrain even when the search rectangle extends outside the image', () => {
    const image = background(40, 30, [235, 235, 235]);
    for (const row of [{ x: 2, y: 2, pitch: 30 }, { x: 39, y: 29, pitch: 30 }]) {
      expect(readPlantName(image, row, 'planter')).toBeNull();
      expect(searchPlantTitle(image, row, 'inventory').read.plant).toBeNull();
    }
  });

  it('does not scan the image when the gene pitch is invalid or too small', () => {
    const image = background(100, 100, [45, 45, 45]);
    for (const pitch of [0, -1, 4, Number.NaN]) {
      expect(readPlantName(image, { x: 50, y: 50, pitch }, 'planter')).toBeNull();
    }
  });
});
