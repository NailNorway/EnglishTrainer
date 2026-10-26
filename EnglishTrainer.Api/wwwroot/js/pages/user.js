/*
 * Профиль другого игрока.
 *
 * Открывается из списка друзей и из рейтинга недели. Своих данных здесь нет:
 * настройки, почта и достижения остаются в личном профиле, а тут — то, ради чего
 * чужой профиль и открывают: сколько человек выучил, какая у него серия и как
 * он занимался последнюю неделю.
 */

import { api } from '../api.js';
import { esc, icon, plural, toast, avatar, formatDate, emptyState } from '../ui.js';

export async function render(container, context) {
    const username = readUsername();

    if (!username) {
        showFailure(container, context, 'Не указан ник игрока.');
        return;
    }

    container.innerHTML = '<p class="hint">Загрузка…</p>';

    try {
        const profile = await api(`/api/users/${encodeURIComponent(username)}/profile`);
        // Признак «это вы» приходит с сервера: в своём профиле даты дружбы нет.
        // Так экран не зависит от того, совпал ли ник в адресе с ником из сеанса.
        const isMe = profile.friendsSince === null;

        container.innerHTML = `
            <div class="card avatar-editor">
                ${avatar(profile, 'avatar--lg')}

                <div class="avatar-editor__actions">
                    <div>
                        <h2>${esc(profile.displayName)}</h2>
                        <p class="hint" style="margin-top: 4px">
                            @${esc(profile.username)}${isMe ? ' · это вы' : ''}
                        </p>
                    </div>

                    <div class="row">
                        <button type="button" class="btn btn--ghost btn--sm" id="to-friends">
                            ${icon('users', 'icon--sm')} К друзьям
                        </button>
                        ${isMe ? '' : `<button type="button" class="btn btn--ghost btn--sm" id="remove-friend">
                            ${icon('close', 'icon--sm')} Убрать из друзей
                        </button>`}
                    </div>
                </div>

                <div style="margin-left: auto; text-align: right">
                    <div class="badge badge--flame">🔥 серия ${plural(profile.currentStreak, ['день', 'дня', 'дней'])}</div>
                </div>
            </div>

            <div class="section-title">${icon('chart', 'icon--sm')} Прогресс</div>

            <div class="stat-grid">
                ${tile(profile.learnedCards, 'выучено', 'accent')}
                ${tile(profile.cardsInProgress, 'в работе')}
                ${tile(profile.reviewedTotal, 'ответов всего')}
                ${tile(profile.longestStreak, 'лучшая серия')}
                ${tile(profile.friendCount, 'друзей')}
                ${tile(profile.reviewedToday, 'ответов сегодня')}
            </div>

            <div class="section-title">${icon('chart', 'icon--sm')} Последняя неделя</div>

            <div class="card">
                <p class="hint" style="margin-bottom: 12px">
                    Семь дней по часовому поясу @${esc(profile.username)} — так же,
                    как в рейтинге среди друзей.
                </p>

                <div class="stat-grid">
                    ${tile(profile.weeklyWords, 'новых слов за неделю')}
                    ${tile(profile.weeklyReviews, 'ответов за неделю')}
                </div>
            </div>

            <div class="card">
                <dl class="kv">
                    <div class="kv__row"><dt>В сервисе с</dt><dd>${esc(formatDate(profile.memberSince))}</dd></div>
                    <div class="kv__row"><dt>Друзья</dt><dd>${
                        profile.friendsSince
                            ? `с ${esc(formatDate(profile.friendsSince))}`
                            : 'это вы'
                    }</dd></div>
                </dl>
            </div>`;

        container.querySelector('#to-friends').addEventListener('click', () => context.navigate('friends'));

        container.querySelector('#remove-friend')?.addEventListener('click', async () => {
            if (!window.confirm(`Убрать @${profile.username} из друзей?`)) {
                return;
            }

            try {
                await api(`/api/friends/${encodeURIComponent(profile.username)}`, { method: 'DELETE' });
                toast('Готово');
                context.navigate('friends');
            } catch (error) {
                toast(error.message, 'error');
            }
        });
    } catch (error) {
        // Сервер отвечает 404 и на «нет такого человека», и на «это не ваш друг»:
        // различать эти случаи значило бы подтверждать существование ников.
        showFailure(container, context, error.message);
    }
}

/** Ник из адреса вида /user/ivan. */
function readUsername() {
    const match = /^\/user\/([^/]+)\/?$/.exec(location.pathname);

    if (!match) {
        return '';
    }

    try {
        return decodeURIComponent(match[1]).trim();
    } catch {
        return match[1].trim();
    }
}

function showFailure(container, context, message) {
    container.innerHTML = emptyState({
        iconName: 'users',
        title: 'Профиль недоступен',
        text: message,
        action: '<button type="button" class="btn btn--primary" id="back">К друзьям</button>'
    });

    container.querySelector('#back').addEventListener('click', () => context.navigate('friends'));
}

/** Плитка статистики: то же оформление, что и в личном профиле. */
function tile(value, label, kind = '') {
    return `
        <div class="stat ${kind ? `stat--${kind}` : ''}">
            <b>${esc(value)}</b>
            <span>${esc(label)}</span>
        </div>`;
}
