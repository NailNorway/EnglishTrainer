// Дымовой тест интерфейса: запускает настоящий app.js на заглушке DOM.
//
// Зачем: ошибка вида «вызвал переменную как функцию» валит весь запуск, но
// видна только в браузере. Headless-браузер в песочнице не стартует (Chrome
// не может создать свои внутренние каналы), поэтому здесь достаточно
// минимального DOM, чтобы выполнить boot() и роутер по-настоящему.
//
// Два режима:
//   node scripts/js-smoke.mjs            — гость: главная и форма восстановления
//   SMOKE_TOKEN=1 node scripts/js-smoke.mjs — вошедший: оболочка, меню, разделы
//
// Второй режим важен отдельно: у вошедшего пользователя другой путь запуска
// (loadShell → enterApp → route), и ошибка в нём не видна гостю.

import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const AUTHORIZED = process.env.SMOKE_TOKEN === '1';

// ── Заглушка DOM ─────────────────────────────────────────────────────────────

class El {
    constructor(id = '') {
        this.id = id;
        this.hidden = true;
        this.textContent = '';
        this.title = '';
        this.dataset = {};
        this.style = {};
        this.children = [];
        this._html = '';
        this.classList = {
            add() {}, remove() {}, toggle() {}, contains() { return false; }
        };
    }

    get innerHTML() { return this._html; }
    set innerHTML(value) { this._html = String(value); }

    addEventListener() {}
    removeEventListener() {}
    setAttribute() {}
    getAttribute() { return null; }
    removeAttribute() {}
    hasAttribute() { return false; }
    appendChild(child) { this.children.push(child); return child; }
    insertBefore(child) { this.children.push(child); return child; }
    removeChild() {}
    remove() {}
    focus() {}
    blur() {}
    closest() { return null; }
    querySelector() { return new El(); }
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { top: 0, left: 0, width: 0, height: 0 }; }
}

const elements = new Map();
const getElement = (id) => {
    if (!elements.has(id)) elements.set(id, new El(id));
    return elements.get(id);
};

const documentStub = {
    documentElement: new El('html'),
    body: new El('body'),
    title: '',
    scripts: [],
    getElementById: getElement,
    createElement: () => new El(),
    createTextNode: (text) => ({ textContent: text }),
    querySelector: () => new El(),
    querySelectorAll: () => [],
    addEventListener() {},
    removeEventListener() {},
    dispatchEvent() { return true; }
};

const locationStub = {
    // Вошедший пользователь проверяется не с корня: на «/» приложение по
    // задумке показывает главную с кнопками, а оболочка живёт на разделах.
    pathname: AUTHORIZED ? '/study' : '/',
    search: '',
    hash: '',
    href: AUTHORIZED ? 'http://127.0.0.1:5099/study' : 'http://127.0.0.1:5099/',
    origin: 'http://127.0.0.1:5099',
    assign() {},
    replace() {}
};

const syncFromUrl = (url) => {
    const parsed = new URL(url, locationStub.origin);
    locationStub.pathname = parsed.pathname;
    locationStub.search = parsed.search;
    locationStub.hash = parsed.hash;
    locationStub.href = parsed.href;
};

const historyStub = {
    entries: [],
    pushState(_state, _title, url) { syncFromUrl(url); this.entries.push(locationStub.pathname); },
    replaceState(_state, _title, url) { syncFromUrl(url); }
};

const storage = new Map();
if (AUTHORIZED) {
    storage.set('englishtrainer.token', 'smoke-test-token');
}

const localStorageStub = {
    getItem: (k) => (storage.has(k) ? storage.get(k) : null),
    setItem: (k, v) => storage.set(k, String(v)),
    removeItem: (k) => storage.delete(k),
    clear: () => storage.clear()
};

// Ответы API: минимальные правдоподобные структуры, чтобы страницы не падали
// на первом же обращении к полю. Всё, что не перечислено, получает {}.
const responses = {
    '/api/me': {
        user: {
            id: 1, username: 'smoke', displayName: 'Smoke', publicId: '00000000-0000-0000-0000-000000000000',
            createdAt: '2026-01-01T00:00:00Z', hasAvatar: false
        },
        settings: { newWordsPerDay: 10, dailyGoal: 10, sessionSize: 8, reminderEnabled: false },
        summary: { learnedCards: 0, cardsInProgress: 0, dueNow: 0, reviewedToday: 0, dailyGoal: 10, currentStreak: 0, accuracyLast30Days: 0 }
    },
    '/api/favorites/ids': [],
    '/api/friends': { incoming: [], outgoing: [], accepted: [] },
    '/api/version': { version: '1.3.1' }
};

