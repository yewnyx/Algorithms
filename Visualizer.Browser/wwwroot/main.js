// Threads need SharedArrayBuffer, which browsers only allow on cross-origin isolated pages.
// Hosts that cannot send the COOP and COEP headers (GitHub Pages) rely on coi-serviceworker.js,
// which adds them and reloads the page once. Until then, wait instead of loading .NET.
if (globalThis.crossOriginIsolated) {
    const { dotnet } = await import('./_framework/dotnet.js');
    // Start Web Workers up front: a new browser thread takes about 20 ms to start, longer than a
    // whole scan of a small graph, so late workers would get no work. The runtime default is 5.
    const runtime = await dotnet.withConfig({ pthreadPoolInitialSize: 32, pthreadPoolUnusedSize: 8 }).create();
    await runtime.runMain();
} else {
    setTimeout(() => {
        document.querySelector('#out .status').textContent =
            'This page needs cross-origin isolation to run threads, and the browser did not grant it. ' +
            'Try reloading, or a current Chrome, Edge or Firefox outside private browsing.';
    }, 4000);
}
