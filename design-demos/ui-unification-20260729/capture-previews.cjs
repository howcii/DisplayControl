const path = require("path");
const { pathToFileURL } = require("url");
const { chromium } = require("playwright");

async function main() {
  const demoDir = __dirname;
  const htmlPath = path.join(demoDir, "ui-unification-preview.html");
  const browser = await chromium.launch({
    headless: true,
    executablePath: process.env.CHROME_PATH || undefined,
  });
  const page = await browser.newPage({
    viewport: { width: 1920, height: 1080 },
    deviceScaleFactor: 1,
  });

  await page.goto(pathToFileURL(htmlPath).href, { waitUntil: "networkidle" });

  for (const id of ["direction-a", "direction-b", "direction-c"]) {
    const out = path.join(demoDir, `${id}.png`);
    await page.locator(`#${id}`).screenshot({ path: out });
    console.log(out);
  }

  await browser.close();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
