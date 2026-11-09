'use strict';
const $ = (selector, root = document) => root.querySelector(selector);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const money = value => new Intl.NumberFormat('ru-RU', { style:'currency', currency:'RUB', maximumFractionDigits:2 }).format(value);
const date = value => new Intl.DateTimeFormat('ru-RU', { dateStyle:'medium' }).format(new Date(value));
const labels = { Draft:'Черновик', Published:'Опубликован', Unpublished:'Снят с публикации', New:'Новый', Confirmed:'Подтвержден', Completed:'Завершен', Cancelled:'Отменен' };
const badge = status => `<span class="badge ${escapeHtml(status)}">${escapeHtml(labels[status] || status)}</span>`;
let profile = null, routeCounter = 0, noticeTimer, currentProduct = null, productDirty = false;
let activeAuthMode = 'login', accountLink = null;
let categories = [];
function notify(message) { const box = $('#notice'); box.textContent = message; box.hidden = false; clearTimeout(noticeTimer); noticeTimer = setTimeout(() => { box.hidden = true; }, 6000); }
function showAuth() { $('#storefront').hidden = true; $('#guest-login').hidden = !!profile; $('#cart-count').hidden = true; routeCounter++; $('#cabinet').hidden = true; $('#userbar').hidden = true; $('#auth-screen').hidden = false; $('#content').replaceChildren(); $('#detail-dialog').close(); }
async function api(path, options = {}) {
  const headers = { 'X-NeAvito-Request':'1', ...options.headers };
  if (options.body && !(options.body instanceof FormData)) headers['Content-Type'] = 'application/json';
  let response;
  try { response = await fetch(path, { ...options, headers, credentials:'same-origin', cache:'no-store' }); }
  catch { throw new Error('Не удалось связаться с сервером. Проверьте подключение и повторите действие.'); }
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    if (response.status === 401 && profile && path !== '/api/auth/login') {
      profile = null;
      const section = location.hash.slice(1).split('?')[0];
      if (['item', 'profile', 'purchases', 'products', 'sales', 'product', 'listings', 'listing'].includes(section)) loginReturnHash = location.hash;
      history.replaceState(null, '', '#login');
      authMode('login');
      showAuth();
      notify('Сессия завершилась. Войдите снова.');
      throw new Error('Сессия завершилась. Войдите снова.');
    }
    const identityErrors = { DuplicateUserName:'Эта почта уже зарегистрирована.', DuplicateEmail:'Эта почта уже зарегистрирована.', PasswordTooShort:'Пароль должен содержать не менее 8 символов.', PasswordRequiresNonAlphanumeric:'Добавьте в пароль специальный символ.', PasswordRequiresDigit:'Добавьте в пароль цифру.', PasswordRequiresUpper:'Добавьте в пароль прописную латинскую букву.', PasswordRequiresLower:'Добавьте в пароль строчную латинскую букву.' };
    identityErrors.PasswordMismatch = 'Текущий пароль указан неверно.';
    identityErrors.ConcurrencyFailure = 'Данные аккаунта изменились. Обновите страницу и повторите действие.';
    let message = data?.message;
    if (!message && data?.errors) message = Array.isArray(data.errors) ? data.errors.map(x => identityErrors[x.code] || x.description).join('\n') : 'Проверьте заполнение полей формы.';
    throw new Error(message || (response.status === 404 ? 'Запись не найдена или недоступна.' : 'Не удалось выполнить действие. Повторите попытку.'));
  }
  return data;
}
const authCopy = {
  login: ['Вход', 'Войдите, чтобы открыть личный кабинет. Перед первым входом подтвердите почту.', 'Войти'],
  register: ['Регистрация', 'Создайте аккаунт, чтобы размещать объявления и связываться с авторами.', 'Создать аккаунт'],
  'forgot-password': ['Восстановление пароля', 'Укажите почту, которую подтвердили при регистрации.', 'Запросить ссылку'],
  'resend-confirmation': ['Подтверждение почты', 'Укажите почту, с которой зарегистрировались.', 'Запросить ссылку'],
  'confirm-email': ['Подтверждение почты', 'Подтвердите адрес, чтобы войти в аккаунт.', 'Подтвердить почту'],
  'reset-password': ['Новый пароль', 'После смены пароля потребуется войти заново.', 'Сохранить пароль']
};

