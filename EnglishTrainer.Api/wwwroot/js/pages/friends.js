/*
 * Друзья: поиск по нику, заявки и прогресс друг друга.
 */

import { api } from '../api.js';
import { state } from '../state.js';
import { esc, icon, plural, toast, emptyState, avatar } from '../ui.js';

// Значения FriendRelation на сервере.
const RELATION = { NONE: 0, OUTGOING: 1, INCOMING: 2, FRIENDS: 3 };

let searchResults = [];
let searchQuery = '';

export async function render(container, context) {
    container.innerHTML = '<p class="hint">Загрузка…</p>';

    try {
        const [overview, leaderboard] = await Promise.all([
            api('/api/friends'),
            // Рейтинг запрашивается вместе со списком: это две части одного
            // экрана, и последовательные запросы заставили бы ждать дважды.
            api('/api/friends/leaderboard')
        ]);

        state.badges.friends = overview.incoming.length;

        container.innerHTML = `
            ${leaderboard.entries.length > 1 ? `
                <div class="section-title">
                    ${icon('chart', 'icon--sm')} Рейтинг недели
                    <span class="badge">${leaderboard.friendCount + 1}</span>
                </div>
                <div class="card">
                    <p class="hint" style="margin-bottom: 12px">
                        Последние семь дней по вашему часовому поясу. Порядок: слова за неделю,
                        затем ответы, затем серия дней.
                    </p>
                    <ol class="leaderboard">
                        ${leaderboard.entries.map(leaderboardRow).join('')}
                    </ol>
                </div>` : ''}

            <div class="card">
                <div class="section-title" style="margin: 0 0 12px">
                    ${icon('search', 'icon--sm')} Найти человека
                </div>
                <form class="search-box" id="search-form">
                    <input id="search-input" placeholder="Ник, например johannesmetz98"
                           autocomplete="off" value="${esc(searchQuery)}">
                    <button type="submit" class="btn btn--primary">${icon('search', 'icon--sm')} Найти</button>
                </form>
                <div id="search-results" style="margin-top: 14px"></div>
            </div>

            ${overview.incoming.length ? `
                <div class="section-title">
                    ${icon('users', 'icon--sm')} Входящие заявки
                    <span class="badge badge--brand">${overview.incoming.length}</span>
                </div>
                <div class="word-list">
                    ${overview.incoming.map(requestRow).join('')}
                </div>` : ''}

            ${overview.friends.length ? `
                <div class="section-title">${icon('users', 'icon--sm')} Друзья
                    <span class="badge">${overview.friends.length}</span>
                </div>
                <div class="word-list">
                    ${overview.friends.map(friendRow).join('')}
                </div>`
                : emptyState({
                    iconName: 'users',
                    title: 'Друзей пока нет',
                    text: 'Найдите человека по нику и отправьте заявку. В списке будет видно, '
                        + 'сколько слов он выучил и какая у него серия дней — это хорошо мотивирует.',
                    action: ''
                })}

            ${overview.outgoing.length ? `
                <div class="section-title">Исходящие заявки</div>
                <div class="word-list">
                    ${overview.outgoing.map(outgoingRow).join('')}
                </div>` : ''}`;

        container.querySelector('#search-form').addEventListener('submit', async (event) => {
            event.preventDefault();
            searchQuery = container.querySelector('#search-input').value.trim();
            await runSearch(container, context);
        });

        if (searchQuery) {
            await runSearch(container, context);
        }

        bindActions(container, context);
    } catch (error) {
        container.innerHTML = emptyState({
            iconName: 'bulb',
            title: 'Не удалось загрузить друзей',
            text: error.message,
            action: '<button type="button" class="btn btn--primary" id="retry">Повторить</button>'
        });

        container.querySelector('#retry').addEventListener('click', () => render(container, context));
    }
}

/**
 * Строка рейтинга.
 *
 * Своя строка подсвечивается: искать себя глазами в списке из десяти человек —
 * лишняя работа, а сравнение с соседями и есть смысл экрана. Серия показывается
 * отдельным столбцом, потому что она не зависит от того, сколько слов человек
 * взял на этой неделе, и по ней видно, кто занимается ровно, а кто рывками.
 */
function leaderboardRow(entry) {
    return `
        <li class="leaderboard__row ${entry.isMe ? 'is-me' : ''}">
            <span class="leaderboard__rank">${entry.rank}</span>
            ${avatar(entry, 'avatar--sm')}
            <span class="leaderboard__name">
                ${personLink(entry, `<b>${esc(entry.displayName)}${entry.isMe ? ' <em class="muted">— вы</em>' : ''}</b>
                    <span class="hint">@${esc(entry.username)}</span>`)}
            </span>
            <span class="leaderboard__score">
                <b>${entry.weeklyWords}</b>
                <span class="hint">${plural(entry.weeklyWords, ['слово', 'слова', 'слов'])} за неделю</span>
            </span>
            <span class="leaderboard__streak" title="Серия дней">
                ${entry.currentStreak > 0 ? `🔥 ${entry.currentStreak}` : '—'}
            </span>
        </li>`;
}

