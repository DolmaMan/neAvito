// Run with Playwright installed (or NODE_PATH pointing to a bundled installation).
// Uses only the isolated server created by Start-P0Server.ps1, never the user's main database.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const report = JSON.parse(fs.readFileSync(process.argv[2], 'utf8').replace(/^\uFEFF/, ''));
const origin = 'https://localhost:7253';
const email = `p0-${Date.now()}@example.invalid`;
const initialPassword = 'InitialPassword123!';
const updatedPassword = 'UpdatedPassword456!';
const recoveredPassword = 'RecoveredPassword789!';
const checks = [];

async function message(page, selector, text) {
  await page.waitForFunction(({selector, text}) => document.querySelector(selector)?.textContent.includes(text), {selector,text});
}
async function login(page, password) {
  await page.goto(origin + '/#login');
  await page.locator('#auth-form [name=email]').fill(email);
  await page.locator('#auth-form [name=password]').fill(password);
  await page.locator('#auth-submit').click();
  await page.locator('#change-password-form').waitFor();
}
async function linkFromMail(route) {
  const folder = path.join(report.artifactPath, 'mail');
  const files = fs.readdirSync(folder).filter(x => x.endsWith('.eml'));
  for (const name of files.reverse()) {
    const raw = fs.readFileSync(path.join(folder, name),'utf8');
    const parts = raw.replace(/\r\n/g,'\n').split('\n\n');
    const body = /Content-Transfer-Encoding: base64/i.test(parts[0])
      ? Buffer.from(parts.slice(1).join('\n\n').replace(/\s/g,''),'base64').toString('utf8')
      : parts.slice(1).join('\n\n');
    const link = body.split(/\r?\n/).find(line => line.startsWith(origin + '/#' + route + '?'));
    if (link) return link.trim();
  }
  throw new Error('Local test mail not found: ' + route);
}
async function passwordFields(page, expected) {
  const fields = page.locator('.password-field:visible');
  assert.equal(await fields.count(), expected);
  for (let i=0;i<expected;i++) {
    const field = fields.nth(i), input = field.locator('input'), button = field.locator('button');
    const before = await input.inputValue();
    assert.equal(await input.getAttribute('type'), 'password');
    await button.focus();
    await page.keyboard.press('Space');
    assert.equal(await input.getAttribute('type'), 'text');
    assert.equal(await button.getAttribute('aria-pressed'), 'true');
    assert.equal(await input.inputValue(), before);
    await page.keyboard.press('Enter');
    assert.equal(await input.getAttribute('type'), 'password');
  }
}

(async () => {
  const browser = await chromium.launch({ channel:'msedge', headless:true });
  const context = await browser.newContext({ ignoreHTTPSErrors:true, viewport:{width:1440,height:1000} });
  const second = await browser.newContext({ ignoreHTTPSErrors:true });
  const page = await context.newPage(), other = await second.newPage();
  const errors=[];
  page.on('pageerror', error => errors.push(error.message));
  try {
    await page.goto(origin + '/#login');
    await page.locator('[data-auth-mode=register]').click();
    await page.locator('[name=displayName]').fill('P0 Browser');
    await page.locator('#auth-form [name=email]').fill(email);
    await page.locator('#auth-form [name=password]').fill(initialPassword);
    await passwordFields(page,1);
    await page.locator('.password-toggle').click();
    await page.locator('[data-auth-mode=login]').click();
    assert.equal(await page.locator('#auth-form [name=password]').getAttribute('type'),'password');
    await page.locator('[data-auth-mode=register]').click();
    await page.locator('#auth-form [name=password]').fill(initialPassword);
    await page.locator('#auth-submit').click();
    await message(page,'#auth-status','Аккаунт создан');
    checks.push('Registration and keyboard-operated eye; switching form conceals password');
    await page.goto(await linkFromMail('confirm-email'));
    await page.locator('#auth-submit').click();
    await message(page,'#auth-status','Почта подтверждена');
    await login(page,initialPassword);
    await login(other,initialPassword);
    await passwordFields(page,3);
    checks.push('Local confirmation email, login, independent sessions, three profile toggles');
    const form=page.locator('#change-password-form');
    await form.locator('[name=currentPassword]').fill(initialPassword);
    await form.locator('[name=newPassword]').fill(updatedPassword);
    await form.locator('[name=confirmPassword]').fill('DifferentPassword123!');
    await form.locator('button[type=submit]').click();
    await message(page,'#security-error','не совпадают');
    await form.locator('[name=currentPassword]').fill('WrongPassword123!');
    await form.locator('[name=confirmPassword]').fill(updatedPassword);
    await form.locator('button[type=submit]').click();
    await message(page,'#security-error','Текущий пароль указан неверно');
    await form.locator('[name=currentPassword]').fill(initialPassword);
    await context.setOffline(true);
    await form.locator('button[type=submit]').click();
    await message(page,'#security-error','Не удалось связаться');
    assert.equal(await form.locator('button[type=submit]').isEnabled(),true);
    await context.setOffline(false);
    checks.push('Password mismatch, incorrect current password, offline failure and recovery');
    // Capture only empty/masked fields; never save visible credentials in screenshots.
    await form.locator('[name=currentPassword]').fill('');
    await form.locator('[name=newPassword]').fill('');
    await form.locator('[name=confirmPassword]').fill('');
    for (const width of [390,768,1440]) {
      await page.setViewportSize({width,height:1000});
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
      await page.screenshot({path:path.join(report.artifactPath,`profile-${width}.png`),fullPage:true});
    }
    await form.locator('[name=currentPassword]').fill(initialPassword);
    await form.locator('[name=newPassword]').fill(updatedPassword);
    await form.locator('[name=confirmPassword]').fill(updatedPassword);
    await form.locator('button[type=submit]').click();
    await page.locator('#auth-screen').waitFor();
    await other.reload();
    await other.locator('#auth-screen').waitFor();
    checks.push('Password change revokes both browser sessions');
    await login(page,updatedPassword);
    await login(other,updatedPassword);
    page.once('dialog', dialog=>dialog.accept());
    await page.locator('#logout-all').click();
    await page.locator('#auth-screen').waitFor();
    await other.reload();
    await other.locator('#auth-screen').waitFor();
    checks.push('Logout all revokes two independent browser sessions');
    await page.locator('[data-auth-mode=forgot-password]').click();
    await page.locator('#auth-form [name=email]').fill(email);
    await page.locator('#auth-submit').click();
    await message(page,'#auth-status','будет отправлена ссылка');
    await page.goto(await linkFromMail('reset-password'));
    await page.locator('#auth-form [name=password]').fill(recoveredPassword);
    await passwordFields(page,1);
    await page.locator('#auth-submit').click();
    await message(page,'#auth-status','Пароль сохранён');
    await login(page,recoveredPassword);
    checks.push('Password recovery through local email and reset-form eye');
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(report.artifactPath,'browser-result.json'),JSON.stringify({passed:true,checks,viewports:[390,768,1440],consoleErrors:errors},null,2));
    console.log('Browser P0 checks passed: ' + checks.length);
  } catch(error) {
    // Text-only diagnostic: passwords and mail links must not be printed.
    console.error('Browser P0 check failed: ' + error.message);
    process.exitCode=1;
  } finally { await browser.close(); }
})();
