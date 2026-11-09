'use strict';
const $ = (selector, root = document) => root.querySelector(selector);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const money = value => new Intl.NumberFormat('ru-RU', { style:'currency', currency:'RUB', maximumFractionDigits:2 }).format(value);
const date = value => new Intl.DateTimeFormat('ru-RU', { dateStyle:'medium' }).format(new Date(value));
const labels = { Draft:'Черновик', Published:'Опубликован', Unpublished:'Снят с публикации', New:'Новый', Confirmed:'Подтвержден', Completed:'Завершен', Cancelled:'Отменен' };
const badge = status => `<span class="badge ${escapeHtml(status)}">${escapeHtml(labels[status] || status)}</span>`;
let profile = null, routeCounter = 0, noticeTimer, productDirty = false;
let activeAuthMode = 'login', accountLink = null;
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
  if (activeRoute === 'products' || activeRoute === 'product') { location.hash = location.hash.replace(/^#products/, '#listings').replace(/^#product/, '#listing'); return; }
  if (!activeRoute || activeRoute === 'catalog' || activeRoute === 'item') return renderStorefront();
  if (!profile) { if (activeRoute !== 'login') loginReturnHash = location.hash; authMode('login'); showAuth(); return; }
  if (activeRoute === 'login') { location.hash = '#profile'; return; }
  if (activeRoute === 'listings' || activeRoute === 'listing') return renderListings();
  $('#storefront').hidden = true; $('#auth-screen').hidden = true; $('#cabinet').hidden = false; updateStoreHeader();
  const token = ++routeCounter;
  const [route = 'profile', query = ''] = location.hash.slice(1).split('?');
  const params = new URLSearchParams(query); const page = Math.min(1000000,Math.max(1,parseInt(params.get('page'),10)||1));
  const section = ['profile','purchases','sales'].includes(route) ? route : 'profile';
  document.querySelectorAll('nav a').forEach(a => a.classList.toggle('active', a.hash === '#' + section));
  $('#content').innerHTML = '<p class="loading">Загружаем данные…</p>';
  try {
    let html;
    if (section === 'profile') { const fresh = await api('/api/profile'); if (token !== routeCounter) return; profile = fresh; html = profilePage() + accountSecurityPage(); }
    if (section === 'purchases' || section === 'sales') { const data = await api(`${section === 'sales' ? '/api/seller/orders' : '/api/orders'}?page=${page}&pageSize=10`); html = ordersPage(data,section); }
    if (token !== routeCounter) return;
    $('#content').innerHTML = html; document.title = ({profile:'Мой профиль',products:'Мои товары',purchases:'Мои покупки',sales:'Полученные заказы',product:'Редактирование товара'}[section]) + ' — NeAvito';
    if (section === 'profile') {
      initializePasswordToggles($('#content'));
      $('#profile-form').addEventListener('submit', saveProfile);
      $('#change-password-form').addEventListener('submit', changeAccountPassword);
      $('#logout-all').addEventListener('click', logoutEverywhere);
    }
  } catch(error) { if (token === routeCounter && profile) { $('#content').innerHTML = '<div class="panel"><h2>Не удалось загрузить раздел</h2><p id="load-error" class="error"></p><button data-action="reload">Повторить</button></div>'; $('#load-error').textContent = error.message; } }
}
function profilePage() { return pageHeader('Мой профиль','Ваши приватные данные. Телефон объявления задаётся отдельно.') + `<div class="profile-layout"><section class="panel"><h2>Личные данные</h2><form id="profile-form"><label>Имя<input name="displayName" value="${escapeHtml(profile.displayName)}" maxlength="100" required autocomplete="name"></label><label>Электронная почта<input value="${escapeHtml(profile.email)}" readonly type="email"></label><label>Телефон<input name="phone" value="${escapeHtml(profile.phone)}" maxlength="30" type="tel" autocomplete="tel" placeholder="+7 999 123-45-67"></label><label>Адрес или место получения<textarea name="deliveryAddress" maxlength="500" autocomplete="street-address" placeholder="Укажите удобное место получения">${escapeHtml(profile.deliveryAddress)}</textarea></label><p class="error" id="profile-error" role="alert"></p><button class="primary">Сохранить изменения</button></form></section><aside class="note-panel"><div class="avatar">${escapeHtml(profile.displayName.slice(0,1).toUpperCase())}</div><h3>Покупатель и продавец</h3><p>Размещайте объявления и связывайтесь с авторами из одного аккаунта.</p><p class="muted">Изменения профиля не меняют контакты объявлений и историю заказов.</p></aside></div>`; }
async function saveProfile(event) { event.preventDefault(); const form = event.target; const button = $('button',form); button.disabled = true; $('#profile-error').textContent = ''; try { const data = Object.fromEntries(new FormData(form)); data.phone = data.phone.trim() || null; data.version = profile.version; profile = await api('/api/profile',{method:'PUT',body:JSON.stringify(data)}); $('#user-name').textContent = profile.displayName; notify('Профиль сохранен.'); } catch(error) { const errorBox = $('#profile-error'); if (errorBox) errorBox.textContent = error.message; else notify(error.message); } finally { button.disabled = false; } }
function ordersPage(data, route) { const sales = route === 'sales'; return pageHeader(sales ? 'Полученные заказы' : 'Мои покупки', 'Архив прежних заказов. Доступен только просмотр.') + (data.items.length ? data.items.map(o => `<article class="order-card"><div class="order-row"><div><strong>Заказ ${escapeHtml(o.number.slice(0,11))}…</strong><div class="meta">${date(o.createdAtUtc)} · ${o.items.length} поз.</div></div>${badge(o.status)}</div><div class="order-items">${o.items.slice(0,3).map(i => `${escapeHtml(i.productName)} × ${i.quantity}`).join('<br>')}${o.items.length > 3 ? '<br>И другие товары' : ''}</div><div class="order-row"><span class="price">${money(o.total)}</span><button data-order="${o.id}" data-sales="${sales}">Подробнее</button></div></article>`).join('') : empty(sales ? 'Заказов пока нет' : 'Покупок пока нет','Здесь сохраняется история прежних заказов. Новые заказы на сайте не оформляются.')) + pagination(data,route); }
async function openOrder(id,sales) { const o = await api(`${sales ? '/api/seller/orders' : '/api/orders'}/${id}`); if (!profile) return; $('#dialog-title').textContent = 'Заказ ' + o.number.slice(0,11) + '…'; $('#dialog-content').innerHTML = `<p class="muted small">${escapeHtml(o.number)}<br>${date(o.createdAtUtc)}</p>${badge(o.status)}<div class="order-items">${o.items.map(i => `<div class="detail-line"><span>${escapeHtml(i.productName)}<br><small class="muted">${i.quantity} × ${money(i.unitPrice)}</small></span><strong>${money(i.lineTotal)}</strong></div>`).join('')}</div><div class="order-row"><strong>Итого</strong><strong class="price">${money(o.total)}</strong></div><div class="panel"><h3>Получатель</h3><p>${escapeHtml(o.recipientName)}<br>${escapeHtml(o.phone)}</p><p class="address">${escapeHtml(o.deliveryAddress)}</p></div><p class="muted small">История доступна только для чтения.</p>`; $('#detail-dialog').showModal(); }
document.addEventListener('click', async event => {
  const button=event.target.closest('button');if(!button || button.disabled || !profile)return;
  const d=button.dataset;if(d.action!=='reload' && !d.page && !d.order)return;
  button.disabled=true;
  try {if(d.action==='reload')await render();if(d.page)location.hash=`#${d.route}?page=${d.page}`;if(d.order)await openOrder(d.order,d.sales==='true');}
  catch(error){notify(error.message);}finally{button.disabled=false;}
});
document.querySelectorAll('nav a, .brand, .store-nav a, #userbar a').forEach(link => link.addEventListener('click', event => { if (productDirty && link.hash !== location.hash) { if (!confirm('Выйти без сохранения изменений?')) event.preventDefault(); else productDirty = false; } }));
window.addEventListener('hashchange', () => { $('#detail-dialog').close(); render(); });
window.addEventListener('beforeunload', event => { if (productDirty) { event.preventDefault(); event.returnValue = ''; } });
// Startup is handled after catalog.js has loaded.

