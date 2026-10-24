/*
 * Страница тренировки: колоды, словарь колоды и прохождение сессии.
 */

import { api } from '../api.js';
import { isFavorite, toggleFavorite, refreshFavorites } from '../state.js';
import {
    esc, icon, plural, toast, emptyState, starButton, achievementGoal
} from '../ui.js';
import { canSpeak, speak } from '../speech.js';
import { runSession, stopSession } from '../session.js';
import { confirmAction } from '../telegram.js';

export async function render(container, context) {
    await showDecks(container, context);
}

/* ── Колоды ─────────────────────────────────────────────────────────────── */

async function showDecks(container, context) {
    stopSession();

    container.innerHTML = '<p class="hint">Загрузка…</p>';

    try {
        const [decks, summary, active, achievements] = await Promise.all([
            api('/api/decks'),
            api('/api/stats/summary'),
            // Активная сессия запрашивается отдельным эндпоинтом: обычный
            // GET /api/study/session создал бы новую и израсходовал дневной лимит
            // новых слов, даже если пользователь просто открыл страницу.
            api('/api/study/session/active'),
            // Достижения читаются здесь же: чтение проверяет пороги и выдаёт
            // заработанное, поэтому награда за законченную сессию появляется
            // сразу после возврата на эту страницу.
            api('/api/achievements')
        ]);

        container.innerHTML = `
            ${active && active.currentCard ? `
                <div class="card" style="margin-bottom: 20px; display: flex; align-items: center; gap: 16px; flex-wrap: wrap">
                    <div style="flex: 1; min-width: 200px">
                        <b>Незавершённая сессия</b>
                        <p class="hint" style="margin-top: 4px">
                            ${plural(active.totalItems, ['карточка', 'карточки', 'карточек'])}
                            в очереди · начата ${esc(active.localDate)}
                        </p>
                    </div>
                    <button type="button" class="btn btn--primary" id="resume">
                        ${icon('play', 'icon--sm')} Продолжить
                    </button>
                </div>` : ''}

            <div class="stat-grid">
                ${statTile(summary.learnedCards, 'выучено', 'accent')}
                ${statTile(summary.cardsInProgress, 'в работе')}
                ${statTile(summary.dueNow, 'пора повторить')}
                ${statTile(`${summary.reviewedToday} из ${summary.dailyGoal}`, 'ответов сегодня')}
                ${statTile(`${summary.accuracyLast30Days}%`, 'удержание')}
                ${statTile(`🔥 ${summary.currentStreak}`, 'серия дней', 'flame')}
            </div>

            ${achievementGoal(achievements)}

            <div class="section-title">
                ${icon('book', 'icon--sm')} Колоды
                <span class="badge">${decks.length}</span>
            </div>

            <div class="deck-grid">
                ${decks.map(deckCard).join('')}
            </div>

            ${newDeckForm()}`;

        if (active && active.currentCard) {
            container.querySelector('#resume').addEventListener('click', () => {
                runSession(container, active, (reason) => afterSession(container, context, reason));
            });
        }

        container.querySelectorAll('[data-start]').forEach((button) => {
            button.addEventListener('click', () => startSession(container, context, Number(button.dataset.start)));
        });

        container.querySelectorAll('[data-words]').forEach((button) => {
            button.addEventListener('click', () => showDeckWords(container, context, button.dataset.words));
        });

        container.querySelectorAll('[data-delete-deck]').forEach((button) => {
            button.addEventListener('click', () => deleteDeck(container, context, button.dataset.deleteDeck));
        });

        container.querySelector('#new-deck-form').addEventListener('submit', (event) => createDeck(event, container, context));
    } catch (error) {
        container.innerHTML = emptyState({
            iconName: 'bulb',
            title: 'Не удалось загрузить колоды',
            text: error.message,
            action: '<button type="button" class="btn btn--primary" id="retry">Повторить</button>'
        });

        container.querySelector('#retry').addEventListener('click', () => showDecks(container, context));
    }
}

/* ── Свои колоды ────────────────────────────────────────────────────────── */

