'use strict';
const reportReasons={InaccurateDescription:'Недостоверное описание',ProhibitedItem:'Запрещённый товар',SuspectedFraud:'Подозрение на мошенничество',Other:'Другое'};
const decisionLabels={Reject:'Отклонить жалобу',HideListing:'Скрыть объявление',BlockAuthor:'Заблокировать автора',RestoreListing:'Снять скрытие объявления',UnblockAuthor:'Разблокировать автора'};
let moderationSelection=null;
function moderationLink(params,changes){const next=new URLSearchParams(params);for(const [key,value] of Object.entries(changes)){if(value===null)next.delete(key);else next.set(key,String(value));}return '#moderation?'+next;}
function moderationPager(data,params,key){return `<nav class="pager" aria-label="Страницы ${key==='page'?'жалоб':'истории'}">${data.page>1?`<a href="${escapeHtml(moderationLink(params,{[key]:data.page-1}))}">Назад</a>`:''}<span>Страница ${data.page} из ${Math.max(1,data.totalPages)}</span>${data.page<data.totalPages?`<a href="${escapeHtml(moderationLink(params,{[key]:data.page+1}))}">Далее</a>`:''}</nav>`;}
async function renderModeration(){
 const generation=++routeCounter;moderationSelection=null;
 $('#storefront').hidden=true;$('#auth-screen').hidden=true;$('#cabinet').hidden=false;updateStoreHeader();
 document.querySelectorAll('.cabinet nav a').forEach(a=>a.classList.toggle('active',a.hash==='#moderation'));
 document.title='Модерация — NeAvito';$('#content').innerHTML='<p class="loading">Загружаем обращения…</p>';
 try{
  const fresh=await api('/api/profile');if(generation!==routeCounter)return;profile=fresh;updateStoreHeader();
  if(!profile.canModerate){$('#content').innerHTML=empty('Нет доступа','Раздел доступен только модераторам.');return;}
  const params=new URLSearchParams(location.hash.split('?')[1]||'');
  const status=params.get('status')||'Pending',page=params.get('page')||'1',id=params.get('id');
  const query=new URLSearchParams({page,pageSize:'10'});if(status!=='All')query.set('status',status);
  const data=await api('/api/moderation/reports?'+query);if(generation!==routeCounter)return;
  let html=pageHeader('Модерация','Жалобы, решения и история ограничений.')+`<section class="panel moderation-queue"><h2>Очередь жалоб</h2><div class="moderation-filters">${[['All','Все обращения'],['Pending','Ожидают решения'],['Resolved','Рассмотренные']].map(([v,l])=>`<a class="page-link" ${v===status?'aria-current="page"':''} href="${escapeHtml(moderationLink(params,{status:v,page:1,id:null,auditPage:null}))}">${l}</a>`).join('')}</div><p>Обращений: ${data.totalCount}</p><div class="moderation-reports">${data.items.length?data.items.map(r=>`<article class="order-card"><h3><a href="${escapeHtml(moderationLink(params,{id:r.id,auditPage:null}))}">${escapeHtml(r.title)}</a></h3><p>${reportReasons[r.reason]||escapeHtml(r.reason)} · ${r.status==='Pending'?'Ожидает решения':'Рассмотрено'} · ${date(r.createdAtUtc)}</p><small class="muted">${escapeHtml(r.id)}</small></article>`).join(''):'<p>Обращений с выбранным статусом нет.</p>'}</div>${moderationPager(data,params,'page')}</section>`;
  if(id){
   const [detail,audit]=await Promise.all([api('/api/moderation/reports/'+encodeURIComponent(id)),api('/api/moderation/reports/'+encodeURIComponent(id)+'/audit?pageSize=10&page='+encodeURIComponent(params.get('auditPage')||'1'))]);
   if(generation!==routeCounter)return;moderationSelection=detail;
   const r=detail.report,l=detail.listing,a=detail.author;
   html+=`<section class="panel" id="moderation-detail"><h2>${escapeHtml(l.title)}</h2><p class="moderation-text">${escapeHtml(l.description)}</p><div class="images">${l.images.map(i=>`<a href="${escapeHtml(i.url)}" target="_blank" rel="noopener"><img class="moderation-image" src="${escapeHtml(i.url)}" alt="Фото объявления"></a>`).join('')}</div><p>Автор: <strong>${escapeHtml(a.displayName)}</strong>${a.isBlocked?' — заблокирован':''}</p><p>Жалоба: ${reportReasons[r.reason]||escapeHtml(r.reason)}</p><p class="moderation-text">${escapeHtml(detail.comment)}</p>${l.moderationHidden?`<p class="error">Скрыто: ${escapeHtml(l.moderationReason)}</p>`:''}${a.isBlocked?`<p class="error">Блокировка: ${escapeHtml(a.blockReason)}</p>`:''}${r.status==='Resolved'?`<p>Решение: ${decisionLabels[r.decision]||escapeHtml(r.decision)}. Причина: ${escapeHtml(r.decisionReason)}</p>`:''}<form id="moderation-form"><label>Причина решения<textarea name="reason" required maxlength="1000"></textarea></label><div class="form-actions">${(r.status==='Pending'?['Reject','HideListing','BlockAuthor']:[]).map(action=>`<button type="submit" name="action" value="${action}">${decisionLabels[action]}</button>`).join('')}${a.isBlocked?'<button type="submit" name="action" value="UnblockAuthor">Разблокировать автора</button>':''}${l.moderationHidden&&!a.isBlocked?'<button type="submit" name="action" value="RestoreListing">Снять скрытие объявления</button>':''}</div><p id="moderation-error" class="error" role="alert"></p></form><button type="button" id="moderation-refresh">Обновить обращение</button><p class="muted small">Снятие скрытия не публикует объявление. Разблокировка автора не возвращает старые сеансы; остальные опубликованные и не скрытые объявления снова станут доступны.</p></section><section class="panel"><h2>История решений</h2>${audit.items.length?audit.items.map(x=>`<article class="order-card"><strong>${decisionLabels[x.action]||escapeHtml(x.action)}</strong><p class="moderation-text">${escapeHtml(x.reason)}</p><small>${date(x.createdAtUtc)} · Модератор ${escapeHtml(x.moderatorId)} · Объявление ${escapeHtml(x.listingId)}</small></article>`).join(''):'<p>Решений пока нет.</p>'}${moderationPager(audit,params,'auditPage')}</section>`;
  }
  $('#content').innerHTML=html;$('#moderation-form')?.addEventListener('submit',submitModeration);$('#moderation-refresh')?.addEventListener('click',()=>renderModeration());
 }catch(error){if(generation===routeCounter){moderationSelection=null;$('#content').innerHTML='<section class="panel"><h2>Не удалось загрузить модерацию</h2><p id="moderation-load-error" class="error" role="alert"></p><button id="moderation-retry">Повторить</button></section>';$('#moderation-load-error').textContent=error.message;$('#moderation-retry').addEventListener('click',()=>renderModeration());}}
}
async function submitModeration(event){
 event.preventDefault();const detail=moderationSelection,form=event.currentTarget,button=event.submitter;if(!detail||!button||button.disabled)return;
 const action=button.value,reason=form.elements.reason.value.trim();if(!reason){$('#moderation-error').textContent='Укажите причину решения.';return;}
 const effects=action==='BlockAuthor'?' Все объявления автора исчезнут из публичного доступа, его сеансы будут отозваны.':action==='UnblockAuthor'?' Остальные опубликованные объявления автора снова станут доступны.':action==='RestoreListing'?' Автору потребуется опубликовать объявление повторно.':'';
 if(!confirm(`${decisionLabels[action]}: «${detail.listing.title}», автор ${detail.author.displayName}?${effects}`))return;
 const generation=routeCounter;form.querySelectorAll('button').forEach(b=>b.disabled=true);$('#moderation-error').textContent='';
 try{await api(`/api/moderation/reports/${detail.report.id}/${['RestoreListing','UnblockAuthor'].includes(action)?'release':'decision'}`,{method:'POST',body:JSON.stringify({action,reason,version:detail.report.version,listingVersion:detail.listing.version,authorVersion:detail.author.version})});if(generation!==routeCounter)return;notify('Решение сохранено.');await renderModeration();}
 catch(error){if(generation===routeCounter&&form.isConnected){$('#moderation-error').textContent=error.message;form.querySelectorAll('button').forEach(b=>b.disabled=false);}}
}
document.addEventListener('click',event=>{
 if(event.target.closest('.skip-link')){event.preventDefault();const main=[$('#storefront'),$('#content'),$('#auth-screen')].find(e=>!e.hidden&&e.getClientRects().length);main?.setAttribute('tabindex','-1');main?.focus();}
 if(!event.target.closest('[data-report]'))return;
 if(!profile){loginReturnHash=location.hash;location.hash='#login';notify('Войдите, затем нажмите «Пожаловаться» ещё раз.');return;}
 const id=new URLSearchParams(location.hash.split('?')[1]).get('id');if(!id)return;
 $('#dialog-title').textContent='Пожаловаться на объявление';
 $('#dialog-content').innerHTML=`<form id="report-form"><label>Причина<select name="reason" required><option value="">Выберите причину</option>${Object.entries(reportReasons).map(([v,l])=>`<option value="${v}">${l}</option>`).join('')}</select></label><label>Пояснение (обязательно для «Другое»)<textarea name="comment" maxlength="2000"></textarea></label><p id="report-error" class="error" role="alert"></p><p id="report-status" role="status"></p><button class="primary wide" type="submit">Отправить жалобу</button></form>`;
 $('#report-form').addEventListener('submit',async event=>{
  event.preventDefault();const form=event.currentTarget,button=form.querySelector('button');if(button.disabled)return;button.disabled=true;$('#report-error').textContent='';const generation=routeCounter;
  try{const result=await api(`/api/listings/${encodeURIComponent(id)}/reports`,{method:'POST',body:JSON.stringify(Object.fromEntries(new FormData(form)))});if(!form.isConnected||generation!==routeCounter)return;$('#report-status').textContent='Обращение принято и ожидает рассмотрения. Номер: '+result.id;form.querySelectorAll('input,select,textarea').forEach(e=>e.disabled=true);}
  catch(error){if(form.isConnected&&generation===routeCounter){$('#report-error').textContent=error.message;button.disabled=false;}}
 });$('#detail-dialog').showModal();
});
window.addEventListener('hashchange',()=>{if($('#detail-dialog').open)$('#detail-dialog').close();});
