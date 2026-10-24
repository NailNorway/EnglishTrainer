/*
 * Оболочка приложения: вход, регистрация, восстановление пароля, навигация
 * и роутер.
 *
 * Страницы — отдельные модули, каждая умеет только отрисоваться в контейнер.
 * Оболочка отвечает за то, что вокруг: сеанс, меню, выбор страницы по адресу.
 * Роутинг на хеше выбран потому, что не требует настройки сервера: любой
 * адрес вида /#/friends открывается без правки маршрутов на стороне API.
 * Экраны авторизации живут вне оболочки и показываются вместо неё, поэтому
 * в routes их нет: адреса #/forgot и #/reset перехватывает роутер.
 */

import { api, getToken, setToken, onUnauthorized } from './api.js';
import { state, refreshFavorites, setUser } from './state.js';
import { esc, icon, setLoading, avatar } from './ui.js';
import { initTheme, toggleTheme, resolvedTheme } from './theme.js';
import { initSpeech } from './speech.js';
import { initTelegram, signInWithTelegram, telegramInitData, openExternal } from './telegram.js';
import { stopSession } from './session.js';

import * as study from './pages/study.js';
import * as favorites from './pages/favorites.js';
import * as friends from './pages/friends.js';
import * as settings from './pages/settings.js';
import * as profile from './pages/profile.js';
import * as player from './pages/user.js';
import * as updates from './pages/updates.js';

/*
 * Разделы меню. Адрес профиля игрока — единственный с переменной частью
 * (#/user/ник), и в меню он не показывается: это не раздел, а экран, который
 * открывается из списка друзей и из рейтинга. Активным при этом остаётся
 * «Друзья» — nav указывает, какой пункт подсвечивать.
 *
 * «Что нового» тоже не пункт меню: в мини-аппе нижняя панель рассчитана на пять
 * разделов, и шестой отнял бы место у остальных. Страница открывается из профиля,
 * а в браузере — ещё и из подписи под списком разделов.
 */
const routes = [
    { path: 'study', title: 'Тренировка', icon: 'book', page: study },
    { path: 'favorites', title: 'Избранное', icon: 'star', page: favorites, badge: 'favorites' },
    { path: 'friends', title: 'Друзья', icon: 'users', page: friends, badge: 'friends' },
    { path: 'settings', title: 'Настройки', icon: 'settings', page: settings },
    { path: 'profile', title: 'Профиль', icon: 'user', page: profile },
    { path: 'user', title: 'Профиль игрока', page: player, nav: 'friends', menu: false },
    { path: 'updates', title: 'Что нового', page: updates, nav: 'profile', menu: false }
];

/** Элементы разметки адресуются по id, поэтому короткая ссылка читается легче. */
const $ = (id) => document.getElementById(id);

const elements = {
    home: $('screen-home'),
    auth: $('screen-auth'),
    forgot: $('screen-forgot'),
    reset: $('screen-reset'),
    telegram: $('screen-telegram'),
    updates: $('screen-updates'),
    publicUpdates: $('public-updates-content'),
    app: $('app'),
    nav: $('nav'),
    foot: $('sidebar-foot'),
    title: $('page-title'),
    content: $('content')
};

const context = { navigate, refreshShell, logout };

let currentPath = null;
let authMode = 'login';
let resetToken = '';

/* ── Оформление и Telegram ──────────────────────────────────────────────── */

// И то и другое настраивается до первой отрисовки: тема — чтобы страница
// не мигнула светлым, Telegram — чтобы интерфейс сразу занял нужную высоту
// и получил палитру клиента.
initTheme();
const inTelegram = initTelegram();

// Список голосов браузер отдаёт асинхронно. Если его не дождаться заранее,
// первое упражнение на слух прозвучит голосом по умолчанию — а он вполне
// может читать английские слова с русским акцентом.
initSpeech();

bindThemeToggle();

/* ── Запуск ─────────────────────────────────────────────────────────────── */

