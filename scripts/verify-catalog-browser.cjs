const {chromium}=require('playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const report=JSON.parse(fs.readFileSync(process.argv[2],'utf8').replace(/^\uFEFF/,''));
const origin='https://localhost:7253', password='CatalogPassword123!', suffix=Date.now();
async function api(context,url,method='GET',data) {
 const response=await context.request.fetch(origin+url,{method,data,headers:{'X-NeAvito-Request':'1'}});
 assert.ok(response.ok(),method+' '+url+': '+response.status());
 return response.status()===204?null:response.json();
}
async function account(context,name) {
 const email=`${name}-${suffix}@example.invalid`;
 await api(context,'/api/auth/register','POST',{email,password,displayName:name});
 const mailDir=path.join(report.artifactPath,'mail');
 const file=fs.readdirSync(mailDir).filter(x=>x.endsWith('.eml')).sort((a,b)=>fs.statSync(path.join(mailDir,b)).mtimeMs-fs.statSync(path.join(mailDir,a)).mtimeMs)[0];
 const chunks=fs.readFileSync(path.join(mailDir,file),'utf8').replace(/\r\n/g,'\n').split('\n\n');
 const body=Buffer.from(chunks.slice(1).join('\n\n').replace(/\s/g,''),'base64').toString('utf8');
 const params=new URLSearchParams(body.split('\n').find(x=>x.startsWith(origin+'/#confirm-email?')).trim().split('?')[1]);
 await api(context,'/api/auth/confirm-email','POST',Object.fromEntries(params));
 return email;
}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const owner=await browser.newContext({ignoreHTTPSErrors:true}), viewer=await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:1440,height:1000}});
 const page=await viewer.newPage(),errors=[],checks=[]; page.on('pageerror',e=>errors.push(e.message));
 try {
  const ownerEmail=await account(owner,'author'); await api(owner,'/api/auth/login','POST',{email:ownerEmail,password});
  const viewerEmail=await account(viewer,'viewer');
  await page.goto(origin);
  const buffer=Buffer.from(await page.evaluate(()=>{const c=document.createElement('canvas');c.width=32;c.height=32;const x=c.getContext('2d');x.fillStyle='#176a50';x.fillRect(0,0,32,32);return c.toDataURL('image/png').split(',')[1];}),'base64');
  let target;
  for(let i=0;i<13;i++) {
   let listing=await api(owner,'/api/me/listings','POST',{title:`Вещь ${suffix} ${i}`,description:'Ищите по описанию: туризм',price:i,categoryId:5,cityId:i===0?1:2,condition:i===0?'Used':'New',phone:'+79991234567',contactConsent:true});
   const upload=await owner.request.post(origin+`/api/me/listings/${listing.id}/images`,{headers:{'X-NeAvito-Request':'1'},multipart:{version:listing.version,file:{name:'photo.png',mimeType:'image/png',buffer}}});assert.equal(upload.status(),200);listing=await upload.json();
   listing=await api(owner,`/api/me/listings/${listing.id}/publish`,'POST',{version:listing.version});if(i===0)target=listing;
  }
  await page.goto(origin+'/#catalog');await page.locator('#catalog-results .shop-card').first().waitFor();
  const form=page.locator('#catalog-filters');await form.locator('[name=search]').fill(String(suffix));await form.locator('button[type=submit]').first().click();
  await page.waitForFunction(()=>document.querySelectorAll('.shop-card').length===12);
  await page.getByRole('link',{name:'Далее',exact:true}).click();await page.waitForFunction(()=>document.querySelectorAll('.shop-card').length===1);
  checks.push('Stable pagination and filter URL');
  await page.goto(origin+'/#catalog?search=туризм&categoryId=5&cityId=1&condition=Used&minPrice=0&maxPrice=0');
  await page.waitForFunction(()=>document.querySelectorAll('.shop-card').length===1);
  assert.ok((await page.locator('.shop-card').innerText()).includes('Бесплатно'));
  const filterHash=new URL(page.url()).hash;
  await page.locator('.shop-card h2 a').click();await page.locator('#reveal-contact').waitFor();
  assert.equal(await page.locator('#listing-contact').innerText(),'');assert.equal((await page.content()).includes('+79991234567'),false);
  await page.locator('#reveal-contact').click();await page.locator('#auth-form').waitFor();
  await page.locator('#auth-form [name=email]').fill(viewerEmail);await page.locator('#auth-form [name=password]').fill(password);await page.locator('#auth-submit').click();
  await page.locator('#reveal-contact').waitFor();assert.equal(await page.locator('#listing-contact').innerText(),'');
  await page.locator('#reveal-contact').click();await page.locator('#listing-contact a').waitFor();assert.equal(await page.locator('#listing-contact a').getAttribute('href'),'tel:+79991234567');
  checks.push('Guest return after login, explicit contact reveal, no public phone');
  await page.locator('.store-back').click();await page.locator('#catalog-filters').waitFor();assert.equal(new URL(page.url()).hash,filterHash);
  for(const width of [390,768,1440]) {await page.setViewportSize({width,height:1000});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await page.screenshot({path:path.join(report.artifactPath,`catalog-${width}.png`),fullPage:true});}
  await page.locator('.shop-card h2 a').click();await page.locator('#reveal-contact').waitFor();
  await api(owner,`/api/me/listings/${target.id}/unpublish`,'POST',{version:target.version});
  await page.locator('#reveal-contact').click();await page.waitForFunction(()=>document.querySelector('#listing-contact')?.textContent.includes('недоступен'));
  await page.reload();await page.locator('#store-error').waitFor();
  checks.push('Unpublishing closes contact and card immediately');
  await page.goto(origin+'/#products');await page.waitForURL('**/#listings');await page.locator('#content h1').waitFor();
  await page.getByRole('link',{name:'История покупок',exact:true}).click();await page.locator('#content h1').waitFor();
  assert.equal(await page.locator('[data-cancel],[data-status],[data-add-cart]').count(),0);
  checks.push('Legacy links and read-only history, responsive widths 390/768/1440');
  assert.deepEqual(errors,[]);fs.writeFileSync(path.join(report.artifactPath,'catalog-browser-result.json'),JSON.stringify({passed:true,checks,consoleErrors:errors},null,2));console.log('Browser P2 checks passed: '+checks.length);
 }catch(error){console.error('Browser P2 failed: '+error.message);process.exitCode=1;}
 finally{await browser.close();}
})();
