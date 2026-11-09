'use strict';
let loginReturnHash = null;
let lastCatalogHash = '#catalog';
function updateStoreHeader() {
  $('#userbar').hidden = !profile; $('#guest-login').hidden = !!profile; $('#cart-count').hidden = true;
  if (profile) $('#user-name').textContent = profile.displayName;
}
function catalogHash(params) { const query = params.toString(); return '#catalog' + (query ? '?' + query : ''); }
function navigateStore(hash) { if (location.hash === hash) render(); else location.hash = hash; }
function filtersHtml(params, categories, cities) {
  return `<form id="catalog-filters" class="catalog-filters"><div class="catalog-search"><label for="catalog-search">Поиск по названию и описанию</label><div class="search-row"><input id="catalog-search" name="search" maxlength="200" placeholder="Что вы ищете?" value="${escapeHtml(params.get('search'))}"><button class="primary" type="submit">Найти</button></div></div><div class="filter-row"><label>Категория<select name="categoryId" aria-label="Категория"><option value="">Все категории</option>${categories.map(c=>`<option value="${c.id}" ${params.get('categoryId') === String(c.id) ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}</select></label><label>Город<select name="cityId" aria-label="Город"><option value="">Все города</option>${cities.map(c=>`<option value="${c.id}" ${params.get('cityId')===String(c.id)?'selected':''}>${escapeHtml(c.name)}</option>`).join('')}</select></label><label>Состояние<select name="condition" aria-label="Состояние"><option value="">Любое</option>${[['New','Новое'],['Used','Б/у']].map(([v,l])=>`<option value="${v}" ${params.get('condition')===v?'selected':''}>${l}</option>`).join('')}</select></label><label>Цена от, ₽<input name="minPrice" type="number" min="0" max="1000000000" step="0.01" placeholder="От" value="${escapeHtml(params.get('minPrice'))}"></label><label>Цена до, ₽<input name="maxPrice" type="number" min="0" max="1000000000" step="0.01" placeholder="До" value="${escapeHtml(params.get('maxPrice'))}"></label><label>Сортировка<select name="sort" aria-label="Сортировка">${[['newest','Сначала новые'],['oldest','Сначала старые'],['price_asc','Сначала дешевле'],['price_desc','Сначала дороже']].map(([value,label])=>`<option value="${value}" ${(params.get('sort') || 'newest') === value ? 'selected' : ''}>${label}</option>`).join('')}</select></label><button type="submit" class="filter-apply">Применить</button><a class="reset-filters" href="#catalog">Сбросить</a></div><p id="filter-error" class="error" role="alert"></p></form>`;
}
const listingPrice = value => value === 0 ? 'Бесплатно' : money(value);
function storeCard(p) {
  const href='#item?id='+encodeURIComponent(p.id);
  return `<article class="product-card shop-card"><a class="cover-link" href="${href}">${p.imageUrl?`<img class="product-cover" src="${escapeHtml(p.imageUrl)}" alt="${escapeHtml(p.title)}" loading="lazy">`:'<div class="product-cover">Без фото</div>'}</a><div class="product-body"><span class="muted small">${escapeHtml(p.cityName)} · ${p.condition==='New'?'Новое':'Б/у'}</span><h2><a href="${href}">${escapeHtml(p.title)}</a></h2><p class="price">${listingPrice(p.price)}</p><p class="muted small">${escapeHtml(p.sellerName)}</p></div></article>`;
}
function catalogPager(data, params) {
  if (data.totalPages < 2 && data.page === 1) return '';
  const previous = new URLSearchParams(params); previous.set('page',String(data.page - 1));
  const next = new URLSearchParams(params); next.set('page',String(data.page + 1));
  return `<nav class="pager" aria-label="Страницы каталога">${data.page > 1 ? `<a class="page-link" href="${escapeHtml(catalogHash(previous))}">Назад</a>` : '<span class="disabled-page">Назад</span>'}<span>Страница ${data.page} из ${Math.max(1,data.totalPages)}</span>${data.page < data.totalPages ? `<a class="page-link" href="${escapeHtml(catalogHash(next))}">Далее</a>` : '<span class="disabled-page">Далее</span>'}</nav>`;
}
async function renderStorefront() {
  const token = ++routeCounter;
  const [route,query = ''] = location.hash.slice(1).split('?');
  const params = new URLSearchParams(query);
  $('#cabinet').hidden = true; $('#auth-screen').hidden = true; $('#storefront').hidden = false;
  updateStoreHeader();
  const root = $('#storefront'); root.innerHTML = '<p class="loading">Загружаем объявления…</p>';
  try {
    if (route === 'item') {
      const id = params.get('id');
      if (!id || !/^[0-9a-f-]{36}$/i.test(id)) throw new Error('Объявление не найдено. Вернитесь в каталог и выберите другой.');
      const product = await api('/api/listings/' + encodeURIComponent(id));
      if (token !== routeCounter) return;
      root.innerHTML = detailHtml(product); document.title = product.title + ' — NeAvito';
    } else {
      const allowed = ['search','categoryId','cityId','condition','minPrice','maxPrice','sort','page'];
      for (const key of [...params.keys()]) if (!allowed.includes(key)) params.delete(key);
      lastCatalogHash = catalogHash(params);
      const [categories,cities] = await Promise.all([api('/api/listings/categories'),api('/api/listings/cities')]);
      if (token !== routeCounter) return;
      root.innerHTML = `<section class="catalog-intro"><div><span class="eyebrow">NEAVITO / КАТАЛОГ</span><h1>Хорошие вещи.<br>Новая история.</h1><p>Найдите то, что нужно вам — от одежды до электроники.</p></div><div class="catalog-intro-note"><span class="intro-symbol" aria-hidden="true">↗</span><p>Выбирайте.<br>Связывайтесь с автором.<br>Договаривайтесь о встрече.</p></div></section>${filtersHtml(params,categories,cities)}<section id="catalog-results" aria-live="polite"><p class="loading">Ищем объявления…</p></section>`;
      document.title = 'Каталог — NeAvito';
      $('#catalog-filters').addEventListener('submit', event => {
        event.preventDefault(); const data = new FormData(event.target);
        const min = data.get('minPrice'), max = data.get('maxPrice');
        if (min !== '' && max !== '' && Number(min) > Number(max)) { $('#filter-error').textContent = 'Цена «от» не может быть больше цены «до».'; return; }
        const next = new URLSearchParams(); for (const [key,value] of data) if (String(value).trim()) next.set(key,String(value).trim());
        navigateStore(catalogHash(next));
      });
      const apiParams = new URLSearchParams(params); apiParams.set('pageSize','12');
      try {
        const data = await api('/api/listings?' + apiParams);
        if (token !== routeCounter) return;
        $('#catalog-results').innerHTML = `<div class="catalog-result-heading"><h2>Объявления</h2><span class="muted">Найдено: ${data.totalCount}</span></div>` + (data.items.length ? `<div class="catalog-grid">${data.items.map(storeCard).join('')}</div>` : empty(data.totalCount ? 'На этой странице нет объявлений' : 'Объявления не найдены', 'Попробуйте изменить запрос или сбросить фильтры.') + '<a class="page-link" href="#catalog">Все объявления</a>') + catalogPager(data,params);
      } catch(error) { if (token === routeCounter) { $('#catalog-results').innerHTML = '<div class="panel"><p id="catalog-error" class="error" role="alert"></p><button data-store-retry>Повторить</button></div>'; $('#catalog-error').textContent = error.message; } }
    }
  } catch(error) {
    if (token !== routeCounter) return;
    root.innerHTML = '<div class="panel empty"><h1>Не удалось открыть страницу</h1><p id="store-error" class="error" role="alert"></p><div class="form-actions"><button data-store-retry>Повторить</button><a href="#catalog" class="page-link">В каталог</a></div></div>';
    $('#store-error').textContent = error.message;
  }
}
function detailHtml(p) {
  return `<a class="back store-back" href="${escapeHtml(lastCatalogHash)}">← Назад в каталог</a><div class="product-detail"><section class="product-gallery" aria-label="Фотографии объявления">${p.images.length?`<img id="main-photo" class="main-photo" src="${escapeHtml(p.images[0].url)}" alt="${escapeHtml(p.title)}"><div class="photo-thumbs">${p.images.map((i,n)=>`<button class="photo-thumb" data-photo="${escapeHtml(i.url)}" aria-label="Фото ${n+1}" aria-pressed="${n===0}"><img src="${escapeHtml(i.url)}" alt=""></button>`).join('')}</div>`:'Нет фото'}</section><section class="product-summary"><span class="eyebrow">${escapeHtml(p.category?.name)}</span><h1>${escapeHtml(p.title)}</h1><p class="detail-price">${listingPrice(p.price)}</p><p>${escapeHtml(p.city?.name)} · ${p.condition==='New'?'Новое':'Б/у'}</p><div class="seller-panel"><h2>${escapeHtml(p.seller?.displayName)}</h2><p class="muted small">Опубликовано ${date(p.publishedAtUtc)}</p></div><button id="reveal-contact" class="primary">${profile?'Показать телефон':'Войти и показать телефон'}</button><p id="listing-contact" role="status"></p><p class="muted small">Договоритесь с автором о передаче вещи. Оплата и доставка через сайт не предусмотрены.</p></section><section class="panel product-description"><h2>Описание</h2><p>${escapeHtml(p.description)}</p></section></div>`;
}
document.addEventListener('click', async event => {
  const button=event.target.closest('button'); if(!button || button.disabled)return;
  if(button.hasAttribute('data-store-retry')) { await renderStorefront(); return; }
  if(button.dataset.photo) { $('#main-photo').src=button.dataset.photo; document.querySelectorAll('.photo-thumb').forEach(b=>b.setAttribute('aria-pressed',String(b===button))); return; }
  if(button.id!=='reveal-contact')return;
  if(!profile){loginReturnHash=location.hash; location.hash='#login';return;}
  const generation=routeCounter, viewer=profile, box=$('#listing-contact');
  const id=new URLSearchParams(location.hash.split('?')[1]).get('id');
  box.replaceChildren();button.disabled=true;
  try {
    const contact=await api('/api/listings/'+encodeURIComponent(id)+'/contact');
    if(generation!==routeCounter || viewer!==profile || !box.isConnected)return;
    const link=document.createElement('a');link.href='tel:'+contact.phone;link.textContent=contact.phone;box.append(link);
    button.textContent='Показать телефон ещё раз';
  } catch(error) {if(generation===routeCounter && box.isConnected)box.textContent=error.message;}
  finally {if(button.isConnected)button.disabled=false;}
});
window.addEventListener('hashchange',()=>$('#listing-contact')?.replaceChildren());
document.addEventListener('visibilitychange',()=>{if(document.hidden)$('#listing-contact')?.replaceChildren();});
(async function initializeApp() {
  try { profile = await api('/api/profile'); } catch { profile = null; }
  updateStoreHeader(); await render();
})();
