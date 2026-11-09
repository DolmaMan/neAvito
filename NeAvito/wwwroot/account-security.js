'use strict';

function accountSecurityPage() {
  return `<section class="panel"><h2>Безопасность аккаунта</h2>
    <form id="change-password-form">
      <label>Текущий пароль<input name="currentPassword" type="password" autocomplete="current-password" maxlength="128" required></label>
      <label>Новый пароль<input name="newPassword" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></label>
      <label>Повторите новый пароль<input name="confirmPassword" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></label>
      <p class="muted small">Не менее 8 символов: строчная и прописная латинские буквы, цифра и специальный символ. После смены пароля потребуется войти заново на всех устройствах.</p>
      <p id="security-error" class="error" role="alert"></p>
      <button class="primary" type="submit">Сменить пароль</button>
    </form>
    <h3>Сессии</h3><p class="muted">Если вы забыли выйти на другом устройстве, завершите все сеансы, включая текущий. Пароль останется прежним.</p>
    <button id="logout-all" class="quiet" type="button">Выйти на всех устройствах</button>
  </section>`;
}

async function finishAccountSession(message) {
  profile = null;
  loginReturnHash = null;
  updateStoreHeader();
  history.replaceState(null, '', '#login');
  await render();
  notify(message);
}

async function changeAccountPassword(event) {
  event.preventDefault();
  const form = event.currentTarget;
  const errorBox = $('#security-error');
  const data = Object.fromEntries(new FormData(form));
  errorBox.textContent = '';
  if (data.newPassword !== data.confirmPassword) {
    errorBox.textContent = 'Новые пароли не совпадают.';
    form.elements.confirmPassword.focus();
    return;
  }
  const buttons = [form.querySelector('button[type="submit"]'), $('#logout-all')];
  if (buttons.some(button => button.disabled)) return;
  buttons.forEach(button => { button.disabled = true; });
  try {
    await api('/api/auth/change-password', { method:'POST', body:JSON.stringify({
      currentPassword:data.currentPassword, newPassword:data.newPassword
    }) });
    form.reset();
    await finishAccountSession('Пароль изменён. Все прежние сеансы завершены. Войдите с новым паролем.');
  } catch (error) {
    if (errorBox.isConnected) errorBox.textContent = error.message;
  } finally {
    buttons.forEach(button => { button.disabled = false; });
  }
}

async function logoutEverywhere(event) {
  const button = event.currentTarget;
  if (button.disabled || !window.confirm('Завершить все сеансы, включая текущий?')) return;
  const submit = $('#change-password-form button[type="submit"]');
  const errorBox = $('#security-error');
  errorBox.textContent = '';
  button.disabled = submit.disabled = true;
  try {
    await api('/api/auth/logout-all', { method:'POST' });
    await finishAccountSession('Все сеансы завершены. Для продолжения войдите снова.');
  } catch (error) {
    if (errorBox.isConnected) errorBox.textContent = error.message;
  } finally {
    button.disabled = submit.disabled = false;
  }
}