onUnauthorized(async () => {
    setToken(null);
    setUser(null);

    if (inTelegram) {
        // Внутри Telegram сеанс восстанавливается сам: подпись initData живёт
        // сутки, и требовать пароль там, где пароля нет, бессмысленно.
        // Если и это не выйдет, человека встретит экран привязки.
        try {
            await enterWithTelegram();
            return;
        } catch {
            showTelegramLink();
            return;
        }
    }

    showAuth('Сеанс истёк — войдите заново.');
});

boot();

async function boot() {
    bindHome();
    bindAuthForm();
    bindForgotForm();
    bindResetForm();
    bindTelegramForm();
    bindPasswordToggles();
    window.addEventListener('hashchange', route);

    // Ссылки восстановления приходят письмом и открываются по прямому адресу:
    // в браузере при этом мог остаться действующий токен другого человека,
    // и показать вместо формы сброса чужой аккаунт было бы худшим решением.
    if (isResetHash()) {
        showReset();
        return;
    }

    if (isForgotHash()) {
        showForgot();
        return;
    }

    if (!getToken()) {
        // История обновлений открывается и без входа: на неё ведёт ссылка
        // с главной страницы.
        if (isUpdatesHash()) {
            await showPublicUpdates();
            return;
        }

        if (inTelegram) {
            // Внутри Telegram формы входа нет: человек уже подтвердил, кто он.
            // Остаётся понять, привязан ли этот аккаунт к прогрессу.
            await bootInTelegram();
            return;
        }

        showHome();
        return;
    }

    // Пользователь уже вошёл, но пришёл на корень сайта: показываем главную
    // с кнопками, а не сразу тренировку. Это нужно, чтобы логотип в меню вёл
    // на сайт и после входа. Кнопка «Войти» здесь откроет тренировку.
    if (!location.hash || location.hash === '#/') {
        showHome();
        return;
    }

    setLoading(true);

    try {
        await loadShell();
        await enterApp();
    } catch {
        // Токен есть, но сервер его не принял — показываем вход.
        showAuth();
    } finally {
        setLoading(false);
    }
}

/**
 * Вход внутри мини-аппа.
 *
 * Отдельная ветка, потому что исходов здесь не два, а три: вошёл, не привязан
 * и «Telegram не ответил». Показать форму входа во втором и третьем случае
 * нельзя — пароля человек всё равно не помнит, а приложение выглядело бы
 * сломанным.
 */
async function bootInTelegram() {
    setLoading(true);

    try {
        await enterWithTelegram();
    } catch (error) {
        // 403 — «этот Telegram не привязан»; остальное — сеть, истёкшая подпись,
        // недоступный сервер. Тексты разные, потому что и делать нужно разное:
        // в первом случае идти привязывать аккаунт, во втором — просто повторить.
        showTelegramLink(error.status === 403
            ? error.message
            : `Не удалось войти через Telegram: ${error.message}`);
    } finally {
        setLoading(false);
    }
}

/** Вход по подписи Telegram: токен, оболочка, приложение. */
async function enterWithTelegram() {
    const response = await signInWithTelegram();

    setToken(response.accessToken);
    setUser(response.user);

    await loadShell();
    await enterApp();
}

function showTelegramLink(message) {
    stopSession();
    showAuthScreen('telegram');

    if (message) {
        setNotice('telegram-alert', message, 'error');
    } else {
        clearNotice('telegram-alert');
    }
}

function bindTelegramForm() {
    $('telegram-open-site').addEventListener('click', () => openExternal(location.origin));

    $('telegram-retry').addEventListener('click', async (event) => {
        event.preventDefault();
        await bootInTelegram();
    });

    $('telegram-code-form').addEventListener('submit', async (event) => {
        event.preventDefault();

        const submit = $('telegram-code-submit');
        const code = $('telegram-code').value.trim();

        clearNotice('telegram-alert');

        if (!code) {
            setNotice('telegram-alert', 'Вставьте код из профиля на сайте.', 'error');
            $('telegram-code').focus();
            return;
        }

        setButtonBusy(submit, true, 'Привязываем…');

        try {
            // Код переносится без бота: мини-апп уже знает, какой это аккаунт
            // Telegram, и знает код. Отправлять человека в чат с командой
            // ради того же результата — лишний шаг, а на телефоне неудобный.
            const response = await api('/api/auth/telegram/link', {
                method: 'POST',
                body: { initData: telegramInitData(), code },
                anonymous: true
            });

            setToken(response.accessToken);
            setUser(response.user);

            setLoading(true);
            await loadShell();
            await enterApp();
        } catch (error) {
            setNotice('telegram-alert', error.message, 'error');
        } finally {
            setLoading(false);
            setButtonBusy(submit, false, 'Привязать и войти');
        }
    });
}

