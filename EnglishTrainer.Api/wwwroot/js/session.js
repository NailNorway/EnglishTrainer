/*
 * Прохождение сессии: показ карточки, проверка ответа, разбор.
 *
 * Один и тот же код обслуживает обычную тренировку и тренировку по избранному:
 * отличается только способ получения сессии.
 */

import { api, ApiError } from './api.js';
import { isFavorite, toggleFavorite } from './state.js';
import { canSpeak, speak, stopSpeaking } from './speech.js';
import { hideBackButton, showBackButton } from './telegram.js';
import {
    esc, icon, emptyState, toast, humanInterval, kindLabel, exerciseLabel, starButton
} from './ui.js';

/**
 * Значение <c>ExerciseKind.Listening</c> в перечислении на сервере.
 *
 * Число, а не строка: сервер отдаёт перечисление числом, и это осознанный
 * выбор — так же устроены Kind и Rating. Константа названа, потому что
 * «card.exercise === 2» в условии не читается.
 */
const LISTENING = 2;

/** Скорость обычного и замедленного произношения. */
const SPEECH_RATE = 0.9;
const SLOW_SPEECH_RATE = 0.55;

/**
 * Обработчики текущей сессии.
 *
 * Сессия живёт внутри страницы, которую роутер перерисовывает целиком, поэтому
 * подписки на document нужно снимать явно — иначе после ухода со страницы
 * клавиши продолжат управлять уже несуществующими карточками.
 */
let cleanups = [];

function listen(target, type, handler) {
    target.addEventListener(type, handler);
    cleanups.push(() => target.removeEventListener(type, handler));
}

export function stopSession() {
    for (const cleanup of cleanups) {
        cleanup();
    }

    cleanups = [];

    // Произношение обрывается вместе с сессией: иначе голос продолжает читать
    // слово, которого на экране уже нет.
    stopSpeaking();

    // Кнопка «назад» принадлежит Telegram, а не странице: если её не спрятать,
    // она останется висеть в заголовке после выхода из тренировки и будет
    // выглядеть как кнопка выхода из приложения.
    hideBackButton();

    // Признак «идёт сессия» нужен оформлению: на телефоне экран ответа
    // уплотняется, а заголовок страницы убирается — иначе карточка не
    // помещается в окно и до кнопки «Проверить» приходится пролистывать.
    document.body.classList.remove('session-active');
}

/**
 * Вопрос карточки.
 *
 * Упражнение на слух — единственное, где текст вопроса не показывается вовсе:
 * слово произносится. Перевод на экране был бы подсказкой, поэтому он остаётся
 * только запасным вариантом — для браузеров без синтеза речи. В этом случае
 * упражнение превращается в написание по переводу, и оценка от этого не
 * меняется: принимается всё то же английское слово.
 *
 * @param {object} card карточка с сервера
 * @param {boolean} speechAvailable можно ли рассчитывать на произношение
 */
function renderQuestion(card, speechAvailable) {
    if (!speechAvailable) {
        return `<p class="prompt">${esc(card.prompt)}</p>`;
    }

    // Запись, если она когда-нибудь появится в словаре, важнее синтеза:
    // живой голос точнее любого синтезатора.
    const player = card.audioUrl
        ? `<audio controls autoplay src="${esc(card.audioUrl)}"></audio>`
        : `<div class="row">
               <button type="button" class="btn btn--primary" id="listen">
                   ${icon('play', 'icon--sm')} Прослушать
               </button>
               <button type="button" class="btn btn--ghost" id="listen-slow">Медленнее</button>
           </div>`;

    return `${player}
            <p class="hint">Напишите слово, которое услышали.</p>`;
}

/**
 * Запускает прохождение сессии в контейнере.
 *
 * @param {HTMLElement} container куда рисовать
 * @param {object} session ответ сервера с очередью карточек
 * @param {(reason: string) => void} onExit что делать после выхода или завершения
 */
