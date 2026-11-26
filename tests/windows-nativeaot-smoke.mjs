import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readdir, readFile } from 'node:fs/promises';
import { join, relative, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const publish = resolve(process.argv[2]);
const app = spawn(join(publish, 'VISaveManager.exe'), ['--test'], {
    stdio: 'inherit',
    env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9315' },
});
let socket;

async function waitFor(action, description) {
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline && app.exitCode === null) {
        const result = await action();
        if (result) return result;
        await delay(250);
    }
    throw new Error(`Timed out waiting for ${description}; application exit code ${app.exitCode}`);
}

try {
    let lastTargets;
    const url = await waitFor(async () => {
        const response = await fetch('http://127.0.0.1:9315/json/list', {
            signal: AbortSignal.timeout(1000),
        }).catch(() => null);
        if (!response?.ok) return null;
        const pages = await response.json();
        const targets = pages.map(page => page.url).join(', ');
        if (targets !== lastTargets) console.log('WebView2 targets:', targets);
        lastTargets = targets;
        return pages.find(page => page.url.startsWith('file:') && page.url.endsWith('/ui/index.html'))?.webSocketDebuggerUrl;
    }, 'the bundled WebView2 page');

    socket = new WebSocket(url);
    await new Promise((opened, failed) => {
        socket.addEventListener('open', opened, { once: true });
        socket.addEventListener('error', failed, { once: true });
    });
    let sequence = 0;

    function evaluate(expression) {
        return new Promise((response, failed) => {
            const id = ++sequence;
            const timer = setTimeout(() => {
                socket.removeEventListener('message', onMessage);
                failed(new Error(`WebView2 did not complete: ${expression}`));
            }, 5000);

            function onMessage(event) {
                const reply = JSON.parse(event.data);
                if (reply.id !== id) return;
                clearTimeout(timer);
                socket.removeEventListener('message', onMessage);
                if (reply.error || reply.result.exceptionDetails) {
                    failed(new Error(JSON.stringify(reply)));
                    return;
                }
                response(reply.result.result.value);
            }

            socket.addEventListener('message', onMessage);
            socket.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }));
        });
    }

    const loadButton = `document.querySelector('[data-sv="load"][data-kind="team"][data-name="Boat"]')`;
    await waitFor(() => evaluate(`Boolean(${loadButton}) && !document.querySelector('#testTag').hidden`), 'the test save list');
    await evaluate(`${loadButton}.click(); ${loadButton}.click();`);
    await waitFor(() => evaluate(`document.querySelector('#svNow').textContent.includes('Current game save')`), 'the loaded test save');

    const fixture = join(publish, 'team-saves', 'Boat');
    const destination = join(process.env.LOCALAPPDATA, 'VISaveManager-test', 'victory-data', '7952987A', 'saves');
    const files = (await readdir(fixture, { recursive: true, withFileTypes: true }))
        .filter(entry => entry.isFile() && entry.name !== 'about.txt');
    assert.ok(files.length > 0);
    for (const file of files) {
        const path = join(relative(fixture, file.parentPath), file.name);
        assert.deepEqual(await readFile(join(destination, path)), await readFile(join(fixture, path)));
    }
    console.log('Windows NativeAOT loaded the bundled page and restored the test save byte-for-byte.');
} finally {
    socket?.close();
    if (app.exitCode === null) app.kill();
}