/* ── Переключатель темы ─────────────────────────────────────────────────── */

function bindThemeToggle() {
    const button = $('theme-toggle');

    renderThemeIcon();
    document.addEventListener('themechange', renderThemeIcon);
    button.addEventListener('click', toggleTheme);
}

/**
 * Иконка показывает, что произойдёт, а не то, что сейчас.
 *
 * На тёмной теме нарисовано солнце: нажатие включает светлую. Обратный
 * вариант («показано то, что сейчас») заставляет нажимать кнопку мысленно
 * наугад — а это половина всех жалоб на переключатели темы.
 */
function renderThemeIcon() {
    const button = $('theme-toggle');
    const dark = resolvedTheme() === 'dark';
    const label = dark ? 'Светлая тема' : 'Тёмная тема';

    button.innerHTML = icon(dark ? 'sun' : 'moon');
    button.title = label;
    button.setAttribute('aria-label', label);
}

/* ── Главная страница ───────────────────────────────────────────────────── */

function bindHome() {
    $('home-login').addEventListener('click', () => {
        // Если человек уже вошёл, «Войти» с главной ведёт прямо в тренировку:
        // заставлять его снова вводить пароль бессмысленно.
        if (getToken()) {
            void enterExistingSession();
            return;
        }

        showAuth();
    });

    $('home-register').addEventListener('click', () => {
        showAuth();
        setAuthMode('register');
    });

    $('auth-home').addEventListener('click', (event) => {
        event.preventDefault();
        showHome();
    });

    $('updates-home').addEventListener('click', () => showHome());
}

function showHome() {
    stopSession();
    showAuthScreen('home');

    // Главная живёт без адреса раздела: после публичной истории обновлений
    // или выхода из профиля в строке не должен оставаться чужой хеш.
    if (location.hash) {
        history.replaceState(null, '', location.pathname + location.search);
    }
}

async function showPublicUpdates() {
    stopSession();
    showAuthScreen('updates');

    // Страница обновлений — обычный модуль, но её кнопка «В профиль» в
    // публичном режиме ведёт на главную: профиля без входа ещё нет.
    await updates.render(elements.publicUpdates, {
        ...context,
        public: true,
        navigate: (path) => {
            if (path === 'home' || path === 'profile') {
                showHome();
            }
        }
    });
}

/* ── Вход и регистрация ─────────────────────────────────────────────────── */

function bindAuthForm() {
    $('auth-form').addEventListener('submit', (event) => {
        event.preventDefault();
        authenticate();
    });

    $('register-toggle').addEventListener('click', () => {
        setAuthMode(authMode === 'login' ? 'register' : 'login');
    });

    // Обычно экран отрисует hashchange, но при переходе по адресу, который уже
    // стоит в строке, событие не придёт — тогда показываем экран вручную.
    $('forgot-link').addEventListener('click', (event) => {
        event.preventDefault();
        goToAuthScreen('#/forgot', showForgot);
    });
}

/** Переход на экран вне оболочки: хеш остаётся источником истины. */
function goToAuthScreen(hash, show) {
    if (location.hash.startsWith(hash)) {
        show();
        return;
    }

    location.hash = hash;
}

function setAuthMode(mode) {
    authMode = mode;

    const isRegistration = mode === 'register';

    // При входе принимается и email, и ник, при регистрации — только email:
    // без смены подписи человек с ником не понимает, что от него хотят.
    $('identifier-label').textContent = isRegistration ? 'Email' : 'Email или ник';
    $('email').placeholder = isRegistration ? 'you@example.com' : 'you@example.com или ваш ник';
    $('identifier-hint').hidden = isRegistration;

    // Менеджер паролей смотрит на autocomplete: с current-password при
    // регистрации он подставляет пароль от старого аккаунта и мешает задать новый.
    $('password').autocomplete = isRegistration ? 'new-password' : 'current-password';

    toggleAuthField('username-field', 'username', isRegistration);
    toggleAuthField('password2-field', 'password2', isRegistration);

    $('forgot-hint').hidden = isRegistration;
    $('login').textContent = authSubmitText();
    $('register-toggle').textContent = isRegistration ? 'У меня уже есть аккаунт' : 'Регистрация';

    // Сообщение относилось к другому набору полей: после переключения режима
    // оно только сбивает с толку.
    clearNotice('auth-alert');
    hidePasswords();
}

