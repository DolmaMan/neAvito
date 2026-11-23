// Real HTTPS/API/PostgreSQL. Only the isolated database in the verification report is used.
const {chromium}=require('playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{spawnSync}=require('node:child_process');
const reportPath=process.argv[2],report=JSON.parse(fs.readFileSync(reportPath,'utf8').replace(/^\uFEFF/,''));
const origin='https://localhost:7253',suffix=Date.now(),password='ModerationPassword123!';
if(!/^neavito_p0_fresh_[0-9a-f]{32}$/.test(report.freshDatabase))throw Error('Expected isolated test database');
async function api(context,url,method='GET',data){const r=await context.request.fetch(origin+url,{method,data,headers:{'X-NeAvito-Request':'1'}});assert.ok(r.ok(),`${method} ${url}: ${r.status()}`);return r.status()===204?null:r.json();}
async function account(context,name){
 const email=`p3-${name}-${suffix}@example.invalid`;await api(context,'/api/auth/register','POST',{email,password,displayName:name});
 const dir=path.join(report.artifactPath,'mail'),files=fs.readdirSync(dir).filter(x=>x.endsWith('.eml')).sort((a,b)=>fs.statSync(path.join(dir,b)).mtimeMs-fs.statSync(path.join(dir,a)).mtimeMs);
 const raw=fs.readFileSync(path.join(dir,files[0]),'utf8').replace(/\r\n/g,'\n').split('\n\n');
 const body=Buffer.from(raw.slice(1).join('\n\n').replace(/\s/g,''),'base64').toString('utf8');
 const params=new URLSearchParams(body.split('\n').find(x=>x.startsWith(origin+'/#confirm-email?')).trim().split('?')[1]);
 await api(context,'/api/auth/confirm-email','POST',Object.fromEntries(params));return email;
}
async function decide(page,action,reason){await page.locator('#moderation-form [name=reason]').fill(reason);await page.locator(`#moderation-form [value=${action}]`).click();}
async function ready(page){await page.locator('#moderation-detail').waitFor();}
async function hasText(page,selector,text){await page.waitForFunction(({selector,text})=>document.querySelector(selector)?.textContent.includes(text),{selector,text});}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const owner=await browser.newContext({ignoreHTTPSErrors:true}),viewer=await browser.newContext({ignoreHTTPSErrors:true}),moderator=await browser.newContext({ignoreHTTPSErrors:true});
 const page=await viewer.newPage(),mod=await moderator.newPage(),authorPage=await owner.newPage();const errors=[],checks=[];
 for(const p of [page,mod,authorPage]){p.on('pageerror',e=>errors.push(e.message));p.on('dialog',d=>d.accept());}
 try{
  const ownerEmail=await account(owner,'owner'),viewerEmail=await account(viewer,'viewer'),modEmail=await account(moderator,'moderator');
  const grant=spawnSync('pwsh',['-NoProfile','-File','scripts/Set-TestModerator.ps1','-ReportPath',reportPath,'-Email',modEmail],{encoding:'utf8'});assert.equal(grant.status,0,'Test role assignment failed');
  await api(owner,'/api/auth/login','POST',{email:ownerEmail,password});await api(moderator,'/api/auth/login','POST',{email:modEmail,password});
  await page.goto(origin);
  const buffer=Buffer.from(await page.evaluate(()=>{const c=document.createElement('canvas');c.width=80;c.height=80;const g=c.getContext('2d');g.fillStyle='#454545';g.fillRect(0,0,80,80);return c.toDataURL().split(',')[1];}),'base64');
  let listing=await api(owner,'/api/me/listings','POST',{title:'Проверка модерации '+suffix,description:'Реальная карточка с фотографией для проверки жалобы.',price:500,cityId:1,categoryId:1,condition:'Used',phone:'+79991234567',contactConsent:true});
  const upload=await owner.request.post(origin+`/api/me/listings/${listing.id}/images`,{headers:{'X-NeAvito-Request':'1'},multipart:{version:listing.version,file:{name:'photo.png',mimeType:'image/png',buffer}}});assert.equal(upload.status(),200);listing=await upload.json();
  listing=await api(owner,`/api/me/listings/${listing.id}/publish`,'POST',{version:listing.version});const id=listing.id,photo=listing.images[0].url;
  await page.goto(origin+'/#item?id='+id);await page.locator('[data-report]').click();await page.locator('#auth-form').waitFor();
  await page.locator('#auth-form [name=email]').fill(viewerEmail);await page.locator('#auth-form [name=password]').fill(password);await page.locator('#auth-submit').click();
  await page.locator('[data-report]').waitFor();await page.locator('[data-report]').click();
  await page.locator('#report-form [name=reason]').selectOption('Other');await page.locator('#report-form [name=comment]').fill('Нужна проверка <script>опасный текст</script>');
  const reportUrl=`**/api/listings/${id}/reports`;await page.route(reportUrl,r=>r.abort());await page.locator('#report-form button').click();await hasText(page,'#report-error','связаться');assert.equal(await page.locator('#report-status').innerText(),'');
  await page.unroute(reportUrl);await page.locator('#report-form button').click();await hasText(page,'#report-status','Обращение принято');
  const reportId=(await page.locator('#report-status').innerText()).split('Номер: ')[1];
  const duplicate=await api(viewer,`/api/listings/${id}/reports`,'POST',{reason:'SuspectedFraud'});assert.equal(duplicate.id,reportId);
  await page.locator('#close-dialog').click();await page.goto(origin+'/#moderation');await page.getByRole('heading',{name:'Нет доступа'}).waitFor();assert.equal(await page.locator('a[href="#moderation"]').isVisible(),false);
  checks.push('Guest return, report validation/network failure/retry, duplicate ID, role access');
  await mod.route('**/api/moderation/reports?*',r=>r.abort());await mod.goto(origin+'/#moderation');await mod.locator('#moderation-load-error').waitFor();await mod.unroute('**/api/moderation/reports?*');await mod.locator('#moderation-retry').click();
  await mod.getByRole('link',{name:listing.title,exact:true}).click();await ready(mod);await mod.waitForFunction(()=>document.querySelector('.moderation-image')?.naturalWidth>0);
  assert.ok((await mod.locator('#moderation-detail').innerText()).includes('<script>опасный текст</script>'));
  const stale=await moderator.newPage();stale.on('dialog',d=>d.accept());stale.on('pageerror',e=>errors.push(e.message));await stale.goto(mod.url());await ready(stale);
  await decide(mod,'HideListing','Недостоверное описание');await hasText(mod,'#moderation-detail','Скрыто:');
  await decide(stale,'Reject','Устаревшее решение');await hasText(stale,'#moderation-error','изменились');await stale.close();
  for(const url of [`/api/listings/${id}`,photo,`/api/listings/${id}/contact`])assert.equal((await viewer.request.get(origin+url)).status(),404);
  await authorPage.goto(origin+'/#listing?id='+id);await hasText(authorPage,'#content','Недостоверное описание');
  for(const width of [390,768,1440]){await mod.setViewportSize({width,height:1000});assert.equal(await mod.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await mod.evaluate(()=>window.scrollTo(0,0));await mod.screenshot({path:path.join(report.artifactPath,`moderation-${width}.png`),fullPage:true});}
  checks.push('Queue retry, real photos, escaped content, stale decision 409, hide visibility and private reason, responsive audit');
  await decide(mod,'RestoreListing','Автор исправит данные');await mod.locator('#moderation-refresh').waitFor();await hasText(mod,'#notice','Решение сохранено');await mod.waitForFunction(()=>!document.querySelector('#moderation-form [value=RestoreListing]'));
  assert.equal((await viewer.request.get(origin+`/api/listings/${id}`)).status(),404);
  listing=await api(owner,`/api/me/listings/${id}`);await api(owner,`/api/me/listings/${id}/publish`,'POST',{version:listing.version});
  const second=await api(viewer,`/api/listings/${id}/reports`,'POST',{reason:'SuspectedFraud'});
  await mod.goto(origin+'/#moderation?id='+second.id);await ready(mod);await decide(mod,'BlockAuthor','Повторное нарушение');await hasText(mod,'#moderation-detail','Блокировка:');
  assert.equal((await owner.request.get(origin+'/api/profile')).status(),401);
  for(const url of [`/api/listings/${id}`,photo,`/api/listings/${id}/contact`])assert.equal((await viewer.request.get(origin+url)).status(),404);
  await decide(mod,'UnblockAuthor','Проверка личности завершена');await mod.waitForFunction(()=>!document.querySelector('#moderation-form [value=UnblockAuthor]'));
  assert.equal((await owner.request.get(origin+'/api/profile')).status(),401);await api(owner,'/api/auth/login','POST',{email:ownerEmail,password});
  await decide(mod,'RestoreListing','Публикация разрешена');await mod.waitForFunction(()=>!document.querySelector('#moderation-form [value=RestoreListing]'));
  assert.equal((await viewer.request.get(origin+`/api/listings/${id}`)).status(),404);
  listing=await api(owner,`/api/me/listings/${id}`);await api(owner,`/api/me/listings/${id}/publish`,'POST',{version:listing.version});
  const third=await api(viewer,`/api/listings/${id}/reports`,'POST',{reason:'InaccurateDescription'});
  await mod.goto(origin+'/#moderation?id='+third.id);await ready(mod);await decide(mod,'Reject','Нарушений не обнаружено');await hasText(mod,'#moderation-detail','Нарушений не обнаружено');
  assert.equal((await viewer.request.get(origin+`/api/listings/${id}`)).status(),200);
  const audit=await api(moderator,`/api/moderation/reports/${third.id}/audit`);assert.equal(audit.totalCount,6);
  checks.push('Restore without auto-publish, block and session revocation, unblock without session revival, reject, six audit entries');
  assert.deepEqual(errors,[]);fs.writeFileSync(path.join(report.artifactPath,'moderation-browser-result.json'),JSON.stringify({passed:true,checks,viewports:[390,768,1440],consoleErrors:errors},null,2));console.log('P3 browser checks passed: '+checks.length);
 }catch(error){console.error('P3 browser failed: '+error.message);process.exitCode=1;}finally{await browser.close();}
})();