async function createDeck(event, container, context) {
    event.preventDefault();

    const submit = container.querySelector('#deck-submit');

    if (submit.disabled) {
        return;
    }

    const title = container.querySelector('#deck-title').value.trim();
    const description = container.querySelector('#deck-description').value.trim();

    setNotice(container, 'new-deck-alert', '');

    if (!title) {
        setNotice(container, 'new-deck-alert', 'У колоды должно быть название.');
        return;
    }

    setBusy(submit, true, 'Создаём…');

    try {
        await api('/api/decks', { method: 'POST', body: { title, description } });
        toast('Колода создана — добавьте в неё слова', 'ok');

        // Страница перерисовывается целиком: новая колода появляется в списке,
        // и её же нужно открыть, чтобы наполнить словами. Отдельного перехода
        // нет намеренно — пустая колода в списке сама подсказывает следующий шаг.
        await showDecks(container, context);
    } catch (error) {
        setNotice(container, 'new-deck-alert', error.message);
        setBusy(submit, false, 'Создать');
    }
}

async function deleteDeck(container, context, slug) {
    const deck = (await safeDecks(container, context)).find((item) => item.slug === slug);

    const confirmed = await confirmAction(
        `Удалить колоду «${deck?.title ?? slug}»? Слова останутся в общем словаре, `
        + 'а прогресс по ним сохранится.');

    if (!confirmed) {
        return;
    }

    try {
        await api(`/api/decks/${encodeURIComponent(slug)}`, { method: 'DELETE' });
        toast('Колода удалена', 'ok');

        await showDecks(container, context);
    } catch (error) {
        toast(error.message, 'error');
    }
}

/** Список колод только ради названия в вопросе об удалении. */
async function safeDecks(container, context) {
    try {
        return await api('/api/decks');
    } catch {
        return [];
    }
}

function statTile(value, label, modifier = '') {
    return `<div class="stat ${modifier ? `stat--${modifier}` : ''}"><b>${esc(value)}</b><span>${esc(label)}</span></div>`;
}

function deckCard(deck) {
    const percent = deck.totalWords ? Math.round(deck.startedWords * 100 / deck.totalWords) : 0;

    return `
        <article class="deck">
            <div class="deck__head">
                <div class="deck__title">${esc(deck.title)}</div>
                ${deck.isOwned ? '<span class="badge badge--brand">своя</span>' : ''}
            </div>
            <p class="deck__desc">${esc(deck.description ?? '')}</p>
            <div class="progress ${percent === 100 ? 'progress--ok' : ''}"><span style="width:${percent}%"></span></div>
            <div class="deck__meta">
                <span>начато ${deck.startedWords} из ${deck.totalWords}</span>
                <span>выучено ${deck.learnedWords}</span>
            </div>
            <div class="deck__foot">
                <button type="button" class="btn btn--primary btn--sm" data-start="${deck.id}">
                    ${icon('play', 'icon--sm')} Тренироваться
                </button>
                <button type="button" class="btn btn--ghost btn--sm" data-words="${esc(deck.slug)}">
                    ${icon('book', 'icon--sm')} Слова
                </button>
                ${deck.isOwned ? `
                    <button type="button" class="btn btn--ghost btn--sm" data-delete-deck="${esc(deck.slug)}"
                            title="Удалить колоду">
                        ${icon('close', 'icon--sm')}
                    </button>` : ''}
            </div>
        </article>`;
}

/**
 * Форма создания своей колоды.
 *
 * Свёрнута в <details>: на странице тренировки главное — начать заниматься,
 * а форма, развёрнутая по умолчанию, отодвигала бы колоды вниз ради действия,
 * которое нужно один раз.
 */
function newDeckForm() {
    return `
        <details class="card new-deck">
            <summary>${icon('plus', 'icon--sm')} Создать свою колоду</summary>

            <form class="stack" id="new-deck-form" novalidate style="margin-top: 14px">
                <div class="form-alert" id="new-deck-alert" role="alert" hidden></div>

                <div class="form-grid">
                    <label class="field">
                        <span>Название</span>
                        <input type="text" id="deck-title" maxlength="128" placeholder="например, слова с работы">
                    </label>

                    <label class="field">
                        <span>Описание <em class="muted">— необязательно</em></span>
                        <input type="text" id="deck-description" maxlength="512">
                    </label>
                </div>

                <p class="hint">
                    Своя колода тренируется так же, как системная: по ней собирается сессия,
                    ведётся расписание и считается статистика. Слова в неё добавляются
                    со страницы «Слова».
                </p>

                <div class="row">
                    <button type="submit" class="btn btn--primary" id="deck-submit">Создать</button>
                </div>
            </form>
        </details>`;
}

