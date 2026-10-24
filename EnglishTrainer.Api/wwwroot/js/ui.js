/*
 * Вспомогательные функции интерфейса: экранирование, иконки, форматирование,
 * уведомления. Никакой логики предметной области здесь нет.
 */

/** Экранирование перед вставкой в innerHTML. */
export function esc(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

/** Иконка из спрайта в разметке. */
export function icon(name, className = '') {
    return `<svg class="icon ${className}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

/**
 * Русское согласование числительных: 1 слово, 2 слова, 5 слов.
 * Без этого интерфейс выдаёт «5 слово», что сразу читается как небрежность.
 */
export function plural(count, forms) {
    const abs = Math.abs(count) % 100;
    const last = abs % 10;

    if (abs > 10 && abs < 20) return `${count} ${forms[2]}`;
    if (last === 1) return `${count} ${forms[0]}`;
    if (last >= 2 && last <= 4) return `${count} ${forms[1]}`;
    return `${count} ${forms[2]}`;
}

/** Человеческий вид интервала повторения. */
export function humanInterval(days) {
    if (days === null || days === undefined) return '';

    const minutes = Math.round(days * 24 * 60);

    if (days < 1 / 24) return `через ${Math.max(1, minutes)} мин`;
    if (days < 1) return `через ${Math.max(1, Math.round(days * 24))} ч`;
    if (days < 30) return `через ${Math.max(1, Math.round(days))} дн.`;
    return `через ${Math.max(1, Math.round(days / 30))} мес.`;
}

/** Форматирование даты в местном виде. */
export function formatDate(value) {
    if (!value) return '—';

    return new Date(value).toLocaleDateString('ru-RU', {
        day: 'numeric', month: 'long', year: 'numeric'
    });
}

/** Этап повторения в виде бейджа. */
export function phaseBadge(phase, isLearned) {
    if (isLearned) return '<span class="badge badge--learned">выучено</span>';

    switch (phase) {
        case 1:
        case 3:
            return '<span class="badge badge--learning">учу</span>';
        case 2:
            return '<span class="badge badge--review">повторение</span>';
        default:
            return '<span class="badge badge--new">новое</span>';
    }
}

/** Подпись типа карточки: откуда слово попало в сессию. */
export function kindLabel(kind) {
    // 0 — повторение, 1 — новое слово, 2 — избранное.
    switch (kind) {
        case 1: return 'новое слово';
        case 2: return 'избранное';
        default: return 'повторение';
    }
}

/** Что именно просят сделать в карточке. */
export function exerciseLabel(exercise) {
    // 0 — выбор варианта, 1 — ввод, 2 — на слух, 3 — написание.
    switch (exercise) {
        case 0: return 'Выберите перевод';
        case 2: return 'Напишите слово, которое слышите';
        default: return 'Напишите слово по-английски';
    }
}

/** Инициалы для аватара. */
export function initials(user) {
    const source = (user?.displayName || user?.username || user?.email || '?').trim();
    const words = source.split(/[\s._-]+/).filter(Boolean);

    if (words.length >= 2) {
        return (words[0][0] + words[1][0]).toUpperCase();
    }

    return source.slice(0, 2).toUpperCase();
}

/**
 * Аватар пользователя: загруженное фото, а при его отсутствии — инициалы.
 *
 * alt пустой намеренно: имя пользователя стоит рядом с кружком, и голосовой
 * диктор, прочитав его дважды, превратил бы список друзей в кашу.
 */
export function avatar(user, className = '') {
    const url = user?.avatarUrl;

    if (url) {
        return `<div class="avatar ${className}"><img src="${esc(url)}" alt="" loading="lazy"></div>`;
    }

    return `<div class="avatar ${className}">${esc(initials(user))}</div>`;
}

/** Уведомление внизу экрана. */
export function toast(message, kind = '') {
    const element = document.getElementById('toast');

    element.textContent = message;
    element.className = `toast ${kind ? `toast--${kind}` : ''}`;
    element.hidden = false;

    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => { element.hidden = true; }, 4200);
}

/** Индикатор загрузки поверх страницы. */
export function setLoading(on) {
    document.getElementById('loader').hidden = !on;
}

/** Пустое состояние: объясняет, что делать, вместо пустого экрана. */
export function emptyState({ iconName = 'bulb', title, text, action = '' }) {
    return `
        <div class="empty">
            <div class="empty__icon">${icon(iconName)}</div>
            <div class="empty__title">${esc(title)}</div>
            <p class="empty__text">${esc(text)}</p>
            ${action}
        </div>`;
}

/** Кнопка-звёздочка. Состояние приходит из общего набора избранного. */
export function starButton(wordId, isFavorite) {
    return `<button type="button" class="star ${isFavorite ? 'is-on' : ''}"
                    data-favorite="${wordId}" title="${isFavorite ? 'Убрать из избранного' : 'В избранное'}"
                    aria-pressed="${isFavorite}">${icon('star')}</button>`;
}

/* ── Достижения ─────────────────────────────────────────────────────────── */

/**
 * Полоска прогресса.
 *
 * Значение выше максимума обрезается: «120%» читается как ошибка, а не как
 * перевыполнение — награда не выдаётся дважды, и показывать больше нечего.
 */
export function progressBar(value, max) {
    const percent = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 100;

    return `<div class="bar"><span style="width: ${percent}%"></span></div>`;
}

/** Плашка достижения: полученное светится, закрытое показывает, сколько осталось. */
export function achievementBadge(item) {
    return `
        <div class="achievement ${item.unlocked ? 'is-unlocked' : ''}">
            <div class="achievement__mark" aria-hidden="true">${esc(item.icon ?? '🏅')}</div>
            <div class="achievement__body">
                <b>${esc(item.title)}</b>
                <span class="hint">${item.unlocked
                    ? `получено ${esc(formatDate(item.unlockedAt))}`
                    : esc(item.description)}</span>
                ${item.unlocked ? '' : progressBar(item.progress, item.target)}
                ${item.unlocked ? '' : `<span class="hint">${item.progress} из ${item.target}</span>`}
            </div>
        </div>`;
}

/**
 * Ближайшая незакрытая цель.
 *
 * Показывается отдельно от полного списка: именно она превращает набор наград
 * в план на завтра. «Осталось два слова» тянет вернуться сильнее, чем список,
 * в котором всё закрыто, кроме тысячи ответов.
 */
export function achievementGoal(achievements) {
    const goal = achievements?.nextGoal;

    if (!goal) {
        return '';
    }

    return `
        <div class="card goal">
            <div class="goal__head">
                <span class="goal__mark" aria-hidden="true">${esc(goal.icon ?? '🎯')}</span>
                <div>
                    <b>${esc(goal.title)}</b>
                    <p class="hint">${esc(goal.description)}</p>
                </div>
                <span class="spacer"></span>
                <span class="badge">${goal.progress} из ${goal.target}</span>
            </div>
            ${progressBar(goal.progress, goal.target)}
            <p class="hint">Достижений получено: ${achievements.unlockedCount} из ${achievements.total}</p>
        </div>`;
}
