// Threads need SharedArrayBuffer, which browsers only allow on cross-origin isolated pages.
// Hosts that cannot send the COOP and COEP headers (GitHub Pages) rely on coi-serviceworker.js,
// which adds them and reloads the page once. Where isolation or the threaded start fails, the page
// runs the single-threaded build in single/ instead, which has no Parallel scan. ?single forces it.
const single = new URLSearchParams(location.search).has('single');
const out = document.querySelector('#out');
const status = (text) => { const s = out.querySelector('.status'); if (s) { s.textContent = text; } };
const rendered = () => out.querySelector('canvas') !== null;

async function run(dotnetPath, config) {
    const { dotnet } = await import(dotnetPath);
    const runtime = await dotnet.withConfig(config).create();
    await runtime.runMain();
}

// Drops the service worker, so nothing intercepts requests any more, and reloads into the fallback.
let fallingBack = false;
async function fallBack() {
    if (fallingBack) { return; }
    fallingBack = true;
    status('Threads failed to start. Loading the single-threaded version…');
    const registrations = await navigator.serviceWorker?.getRegistrations() ?? [];
    await Promise.all(registrations.map(r => r.unregister()));
    location.replace(location.pathname + '?single');
}

if (globalThis.crossOriginIsolated && !single) {
    // A failure anywhere before the app has drawn means the threaded start broke.
    addEventListener('unhandledrejection', () => { if (!rendered()) { fallBack(); } });
    setTimeout(() => {
        if (rendered() || fallingBack) { return; }
        const s = out.querySelector('.status');
        s.textContent = 'Still loading. ';
        const a = Object.assign(document.createElement('a'), { href: '?single', textContent: 'Try the single-threaded version' });
        s.append(a, ' if this does not finish.');
    }, 15000);
    // Start Web Workers up front: a new browser thread takes about 20 ms to start, longer than a
    // whole scan of a small graph, so late workers would get no work. The runtime default is 5.
    run('./_framework/dotnet.js', { pthreadPoolInitialSize: 32, pthreadPoolUnusedSize: 8 }).catch(fallBack);
} else if (single && navigator.serviceWorker?.controller) {
    // Opened with ?single while the service worker is still installed: remove it first.
    fallBack();
} else {
    // Give coi-serviceworker time to install and reload the page. If it cannot, run without threads.
    const canIsolate = !single && window.isSecureContext && 'serviceWorker' in navigator;
    setTimeout(() => {
        if (window.coiReloading) { return; }
        run('./single/_framework/dotnet.js', {}).catch(e => status('The app failed to start: ' + e));
    }, canIsolate ? 3000 : 0);
}