function authMode(mode) {
  concealPasswords();
  activeAuthMode = mode;
  const register = mode === 'register';
  const reset = mode === 'reset-password';
  const confirm = mode === 'confirm-email';
  const password = $('#auth-form [name=password]');
  const email = $('#auth-form [name=email]');
  const [title, hint, action] = authCopy[mode];
  $('#register-name').hidden = !register;
  $('#register-name input').disabled = !register;
  $('#register-name input').required = register;
  $('#auth-email').hidden = reset || confirm;
  email.disabled = reset || confirm;
  email.required = !email.disabled;
  password.disabled = !(mode === 'login' || register || reset);
  password.required = !password.disabled;
  $('#auth-password').hidden = password.disabled;
  password.value = '';
  password.autocomplete = register || reset ? 'new-password' : 'current-password';
  password.minLength = register || reset ? 8 : 1;
  $('#password-hint').hidden = !(register || reset);
  $('#auth-title').textContent = title;
  $('#auth-hint').textContent = hint;
  $('#auth-submit').textContent = action;
  $('#auth-submit').hidden = (reset || confirm) && !accountLink;
  $('[data-auth-mode=login]').classList.toggle('selected', mode === 'login');
  $('[data-auth-mode=register]').classList.toggle('selected', register);
  $('#auth-error').textContent = $('#auth-submit').hidden ? 'Запросите новую ссылку и откройте её из письма.' : '';
  $('#auth-status').textContent = '';
}

$('#auth-screen').addEventListener('click', event => {
  const mode = event.target.closest('[data-auth-mode]')?.dataset.authMode;
  if (!mode) return;
  if (accountLink || ['confirm-email', 'reset-password'].includes(location.hash.slice(1))) {
    accountLink = null;
    history.replaceState(null, '', '#login');
  }
  if (profile && mode === 'login') { location.hash = '#profile'; return; }
  authMode(mode);
});

$('#guest-login').addEventListener('click', () => {
  const route = location.hash.slice(1).split('?')[0];
  if (!route || route === 'catalog' || route === 'item') loginReturnHash = location.hash || '#catalog';
});