function authSubmitText() {
    return authMode === 'register' ? 'Зарегистрироваться' : 'Войти';
}

/**
 * Показывает или прячет поле вместе с его вводом. Скрытый ввод отключается:
 * иначе браузер отправил бы оставшееся значение и подставил его из
 * автозаполнения вопреки выбранному режиму формы.
 */
function toggleAuthField(fieldId, inputId, visible) {
    const field = $(fieldId);
    const input = $(inputId);

    field.hidden = !visible;
    input.disabled = !visible;

    if (!visible) {
        input.value = '';
    }
}

function showAuth(message) {
    stopSession();

    showAuthScreen('auth');

    // Режим выставляется до сообщения: setAuthMode очищает прошлую ошибку,
    // а новый текст должен остаться на экране.
    setAuthMode('login');

    if (message) {
        setNotice('auth-alert', message, 'error');
    }
}

/** Показывает один экран вне оболочки и прячет остальные. */
function showAuthScreen(name) {
    elements.home.hidden = name !== 'home';
    elements.auth.hidden = name !== 'auth';
    elements.forgot.hidden = name !== 'forgot';
    elements.reset.hidden = name !== 'reset';
    elements.telegram.hidden = name !== 'telegram';
    elements.updates.hidden = name !== 'updates';
    elements.app.hidden = true;
}

async function authenticate() {
    const submit = $('login');

    // Кнопка блокируется на время запроса: без этого двойное нажатие успевает
    // создать два аккаунта, и второй запрос падает на «email занят».
    if (submit.disabled) {
        return;
    }

    const isRegistration = authMode === 'register';
    // Ник в профиле показывается как @ivan, и его часто копируют в поле целиком.
    // Сервер тоже снимает ведущую «@», здесь — чтобы не отправлять лишнее.
    const identifier = $('email').value.trim().replace(/^@+/, '');
    const password = $('password').value;
    const repeated = $('password2').value;
    // Ник иногда набирают с собачкой, как в адресе профиля: сервер её не ждёт.
    const username = isRegistration ? $('username').value.trim().replace(/^@+/, '') : '';

    clearNotice('auth-alert');

    if (!identifier) {
        failAuth('Укажите email или ник.', 'email');
        return;
    }

    if (password.length < 8) {
        failAuth('Пароль должен быть не короче 8 символов.', 'password');
        return;
    }

    if (isRegistration && password !== repeated) {
        // Сервер видит только один пароль и о расхождении не скажет, а опечатка
        // во втором поле делает свежий аккаунт недоступным — сверяем до запроса.
        failAuth('Пароли не совпадают.', 'password2');
        return;
    }

    let body;

    if (isRegistration) {
        body = {
            email: identifier,
            password,
            displayName: identifier.split('@')[0],
            // Часовой пояс определяется у браузера: от него зависят дневная
            // статистика и серия дней, и угадывать его на сервере незачем.
            timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone
        };

        if (username) {
            body.username = username;
        }
    } else {
        // Поле называется login: сервер принимает и email, и ник.
        body = { login: identifier, password };
    }

    setButtonBusy(submit, true, authSubmitText());

    try {
        const response = await api(
            isRegistration ? '/api/auth/register' : '/api/auth/login',
            { method: 'POST', body, anonymous: true });

        // Сервер уже выдал токен, поэтому после регистрации отдельного шага
        // входа нет: сразу собираем оболочку.
        setToken(response.accessToken);
        setUser(response.user);

        // Индикатор загрузки показывается только на сборке оболочки: на форме
        // он закрывал бы и кнопку, и текст ошибки.
        setLoading(true);
        await loadShell();
        await enterApp();
    } catch (error) {
        setNotice('auth-alert', error.message, 'error');

        if (!isRegistration) {
            // Email или ник остаётся на месте, а пароль очищается: переписывают
            // обычно именно его, и курсор сразу оказывается в нужном поле.
            $('password').value = '';
            hidePasswords();
            $('password').focus();
        }
    } finally {
        setLoading(false);
        setButtonBusy(submit, false, authSubmitText());
    }
}