/* ── Слова колоды ───────────────────────────────────────────────────────── */

/** Части речи для формы нового слова. Значения совпадают с серверными. */
const PARTS_OF_SPEECH = ['noun', 'verb', 'adjective', 'adverb', 'phrase'];

const PART_NAMES = {
    noun: 'существительное',
    verb: 'глагол',
    adjective: 'прилагательное',
    adverb: 'наречие',
    phrase: 'фраза'
};

async function showDeckWords(container, context, slug) {
    stopSession();

    container.innerHTML = '<p class="hint">Загрузка…</p>';

    try {
        const [deck, words] = await Promise.all([
            api(`/api/decks/${encodeURIComponent(slug)}`),
            api(`/api/decks/${encodeURIComponent(slug)}/words`)
        ]);

        container.innerHTML = `
            <div class="row" style="margin-bottom: 18px">
                <button type="button" class="btn btn--ghost btn--sm" id="back">
                    ${icon('close', 'icon--sm')} К колодам
                </button>
            </div>

            <div class="section-title">
                ${icon('book', 'icon--sm')} ${esc(deck.title)}
                <span class="badge">${words.length}</span>
                ${deck.isOwned ? '<span class="badge badge--brand">своя</span>' : ''}
            </div>

            <p class="hint" style="margin-bottom: 16px">
                ${deck.isOwned
                    ? 'Слова в свою колоду добавляются здесь же. Звёздочка отправляет слово в избранное, '
                      + 'а кнопка с динамиком произносит его вслух.'
                    : 'Звёздочка добавляет слово в избранное: такие слова идут первыми в очереди '
                      + 'повторений и доступны отдельной тренировкой. Кнопка с динамиком произносит слово.'}
            </p>

            ${deck.isOwned ? addWordBlock() : ''}

            ${words.length
                ? `<div class="word-list">${words.map((word) => wordRow(word, deck.isOwned)).join('')}</div>`
                : `<div class="card"><p class="hint" style="margin: 0">
                       В колоде пока нет слов — добавьте первое выше.
                   </p></div>`}`;

        container.querySelector('#back').addEventListener('click', () => showDecks(container, context));

        bindSpeakButtons(container);

        container.querySelectorAll('[data-favorite]').forEach((button) => {
            button.addEventListener('click', () => toggleStar(button, button.dataset.favorite));
        });

        if (deck.isOwned) {
            bindWordManagement(container, context, slug);
        }
    } catch (error) {
        container.innerHTML = emptyState({
            iconName: 'bulb',
            title: 'Не удалось загрузить слова',
            text: error.message,
            action: '<button type="button" class="btn btn--primary" id="back">К колодам</button>'
        });

        container.querySelector('#back').addEventListener('click', () => showDecks(container, context));
    }
}

/**
 * Форма добавления слова в свою колоду.
 *
 * Два способа в одном блоке: завести новое слово и найти уже существующее.
 * Разделять их по разным экранам не стоит — человек не знает, есть ли это
 * слово в словаре, и выяснять это переходом туда-обратно неудобно.
 */
function addWordBlock() {
    return `
        <details class="card add-word">
            <summary>${icon('plus', 'icon--sm')} Добавить слово</summary>

            <form class="stack" id="add-word-form" novalidate style="margin-top: 14px">
                <div class="form-alert" id="add-word-alert" role="alert" hidden></div>

                <div class="form-grid">
                    <label class="field">
                        <span>Слово по-английски</span>
                        <input type="text" id="word-lemma" maxlength="64" placeholder="landscape">
                    </label>

                    <label class="field">
                        <span>Перевод</span>
                        <input type="text" id="word-translation" maxlength="128" placeholder="пейзаж">
                    </label>

                    <label class="field">
                        <span>Часть речи</span>
                        <select id="word-pos">
                            ${PARTS_OF_SPEECH.map((part) =>
                                `<option value="${part}">${esc(PART_NAMES[part])}</option>`).join('')}
                        </select>
                    </label>
                </div>

                <div class="row">
                    <button type="submit" class="btn btn--primary" id="word-submit">Добавить</button>
                </div>

                <p class="hint">
                    Словарь общий: если такое слово уже есть, оно будет использовано повторно,
                    а не заведено второй раз — иначе одно слово училось бы дважды.
                </p>
            </form>

            <form class="search-box" id="word-search-form" style="margin-top: 16px">
                <input id="word-search" placeholder="найти слово, которое уже есть в словаре"
                       autocomplete="off">
                <button type="submit" class="btn btn--ghost">${icon('search', 'icon--sm')} Найти</button>
            </form>

            <div id="word-search-results" style="margin-top: 12px"></div>
        </details>`;
}

