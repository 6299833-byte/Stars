const { chromium } = require('/opt/node22/lib/node_modules/playwright/index.js');
const OUT = '/home/user/Stars/android/store/screenshots/';
const URL = 'http://127.0.0.1:8765/index.html';
const args = ['--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader','--ignore-gpu-blocklist'];
const sleep = ms => new Promise(r => setTimeout(r, ms));
const click = (p, id) => p.$eval('#' + id, e => e.click());
const shots = [
  { name: '01-overview', vp: [432,768], steps: async p => { await click(p,'t-names'); await sleep(12000); } },
  { name: '02-saturn', vp: [432,768], steps: async p => { await click(p,'b-saturn'); await sleep(50000); } },
  { name: '03-earth', vp: [432,768], steps: async p => { await click(p,'b-earth'); await sleep(50000); } },
  { name: '04-jupiter', vp: [432,768], steps: async p => { await click(p,'b-jupiter'); await sleep(50000); } },
  { name: '05-menu', vp: [432,768], steps: async p => { await click(p,'t-names'); await sleep(8000); await click(p,'menu-btn'); await sleep(1500); } },
  { name: '06-landscape-saturn', vp: [768,432], steps: async p => { await click(p,'b-saturn'); await sleep(50000); } },
];
(async () => {
  const only = process.argv[2];
  const b = await chromium.launch({ args });
  for (const s of shots) {
    if (only && !s.name.startsWith(only)) continue;
    const ctx = await b.newContext({ viewport: { width: s.vp[0], height: s.vp[1] }, deviceScaleFactor: 2.5, isMobile: true, hasTouch: true });
    const p = await ctx.newPage();
    p.on('pageerror', e => console.log('ERR', s.name, e.message));
    await p.goto(URL); await sleep(4000);
    await s.steps(p);
    await p.screenshot({ path: OUT + s.name + '.png' });
    console.log('done', s.name);
    await ctx.close();
  }
  await b.close();
})();
