/*
 * Страницы прогресса: «В работе», «Пора повторить», «Выучено».
 *
 * Это не отдельные копии одной вёрстки, а один экран с тремя срезами. Отличие —
 * подпись, наличие срока повторения и кнопка запуска повторения; сами слова,
 * озвучка и карточка с примерами общие.
 */

import { api } from '../api.js';
import {
    esc, icon, humanInterval, formatDate, phaseBadge
} from '../ui.js';
import { canSpeak, speak } from '../speech.js';
import { startSession } from './study.js';

const SCOPES = {
    'in-progress': {
        title: 'В работе',
        description: 'Слова, которые уже начаты, но ещё не перешли в выученные.',
        showDue: true,
        start: false
    },
    due: {
        title: 'Пора повторить',
        description: 'Слова, срок повторения которых уже наступил.',
        showDue: true,
        start: true
    },
    learned: {
        title: 'Выучено',
        description: 'Слова с интервалом повторения больше месяца.',
        showDue: false,
        start: false
    }
};

export async function render(container, context) {
    const scope = scopeFromHash();
    const meta = SCOPES[scope] ?? SCOPES['in-progress'];

    bindWordDialog();
    container.innerHTML = '<p class="hint">Загрузка…</p>';

    try {
        const items = await api(`/api/stats/words?scope=${encodeURIComponent(scope)}`);

        container.innerHTML = `
            <div class="row" style="margin-bottom: 18px">
                <button type="button" class="btn btn--ghost btn--sm" id="words-back">
                    ${icon('close', 'icon--sm')} К тренировке
                </button>
            </div>

            <div class="section-title">
                ${icon(scope === 'learned' ? 'check' : 'book', 'icon--sm')} ${esc(meta.title)}
                <span class="badge">${items.length}</span>
            </div>

            <p class="hint words-lead">${esc(meta.description)}</p>

            ${meta.start && items.length
                ? `<div class="row words-start">
                       <button type="button" class="btn btn--primary" id="start-due">
                           ${icon('play', 'icon--sm')} Начать повторение
                       </button>
                   </div>`
                : ''}

            ${items.length
                ? `<div class="word-list word-list--progress">
                       ${items.map((item) => wordRow(item, meta)).join('')}
                   </div>`
                : `<div class="card"><p class="hint" style="margin: 0">
                       ${scope === 'due'
                           ? 'Сейчас повторять нечего — все слова ждут своего срока.'
                           : scope === 'learned'
                               ? 'Выученных слов пока нет. Они появятся, когда интервал повторения превысит месяц.'
                               : 'В работе пока нет слов. Начните тренировку — и они появятся здесь.'}
                   </p></div>`}`;

        container.querySelector('#words-back')
            .addEventListener('click', () => context.navigate('study'));

        if (meta.start && items.length) {
            container.querySelector('#start-due')
                .addEventListener('click', () => startSession(container, context, undefined, false, true));
        }

        bindSpeakButtons(container);
        bindWordRows(container);
    } catch (error) {
        container.innerHTML = `
            <div class="card">
                <p class="hint" style="margin-top: 0">${esc(error.message)}</p>
                <button type="button" class="btn btn--primary" id="words-retry">Повторить</button>
            </div>`;

        container.querySelector('#words-retry')
            .addEventListener('click', () => render(container, context));
    }
}