function bindWordManagement(container, context, slug) {
    container.querySelector('#add-word-form')
        .addEventListener('submit', (event) => addNewWord(event, container, context, slug));

    container.querySelector('#word-search-form')
        .addEventListener('submit', (event) => searchWords(event, container, context, slug));

    container.querySelectorAll('[data-remove-word]').forEach((button) => {
        button.addEventListener('click', () => removeWord(container, context, slug, Number(button.dataset.removeWord)));
    });
}

async function addNewWord(event, container, context, slug) {
    event.preventDefault();

    const submit = container.querySelector('#word-submit');

    if (submit.disabled) {
        return;
    }

    const lemma = container.querySelector('#word-lemma').value.trim();
    const translation = container.querySelector('#word-translation').value.trim();
    const partOfSpeech = container.querySelector('#word-pos').value;

    setNotice(container, 'add-word-alert', '');

    if (!lemma || !translation) {
        setNotice(container, 'add-word-alert', 'Нужны и слово, и перевод.');
        return;
    }

    setBusy(submit, true, 'Добавляем…');

    try {
        await api(`/api/decks/${encodeURIComponent(slug)}/words`, {
            method: 'POST',
            body: { word: { lemma, translation, partOfSpeech } }
        });

        toast('Слово добавлено', 'ok');
        await showDeckWords(container, context, slug);
    } catch (error) {
        setNotice(container, 'add-word-alert', error.message);
        setBusy(submit, false, 'Добавить');
    }
}

async function searchWords(event, container, context, slug) {
    event.preventDefault();

    const query = container.querySelector('#word-search').value.trim();
    const slot = container.querySelector('#word-search-results');

    slot.innerHTML = '<p class="hint">Ищем…</p>';

    try {
        const found = await api(`/api/words?query=${encodeURIComponent(query)}`);

        slot.innerHTML = found.length
            ? `<div class="word-list">
                   ${found.map((word) => `
                       <div class="word-row">
                           <div class="word-row__main">
                               <span class="word-row__lemma">${esc(word.lemma)}</span>
                               <div class="word-row__translation">${esc(word.primaryTranslation)}</div>
                           </div>
                           <div class="word-row__side">
                               <span class="badge">${esc(PART_NAMES[word.partOfSpeech] ?? word.partOfSpeech)}</span>
                               ${speakButton(word.lemma)}
                               <button type="button" class="btn btn--ghost btn--sm" data-add-word="${word.id}">
                                   ${icon('plus', 'icon--sm')} Добавить
                               </button>
                           </div>
                       </div>`).join('')}
               </div>`
            : '<p class="hint">Ничего не нашлось. Заведите слово как новое — оно попадёт в словарь.</p>';

        bindSpeakButtons(slot);

        slot.querySelectorAll('[data-add-word]').forEach((button) => {
            button.addEventListener('click', () => addExistingWord(container, context, slug, Number(button.dataset.addWord)));
        });
    } catch (error) {
        slot.innerHTML = `<p class="hint">${esc(error.message)}</p>`;
    }
}

async function addExistingWord(container, context, slug, wordId) {
    try {
        await api(`/api/decks/${encodeURIComponent(slug)}/words`, {
            method: 'POST',
            body: { wordId }
        });

        toast('Слово добавлено в колоду', 'ok');
        await showDeckWords(container, context, slug);
    } catch (error) {
        toast(error.message, 'error');
    }
}

async function removeWord(container, context, slug, wordId) {
    try {
        await api(`/api/decks/${encodeURIComponent(slug)}/words/${wordId}`, { method: 'DELETE' });

        toast('Слово убрано из колоды', 'ok');
        await showDeckWords(container, context, slug);
    } catch (error) {
        toast(error.message, 'error');
    }
}