export function runSession(container, session, onExit) {
    stopSession();

    document.body.classList.add('session-active');

    const run = {
        card: session.currentCard,
        nextCard: null,
        totalItems: session.totalItems,
        shownAt: 0,
        busy: false
    };

    if (!run.card) {
        renderFinished();
        return;
    }

    listen(document, 'keydown', (event) => {
        // В поле ввода клавиши принадлежат полю: Enter отправляет форму,
        // цифры — это часть ответа, а не выбор варианта.
        if (event.target instanceof HTMLInputElement) {
            return;
        }

        const nextButton = container.querySelector('#next');

        if (nextButton && (event.key === 'Enter' || event.key === ' ')) {
            event.preventDefault();
            nextButton.click();
            return;
        }

        if (/^[1-9]$/.test(event.key)) {
            const choice = container.querySelectorAll('.choice')[Number(event.key) - 1];

            if (choice) {
                event.preventDefault();
                answer(choice.dataset.option);
            }
        }
    });

    renderCard();

    function renderCard() {
        const card = run.card;

        run.busy = false;
        run.shownAt = Date.now();

        const hasChoices = card.options && card.options.length > 0;
        const transcription = [card.ipaUk, card.ipaUs].filter(Boolean).join('   ');

        // Диктант возможен, если есть запись или браузер умеет говорить.
        // Записей в словаре нет, поэтому на практике решает второе.
        const isDictation = card.exercise === LISTENING && !hasChoices;
        const speechAvailable = isDictation && (Boolean(card.audioUrl) || canSpeak());

        // Без произношения задание на слух превращается в написание по переводу,
        // и подпись обязана это отражать: «напишите слово, которое слышите»,
        // когда ничего не слышно, читается как поломка.
        const label = isDictation && !speechAvailable
            ? 'Напишите слово по переводу'
            : exerciseLabel(card.exercise);

        container.innerHTML = `
            <div class="session-bar">
                <span class="pill">${icon('book', 'icon--sm')}карточка ${card.position} из ${run.totalItems}</span>
                ${card.kind === 1
                    ? `<span class="pill pill--new">NEW · ${esc(kindLabel(card.kind))}</span>`
                    : `<span class="pill">${esc(kindLabel(card.kind))}</span>`}
                <span class="spacer"></span>
                <button type="button" class="btn btn--ghost btn--sm" id="session-exit">
                    ${icon('close', 'icon--sm')} Прервать
                </button>
            </div>

            <div class="card-view">
                <div class="row" style="justify-content: space-between; align-items: flex-start">
                    <p class="prompt-label">${esc(label)}</p>
                    ${starButton(card.wordId, isFavorite(card.wordId))}
                </div>

                ${renderQuestion(card, speechAvailable)}

                ${!speechAvailable && card.promptLanguage === 'en' && transcription
                    ? `<p class="ipa">${esc(transcription)}</p>` : ''}

                ${hasChoices
                    ? `<div class="choices">
                           ${card.options.map((option, index) => `
                               <button type="button" class="choice" data-option="${esc(option)}">
                                   <span class="num">${index + 1}</span>${esc(option)}
                               </button>`).join('')}
                       </div>`
                    : `<form class="answer-row" id="answer-form">
                           <input id="answer" placeholder="Ваш ответ" autocomplete="off"
                                  autocapitalize="off" autocorrect="off" spellcheck="false">
                           <button type="submit" class="btn btn--primary" id="check">Проверить</button>
                       </form>
                       ${card.answerLength ? `<p class="hint">Подсказка: в слове ${card.answerLength} букв.</p>` : ''}`}

                <div class="row session-actions">
                    <button type="button" class="btn btn--ghost btn--sm" id="dont-know">
                        ${icon('bulb', 'icon--sm')} Не знаю — показать ответ
                    </button>
                </div>
            </div>

            <div id="result-slot"></div>`;

        container.querySelector('#session-exit').addEventListener('click', exit);
        container.querySelector('#dont-know').addEventListener('click', () => answer(null, true));
        container.querySelector('[data-favorite]').addEventListener('click', (event) => {
            toggleStar(event.currentTarget, card.wordId);
        });

        // В мини-аппе своего заголовка нет, и выйти из тренировки можно только
        // прокруткой вверх. Кнопка «назад» у Telegram уже есть — она и
        // выполняет то же действие, что «Прервать».
        showBackButton(exit);

        if (speechAvailable && !card.audioUrl) {
            const say = (rate) => speak(card.spokenText, rate);

            container.querySelector('#listen').addEventListener('click', () => say(SPEECH_RATE));
            container.querySelector('#listen-slow').addEventListener('click', () => say(SLOW_SPEECH_RATE));

            // Первое произношение запускается сразу при показе карточки: иначе
            // непонятно, что вообще нужно делать, пока не догадаешься нажать
            // кнопку. Часть браузеров (iOS Safari) без жеста пользователя
            // произношение игнорирует — на этот случай кнопка и остаётся.
            say(SPEECH_RATE);
        }

        if (hasChoices) {
            container.querySelectorAll('.choice').forEach((button) => {
                button.addEventListener('click', () => answer(button.dataset.option));
            });
        } else {
            container.querySelector('#answer-form').addEventListener('submit', (event) => {
                event.preventDefault();
                answer(container.querySelector('#answer').value);
            });

            const input = container.querySelector('#answer');

            // Поле прокручивается в видимую часть экрана, когда получает фокус:
            // клавиатура закрывает нижнюю половину, и без этого человек печатает
            // «в никуда» — буквы набираются, а поля не видно. Задержка нужна,
            // чтобы прокрутка шла уже после того, как клавиатура выехала.
            input.addEventListener('focus', () => {
                setTimeout(() => input.scrollIntoView({ block: 'center', behavior: 'smooth' }), 300);
            });

            input.focus();
        }
    }

    /** Отправляет ответ. `dontKnow` — «не знаю»: ответа нет, оценка «забыл». */
    async function answer(value, dontKnow = false) {
        if (run.busy) return;

        if (!dontKnow && (!value || !value.trim())) {
            toast('Введите ответ или нажмите «Не знаю».');
            return;
        }

        run.busy = true;

        for (const id of ['#check', '#dont-know']) {
            const button = container.querySelector(id);
            if (button) button.disabled = true;
        }

        try {
            const result = await api('/api/study/review', {
                method: 'POST',
                body: {
                    itemId: run.card.itemId,
                    answer: dontKnow ? null : value,
                    // Явная оценка «забыл»: при «не знаю» человек не угадывает,
                    // а сразу смотрит ответ, и алгоритм должен узнать именно это.
                    rating: dontKnow ? 1 : null,
                    elapsedMs: Date.now() - run.shownAt
                }
            });

            run.nextCard = result.nextCard;

            // Возврат карточки в конец сессии удлиняет её: без поправки счётчик
            // «карточка N из M» на следующем шаге показал бы меньше, чем есть.
            if (result.requeued) run.totalItems += 1;

            renderResult(result);
        } catch (error) {
            toast(error.message, 'error');

            // 409 означает, что карточка уже отвечена или сессия устарела:
            // продолжать по устаревшей очереди нельзя, возвращаемся к списку колод.
            if (error instanceof ApiError && error.status === 409) {
                await exit();
                return;
            }

            run.busy = false;

            for (const id of ['#check', '#dont-know']) {
                const button = container.querySelector(id);
                if (button) button.disabled = false;
            }
        }
    }

    function renderResult(result) {
        const transcription = [result.ipaUk, result.ipaUs].filter(Boolean).join('   ');

        container.querySelector('#result-slot').innerHTML = `
            <div class="card-view" style="margin-top: 16px">
                <p class="verdict ${result.isCorrect ? 'verdict--ok' : 'verdict--no'}">
                    ${icon(result.isCorrect ? 'check' : 'close')}
                    ${result.isCorrect ? 'Верно' : 'Пока нет'}
                </p>

                <p class="answer-line">
                    ${esc(result.correctAnswer)}
                    ${transcription ? `<span class="ipa">${esc(transcription)}</span>` : ''}
                    ${canSpeak()
                        ? `<button type="button" class="btn btn--ghost btn--icon" id="say-answer"
                                   title="Прослушать" aria-label="Прослушать">
                               ${icon('play', 'icon--sm')}
                           </button>`
                        : ''}
                </p>

                ${result.isCorrect && result.matchedWithTypo
                    ? `<p class="hint">Ответ зачтён с опечаткой — сверьтесь с написанием выше.</p>`
                    : ''}

                ${result.examples?.length
                    ? `<ul class="examples">
                           ${result.examples.map((example) => `
                               <li>
                                   <span class="en">${esc(example.textEn)}</span>
                                   ${example.textRu ? `<span class="ru">${esc(example.textRu)}</span>` : ''}
                               </li>`).join('')}
                       </ul>`
                    : ''}

                <p class="schedule">
                    Следующее повторение ${esc(humanInterval(result.scheduledDays))}${result.requeued
                        ? ', и ещё раз в этой же сессии' : ''}.
                </p>

                <button type="button" class="btn btn--primary" id="next">
                    ${icon('play', 'icon--sm')} Дальше
                </button>
            </div>`;

        container.querySelector('#next').addEventListener('click', proceed);
        container.querySelector('#next').focus();

        // Разбор — самое подходящее место, чтобы услышать слово ещё раз:
        // ответ уже показан, скрывать нечего, а связь «написание — звучание»
        // как раз и закрепляется повторением.
        container.querySelector('#say-answer')
            ?.addEventListener('click', () => speak(result.correctAnswer, SPEECH_RATE));
    }

    function proceed() {
        if (run.nextCard) {
            run.card = run.nextCard;
            run.nextCard = null;
            renderCard();
            return;
        }

        renderFinished();
    }

    async function toggleStar(button, wordId) {
        try {
            const value = await toggleFavorite(wordId);

            button.classList.toggle('is-on', value);
            button.setAttribute('aria-pressed', String(value));
        } catch (error) {
            toast(error.message, 'error');
        }
    }

    async function exit() {
        try {
            await api('/api/study/session', { method: 'DELETE' });
        } catch {
            // Прерывание — не то действие, ради которого стоит показывать ошибку.
        }

        stopSession();
        onExit('exit');
    }

    function renderFinished() {
        stopSession();

        container.innerHTML = emptyState({
            iconName: 'check',
            title: 'Сессия закончена',
            text: 'Все карточки пройдены. Прогресс сохранён, следующие повторения назначены.',
            action: '<button type="button" class="btn btn--primary" id="to-summary">Показать итоги</button>'
        });

        container.querySelector('#to-summary').addEventListener('click', () => onExit('finished'));
    }
}