function scopeFromHash() {
    const part = location.hash.replace(/^#\/words\/?/, '').split('?')[0];
    return part || 'in-progress';
}

function wordRow(item, meta) {
    const word = item.word;
    const translation = word.translations
        ?.map((entry) => entry.text)
        .filter(Boolean)
        .join(', ') ?? '';

    const transcription = [word.ipaUk, word.ipaUs].filter(Boolean).join(' · ');
    const due = meta.showDue ? dueBadge(item.dueAtUtc) : '';

    return `
        <article class="word-row word-row--clickable" data-word-id="${word.id}"
                 tabindex="0" role="button" aria-label="Открыть примеры слова ${esc(word.lemma)}">
            <div class="word-row__main">
                <div class="word-row__lemma-line">
                    <span class="word-row__lemma">${esc(word.lemma)}</span>
                    ${transcription ? `<span class="word-row__ipa">${esc(transcription)}</span>` : ''}
                </div>
                <div class="word-row__translation">${esc(translation)}</div>
            </div>
            <div class="word-row__side">
                ${phaseBadge(item.phase, item.isLearned)}
                ${due}
                ${speakButton(word.lemma)}
            </div>
        </article>`;
}

function dueBadge(value) {
    if (!value) {
        return '';
    }

    const due = new Date(value);
    const days = (due.getTime() - Date.now()) / 86_400_000;

    if (days <= 0) {
        return `<span class="badge badge--due" title="Пора повторить">пора повторить</span>`;
    }

    return `<span class="badge" title="${esc(formatDate(value))}">${esc(humanInterval(days))}</span>`;
}

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

function bindSpeakButtons(root) {
    root.querySelectorAll('[data-speak]').forEach((button) => {
        button.addEventListener('click', (event) => {
            event.preventDefault();
            event.stopPropagation();
            speak(button.dataset.speak);
        });
    });
}

function bindWordRows(container) {
    container.querySelectorAll('[data-word-id]').forEach((row) => {
        row.addEventListener('click', (event) => {
            if (event.target.closest('button')) {
                return;
            }

            void openWord(Number(row.dataset.wordId));
        });

        row.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                void openWord(Number(row.dataset.wordId));
            }
        });
    });
}

/* ── Карточка слова с примерами ─────────────────────────────────────────── */

function bindWordDialog() {
    const dialog = document.getElementById('word-dialog');

    if (!dialog || dialog.dataset.bound === '1') {
        return;
    }

    dialog.dataset.bound = '1';

    dialog.querySelector('#word-dialog-close')
        .addEventListener('click', () => dialog.close());

    // Клик по затемнению — тоже закрытие: на телефоне до крестика ещё дотянуться.
    dialog.addEventListener('click', (event) => {
        if (event.target === dialog) {
            dialog.close();
        }
    });
}

async function openWord(wordId) {
    const dialog = document.getElementById('word-dialog');
    const content = document.getElementById('word-dialog-content');

    if (!dialog || !content) {
        return;
    }

    content.innerHTML = '<p class="hint" style="margin: 0">Загрузка…</p>';

    if (!dialog.open) {
        dialog.showModal();
    }

    try {
        const word = await api(`/api/words/${wordId}`);
        const transcription = [word.ipaUk, word.ipaUs].filter(Boolean).join(' · ');

        content.innerHTML = `
            <div class="word-dialog__head">
                <div>
                    <h2>${esc(word.lemma)}</h2>
                    ${transcription ? `<p class="word-dialog__ipa">${esc(transcription)}</p>` : ''}
                </div>
                ${speakButton(word.lemma)}
            </div>

            <div class="word-dialog__translations">
                ${word.translations
                    .map((translation) => `
                        <span class="badge ${translation.isPrimary ? 'badge--brand' : ''}">
                            ${esc(translation.text)}${translation.sense ? ` · ${esc(translation.sense)}` : ''}
                        </span>`)
                    .join('')}
            </div>

            <div class="section-title">Примеры</div>

            ${word.examples.length
                ? `<div class="word-examples">
                       ${word.examples.map(exampleMarkup).join('')}
                   </div>`
                : '<p class="hint">Примеров пока нет.</p>'}`;

        bindSpeakButtons(content);
    } catch (error) {
        content.innerHTML = `<p class="hint" style="margin: 0">${esc(error.message)}</p>`;
    }
}

function exampleMarkup(example) {
    return `
        <div class="word-example">
            <p class="word-example__en">${esc(example.textEn)}</p>
            ${example.textRu ? `<p class="word-example__ru">${esc(example.textRu)}</p>` : ''}
            ${example.audioUrl
                ? `<audio controls preload="none" src="${esc(example.audioUrl)}"></audio>`
                : ''}
            ${example.attribution
                ? `<p class="hint word-example__source">${esc(example.attribution)}</p>`
                : ''}
        </div>`;
}