function wordRow(word, owned = false) {
    const translation = word.translations?.map((item) => item.text).join(', ') ?? '';

    return `
        <div class="word-row">
            <div class="word-row__main">
                <div>
                    <span class="word-row__lemma">${esc(word.lemma)}</span>
                    ${word.ipaUk ? `<span class="word-row__ipa">${esc(word.ipaUk)}</span>` : ''}
                </div>
                <div class="word-row__translation">${esc(translation)}</div>
            </div>
            <div class="word-row__side">
                ${word.level && word.level !== 0 ? `<span class="badge">${esc(levelName(word.level))}</span>` : ''}
                ${speakButton(word.lemma)}
                ${starButton(word.id, isFavorite(word.id))}
                ${owned ? `
                    <button type="button" class="star" data-remove-word="${word.id}"
                            title="Убрать из колоды">${icon('close')}</button>` : ''}
            </div>
        </div>`;
}

/**
 * Кнопка прослушивания слова.
 *
 * Показывается только там, где браузер умеет синтез речи. В остальных случаях
 * кнопка, которая молчит, хуже её отсутствия: человек решит, что сломалась
 * озвучка, а не браузер.
 */
function speakButton(lemma) {
    if (!canSpeak() || !lemma) {
        return '';
    }

    return `
        <button type="button" class="word-row__speak" data-speak="${esc(lemma)}"
                title="Прослушать" aria-label="Прослушать ${esc(lemma)}">
            ${icon('volume', 'icon--sm')}
        </button>`;
}

/** Обработчик один на всю разметку: слов в колоде бывает много. */
function bindSpeakButtons(root) {
    root.querySelectorAll('[data-speak]').forEach((button) => {
        button.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            speak(button.dataset.speak);
        });
    });
}

/* ── Мелкие помощники страницы ──────────────────────────────────────────── */

/** Сообщение внутри формы. Локальная копия помощника оболочки для её же разметки. */
function setNotice(container, id, message) {
    const element = container.querySelector(`#${id}`);

    if (!element) {
        return;
    }

    element.textContent = message;
    element.className = 'form-alert form-alert--error';
    element.hidden = !message;
}

function setBusy(button, busy, idleText) {
    button.disabled = busy;
    button.textContent = busy ? 'Отправляем…' : idleText;
}

function levelName(level) {
    // CefrLevel на сервере — перечисление, в JSON приходит числом.
    return ['—', 'A1', 'A2', 'B1', 'B2', 'C1', 'C2'][level] ?? '';
}

async function toggleStar(button, rawWordId) {
    const wordId = Number(rawWordId);

    try {
        const value = await toggleFavorite(wordId);

        button.classList.toggle('is-on', value);
        button.setAttribute('aria-pressed', String(value));

        // Набор избранного изменился — пусть следующая отрисовка его учтёт.
        await refreshFavorites();
    } catch (error) {
        toast(error.message, 'error');
    }
}

/* ── Сессия ─────────────────────────────────────────────────────────────── */

async function startSession(container, context, deckId, cram = false) {
    container.innerHTML = '<p class="hint">Собираем очередь…</p>';

    // Параметры собираются через URLSearchParams, а не склейкой строки: пустой
    // deckId превратился бы в «deckId=null», и сервер не смог бы разобрать число.
    const params = new URLSearchParams();
    if (deckId) {
        params.set('deckId', deckId);
    }
    if (cram) {
        params.set('cram', 'true');
    }

    const query = params.toString();

    try {
        const session = await api(`/api/study/session${query ? `?${query}` : ''}`);

        // Пустая очередь — это не «сессия закончена». Человек ничего не прошёл,
        // и говорить ему «прогресс сохранён» было бы неправдой: раньше именно
        // так и получался замкнутый круг из «сессии закончена» и «ещё сессию».
        if (session.totalItems === 0) {
            showNothingToStudy(container, context, session, deckId);
            return;
        }

        runSession(container, session, (reason) => afterSession(container, context, reason));
    } catch (error) {
        toast(error.message, 'error');
        await showDecks(container, context);
    }
}

/*
 * Экран «повторять нечего»: показывает, когда придёт следующее повторение,
 * и даёт возможность заниматься вне расписания. Без этого человек упирается
 * в тупик: карточек нет, а почему и что делать — непонятно.
 *
 * Колода передаётся дальше: тренировка вне расписания должна остаться той же
 * колодой, а не превращаться в общую — иначе выбранная тема теряется ровно там,
 * где человек её и выбрал.
 */
