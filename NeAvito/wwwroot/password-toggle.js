'use strict';

function concealPasswords(root = document) {
  root.querySelectorAll('.password-field input').forEach(input => {
    input.type = 'password';
    const button = input.parentElement.querySelector('.password-toggle');
    button.setAttribute('aria-pressed', 'false');
    button.setAttribute('aria-label', 'Показать пароль');
    button.title = 'Показать пароль';
  });
}

function initializePasswordToggles(root = document) {
  root.querySelectorAll('input[type="password"]').forEach(input => {
    if (input.closest('.password-field')) return;
    const wrapper = document.createElement('span');
    wrapper.className = 'password-field';
    input.before(wrapper);
    wrapper.append(input);
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'password-toggle';
    button.setAttribute('aria-label', 'Показать пароль');
    button.setAttribute('aria-pressed', 'false');
    button.title = 'Показать пароль';
    button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/><path class="eye-slash" d="m3 3 18 18"/></svg>';
    button.addEventListener('click', event => {
      event.preventDefault();
      const visible = input.type === 'password';
      input.type = visible ? 'text' : 'password';
      button.setAttribute('aria-pressed', String(visible));
      button.setAttribute('aria-label', visible ? 'Скрыть пароль' : 'Показать пароль');
      button.title = visible ? 'Скрыть пароль' : 'Показать пароль';
    });
    wrapper.append(button);
  });
}

document.addEventListener('reset', event => concealPasswords(event.target));
initializePasswordToggles();
