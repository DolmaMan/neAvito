'use strict';

// P3 API is not available yet. These previews never send moderation requests.
function renderModerationPreview() {
  routeCounter++;
  $('#storefront').hidden = true;
  $('#auth-screen').hidden = true;
  $('#cabinet').hidden = false;
  updateStoreHeader();
  document.querySelectorAll('.cabinet nav a').forEach(link => link.classList.toggle('active', link.hash === '#moderation'));
  document.title = 'Модерация — NeAvito';
  $('#content').innerHTML = pageHeader('Модерация', 'Жалобы и решения по объявлениям.') +
    '<div class="preview-notice"><strong>Раздел в разработке</strong><p>Это предварительный вид кабинета. Очередь жалоб и действия модератора пока недоступны.</p></div>' +
    '<section class="panel"><h2>Очередь жалоб</h2><p class="muted">После запуска здесь появятся обращения пользователей.</p>' +
    '<div class="form-actions"><button disabled>Все обращения</button><button disabled>Ожидают решения</button><button disabled>Рассмотренные</button></div></section>' +
    '<section class="panel"><h2>Решение модератора</h2><fieldset disabled><label>Причина решения<textarea placeholder="Выберите обращение из очереди"></textarea></label>' +
    '<div class="form-actions"><button>Отклонить жалобу</button><button>Скрыть объявление</button><button>Заблокировать автора</button></div></fieldset>' +
    '<p class="muted small">Решения и история действий станут доступны после запуска модерации.</p></section>';
}

document.addEventListener('click', event => {
  if (event.target.closest('.skip-link')) {
    event.preventDefault();
    const main = [$('#storefront'), $('#content'), $('#auth-screen')].find(element => !element.hidden && element.getClientRects().length);
    main?.setAttribute('tabindex', '-1');
    main?.focus();
  }
  if (!event.target.closest('[data-report]')) return;
  $('#dialog-title').textContent = 'Пожаловаться на объявление';
  $('#dialog-content').innerHTML = '<div class="preview-notice">Приём жалоб скоро появится. Сейчас отправить обращение нельзя.</div>' +
    '<label>Причина<select><option>Выберите причину</option><option>Недостоверное описание</option><option>Запрещённый товар</option><option>Подозрение на мошенничество</option><option>Другое</option></select></label>' +
    '<button class="primary wide" disabled>Отправка пока недоступна</button>';
  $('#detail-dialog').showModal();
});