function showNothingToStudy(container, context, session, deckId) {
    stopSession();

    const next = session.nextDueAtUtc ? formatNextDue(session.nextDueAtUtc) : null;

    container.innerHTML = `
        <div class="card" style="text-align: center; padding: 32px">
            <div class="empty__icon" style="margin-bottom: 16px">${icon('check')}</div>
            <h2>На сегодня всё</h2>
            <p class="hint" style="margin-bottom: 22px">
                ${next
                    ? `Повторять пока нечего: следующее повторение — ${esc(next)}.`
                    : 'В изучении ещё нет ни одного слова.'}
            </p>
            <div class="row" style="justify-content: center">
                <button type="button" class="btn btn--primary" id="cram">
                    ${icon('play', 'icon--sm')} Учить вне расписания
                </button>
                <button type="button" class="btn btn--ghost" id="to-decks">К колодам</button>
            </div>
            <p class="hint" style="margin-top: 16px">
                Интервалы расставлены так, чтобы повторения не скапливались в один день.
                Тренировка вне расписания берёт слова с самым близким сроком; ответы
                учитываются в прогрессе как обычные.
            </p>
        </div>`;

    container.querySelector('#cram')
        .addEventListener('click', () => startSession(container, context, deckId ?? null, true));
    container.querySelector('#to-decks').addEventListener('click', () => showDecks(container, context));
}

/* «Сегодня в 18:40», «завтра в 09:15» или полная дата — как понятнее человеку. */
function formatNextDue(isoUtc) {
    const due = new Date(isoUtc);

    if (Number.isNaN(due.getTime())) {
        return 'скоро';
    }

    const now = new Date();
    const time = due.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });

    if (due.toDateString() === now.toDateString()) {
        return `сегодня в ${time}`;
    }

    const tomorrow = new Date(now);
    tomorrow.setDate(now.getDate() + 1);

    if (due.toDateString() === tomorrow.toDateString()) {
        return `завтра в ${time}`;
    }

    return `${due.toLocaleDateString('ru-RU')} в ${time}`;
}

async function afterSession(container, context, reason) {
    if (reason === 'finished') {
        await showSummary(container, context);
        return;
    }

    await showDecks(container, context);
}

async function showSummary(container, context) {
    stopSession();

    try {
        const summary = await api('/api/stats/summary');
        const profile = await api('/api/me');

        container.innerHTML = `
            <div class="card" style="text-align: center; padding: 32px">
                <div class="empty__icon" style="margin-bottom: 16px">${icon('check')}</div>
                <h2>Сессия закончена</h2>
                <p class="hint" style="margin-bottom: 22px">
                    Прогресс сохранён, время следующих повторений пересчитано.
                </p>
                <div class="row" style="justify-content: center">
                    <button type="button" class="btn btn--primary" id="again">
                        ${icon('refresh', 'icon--sm')} Ещё сессию
                    </button>
                    <button type="button" class="btn btn--ghost" id="favorites">
                        ${icon('star', 'icon--sm')} Избранное
                    </button>
                </div>
            </div>

            <div class="stat-grid" style="margin-top: 20px">
                ${statTile(summary.learnedCards, 'выучено', 'accent')}
                ${statTile(summary.cardsInProgress, 'в работе')}
                ${statTile(summary.dueNow, 'пора повторить')}
                ${statTile(`${summary.reviewedToday} из ${summary.dailyGoal}`, 'ответов сегодня')}
                ${statTile(`${summary.accuracyLast30Days}%`, 'удержание')}
                ${statTile(`🔥 ${summary.currentStreak}`, 'серия дней', 'flame')}
            </div>

            <p class="hint">
                Всего в изучении ${plural(profile.totalCards, ['слово', 'слова', 'слов'])}
                · серия ${plural(profile.currentStreak, ['день', 'дня', 'дней'])}.
            </p>`;

        container.querySelector('#again').addEventListener('click', () => showDecks(container, context));
        container.querySelector('#favorites').addEventListener('click', () => context.navigate('favorites'));
    } catch (error) {
        toast(error.message, 'error');
        await showDecks(container, context);
    }
}

export { showDecks, showDeckWords };
