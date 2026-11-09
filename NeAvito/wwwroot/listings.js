'use strict';
let editedListing = null;
const listingLabels = {Draft:'Черновик',Published:'Опубликовано',Unpublished:'Снято',Sold:'Продано',Archived:'В архиве'};
const listingState = value => `<span class="badge">${listingLabels[value] || escapeHtml(value)}</span>`;
document.addEventListener('click', event => {
  const link=event.target.closest('#content a[href^="#listings"]');
  if(link && productDirty && location.hash.startsWith('#listing?')) {
    if(!confirm('Выйти без сохранения изменений?')) event.preventDefault();
    else productDirty=false;
  }
});

async function renderListings() {
  const generation=++routeCounter;
  $('#storefront').hidden=true; $('#auth-screen').hidden=true; $('#cabinet').hidden=false; updateStoreHeader();
  document.querySelectorAll('nav a').forEach(a=>a.classList.toggle('active',a.hash==='#listings'));
  $('#content').innerHTML='<p class="loading">Загружаем объявления…</p>';
  try {
    const [route,query='']=location.hash.slice(1).split('?');
    const params=new URLSearchParams(query);
    let html;
    if(route==='listings') {
      const page=Math.max(1,Number(params.get('page'))||1);
      const data=await api(`/api/me/listings?page=${page}&pageSize=12`);
      html=pageHeader('Мои объявления','Размещайте вещи и управляйте публикациями.','<a class="page-link" href="#listing">Добавить объявление</a>')+
        '<p class="muted small">Новый модуль объявлений. Публичный поиск и контакты будут подключены на следующем этапе.</p>'+
        (data.items.length?'<div class="cards">'+data.items.map(p=>`<article class="product-card">${p.images.length?`<img class="product-cover" src="${escapeHtml(p.images[0].url)}" alt="">`:'<div class="product-cover">Без фото</div>'}<div class="product-body">${listingState(p.status)}<h2>${escapeHtml(p.title||'Без заголовка')}</h2><p>${p.price===null?'Цена не указана':p.price===0?'Бесплатно':money(p.price)}</p><a class="page-link" href="#listing?id=${p.id}">Открыть</a></div></article>`).join('')+'</div>':empty('Пока нет объявлений','Начните с черновика — его можно заполнить частично.'))+pagination(data,'listings');
    } else {
      const id=params.get('id');
      const [categories,cities,p]=await Promise.all([api('/api/listings/categories'),api('/api/listings/cities'),id?api('/api/me/listings/'+encodeURIComponent(id)):null]);
      if(generation!==routeCounter) return;
      editedListing=p; productDirty=false;
      html=listingEditor(p,categories,cities);
    }
    if(generation!==routeCounter) return;
    $('#content').innerHTML=html; document.title='Мои объявления — NeAvito';
    const form=$('#listing-form');
    if(form) {
      form.addEventListener('submit',saveListing);
      form.addEventListener('input',()=>{productDirty=true;});
      form.elements.phone.addEventListener('input',()=>{form.elements.contactConsent.checked=false;});
      $('#listing-photo')?.addEventListener('change',uploadListingPhoto);
      $('#listing-actions')?.addEventListener('click',listingCommand);
      $('#listing-images')?.addEventListener('click',deleteListingPhoto);
    }
  } catch(error) {
    if(generation===routeCounter && profile) $('#content').innerHTML=empty('Не удалось загрузить объявления',escapeHtml(error.message))+'<button data-action="reload">Повторить</button>';
  }
}