/** Ошибка формы: текст остаётся в форме, фокус уходит в поле, которое правят. */
function failAuth(message, focusId) {
    setNotice('auth-alert', message, 'error');
    $(focusId).focus();
}

/* ── Забыли пароль ──────────────────────────────────────────────────────── */

/**
 * Показывает экран «Забыли пароль?».
 *
 * Своя функция, а не пара строк в обработчике ссылки: экран открывается и из
 * роутера (#/forgot), и с формы входа. Раньше этой функции не было вовсе —
 * ссылка вызывала несуществующее имя, обработчик падал с ReferenceError,
 * и нажатие «Забыли пароль?» не делало ничего.
 */
function showForgot() {
    stopSession();

    showAuthScreen('forgot');

    // Сообщение прошлого захода не должно встречать человека при новом открытии
    // формы: он мог вернуться сюда через час, и «ссылка отправлена» относилось бы
    // к другому вводу.
    clearNotice('forgot-alert');
    $('forgot-login').focus();
}

function bindForgotForm() {
    $('forgot-form').addEventListener('submit', (event) => {
        event.preventDefault();
        sendResetLink();
    });

    $('forgot-back').addEventListener('click', backToAuth);
}

async function sendResetLink() {
    const submit = $('forgot-submit');

    if (submit.disabled) {
        return;
    }

    const login = $('forgot-login').value.trim();

    clearNotice('forgot-alert');

    if (!login) {
        setNotice('forgot-alert', 'Укажите email или ник.', 'error');
        $('forgot-login').focus();
        return;
    }

    setButtonBusy(submit, true, 'Отправить ссылку');

    try {
        // Ответ всегда 204: сервер намеренно не говорит, есть такой
        // пользователь или нет, поэтому и текст не зависит от наличия аккаунта.
        await api('/api/auth/forgot-password',
            { method: 'POST', body: { login }, anonymous: true });

        setNotice('forgot-alert', resetSentMessage(await passwordResetMethod()), 'ok');
    } catch (error) {
        setNotice('forgot-alert', error.message, 'error');
    } finally {
        setButtonBusy(submit, false, 'Отправить ссылку');
    }
}

/**
 * Куда уходит письмо со ссылкой.
 *
 * Спрашивается у сервера, а не угадывается: без почтового провайдера письмо
 * пишется в журнал сервера, и отправлять человека проверять почту, которой он
 * не получит, — худшее, что может сделать форма восстановления доступа.
 * Ответ кэшируется: настройка меняется только перезапуском сервера.
 */
let resetMethod = null;

async function passwordResetMethod() {
    if (resetMethod) {
        return resetMethod;
    }

    try {
        const answer = await api('/api/auth/password-reset-method', { anonymous: true });
        resetMethod = answer.delivery === 'email' ? 'email' : 'log';
    } catch {
        // Не смогли спросить — обещаем только то, что верно в обоих случаях.
        resetMethod = 'unknown';
    }

    return resetMethod;
}

function resetSentMessage(method) {
    switch (method) {
        case 'email':
            return 'Если такой пользователь есть, письмо со ссылкой для сброса пароля '
                + 'отправлено. Проверьте почту — и папку «Спам»: письма от новых '
                + 'отправителей часто попадают туда.';

        case 'log':
            return 'Если такой пользователь есть, ссылка для сброса отправлена. '
                + 'В этой сборке почтовый провайдер не настроен, поэтому письмо пишется '
                + 'в журнал сервера — посмотрите окно, где запущен API.';

        default:
            return 'Если такой пользователь есть, ссылка для сброса отправлена.';
    }
}

/* ── Сброс пароля по ссылке ─────────────────────────────────────────────── */