$('#auth-form').addEventListener('submit', async event => {
  event.preventDefault();
  const mode = activeAuthMode;
  const generation = routeCounter;
  const button = $('#auth-submit');
  if (button.disabled || button.hidden) return;
  button.disabled = true;
  $('#auth-error').textContent = '';
  $('#auth-status').textContent = '';
  const data = Object.fromEntries(new FormData(event.target));
  const body = ['reset-password', 'confirm-email'].includes(mode) ? { ...data, ...accountLink } : data;
  try {
    const result = await api('/api/auth/' + mode, { method:'POST', body:JSON.stringify(body) });
    if (mode === 'reset-password' && profile?.id === body.userId) {
      profile = null;
      updateStoreHeader();
    }
    if (mode === 'login') {
      if (generation !== routeCounter || mode !== activeAuthMode) {
        profile = await api('/api/profile');
        updateStoreHeader();
        return;
      }
      event.target.reset();
      await openCabinet();
      return;
    }
    if (generation !== routeCounter || mode !== activeAuthMode) return;
    if (mode === 'register') {
      authMode('login');
      $('#auth-status').textContent = result.confirmationSent
        ? 'Аккаунт создан. Откройте ссылку из письма, затем войдите.'
        : 'Аккаунт создан, но письмо не удалось отправить. Запросите ссылку повторно.';
    } else if (mode === 'confirm-email' || mode === 'reset-password') {
      accountLink = null;
      history.replaceState(null, '', '#login');
      authMode('login');
      $('#auth-status').textContent = mode === 'confirm-email' ? 'Почта подтверждена. Теперь можно войти.' : 'Пароль сохранён. Войдите с новым паролем.';
    } else {
      $('#auth-status').textContent = result.message;
    }
  } catch (error) {
    if (generation === routeCounter && mode === activeAuthMode) $('#auth-error').textContent = error.message;
  } finally {
    button.disabled = false;
  }
});
$('#logout').addEventListener('click', async () => { $('#logout').disabled = true; try { await api('/api/auth/logout', {method:'POST'}); profile = null; updateStoreHeader(); location.hash = '#catalog'; await render(); } catch(error) { notify(error.message); } finally { $('#logout').disabled = false; } });
$('#close-dialog').addEventListener('click', () => $('#detail-dialog').close());
async function openCabinet() { profile = await api('/api/profile'); updateStoreHeader(); const target = loginReturnHash || '#profile'; loginReturnHash = null; if (location.hash !== target) location.hash = target; else await render(); }
const pageHeader = (title, description, action = '') => `<div class="page-header"><div><h1>${title}</h1><p class="muted">${description}</p></div>${action}</div>`;
function empty(title, description) { return `<div class="panel empty"><div class="empty-icon" aria-hidden="true">◇</div><h2>${title}</h2><p class="muted">${description}</p></div>`; }
function pagination(data, route) { if (data.totalPages < 2 && data.page === 1) return ''; return `<div class="pager"><button data-page="${data.page - 1}" data-route="${route}" ${data.page <= 1 ? 'disabled' : ''}>Назад</button><span>Страница ${data.page} из ${Math.max(1,data.totalPages)}</span><button data-page="${data.page + 1}" data-route="${route}" ${data.page >= data.totalPages ? 'disabled' : ''}>Далее</button></div>`; }
async function render() {
  const [activeRoute, authQuery] = location.hash.slice(1).split('?');
  if (activeRoute === 'confirm-email' || activeRoute === 'reset-password') {
    if (authQuery !== undefined) {
      const params = new URLSearchParams(authQuery);
      const userId = params.get('userId');
      const token = params.get('token');
      accountLink = userId && token ? { userId, token } : null;
      history.replaceState(null, '', '#' + activeRoute);
    } else if (activeRoute !== activeAuthMode) {
      accountLink = null;
    }
    showAuth();
    authMode(activeRoute);
    return;
  }
  accountLink = null;
  if (!activeRoute || activeRoute === 'catalog' || activeRoute === 'item') return renderStorefront();
  if (!profile) { if (activeRoute !== 'login') loginReturnHash = location.hash; authMode('login'); showAuth(); return; }
  if (activeRoute === 'login') { location.hash = '#profile'; return; }
  if (activeRoute === 'listings' || activeRoute === 'listing') return renderListings();
  $('#storefront').hidden = true; $('#auth-screen').hidden = true; $('#cabinet').hidden = false; updateStoreHeader();
  const token = ++routeCounter;
  const [route = 'profile', query = ''] = location.hash.slice(1).split('?');
  const params = new URLSearchParams(query); const page = Math.min(1000000,Math.max(1,parseInt(params.get('page'),10)||1));
  const section = ['profile','purchases','products','sales','product'].includes(route) ? route : 'profile';
  document.querySelectorAll('nav a').forEach(a => a.classList.toggle('active', a.hash === '#' + (section === 'product' ? 'products' : section)));
  $('#content').innerHTML = '<p class="loading">Загружаем данные…</p>';
  try {
    let html;
    if (section === 'profile') { const fresh = await api('/api/profile'); if (token !== routeCounter) return; profile = fresh; html = profilePage() + accountSecurityPage(); }
    if (section === 'products') { const data = await api(`/api/seller/products?page=${page}&pageSize=8`); html = productsPage(data); }
    if (section === 'purchases' || section === 'sales') { const data = await api(`${section === 'sales' ? '/api/seller/orders' : '/api/orders'}?page=${page}&pageSize=10`); html = ordersPage(data,section); }
    if (section === 'product') { const id = params.get('id'); const [cats,product] = await Promise.all([api('/api/catalog/categories'), id ? api('/api/seller/products/' + encodeURIComponent(id)) : Promise.resolve(null)]); if (token !== routeCounter) return; categories = cats; currentProduct = product; productDirty = false; html = editorPage(product); }
    if (token !== routeCounter) return;
    $('#content').innerHTML = html; document.title = ({profile:'Мой профиль',products:'Мои товары',purchases:'Мои покупки',sales:'Полученные заказы',product:'Редактирование товара'}[section]) + ' — NeAvito';
    if (section === 'profile') {
      initializePasswordToggles($('#content'));
      $('#profile-form').addEventListener('submit', saveProfile);
      $('#change-password-form').addEventListener('submit', changeAccountPassword);
      $('#logout-all').addEventListener('click', logoutEverywhere);
    }
    if (section === 'product') { $('#product-form').addEventListener('submit', saveProduct); $('#product-form').addEventListener('input', () => { productDirty = true; }); $('#image-file')?.addEventListener('change', uploadImage); }
  } catch(error) { if (token === routeCounter && profile) { $('#content').innerHTML = '<div class="panel"><h2>Не удалось загрузить раздел</h2><p id="load-error" class="error"></p><button data-action="reload">Повторить</button></div>'; $('#load-error').textContent = error.message; } }
}
function profilePage() { return pageHeader('Мой профиль','Ваши данные для покупок и общения с продавцами.') + `<div class="profile-layout"><section class="panel"><h2>Личные данные</h2><form id="profile-form"><label>Имя<input name="displayName" value="${escapeHtml(profile.displayName)}" maxlength="100" required autocomplete="name"></label><label>Электронная почта<input value="${escapeHtml(profile.email)}" readonly type="email"></label><label>Телефон<input name="phone" value="${escapeHtml(profile.phone)}" maxlength="30" type="tel" autocomplete="tel" placeholder="+7 999 123-45-67"></label><label>Адрес или место получения<textarea name="deliveryAddress" maxlength="500" autocomplete="street-address" placeholder="Укажите удобное место получения">${escapeHtml(profile.deliveryAddress)}</textarea></label><p class="error" id="profile-error" role="alert"></p><button class="primary">Сохранить изменения</button></form></section><aside class="note-panel"><div class="avatar">${escapeHtml(profile.displayName.slice(0,1).toUpperCase())}</div><h3>Покупатель и продавец</h3><p>Вы можете покупать вещи и размещать свои товары из одного аккаунта.</p><p class="muted">Изменения профиля не меняют данные уже оформленных заказов.</p></aside></div>`; }
async function saveProfile(event) { event.preventDefault(); const form = event.target; const button = $('button',form); button.disabled = true; $('#profile-error').textContent = ''; try { const data = Object.fromEntries(new FormData(form)); data.phone = data.phone.trim() || null; data.version = profile.version; profile = await api('/api/profile',{method:'PUT',body:JSON.stringify(data)}); $('#user-name').textContent = profile.displayName; notify('Профиль сохранен.'); } catch(error) { const errorBox = $('#profile-error'); if (errorBox) errorBox.textContent = error.message; else notify(error.message); } finally { button.disabled = false; } }
function productsPage(data) { return pageHeader('Мои товары',`Всего товаров: ${data.totalCount}. Управляйте объявлениями и остатками.`, '<button class="primary" data-action="new-product">Добавить товар</button>') + (data.items.length ? '<div class="cards">' + data.items.map(p => `<article class="product-card">${p.images.length ? `<img class="product-cover" src="${escapeHtml(p.images[0].url)}" alt="${escapeHtml(p.name)}">` : '<div class="product-cover">Без фотографии</div>'}<div class="product-body"><div class="card-top">${badge(p.status)}<span class="muted small">В наличии: ${p.stock}</span></div><h2>${escapeHtml(p.name)}</h2><p class="price">${money(p.price)}</p><div class="form-actions"><button data-edit="${p.id}">Редактировать</button><button class="quiet" data-publication="${p.status === 'Published' ? 'unpublish' : 'publish'}" data-id="${p.id}">${p.status === 'Published' ? 'Снять с публикации' : 'Опубликовать'}</button></div></div></article>`).join('') + '</div>' : empty('Пока нет товаров','Добавьте первый товар: название, описание, цену и фотографии.')) + pagination(data,'products'); }
function editorPage(p) { return '<button class="quiet back" data-action="back-products">← Мои товары</button>' + pageHeader(p ? 'Редактирование товара' : 'Новый товар', p ? 'Изменения опубликованного товара видны покупателям после сохранения.' : 'Сначала сохраним черновик. Затем можно добавить фотографии и опубликовать.') + `<section class="panel"><form id="product-form"><label>Название<input name="name" required maxlength="200" value="${escapeHtml(p?.name)}"></label><label>Описание<textarea name="description" required maxlength="10000">${escapeHtml(p?.description)}</textarea></label><div class="grid-two"><label>Цена, ₽<input name="price" type="number" min="0.01" max="1000000000" step="0.01" required value="${p?.price ?? ''}"></label><label>Количество<input name="stock" type="number" min="0" max="2147483647" step="1" required value="${p?.stock ?? 1}"></label></div><label>Категория<select name="categoryId" required><option value="">Выберите категорию</option>${categories.map(c => `<option value="${c.id}" ${p?.categoryId === c.id ? 'selected' : ''}>${escapeHtml(c.name)}</option>`).join('')}</select></label><p id="product-error" class="error" role="alert"></p><div class="form-actions"><button class="primary">${p ? 'Сохранить изменения' : 'Создать черновик'}</button><button type="button" class="quiet" data-action="back-products">Отмена</button></div></form></section>` + (p ? `<section class="panel"><h2>Фотографии</h2><p class="muted small">До 10 файлов JPEG, PNG или WebP по 5 МБ. До 12 мегапикселей и 6000 пикселей по стороне.</p><div class="images">${p.images.map(i => `<div class="image-tile"><img src="${escapeHtml(i.url)}" alt="Фото товара"><button data-delete-image="${i.id}" class="quiet danger">Удалить</button></div>`).join('')}</div><label class="upload-label">Добавить фотографию<input id="image-file" type="file" accept="image/jpeg,image/png,image/webp" ${p.images.length >= 10 ? 'disabled' : ''}></label></section><section class="panel"><div class="card-top"><h2>Публикация</h2>${badge(p.status)}</div><button data-publication="${p.status === 'Published' ? 'unpublish' : 'publish'}" data-id="${p.id}">${p.status === 'Published' ? 'Снять с публикации' : 'Опубликовать товар'}</button></section>` : ''); }
async function saveProduct(event) { event.preventDefault(); const button = $('button',event.target); button.disabled = true; $('#product-error').textContent = ''; try { const data = Object.fromEntries(new FormData(event.target)); data.price = Number(data.price); data.stock = Number(data.stock); data.categoryId = Number(data.categoryId); if (currentProduct) data.version = currentProduct.version; const saved = await api('/api/seller/products' + (currentProduct ? '/' + currentProduct.id : ''),{method:currentProduct ? 'PUT' : 'POST',body:JSON.stringify(data)}); productDirty = false; notify('Товар сохранен.'); const route = '#product?id=' + saved.id; if (location.hash === route) await render(); else location.hash = route; } catch(error) { const errorBox = $('#product-error'); if (errorBox) errorBox.textContent = error.message; else notify(error.message); button.disabled = false; } }
async function uploadImage(event) { const input = event.target; const file = input.files[0]; if (!file) return; if (productDirty) { notify('Сначала сохраните изменения товара.'); input.value = ''; return; } if (file.size > 5 * 1024 * 1024) { notify('Размер фотографии не должен превышать 5 МБ.'); input.value = ''; return; } input.disabled = true; try { const form = new FormData(); form.append('file',file); await api(`/api/seller/products/${currentProduct.id}/images`, {method:'POST',body:form}); notify('Фотография добавлена.'); await render(); } catch(error) { notify(error.message); input.disabled = false; input.value = ''; } }
function ordersPage(data, route) { const sales = route === 'sales'; return pageHeader(sales ? 'Полученные заказы' : 'Мои покупки', sales ? 'Подтверждайте заказы и отмечайте завершенные сделки.' : 'История покупок, состав и статус каждого заказа.') + (data.items.length ? data.items.map(o => `<article class="order-card"><div class="order-row"><div><strong>Заказ ${escapeHtml(o.number.slice(0,11))}…</strong><div class="meta">${date(o.createdAtUtc)} · ${o.items.length} поз.</div></div>${badge(o.status)}</div><div class="order-items">${o.items.slice(0,3).map(i => `${escapeHtml(i.productName)} × ${i.quantity}`).join('<br>')}${o.items.length > 3 ? '<br>И другие товары' : ''}</div><div class="order-row"><span class="price">${money(o.total)}</span><button data-order="${o.id}" data-sales="${sales}">Подробнее</button></div></article>`).join('') : empty(sales ? 'Заказов пока нет' : 'Покупок пока нет',sales ? 'Здесь появятся заказы на ваши опубликованные товары.' : 'После оформления покупки здесь появятся ее состав и статус.')) + pagination(data,route); }
async function openOrder(id,sales) { const o = await api(`${sales ? '/api/seller/orders' : '/api/orders'}/${id}`); if (!profile) return; $('#dialog-title').textContent = 'Заказ ' + o.number.slice(0,11) + '…'; $('#dialog-content').innerHTML = `<p class="muted small">${escapeHtml(o.number)}<br>${date(o.createdAtUtc)}</p>${badge(o.status)}<div class="order-items">${o.items.map(i => `<div class="detail-line"><span>${escapeHtml(i.productName)}<br><small class="muted">${i.quantity} × ${money(i.unitPrice)}</small></span><strong>${money(i.lineTotal)}</strong></div>`).join('')}</div><div class="order-row"><strong>Итого</strong><strong class="price">${money(o.total)}</strong></div><div class="panel"><h3>Получатель</h3><p>${escapeHtml(o.recipientName)}<br>${escapeHtml(o.phone)}</p><p class="address">${escapeHtml(o.deliveryAddress)}</p></div><div class="form-actions">${sales && o.status === 'New' ? `<button class="primary" data-status="Confirmed" data-id="${id}">Подтвердить</button>` : ''}${sales && o.status === 'Confirmed' ? `<button class="primary" data-status="Completed" data-id="${id}">Завершить</button>` : ''}${['New','Confirmed'].includes(o.status) ? `<button class="danger" data-cancel="${id}" data-sales="${sales}">Отменить заказ</button>` : ''}</div>`; $('#detail-dialog').showModal(); }
document.addEventListener('click', async event => { const button = event.target.closest('button'); if (!button || button.disabled || !profile) return; const d = button.dataset; if (!Object.keys(d).length) return; button.disabled = true; try { if (d.action === 'reload') await render(); if (d.action === 'new-product') location.hash = '#product'; if (d.action === 'back-products') { if (!productDirty || confirm('Выйти без сохранения изменений?')) { productDirty = false; location.hash = '#products'; } } if (d.edit) location.hash = '#product?id=' + d.edit; if (d.page) location.hash = `#${d.route}?page=${d.page}`; if (d.publication) { if (location.hash.startsWith('#product?') && productDirty) throw new Error('Сначала сохраните изменения товара.'); await api(`/api/seller/products/${d.id}/${d.publication}`,{method:'POST'}); notify(d.publication === 'publish' ? 'Товар опубликован.' : 'Товар снят с публикации.'); await render(); } if (d.deleteImage) { if (productDirty) throw new Error('Сначала сохраните изменения товара.'); if (confirm('Удалить фотографию?')) { await api(`/api/seller/products/${currentProduct.id}/images/${d.deleteImage}`, {method:'DELETE'}); await render(); } } if (d.order) await openOrder(d.order,d.sales === 'true'); if (d.status || d.cancel) { const id = d.id || d.cancel; if (d.cancel && !confirm('Отменить заказ? Товары вернутся в наличие.')) return; const seller = d.status || d.sales === 'true'; await api(seller ? `/api/seller/orders/${id}/status` : `/api/orders/${id}/cancel`, {method:'POST',...(seller ? {body:JSON.stringify({status:d.status || 'Cancelled'})} : {})}); $('#detail-dialog').close(); notify('Статус заказа обновлен.'); await render(); } } catch(error) { notify(error.message); } finally { button.disabled = false; } });
document.querySelectorAll('nav a, .brand, .store-nav a, #userbar a').forEach(link => link.addEventListener('click', event => { if (productDirty && link.hash !== location.hash) { if (!confirm('Выйти без сохранения изменений?')) event.preventDefault(); else productDirty = false; } }));
window.addEventListener('hashchange', () => { $('#detail-dialog').close(); render(); });
window.addEventListener('beforeunload', event => { if (productDirty) { event.preventDefault(); event.returnValue = ''; } });
// Startup is handled after catalog.js has loaded.

