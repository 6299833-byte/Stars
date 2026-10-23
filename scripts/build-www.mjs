// Builds www/ for the Android app (Capacitor) from solar-system.html.
// solar-system.html stays the single source of truth: this script only wraps it in a full
// document, swaps Google Fonts for bundled woff2 files and copies the images.
import { readFileSync, writeFileSync, mkdirSync, rmSync, cpSync, existsSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(root, 'www');
const fail = msg => { console.error(`build-www: ${msg}`); process.exit(1); };

rmSync(out, { recursive: true, force: true });
mkdirSync(join(out, 'fonts'), { recursive: true });
cpSync(join(root, 'img'), join(out, 'img'), { recursive: true });

// ---- page ----
let page = readFileSync(join(root, 'solar-system.html'), 'utf8');
const gf = /<link[^>]+fonts\.googleapis\.com[^>]*>/g;
const n = (page.match(gf) || []).length;
if (n !== 1) fail(`expected exactly 1 Google Fonts <link> in solar-system.html, found ${n}`);
page = page.replace(gf, '<link rel="stylesheet" href="fonts/fonts.css">');
page = page.replace(/<link[^>]+fonts\.(gstatic|googleapis)\.com[^>]*>\s*/g, '');   // preconnects

const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover">
</head><body>
${page}
</body></html>
`;

// ---- fonts ----
const fonts = [
  { pkg: 'cormorant-garamond', family: 'Cormorant Garamond', weight: 500, style: 'normal', license: 'LICENSE-CormorantGaramond.txt' },
  { pkg: 'cormorant-garamond', family: 'Cormorant Garamond', weight: 500, style: 'italic' },
  { pkg: 'jetbrains-mono', family: 'JetBrains Mono', weight: 400, style: 'normal', license: 'LICENSE-JetBrainsMono.txt' },
];
const subsets = ['cyrillic', 'latin'];
let css = '/* Bundled from @fontsource (SIL Open Font License 1.1, see LICENSE-*.txt) */\n';
for (const f of fonts) {
  const dir = join(root, 'node_modules', '@fontsource', f.pkg);
  if (!existsSync(dir)) fail(`missing node_modules/@fontsource/${f.pkg}; run npm ci`);
  const ranges = JSON.parse(readFileSync(join(dir, 'unicode.json'), 'utf8'));
  for (const s of subsets) {
    const file = `${f.pkg}-${s}-${f.weight}-${f.style}.woff2`;
    const src = join(dir, 'files', file);
    if (!existsSync(src)) fail(`missing font file ${src}`);
    if (!ranges[s]) fail(`no unicode-range for ${f.pkg} ${s}`);
    copyFileSync(src, join(out, 'fonts', file));
    css += `@font-face {\n  font-family: "${f.family}";\n  font-style: ${f.style};\n  font-weight: ${f.weight};\n  font-display: swap;\n  src: url(${file}) format("woff2");\n  unicode-range: ${ranges[s]};\n}\n`;
  }
  if (f.license) copyFileSync(join(dir, 'LICENSE'), join(out, 'fonts', f.license));
}
writeFileSync(join(out, 'fonts', 'fonts.css'), css);
writeFileSync(join(out, 'index.html'), html);

// ---- checks ----
const remote = [
  ...html.matchAll(/<(?:link|script|img|source|iframe|video|audio)\b[^>]*\b(?:href|src)\s*=\s*["']?(https?:\/\/[^"'\s>]+)/gi),
  ...html.matchAll(/url\(\s*["']?(https?:\/\/[^"')]+)/gi),
  ...html.matchAll(/@import\s+(?:url\()?\s*["']?(https?:\/\/[^"')\s;]+)/gi),
  ...html.matchAll(/\.src\s*=\s*["'](https?:\/\/[^"']+)/g),
].map(m => m[1]);
if (remote.length) fail(`remote resources left in www/index.html:\n  ${[...new Set(remote)].join('\n  ')}`);
const imgs = [...new Set([...html.matchAll(/img\/[\w.-]+\.(?:jpe?g|png|webp|svg|gif)/g)].map(m => m[0]))];
const missing = imgs.filter(p => !existsSync(join(out, p)));
if (missing.length) fail(`missing images: ${missing.join(', ')}`);

console.log(`build-www: www/index.html (${(html.length / 1024).toFixed(0)} KB), ${imgs.length} images, ${fonts.length * subsets.length} font files`);