function listingEditor(p,categories,cities) {
  const locked=p && ['Sold','Archived'].includes(p.status);
  const options=(items,id)=>'<option value="">Пока не выбрано</option>'+items.map(x=>`<option value="${x.id}" ${x.id===id?'selected':''}>${escapeHtml(x.name)}</option>`).join('');
  const actions=p?({Draft:['publish'],Published:['unpublish','sold'],Unpublished:['publish','archive'],Sold:['reopen','archive'],Archived:[]}[p.status]):[];
  const actionLabels={publish:'Опубликовать',unpublish:'Снять с публикации',sold:'Отметить проданным',reopen:'Вернуть в снятые',archive:'В архив'};
  return '<a class="page-link" href="#listings">← Мои объявления</a>'+pageHeader(p?'Объявление':'Новое объявление','Черновик можно сохранить частично. Для публикации заполните все поля и добавьте фото.')+
    `<section class="panel"><form id="listing-form"><fieldset ${locked?'disabled':''}>
    <label>Заголовок<input name="title" maxlength="200" value="${escapeHtml(p?.title)}"></label>
    <label>Описание<textarea name="description" maxlength="10000">${escapeHtml(p?.description)}</textarea></label>
    <label>Цена, ₽ (0 — бесплатно)<input name="price" type="number" min="0" max="1000000000" step="0.01" value="${p?.price??''}"></label>
    <div class="grid-two"><label>Категория<select name="categoryId">${options(categories,p?.categoryId)}</select></label><label>Город<select name="cityId">${options(cities,p?.cityId)}</select></label></div>
    <label>Состояние вещи<select name="condition"><option value="">Пока не выбрано</option><option value="New" ${p?.condition==='New'?'selected':''}>Новое</option><option value="Used" ${p?.condition==='Used'?'selected':''}>Б/у</option></select></label>
    <label>Телефон для этого объявления<input name="phone" type="tel" maxlength="30" value="${escapeHtml(p?.phone??profile?.phone??'')}"></label>
    <label class="consent-label"><input name="contactConsent" type="checkbox" ${p?.contactConsent?'checked':''}> Разрешаю показывать этот телефон вошедшим пользователям для связи по объявлению</label>
    <p class="muted small">Смена номера в профиле не изменяет контакт объявления. При изменении номера здесь нужно подтвердить согласие снова. Отзыв согласия снимает опубликованное объявление.</p>
    <button type="submit" class="primary">Сохранить ${p?'изменения':'черновик'}</button></fieldset><p id="listing-error" class="error" role="alert"></p></form></section>`+
    (p?`<section class="panel"><h2>Фотографии</h2><p class="muted small">До 10 JPEG, PNG или WebP, до 5 МБ; для публикации нужно хотя бы одно фото.</p><div id="listing-images" class="images">${p.images.map(i=>`<div class="image-tile"><img src="${escapeHtml(i.url)}" alt="Фото объявления"><button type="button" data-listing-image="${i.id}" ${locked?'disabled':''}>Удалить</button></div>`).join('')}</div><label>Добавить фото<input id="listing-photo" type="file" accept="image/jpeg,image/png,image/webp" ${locked||p.images.length>=10?'disabled':''}></label></section>
    <section class="panel"><h2>Публикация</h2>${listingState(p.status)}${p.moderationHidden?'<p class="error">Скрыто модератором</p>':''}${p.publicationErrors.length?'<ul>'+p.publicationErrors.map(x=>'<li>'+escapeHtml(x)+'</li>').join('')+'</ul>':''}<div id="listing-actions" class="form-actions">${actions.map(a=>`<button type="button" data-listing-command="${a}">${actionLabels[a]}</button>`).join('')}</div><p class="muted small">${locked?'Проданное можно вернуть в снятые. Архив доступен только для просмотра.':'Изменения сначала сохраните, затем меняйте публикацию или фотографии.'}</p></section>`:'');
}
async function saveListing(event) {
  event.preventDefault();
  const form=event.currentTarget, button=form.querySelector('button[type=submit]'), errorBox=$('#listing-error');
  if(button.disabled)return; button.disabled=true; errorBox.textContent='';
  const fields=Object.fromEntries(new FormData(form));
  const payload={title:fields.title,description:fields.description,price:fields.price===''?null:Number(fields.price),categoryId:fields.categoryId?Number(fields.categoryId):null,cityId:fields.cityId?Number(fields.cityId):null,condition:fields.condition||null,phone:fields.phone,contactConsent:form.elements.contactConsent.checked,...(editedListing?{version:editedListing.version}:{})};
  try {
    const before=editedListing?.status;
    const saved=await api('/api/me/listings'+(editedListing?'/'+editedListing.id:''),{method:editedListing?'PUT':'POST',body:JSON.stringify(payload)});
    productDirty=false;
    notify(before==='Published'&&saved.status==='Unpublished'?'Изменения сохранены. Объявление снято: проверьте условия публикации.':'Объявление сохранено.');
    const hash='#listing?id='+saved.id;
    if(location.hash===hash)await render();else location.hash=hash;
  } catch(error) { if(errorBox.isConnected)errorBox.textContent=error.message; }
  finally {button.disabled=false;}
}
async function listingCommand(event) {
  const button=event.target.closest('[data-listing-command]'); if(!button||button.disabled)return;
  if(productDirty){notify('Сначала сохраните изменения.');return;}
  const command=button.dataset.listingCommand;
  if(['archive','sold'].includes(command)&&!confirm(command==='sold'?'Отметить вещь проданной?':'Переместить объявление в архив? Редактирование станет недоступно.'))return;
  button.disabled=true;
  try {await api(`/api/me/listings/${editedListing.id}/${command}`,{method:'POST',body:JSON.stringify({version:editedListing.version})});await render();}
  catch(error){notify(error.message);}finally{button.disabled=false;}
}
async function uploadListingPhoto(event) {
  const input=event.currentTarget,file=input.files[0];if(!file)return;
  if(productDirty){notify('Сначала сохраните изменения.');input.value='';return;}
  input.disabled=true;
  try {const form=new FormData();form.append('file',file);form.append('version',editedListing.version);await api(`/api/me/listings/${editedListing.id}/images`,{method:'POST',body:form});await render();}
  catch(error){notify(error.message);input.value='';}finally{input.disabled=false;}
}
async function deleteListingPhoto(event) {
  const button=event.target.closest('[data-listing-image]');if(!button||button.disabled)return;
  if(productDirty){notify('Сначала сохраните изменения.');return;}
  if(!confirm('Удалить фото? Если оно последнее, объявление будет снято с публикации.'))return;
  button.disabled=true;
  try {await api(`/api/me/listings/${editedListing.id}/images/${button.dataset.listingImage}?version=${editedListing.version}`,{method:'DELETE'});await render();}
  catch(error){notify(error.message);}finally{button.disabled=false;}
}
