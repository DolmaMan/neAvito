'use strict';
let loginReturnHash = null;
let lastCatalogHash = '#catalog';
let cartQuantities = new Map();
let cartReadable = false;

function updateStoreHeader() {
  $('#userbar').hidden = !profile;
  $('#guest-login').hidden = !!profile;
  $('#cart-count').hidden = !profile || !cartReadable;
  if (profile) $('#user-name').textContent = profile.displayName;
  $('#cart-count').textContent = 'В корзине: ' + [...cartQuantities.values()].reduce((sum, qty) => sum + qty, 0);
}
function catalogHash(params) { const query = params.toString(); return '#catalog' + (query ? '?' + query : ''); }
function navigateStore(hash) { if (location.hash === hash) render(); else location.hash = hash; }
function cartButton(product) {
  const quantity = cartQuantities.get(product.id);
  return `<button class="primary" data-add-cart="${escapeHtml(product.id)}" ${!product.isAvailable || (profile && quantity) ? 'disabled' : ''}>${!product.isAvailable ? 'Нет в наличии' : profile && quantity ? `В корзине · ${quantity}` : 'В корзину'}</button>`;
}
async function readStoreCart() {
  cartQuantities = new Map(); cartReadable = false;
  if (profile) {
    try { const cart = await api('/api/cart'); cartQuantities = new Map(cart.items.map(item => [item.productId,item.quantity])); cartReadable = true; }
    catch (error) { if (profile) notify('Не удалось обновить сведения о корзине. При добавлении проверим ее еще раз.'); }
  }
  updateStoreHeader();
}
function filtersHtml(params, categories) {
  return `<form id="catalog-filters" class="catalog-filters"><div class="catalog-search"><label for="catalog-search">Поиск по названию</label><div class="search-row"><input id="catalog-search" name="search" maxlength="200" placeholder="Что вы ищете?" value="${escapeHtml(params.get('search'))}"><button class="primary" type="submit">Найти</button></div></div><div class="filter-row"><label>Категория<select name="categoryId" aria-label="Категория"><option value="">Все категории</option>${categories.map(c=>`<option value="${c.id}" ${params.get('categoryId') === String(c.id) ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}</select></label><label>Цена от, ₽<input name="minPrice" type="number" min="0" max="1000000000" step="0.01" placeholder="От" value="${escapeHtml(params.get('minPrice'))}"></label><label>Цена до, ₽<input name="maxPrice" type="number" min="0" max="1000000000" step="0.01" placeholder="До" value="${escapeHtml(params.get('maxPrice'))}"></label><label>Сортировка<select name="sort" aria-label="Сортировка">${[['newest','Сначала новые'],['oldest','Сначала старые'],['price_asc','Сначала дешевле'],['price_desc','Сначала дороже']].map(([value,label])=>`<option value="${value}" ${(params.get('sort') || 'newest') === value ? 'selected' : ''}>${label}</option>`).join('')}</select></label><button type="submit" class="filter-apply">Применить</button><a class="reset-filters" href="#catalog">Сбросить</a></div><p id="filter-error" class="error" role="alert"></p></form>`;
}
function storeCard(product) {
  const href = '#item?id=' + encodeURIComponent(product.id);
  return `<article class="product-card shop-card"><a class="cover-link" href="${href}" aria-label="Открыть ${escapeHtml(product.name)}">${product.imageUrl ? `<img class="product-cover" src="${escapeHtml(product.imageUrl)}" alt="${escapeHtml(product.name)}" loading="lazy">` : '<div class="product-cover image-placeholder"><span aria-hidden="true">◇</span>Без фотографии</div>'}</a><div class="product-body"><div class="card-top"><span class="muted small">${escapeHtml(product.categoryName)}</span><span class="stock-label ${product.isAvailable ? '' : 'sold-out'}">${product.isAvailable ? 'В наличии' : 'Нет в наличии'}</span></div><h2><a href="${href}">${escapeHtml(product.name)}</a></h2><p class="muted small seller-label">${escapeHtml(product.seller.displayName)}</p><div class="order-row"><span class="price">${money(product.price)}</span>${cartButton(product)}</div></div></article>`;
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
  const root = $('#storefront'); root.innerHTML = '<p class="loading">Загружаем товары…</p>';
  try {
    if (route === 'item') {
      const id = params.get('id');
      if (!id || !/^[0-9a-f-]{36}$/i.test(id)) throw new Error('Товар не найден. Вернитесь в каталог и выберите другой.');
      const [product] = await Promise.all([api('/api/catalog/products/' + encodeURIComponent(id)),readStoreCart()]);
      if (token !== routeCounter) return;
      root.innerHTML = detailHtml(product); document.title = product.name + ' — NeAvito';
    } else {
      const allowed = ['search','categoryId','minPrice','maxPrice','sort','page'];
      for (const key of [...params.keys()]) if (!allowed.includes(key)) params.delete(key);
      lastCatalogHash = catalogHash(params);
      const [categories] = await Promise.all([api('/api/catalog/categories'),readStoreCart()]);
      if (token !== routeCounter) return;
      root.innerHTML = `<section class="catalog-intro"><div><span class="eyebrow">NEAVITO / КАТАЛОГ</span><h1>Хорошие вещи.<br>Новая история.</h1><p>Найдите то, что нужно вам — от одежды до электроники.</p></div><div class="catalog-intro-note"><span class="intro-symbol" aria-hidden="true">↗</span><p>Выбирайте.<br>Покупайте.<br>Пользуйтесь с удовольствием.</p></div></section>${filtersHtml(params,categories)}<section id="catalog-results" aria-live="polite"><p class="loading">Ищем товары…</p></section>`;
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
        const data = await api('/api/catalog/products?' + apiParams);
        if (token !== routeCounter) return;
        $('#catalog-results').innerHTML = `<div class="catalog-result-heading"><h2>Товары</h2><span class="muted">Найдено: ${data.totalCount}</span></div>` + (data.items.length ? `<div class="catalog-grid">${data.items.map(storeCard).join('')}</div>` : empty(data.totalCount ? 'На этой странице нет товаров' : 'Товары не найдены', 'Попробуйте изменить запрос или сбросить фильтры.') + '<a class="page-link" href="#catalog">Все товары</a>') + catalogPager(data,params);
      } catch(error) { if (token === routeCounter) { $('#catalog-results').innerHTML = '<div class="panel"><p id="catalog-error" class="error" role="alert"></p><button data-store-retry>Повторить</button></div>'; $('#catalog-error').textContent = error.message; } }
    }
  } catch(error) {
    if (token !== routeCounter) return;
    root.innerHTML = '<div class="panel empty"><h1>Не удалось открыть страницу</h1><p id="store-error" class="error" role="alert"></p><div class="form-actions"><button data-store-retry>Повторить</button><a href="#catalog" class="page-link">В каталог</a></div></div>';
    $('#store-error').textContent = error.message;
  }
}
function detailHtml(product) {
  const images = product.images || [];
  return `<a class="back store-back" href="${escapeHtml(lastCatalogHash)}">← Назад в каталог</a><div class="product-detail"><section class="product-gallery" aria-label="Фотографии товара">${images.length ? `<img id="main-photo" class="main-photo" src="${escapeHtml(images[0].url)}" alt="${escapeHtml(product.name)}"><div class="photo-thumbs">${images.map((image,index)=>`<button class="photo-thumb" data-photo="${escapeHtml(image.url)}" aria-label="Фото ${index + 1}" aria-pressed="${index === 0}"><img src="${escapeHtml(image.url)}" alt="" loading="lazy"></button>`).join('')}</div>` : '<div class="main-photo image-placeholder"><span aria-hidden="true">◇</span>Фотографий пока нет</div>'}</section><section class="product-summary"><span class="eyebrow">${escapeHtml(product.category.name)}</span><h1>${escapeHtml(product.name)}</h1><p class="detail-price">${money(product.price)}</p><p class="stock-label ${product.isAvailable ? '' : 'sold-out'}">${product.isAvailable ? `В наличии: ${product.stock} шт.` : 'Нет в наличии'}</p><div class="purchase-actions">${cartButton(product)}</div><p class="muted small">${profile ? 'Наличие и цена повторно проверяются при оформлении.' : 'Чтобы добавить товар в корзину, войдите в аккаунт.'}</p><div class="seller-panel"><span class="muted small">Продавец</span><h2>${escapeHtml(product.seller.displayName)}</h2><span class="muted small">Опубликовано ${date(product.createdAtUtc)}</span></div></section><section class="panel product-description"><h2>Описание</h2><p>${escapeHtml(product.description)}</p></section></div>`;
}
document.addEventListener('click', async event => {
  const button = event.target.closest('button'); if (!button || button.disabled) return;
  if (button.hasAttribute('data-store-retry')) { await renderStorefront(); return; }
  if (button.dataset.photo) { $('#main-photo').src = button.dataset.photo; document.querySelectorAll('.photo-thumb').forEach(b=>b.setAttribute('aria-pressed',String(b===button))); return; }
  const id = button.dataset.addCart; if (!id) return;
  if (!profile) { loginReturnHash = '#item?id=' + encodeURIComponent(id); location.hash = '#login'; notify('Войдите, затем добавьте выбранный товар в корзину.'); return; }
  button.disabled = true;
  try {
    const current = await api('/api/cart');
    let cart = current;
    if (!current.items.some(item => item.productId === id)) cart = await api('/api/cart/items/' + encodeURIComponent(id),{method:'PUT',body:JSON.stringify({quantity:1})});
    cartQuantities = new Map(cart.items.map(item=>[item.productId,item.quantity])); cartReadable = true; updateStoreHeader();
    document.querySelectorAll('[data-add-cart]').forEach(b=>{ const quantity=cartQuantities.get(b.dataset.addCart); if (quantity) {b.textContent=`В корзине · ${quantity}`;b.disabled=true;} });
    notify(current.items.some(item=>item.productId===id) ? 'Этот товар уже есть в корзине. Количество сохранено.' : 'Товар добавлен в корзину.');
  } catch(error) { if (button.isConnected) button.disabled = false; notify(error.message); }
});
(async function initializeApp() {
  try { profile = await api('/api/profile'); } catch { profile = null; }
  updateStoreHeader(); await render();
})();
