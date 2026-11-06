// Draws the icons of the collector (collector/assets/brain.svg, one colour per state) as raw RGBA files, 64 x 64, that the program embeds: a tray icon is a
// bitmap, not an SVG. Run it again when the drawing or a colour changes:  node tools/icons/render.mjs
import { chromium } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';

const SIZE = 64;
/** The colour of the line for each state: green when all is well, orange when paused, red when Mnemarr cannot be reached or something is wrong, grey-blue while starting. */
const STATES = { online: '#22c55e', paused: '#f59e0b', offline: '#ef4444', starting: '#94a3b8' };
const template = readFileSync(new URL('../../collector/assets/brain.svg', import.meta.url), 'utf8');

const browser = await chromium.launch();
const page = await browser.newPage();
for (const [state, colour] of Object.entries(STATES)) {
  const svg = template.replace('COLOUR', colour);
  const bytes = await page.evaluate(
    async ({ svg, size }) => {
      const image = new Image();
      image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
      await image.decode();
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = size;
      const context = canvas.getContext('2d');
      context.drawImage(image, 0, 0, size, size);
      return Array.from(context.getImageData(0, 0, size, size).data);
    },
    { svg, size: SIZE },
  );
  writeFileSync(new URL(`../../collector/assets/brain-${state}.rgba`, import.meta.url), Buffer.from(bytes));
  console.log(state, colour, bytes.length, 'bytes');
}
await browser.close();