function bindResetForm() {
    $('reset-form').addEventListener('submit', (event) => {
        event.preventDefault();
        saveNewPassword();
    });

    $('reset-back').addEventListener('click', backToAuth);
}

function showReset() {
    stopSession();

    showAuthScreen('reset');

    resetToken = readResetToken();

    if (!resetToken) {
        // Запрос без токена всё равно вернёт 400, поэтому форма сразу объясняет
        // причину и не предлагает заведомо провальную отправку.
        setResetFormEnabled(false);
        setNotice('reset-alert', 'Ссылка неполная: в ней нет токена', 'error');
        return;
    }

    setResetFormEnabled(true);
    clearNotice('reset-alert');
}

function setResetFormEnabled(on) {
    ['reset-password', 'reset-password2', 'reset-submit',
     'reset-password-eye', 'reset-password2-eye']
        .forEach((id) => { $(id).disabled = !on; });

    if (!on) {
        $('reset-password').value = '';
        $('reset-password2').value = '';
    }
}

/**
 * Токен из адреса вида #/reset?token=… Читаем строку вручную, а не через
 * URLSearchParams: тот превращает «+» в пробел и испортил бы токен в base64.
 */
function readResetToken() {
    const match = /[?&]token=([^&]*)/.exec(location.hash);

    if (!match) {
        return '';
    }

    try {
        return decodeURIComponent(match[1]).trim();
    } catch {
        // Битую percent-последовательность разбирать нечем: отдаём как есть,
        // сервер сам ответит, что ссылка недействительна.
        return match[1].trim();
    }
}

async function saveNewPassword() {
    const submit = $('reset-submit');

    if (submit.disabled || !resetToken) {
        return;
    }

    const password = $('reset-password').value;
    const repeated = $('reset-password2').value;

    clearNotice('reset-alert');

    if (password.length < 8) {
        setNotice('reset-alert', 'Пароль должен быть не короче 8 символов.', 'error');
        $('reset-password').focus();
        return;
    }

    if (password !== repeated) {
        setNotice('reset-alert', 'Пароли не совпадают.', 'error');
        $('reset-password2').focus();
        return;
    }

    setButtonBusy(submit, true, 'Сохранить пароль');

    try {
        await api('/api/auth/reset-password',
            { method: 'POST', body: { token: resetToken, newPassword: password }, anonymous: true });

        // Ссылка одноразовая: повторная отправка той же формы получит 400,
        // поэтому экран закрывается, а сообщение переезжает на форму входа.
        resetToken = '';
        location.hash = '';
        showAuth();
        setNotice('auth-alert', 'Пароль изменён, войдите с новым паролем', 'ok');
    } catch (error) {
        // 400 приходит с понятным detail: «Ссылка недействительна или устарела».
        setNotice('reset-alert', error.message, 'error');
    } finally {
        setButtonBusy(submit, false, 'Сохранить пароль');
    }
}

/** Возврат на форму входа: экраны восстановления следов в адресе не оставляют. */
function backToAuth(event) {
    event.preventDefault();

    location.hash = '';
    showAuth();
}

/* ── Сообщения и поля пароля ────────────────────────────────────────────── */

/**
 * Сообщение внутри формы. В отличие от уведомления оно остаётся на экране,
 * пока пользователь не исправит ввод и не отправит форму снова.
 */
function setNotice(elementId, text, kind = 'error') {
    const element = $(elementId);

    element.textContent = text;
    element.className = `form-alert form-alert--${kind}`;
    element.hidden = false;
}

function clearNotice(elementId) {
    const element = $(elementId);

    element.textContent = '';
    element.hidden = true;
}

/**
 * Кнопка на время запроса: текст меняется, повторное нажатие не проходит.
 * Иначе двойной клик по «Зарегистрироваться» создаёт вторую попытку, которая
 * падает на «email занят», хотя аккаунт уже создан.
 */
function setButtonBusy(button, busy, idleText) {
    button.disabled = busy;
    button.textContent = busy ? 'Отправляем…' : idleText;
}