function requestRow(request) {
    return `
        <div class="friend-row">
            ${avatar(request)}
            <div class="friend-row__name">
                <b>${esc(request.displayName)}</b>
                <span>@${esc(request.username)}</span>
            </div>
            <div class="row">
                <button type="button" class="btn btn--ok btn--sm" data-accept="${esc(request.username)}">
                    ${icon('check', 'icon--sm')} Принять
                </button>
                <button type="button" class="btn btn--ghost btn--sm" data-decline="${esc(request.username)}">
                    Отклонить
                </button>
            </div>
        </div>`;
}

function friendRow(friend) {
    return `
        <div class="friend-row">
            ${avatar(friend)}
            <div class="friend-row__name">
                ${personLink(friend, `<b>${esc(friend.displayName)}</b>
                    <span>@${esc(friend.username)} · друзья с ${new Date(friend.friendsSince).toLocaleDateString('ru-RU')}</span>`)}
            </div>
            <div class="friend-row__stats">
                <div><b>${friend.learnedCards}</b><span>выучено</span></div>
                <div><b>🔥 ${friend.currentStreak}</b><span>серия</span></div>
                <div><b>${friend.reviewedToday}</b><span>сегодня</span></div>
            </div>
            <button type="button" class="btn btn--ghost btn--sm" data-remove="${esc(friend.username)}" title="Убрать из друзей">
                ${icon('close', 'icon--sm')}
            </button>
        </div>`;
}

/**
 * Имя человека — ссылка на его профиль.
 *
 * Профиль открывается и из списка друзей, и из рейтинга: иначе посмотреть
 * результаты другого игрока было бы неоткуда, а «выучено» и «серия» в строке —
 * это уже его итоги, а не подпись к кнопке.
 */
function personLink(person, content) {
    return `<a class="person" href="/user/${encodeURIComponent(person.username)}">${content}</a>`;
}

function outgoingRow(request) {
    return `
        <div class="friend-row">
            ${avatar(request)}
            <div class="friend-row__name">
                <b>${esc(request.displayName)}</b>
                <span>@${esc(request.username)} · заявка отправлена</span>
            </div>
            <button type="button" class="btn btn--ghost btn--sm" data-remove="${esc(request.username)}">
                Отменить
            </button>
        </div>`;
}

function searchResultRow(result) {
    return `
        <div class="friend-row" style="box-shadow: none; background: var(--surface-2)">
            ${avatar(result)}
            <div class="friend-row__name">
                <b>${esc(result.displayName)}</b>
                <span>@${esc(result.username)}</span>
            </div>
            ${relationAction(result)}
        </div>`;
}

function relationAction(result) {
    switch (result.relation) {
        case RELATION.FRIENDS:
            return '<span class="badge badge--learned">уже друзья</span>';

        case RELATION.OUTGOING:
            return '<span class="badge">заявка отправлена</span>';

        case RELATION.INCOMING:
            return `<button type="button" class="btn btn--ok btn--sm" data-accept="${esc(result.username)}">
                        ${icon('check', 'icon--sm')} Принять заявку
                    </button>`;

        default:
            return `<button type="button" class="btn btn--primary btn--sm" data-add="${esc(result.username)}">
                        ${icon('plus', 'icon--sm')} В друзья
                    </button>`;
    }
}

async function runSearch(container, context) {
    const slot = container.querySelector('#search-results');

    if (searchQuery.length < 2) {
        slot.innerHTML = '<p class="hint">Введите хотя бы два символа ника.</p>';
        return;
    }

    slot.innerHTML = '<p class="hint">Ищем…</p>';

    try {
        searchResults = await api(`/api/users/search?query=${encodeURIComponent(searchQuery)}`);

        slot.innerHTML = searchResults.length
            ? `<div class="word-list">${searchResults.map(searchResultRow).join('')}</div>`
            : '<p class="hint">Никого не нашли. Ник чувствителен к написанию, но не к регистру.</p>';

        bindActions(container, context);
    } catch (error) {
        slot.innerHTML = `<p class="hint">${esc(error.message)}</p>`;
    }
}

function bindActions(container, context) {
    container.querySelectorAll('[data-add]').forEach((button) => {
        button.addEventListener('click', () => act(async () => {
            await api('/api/friends', { method: 'POST', body: { username: button.dataset.add } });
            toast(`Заявка отправлена: @${button.dataset.add}`, 'ok');
        }, container, context));
    });

    container.querySelectorAll('[data-accept]').forEach((button) => {
        button.addEventListener('click', () => act(async () => {
            await api(`/api/friends/${encodeURIComponent(button.dataset.accept)}/accept`, { method: 'POST' });
            toast('Заявка принята', 'ok');
        }, container, context));
    });

    container.querySelectorAll('[data-decline]').forEach((button) => {
        button.addEventListener('click', () => act(async () => {
            await api(`/api/friends/${encodeURIComponent(button.dataset.decline)}/decline`, { method: 'POST' });
            toast('Заявка отклонена');
        }, container, context));
    });

    container.querySelectorAll('[data-remove]').forEach((button) => {
        button.addEventListener('click', () => {
            const username = button.dataset.remove;

            if (!window.confirm(`Убрать @${username} из друзей?`)) {
                return;
            }

            act(async () => {
                await api(`/api/friends/${encodeURIComponent(username)}`, { method: 'DELETE' });
                toast('Готово');
            }, container, context);
        });
    });
}

/** Выполняет действие и перерисовывает страницу: список мог заметно измениться. */
async function act(action, container, context) {
    try {
        await action();
        await render(container, context);
    } catch (error) {
        toast(error.message, 'error');
    }
}
