const fs = require("fs");
const path = require("path");
const { chromium } = require("playwright");

const serverUrl = process.argv[2] || "http://127.0.0.1:18080";
const dataPath = process.argv[3];
const screenshotPath = process.argv[4];

if (!dataPath || !screenshotPath) {
  throw new Error("Usage: node run-single-session-e2e.cjs <serverUrl> <dataPath> <screenshotPath>");
}

function readTemporaryPassword() {
  const text = fs.readFileSync(path.join(dataPath, "initial-admin-credentials.txt"), "utf8");
  const line = text.split(/\r?\n/).find((value) => value.startsWith("Temporary password:"));
  if (!line) throw new Error("Temporary password was not generated.");
  return line.slice("Temporary password:".length).trim();
}

async function readCaptcha(page) {
  await page.waitForFunction(() => document.querySelector("#captchaImage")?.src.startsWith("data:image/svg+xml;base64,"));
  const source = await page.locator("#captchaImage").getAttribute("src");
  const svg = Buffer.from(source.slice(source.indexOf(",") + 1), "base64").toString("utf8");
  if (!svg.includes("<feDisplacementMap") || !svg.includes(" scale(")) {
    throw new Error("Captcha warp filters were not present in the rendered page.");
  }
  const glyphs = [...svg.matchAll(/>([A-Za-z0-9])<\/text>/g)].map((match) => match[1]);
  if (glyphs.length !== 15) throw new Error(`Expected 15 sliced captcha glyphs, received ${glyphs.length}.`);
  const code = glyphs.slice(0, 5).join("");
  return code;
}

async function login(page, password) {
  await page.locator("#username").fill("admin");
  await page.locator("#password").fill(password);
  await page.locator("#captchaCode").fill(await readCaptcha(page));
  await page.locator("#loginButton").click();
}

(async () => {
  const browser = await chromium.launch({
    headless: true,
    executablePath: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  });
  const browserErrors = [];
  try {
    const firstContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const firstPage = await firstContext.newPage();
    firstPage.on("pageerror", (error) => browserErrors.push(`first: ${error.message}`));
    firstPage.on("console", (message) => {
      if (message.type() === "error") browserErrors.push(`first: ${message.text()}`);
    });
    await firstPage.goto(serverUrl, { waitUntil: "networkidle" });

    const temporaryPassword = readTemporaryPassword();
    const permanentPassword = "DisplayControl-E2E-2026!";
    await login(firstPage, temporaryPassword);
    await firstPage.locator("#passwordView:not(.hidden)").waitFor();
    await firstPage.locator("#currentPassword").fill(temporaryPassword);
    await firstPage.locator("#newPassword").fill(permanentPassword);
    await firstPage.locator("#confirmPassword").fill(permanentPassword);
    await firstPage.locator("#changePasswordButton").click();
    await firstPage.locator("#loginView:not(.hidden)").waitFor();

    await login(firstPage, permanentPassword);
    await firstPage.locator("#appView:not(.hidden)").waitFor();
    const firstCookies = await firstContext.cookies(serverUrl);
    const firstCookieHeader = firstCookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ");

    const secondContext = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const secondPage = await secondContext.newPage();
    secondPage.on("pageerror", (error) => browserErrors.push(`second: ${error.message}`));
    secondPage.on("console", (message) => {
      if (message.type() === "error") browserErrors.push(`second: ${message.text()}`);
    });
    await secondPage.goto(serverUrl, { waitUntil: "networkidle" });
    await login(secondPage, permanentPassword);
    await secondPage.locator("#appView:not(.hidden)").waitFor();

    const oldSessionResponse = await fetch(`${serverUrl}/api/screens`, {
      headers: { Cookie: firstCookieHeader },
      redirect: "manual",
    });
    if (oldSessionResponse.status !== 401) {
      throw new Error(`Old session must receive HTTP 401, received ${oldSessionResponse.status}.`);
    }
    if (oldSessionResponse.headers.get("x-displaycontrol-auth-reason") !== "session-replaced") {
      throw new Error("Old session did not receive the session-replaced reason.");
    }

    await firstPage.locator("#sessionNotice:not(.hidden)").waitFor({ timeout: 10000 });
    const noticeText = await firstPage.locator("#sessionNotice").innerText();
    if (!noticeText.includes("已在另一台设备登录") || !noticeText.includes("已安全退出")) {
      throw new Error(`Unexpected replacement notice: ${noticeText}`);
    }
    await firstPage.screenshot({ path: screenshotPath });

    if (browserErrors.length) {
      throw new Error(`Browser errors: ${browserErrors.join(" | ")}`);
    }
    process.stdout.write("PASS first login and password change\n");
    process.stdout.write("PASS second device replaces the first session\n");
    process.stdout.write("PASS old API session returns 401 session-replaced\n");
    process.stdout.write("PASS old page shows the dedicated replacement notice\n");
    process.stdout.write("PASS captcha includes readable warp transforms\n");

    await secondContext.close();
    await firstContext.close();
  } finally {
    await browser.close();
  }
})().catch((error) => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