function bindPasswordToggles() {
    // Скрытый пароль выглядит одинаково и при верном наборе, и при опечатке
    // в одном символе, поэтому возможность посмотреть ввод обязательна.
    bindPasswordToggle('password-eye', 'password');
    bindPasswordToggle('password2-eye', 'password2');
    bindPasswordToggle('reset-password-eye', 'reset-password');
    bindPasswordToggle('reset-password2-eye', 'reset-password2');
}

function bindPasswordToggle(buttonId, inputId) {
    const button = $(buttonId);
    const input = $(inputId);

    button.addEventListener('click', () => {
        const reveal = input.type === 'password';

        input.type = reveal ? 'text' : 'password';
        setEyeState(button, reveal);

        // Фокус остаётся в поле: переключение не должно прерывать набор.
        input.focus();
    });
}

/** Кнопка-глаз сообщает своё состояние и голосом, и иконкой. */
function setEyeState(button, revealed) {
    const label = revealed ? 'Скрыть пароль' : 'Показать пароль';

    button.setAttribute('aria-label', label);
    button.setAttribute('aria-pressed', String(revealed));
    button.title = label;
    button.querySelector('use').setAttribute('href', revealed ? '#i-eye-off' : '#i-eye');
}

/** Пароли снова прячутся: значение очищено или форма переключена. */
function hidePasswords() {
    [['password-eye', 'password'],
     ['password2-eye', 'password2'],
     ['reset-password-eye', 'reset-password'],
     ['reset-password2-eye', 'reset-password2']]
        .forEach(([buttonId, inputId]) => {
            $(inputId).type = 'password';
            setEyeState($(buttonId), false);
        });
}

/* ── Оболочка ───────────────────────────────────────────────────────────── */

async function loadShell() {
    const profile = await api('/api/me');

    state.profile = profile;
    state.user = profile.user;

    await refreshFavorites();

    // Входящие заявки видны в меню, поэтому счётчик нужен сразу после входа.
    const overview = await api('/api/friends');
    state.badges.friends = overview.incoming.length;

    renderUserChip();
}

async function enterApp() {
    // Экраны авторизации прячутся: после входа оболочка показывается вместо них.
    elements.home.hidden = true;
    elements.auth.hidden = true;
    elements.forgot.hidden = true;
    elements.reset.hidden = true;
    elements.telegram.hidden = true;
    elements.updates.hidden = true;
    elements.app.hidden = false;

    if (!location.hash) {
        location.hash = `#/${routes[0].path}`;
        // Присвоение хеша вызовет hashchange, но только если он действительно изменился.
        if (!location.hash) await route();
        return;
    }

    await route();
}

/**
 * Вход в уже существующий сеанс с главной страницы.
 *
 * Токен уже есть, но оболочка не загружена: после клика «Войти» нужно получить
 * профиль и показать приложение. Если токен успел истечь, обычная обработка
 * ошибки покажет форму входа.
 */
async function enterExistingSession() {
    setLoading(true);

    try {
        await loadShell();
        await enterApp();
    } catch {
        showAuth();
    } finally {
        setLoading(false);
    }
}

function refreshShell() {
    renderUserChip();
    renderNav();
}

function renderUserChip() {
    const user = state.user;

    if (!user) {
        elements.foot.innerHTML = '';
        return;
    }

    elements.foot.innerHTML = `
        <div class="user-chip">
            ${avatar(user, 'avatar--sm')}
            <div class="user-chip__name">
                <b>${esc(user.displayName)}</b>
                <span>@${esc(user.username)}</span>
            </div>
            <button type="button" class="star" id="logout" title="Выйти">${icon('logout')}</button>
        </div>

        <!-- Ссылка на «Что нового» вместе с версией: в мини-аппе этого блока нет
             (там подвал скрыт), и вход на страницу остаётся в профиле. -->
        <a class="foot-link" href="#/updates">
            ${icon('refresh', 'icon--sm')} <span>Что нового</span>
            <span class="foot-link__version" id="app-version"></span>
        </a>`;

    document.getElementById('logout').addEventListener('click', logout);
    showVersion();
}

/**
 * Показывает версию приложения в подвале и в шапке.
 *
 * Номер спрашивается у сервера, а не пишется в разметке: он должен совпадать
 * с той сборкой, которая работает, иначе «v1.1» в интерфейсе ничего не значит.
 * Ошибка запроса молча оставляет номер пустым — версия не то, ради чего стоит
 * показывать сообщение об ошибке.
 */
