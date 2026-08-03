const path = require("path");
const { pathToFileURL } = require("url");
const { chromium } = require("playwright");

const root = __dirname;
const directions = ["direction-a", "direction-b", "direction-c"];

(async () => {
  const browser = await chromium.launch({
    headless: true,
    executablePath: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  });
  let hasErrors = false;
  try {
    for (const name of directions) {
      const page = await browser.newPage({
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 1,
      });
      const errors = [];
      page.on("console", (message) => {
        if (message.type() === "error") errors.push(message.text());
      });
      page.on("pageerror", (error) => errors.push(error.message));
      await page.goto(pathToFileURL(path.join(root, `${name}.html`)).href, {
        waitUntil: "networkidle",
      });
      await page.screenshot({ path: path.join(root, `${name}.png`) });
      for (const width of [1024, 768, 375]) {
        await page.setViewportSize({ width, height: 900 });
        const layout = await page.evaluate(() => ({
          clientWidth: document.documentElement.clientWidth,
          scrollWidth: document.documentElement.scrollWidth,
          unlabeledInputs: [...document.querySelectorAll("input")]
            .filter((input) => !document.querySelector(`label[for='${input.id}']`)).length,
        }));
        if (layout.scrollWidth > layout.clientWidth || layout.unlabeledInputs > 0) {
          errors.push(
            `${width}px layout failed: scroll=${layout.scrollWidth}/${layout.clientWidth}, unlabeled=${layout.unlabeledInputs}`,
          );
        }
      }
      if (errors.length) {
        hasErrors = true;
        process.stderr.write(`${name}: ${errors.join(" | ")}\n`);
      } else {
        process.stdout.write(`${name}: ok\n`);
      }
      await page.close();
    }
  } finally {
    await browser.close();
  }
  process.exitCode = hasErrors ? 1 : 0;
})();
