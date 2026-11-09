const { chromium } = require('playwright');
const fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const report=JSON.parse(fs.readFileSync(process.argv[2],'utf8').replace(/^\uFEFF/,''));
const origin='https://localhost:7253', email=`listing-${Date.now()}@example.invalid`, password='ListingPassword123!';
async function text(page,selector,value) {await page.waitForFunction(({selector,value})=>document.querySelector(selector)?.textContent.includes(value),{selector,value});}
async function save(page) {
 await Promise.all([page.waitForResponse(r=>r.url().includes('/api/me/listings/')&&r.request().method()==='PUT'),page.locator('#listing-form button[type=submit]').click()]);
 await page.waitForFunction(()=>document.querySelector('#listing-form button[type=submit]')?.disabled===false);
}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const context=await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:1440,height:1000}});
 const page=await context.newPage(), errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 page.on('response',response=>{if(response.url().includes('/api/me/listings') && response.request().method()!=='GET') console.log(response.request().method()+' listing operation: '+response.status());});
 page.on('dialog',dialog=>dialog.accept());
 try {
  await page.goto(origin+'/#login');await page.locator('[data-auth-mode=register]').click();
  await page.locator('[name=displayName]').fill('Автор P1');await page.locator('#auth-form [name=email]').fill(email);
  await page.locator('#auth-form [name=password]').fill(password);await page.locator('#auth-submit').click();
  await text(page,'#auth-status','Аккаунт создан');
  const mailDir=path.join(report.artifactPath,'mail');
  const files=fs.readdirSync(mailDir).filter(x=>x.endsWith('.eml')).sort((a,b)=>fs.statSync(path.join(mailDir,b)).mtimeMs-fs.statSync(path.join(mailDir,a)).mtimeMs);
  const raw=fs.readFileSync(path.join(mailDir,files[0]),'utf8').replace(/\r\n/g,'\n').split('\n\n');
  const body=Buffer.from(raw.slice(1).join('\n\n').replace(/\s/g,''),'base64').toString('utf8');
  const link=body.split('\n').find(x=>x.startsWith(origin+'/#confirm-email?')).trim();
  await page.goto(link);await page.locator('#auth-submit').click();await text(page,'#auth-status','Почта подтверждена');
  await page.locator('#auth-form [name=email]').fill(email);await page.locator('#auth-form [name=password]').fill(password);
  await page.locator('#auth-submit').click();await page.locator('#profile-form').waitFor();
  await page.getByRole('link',{name:'Мои объявления',exact:true}).click();
  await page.getByRole('link',{name:'Добавить объявление'}).click();
  await page.locator('#listing-form button[type=submit]').click();
  await page.locator('[data-listing-command=publish]').waitFor();
  await page.locator('[data-listing-command=publish]').click();await text(page,'#notice','Укажите заголовок');
  const form=page.locator('#listing-form');
  await form.locator('[name=title]').fill('Велосипед P1');await form.locator('[name=description]').fill('Хорошее состояние. Самовывоз.');
  await form.locator('[name=price]').fill('0');await form.locator('[name=categoryId]').selectOption('5');
  await form.locator('[name=cityId]').selectOption('1');await form.locator('[name=condition]').selectOption('Used');
  await form.locator('[name=phone]').fill('+7 999 123-45-67');await form.locator('[name=contactConsent]').check();
  await save(page);await text(page,'#notice','Объявление сохранено');
  const buffer=Buffer.from(await page.evaluate(()=>{const c=document.createElement('canvas');c.width=32;c.height=32;const ctx=c.getContext('2d');ctx.fillStyle='#176a50';ctx.fillRect(0,0,32,32);return c.toDataURL('image/png').split(',')[1];}),'base64');
  await page.locator('#listing-photo').setInputFiles({name:'bicycle.png',mimeType:'image/png',buffer});
  await page.locator('#listing-images img').waitFor();
  await page.locator('[data-listing-command=publish]').click();await page.locator('[data-listing-command=sold]').waitFor();
  const id=new URLSearchParams(page.url().split('?')[1]).get('id');
  // Changing the listing number resets consent in the form.
  await form.locator('[name=phone]').fill('+7 999 765-43-21');assert.equal(await form.locator('[name=contactConsent]').isChecked(),false);
  await save(page);await page.locator('[data-listing-command=publish]').waitFor();
  await form.locator('[name=contactConsent]').check();await save(page);
  await page.locator('[data-listing-command=publish]').click();await page.locator('[data-listing-command=sold]').waitFor();
  const other=await context.newPage();await other.goto(origin+'/#listing?id='+id);await other.locator('#listing-form').waitFor();
  await form.locator('[name=title]').fill('Велосипед — обновлено');await save(page);
  await other.locator('#listing-form [name=title]').fill('Устаревшая правка');await other.locator('#listing-form button[type=submit]').click();
  await text(other,'#listing-error','Объявление изменилось');await other.close();
  for(const width of [390,768,1440]) {
   await page.setViewportSize({width,height:1000});
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   await page.screenshot({path:path.join(report.artifactPath,`listing-${width}.png`),fullPage:true});
  }
  await page.locator('[data-listing-command=sold]').click();await page.locator('[data-listing-command=reopen]').waitFor();
  assert.equal(await form.locator('[name=title]').isDisabled(),true);
  await page.locator('[data-listing-command=reopen]').click();await page.locator('[data-listing-command=publish]').waitFor();
  await page.locator('[data-listing-image]').click();await page.waitForFunction(()=>document.querySelectorAll('#listing-images img').length===0);
  await page.locator('[data-listing-command=archive]').click();await text(page,'#content','В архиве');
  assert.equal(await form.locator('[name=title]').isDisabled(),true);
  assert.deepEqual(errors,[]);
  fs.writeFileSync(path.join(report.artifactPath,'listings-browser-result.json'),JSON.stringify({passed:true,id,checks:['partial draft','required publication fields','free price','photo upload/delete','phone consent reset and unpublication','publish/sold/reopen/archive','stale tab conflict','read-only archive','390/768/1440 layout'],consoleErrors:errors},null,2));
  console.log('P1 browser checks passed');
 } catch(error){console.error(error.message);console.error('Notice: '+await page.locator('#notice').textContent());console.error('Form: '+await page.locator('#listing-error').textContent().catch(()=>''));process.exitCode=1;}
 finally{await browser.close();}
})();
