// Проверка app.js: ищет вызовы идентификаторов, которые нигде не объявлены.
// Грубая, но для этого класса ошибки ("вызвал переменную как функцию") достаточная.

import { readFileSync } from 'node:fs';

const file = process.argv[2];
let src = readFileSync(file, 'utf8');

// Комментарии и строки убираем, чтобы содержимое текстов не давало ложных срабатываний.
src = src.replace(/\/\*[\s\S]*?\*\//g, ' ');
src = src.replace(/(^|[^:])\/\/[^\n]*/g, '$1 ');
src = src.replace(/`(?:[^`\\]|\\.)*`/g, '``');
src = src.replace(/'(?:[^'\\]|\\.)*'/g, "''");
src = src.replace(/"(?:[^"\\]|\\.)*"/g, '""');
// Регулярные выражения: внутри них «en(» — это не вызов функции.
src = src.replace(/\/(?:\\.|\[[^\]]*\]|[^/\n\\])+\/[gimsuy]*/g, ' ');

const declared = new Set();

const collect = (re, group = 1) => {
    for (const m of src.matchAll(re)) {
        for (const name of (m[group] ?? '').split(',')) {
            const clean = name.trim().split(/\s|=/)[0].replace(/[^\w$]/g, '');
            if (clean) declared.add(clean);
        }
    }
};

collect(/function\s+([A-Za-z_$][\w$]*)/g);
collect(/class\s+([A-Za-z_$][\w$]*)/g);
collect(/(?:const|let|var)\s+([A-Za-z_$][\w$]*)/g);
collect(/import\s+\{([^}]*)\}/g);
collect(/import\s+\*\s+as\s+([A-Za-z_$][\w$]*)/g);
collect(/import\s+([A-Za-z_$][\w$]*)\s+from/g);
collect(/catch\s*\(\s*([A-Za-z_$][\w$]*)/g);

// Параметры функций объявлены неявно, но вызвать их как функции можно — пропускаем.
const params = new Set();
for (const m of src.matchAll(/function\s*[A-Za-z_$]?\w*\s*\(([^)]*)\)/g)) {
    for (const p of m[1].split(',')) {
        const clean = p.trim().split(/\s|=/)[0].replace(/[^\w$]/g, '');
        if (clean) params.add(clean);
    }
}
for (const m of src.matchAll(/\(([^)]*)\)\s*=>/g)) {
    for (const p of m[1].split(',')) {
        const clean = p.trim().split(/\s|=/)[0].replace(/[^\w$]/g, '');
        if (clean) params.add(clean);
    }
}
for (const m of src.matchAll(/(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=/g)) params.add(m[1]);

const keywords = new Set([
    'if', 'for', 'while', 'switch', 'catch', 'return', 'typeof', 'function', 'new',
    'delete', 'void', 'in', 'of', 'do', 'else', 'try', 'finally', 'throw', 'case',
    'await', 'async', 'yield', 'class', 'super', 'import', 'export', 'default',
    'constructor'
]);

const globals = new Set([
    'window', 'document', 'location', 'history', 'localStorage', 'sessionStorage',
    'console', 'setTimeout', 'setInterval', 'clearTimeout', 'clearInterval',
    'queueMicrotask', 'requestAnimationFrame', 'cancelAnimationFrame',
    'JSON', 'Date', 'Math', 'Promise', 'URL', 'URLSearchParams', 'Map', 'Set',
    'encodeURIComponent', 'decodeURIComponent', 'parseInt', 'parseFloat',
    'isNaN', 'isFinite', 'String', 'Number', 'Boolean', 'Array', 'Object',
    'Error', 'TypeError', 'RegExp', 'Symbol', 'Intl', 'fetch', 'alert',
    'confirm', 'prompt', 'matchMedia', 'CustomEvent', 'Event', 'navigator',
    'performance', 'structuredClone', 'AbortController', 'FormData', 'Blob',
    'SpeechSynthesisUtterance', 'speechSynthesis', 'Audio', 'Image', 'File',
    'FileReader', 'DeviceOrientationEvent', 'IntersectionObserver',
    'MutationObserver', 'ResizeObserver', 'WebSocket', 'XMLHttpRequest'
]);

const calls = new Map();
for (const m of src.matchAll(/(?<![.\w$])([A-Za-z_$][\w$]*)\s*\(/g)) {
    const name = m[1];
    const line = src.slice(0, m.index).split('\n').length;
    if (!calls.has(name)) calls.set(name, line);
}

const unknown = [...calls].filter(([name]) =>
    !declared.has(name) && !params.has(name) && !globals.has(name) && !keywords.has(name));

if (unknown.length > 0) {
    console.log('НАЙДЕНЫ вызовы без объявления:');
    for (const [name, line] of unknown) console.log(`  строка ~${line}: ${name}()`);
    process.exitCode = 1;
}

// ── Вторая проверка: вызов переменной, в которую не клали функцию ─────────────
// Именно эта ошибка и была: `let currentPath = null`, а дальше `currentPath()`.
// На глаз такое не видно, а падает весь запуск приложения.

const variableDecls = new Map();
for (const m of src.matchAll(/(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*([^\n;]*)/g)) {
    variableDecls.set(m[1], m[2]);
}

const looksLikeFunction = (rhs) =>
    /=>/.test(rhs) || /^\s*function\b/.test(rhs) || /\.bind\s*\(/.test(rhs) || /^\s*new\s+Function\b/.test(rhs);

const calledVariables = [...calls].filter(([name]) =>
    variableDecls.has(name) && !looksLikeFunction(variableDecls.get(name)) && !globals.has(name));

if (calledVariables.length > 0) {
    console.log('ВНИМАНИЕ: вызов значения, которое не является функцией:');
    for (const [name, line] of calledVariables) {
        console.log(`  строка ~${line}: ${name}()   объявлено как: ${variableDecls.get(name).trim().slice(0, 60)}`);
    }
    process.exitCode = 1;
}

if (unknown.length === 0 && calledVariables.length === 0) {
    console.log(`OK: необъявленных вызовов нет, вызовов переменных нет (проверено имён: ${calls.size})`);
}