async function showVersion() {
    try {
        const info = await api('/api/version', { anonymous: true, quiet: true });

        if (!info?.version) {
            return;
        }

        // Два места: подпись под разделами и ссылка в шапке. Номер один и тот же —
        // он приходит из сборки, а не из разметки.
        for (const id of ['app-version', 'updates-version']) {
            const slot = document.getElementById(id);

            if (slot) {
                slot.textContent = `v${info.version}`;
            }
        }
    } catch {
        // Версии нет — ссылки остаются, номера просто не будет.
    }
}

/** Выход из аккаунта: токен и данные пользователя стираются, показывается вход. */
function logout() {
    setToken(null);
    setUser(null);

    // В мини-аппе пароля нет: «выйти» там означает лишь сбросить токен, и войти
    // заново можно тем же аккаунтом Telegram. Показывать форму входа
    // бессмысленно — вводить в неё нечего.
    if (inTelegram) {
        void bootInTelegram();
        return;
    }

    // В браузере после выхода показываем главную: оттуда можно войти другим
    // аккаунтом или зарегистрироваться, не возвращаясь в форму принудительно.
    showHome();
}

function renderNav() {
    elements.nav.innerHTML = routes.filter((route) => route.menu !== false).map((route) => {
        const count = route.badge ? state.badges[route.badge] ?? 0 : 0;

        return `
            <a class="nav-item ${route.path === currentPath ? 'is-active' : ''}" href="#/${route.path}">
                ${icon(route.icon)}
                <span>${esc(route.title)}</span>
                ${count > 0 ? `<span class="badge badge--brand">${count}</span>` : ''}
            </a>`;
    }).join('');
}

/* ── Роутер ─────────────────────────────────────────────────────────────── */

function navigate(path) {
    const target = `#/${path}`;

    if (location.hash === target) {
        route();
        return;
    }

    location.hash = target;
}

function isResetHash() {
    return location.hash.startsWith('#/reset');
}

function isForgotHash() {
    return location.hash.startsWith('#/forgot');
}

function isUpdatesHash() {
    return location.hash.startsWith('#/updates');
}

async function route() {
    // Экраны восстановления пароля живут вне оболочки, но приходят обычным
    // хешем: их нужно перехватить до выбора страницы, иначе роутер отрисует
    // «Тренировку» вместо формы сброса.
    if (isResetHash()) {
        showReset();
        return;
    }

    if (isForgotHash()) {
        showForgot();
        return;
    }

    // История обновлений открывается с главной и без входа, и с уже
    // авторизованным профилем: главная — отдельный экран, оболочка приложения
    // в этот момент ещё скрыта. Внутри приложения этот же хеш обрабатывается
    // обычным роутером ниже.
    if (isUpdatesHash() && elements.app.hidden) {
        await showPublicUpdates();
        return;
    }

    // Пока оболочка скрыта, страницы не отрисовываются: иначе смена хеша на
    // экране входа втихую готовила бы страницу в скрытом контейнере.
    if (elements.app.hidden) {
        // Возврат с публичной истории обновлений по кнопке «назад» в браузере.
        if (!location.hash || location.hash === '#/') {
            showHome();
        }
        return;
    }

    const requested = location.hash.replace(/^#\/?/, '').split('?')[0];

    // Точное совпадение проверяется первым, затем — раздел с переменной частью
    // адреса: #/user/ivan открывает профиль игрока, а не «Тренировку».
    const matched = routes.find((route) => route.path === requested)
        ?? routes.find((route) => requested.startsWith(`${route.path}/`))
        ?? routes[0];

    currentPath = matched.nav ?? matched.path;
    elements.title.textContent = matched.title;

    stopSession();
    renderNav();

    try {
        await matched.page.render(elements.content, context);
    } catch (error) {
        elements.content.innerHTML = `<p class="hint">${esc(error.message)}</p>`;
    }

    // Страница могла обновить счётчики (например, принять заявку в друзья),
    // поэтому меню перерисовывается ещё раз, уже после её работы.
    renderNav();
}