const fetchStub = async (path) => {
    const key = String(path).split('?')[0];
    const payload = Object.hasOwn(responses, key) ? responses[key] : {};
    const text = JSON.stringify(payload);

    return {
        ok: true,
        status: 200,
        text: async () => text
    };
};

const listeners = new Map();

const windowStub = {
    addEventListener(type, handler) {
        if (!listeners.has(type)) listeners.set(type, []);
        listeners.get(type).push(handler);
    },
    removeEventListener() {},
    matchMedia: () => ({ matches: false, addEventListener() {}, addListener() {} }),
    location: locationStub,
    history: historyStub,
    localStorage: localStorageStub,
    document: documentStub,
    innerWidth: 1280,
    innerHeight: 800,
    scrollY: 0,
    scrollTo() {},
    Telegram: undefined,
    speechSynthesis: undefined,
    ym: undefined
};

windowStub.navigator = { language: 'ru-RU', userAgent: 'node-harness' };
windowStub.fetch = fetchStub;

Object.assign(globalThis, {
    window: windowStub,
    document: documentStub,
    location: locationStub,
    history: historyStub,
    localStorage: localStorageStub,
    matchMedia: windowStub.matchMedia,
    fetch: fetchStub
});

// navigator в Node 24 — геттер только для чтения, переопределяем явно.
Object.defineProperty(globalThis, 'navigator', {
    value: windowStub.navigator,
    configurable: true,
    writable: true
});

// CustomEvent нужен теме: она оповещает подписчиков о смене оформления.
globalThis.CustomEvent ??= class CustomEvent {
    constructor(type, options = {}) {
        this.type = type;
        this.detail = options.detail;
    }
};

// ── Запуск ───────────────────────────────────────────────────────────────────

const failures = [];
const check = (name, condition, detail = '') => {
    if (condition) {
        console.log(`  OK       ${name}`);
    } else {
        console.log(`  ПРОБЛЕМА ${name} ${detail}`);
        failures.push(name);
    }
};

const settle = () => new Promise((done) => setTimeout(done, 120));

const go = async (path) => {
    syncFromUrl(path);
    for (const handler of listeners.get('popstate') ?? []) {
        await handler();
    }
    await settle();
};

console.log(AUTHORIZED
    ? 'Режим: вошедший пользователь'
    : 'Режим: гость (без токена)');

let importError = null;
try {
    await import(pathToFileURL(resolve(ROOT, 'EnglishTrainer.Api/wwwroot/js/app.js')).href);
} catch (error) {
    importError = error;
}

await settle();

check('модуль загрузился без исключения', importError === null,
    importError ? `— ${importError.constructor.name}: ${importError.message}` : '');

if (!AUTHORIZED) {
    check('главная показана', getElement('screen-home').hidden === false,
        `hidden=${getElement('screen-home').hidden}`);
    check('оболочка скрыта', getElement('app').hidden === true);

    await go('/forgot');
    check('адрес /forgot открывает форму восстановления', getElement('screen-forgot').hidden === false,
        `hidden=${getElement('screen-forgot').hidden}`);
    check('главная при этом скрыта', getElement('screen-home').hidden === true);

    await go('/profile');
    check('переход на /profile без входа не падает', true);
} else {
    check('оболочка приложения показана', getElement('app').hidden === false,
        `hidden=${getElement('app').hidden}`);
    check('после входа открылась тренировка', locationStub.pathname === '/study',
        `path=${locationStub.pathname}`);
    check('заголовок раздела выставлен', getElement('page-title').textContent === 'Тренировка',
        `title=${getElement('page-title').textContent}`);
    check('меню нарисовано со ссылками-путями',
        String(getElement('nav').innerHTML).includes('href="/study"'),
        'ссылки /study в меню нет');

    await go('/friends');
    check('/friends переключает раздел', getElement('page-title').textContent === 'Друзья',
        `title=${getElement('page-title').textContent}`);
    check('адрес остаётся /friends', locationStub.pathname === '/friends',
        `path=${locationStub.pathname}`);

    await go('/words/learned');
    check('/words/learned открывает слова', getElement('page-title').textContent === 'Слова',
        `title=${getElement('page-title').textContent}`);

    await go('/profile');
    check('/profile открывает профиль', getElement('page-title').textContent === 'Профиль',
        `title=${getElement('page-title').textContent}`);

    await go('/');
    check('корень возвращает на главную', getElement('screen-home').hidden === false,
        `hidden=${getElement('screen-home').hidden}`);
    check('адрес при этом — /', locationStub.pathname === '/');
}

console.log('');
if (failures.length === 0) {
    console.log(`Интерфейс работает: ${AUTHORIZED ? 'путь вошедшего' : 'путь гостя'} пройден без ошибок.`);
} else {
    console.log(`Провалено проверок: ${failures.length}`);
    process.exitCode = 1;
}
