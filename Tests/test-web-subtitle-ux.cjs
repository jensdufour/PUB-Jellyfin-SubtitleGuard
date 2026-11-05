const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const definition = JSON.parse(fs.readFileSync(path.join(__dirname, '../Web/subtitle-loading.json'), 'utf8'));
const rules = definition.transformations;

async function main() {
    assert.ok(process.argv[2], 'Usage: node Tests/test-web-subtitle-ux.cjs <stock-player-chunk> [callback-output]');
    const source = fs.readFileSync(process.argv[2], 'utf8');
    assert.equal(crypto.createHash('sha256').update(source).digest('hex'), definition.originalSha256);
    let candidate = source;
    for (const rule of rules) {
        assert.equal(candidate.split(rule.SearchText).length - 1, rule.Id === 'ec207336-c61e-4018-a7e9-e678f55a7b3d' ? 2 : 1, rule.Id);
        candidate = candidate.split(rule.SearchText).join(rule.ReplaceText);
    }
    assert.ok(new RegExp(definition.filenamePattern).test('htmlVideoPlayer-plugin.012abc.chunk.js'));
    assert.ok(!new RegExp(definition.filenamePattern).test('main.jellyfin.bundle.js'));
    new vm.Script(candidate);
    assert.equal(crypto.createHash('sha256').update(candidate).digest('hex'), definition.patchedSha256);
    if (process.argv[3]) assert.equal(fs.readFileSync(process.argv[3], 'utf8'), candidate);
    for (const rule of rules) assert.equal(candidate.split(rule.SearchText).join(rule.ReplaceText), candidate, rule.Id);

    let fetcher;
    const methods = vm.runInNewContext(`[${rules[0].ReplaceText.slice(0, rules[0].ReplaceText.lastIndexOf(',{key:'))}]`, {
        AbortController,
        document: { createElement: () => ({ setAttribute() {}, style: {}, remove() { this.removed = true; } }) },
        ne: () => '/Videos/test/Subtitles/2/Stream.ass',
        fetch: (...args) => fetcher(...args)
    });
    const renders = [];
    const statuses = [];
    const video = { parentNode: { appendChild: node => statuses.push(node) } };
    const player = {
        _currentPlayOptions: { mediaSource: { IsRemote: true } },
        renderSsaAss: (...args) => renders.push(args)
    };
    for (const method of methods) player[method.key] = method.value;
    const track = { Index: 2, IsExternal: false };
    const item = { Id: 'test' };
    const content = '[Script Info]\n[Events]\nDialogue: complete';
    let resolve;
    let requests = 0;
    fetcher = () => { requests++; return new Promise(done => { resolve = done; }); };
    const pending = player.prepareAssForDisplay(video, track, item);
    assert.equal(statuses.at(-1).textContent, 'Preparing subtitles...');
    assert.equal(renders.length, 0);
    resolve({ ok: true, text: async () => content });
    await pending;
    assert.equal(renders.length, 1);
    assert.equal(renders[0][1].SubtitleGuardContent, content);
    assert.equal(track.SubtitleGuardContent, undefined);
    assert.ok(statuses.at(-1).removed);

    const stale = player.prepareAssForDisplay(video, track, item);
    const staleLoad = player._subtitleGuardAssLoad;
    player.cancelAssPreparation();
    resolve({ ok: true, text: async () => content });
    await stale;
    assert.equal(renders.length, 1);
    assert.ok(staleLoad.controller.signal.aborted);
    assert.ok(staleLoad.status.removed);

    fetcher = async () => { requests++; return { ok: false }; };
    await player.prepareAssForDisplay(video, track, item);
    assert.equal(statuses.at(-1).textContent, 'Subtitles unavailable');
    assert.equal(renders.length, 1);
    fetcher = async () => ({ ok: true, text: async () => '<html>not subtitles</html>' });
    await player.prepareAssForDisplay(video, track, item);
    assert.equal(statuses.at(-1).textContent, 'Subtitles unavailable');
    assert.equal(renders.length, 1);

    player.cancelAssPreparation();
    const before = requests;
    await player.prepareAssForDisplay(video, { ...track, IsExternal: true }, item);
    player._currentPlayOptions.mediaSource.IsRemote = false;
    await player.prepareAssForDisplay(video, track, item);
    assert.equal(requests, before);
    assert.equal(renders.length, 3);
    assert.equal(renders.at(-1)[1], track);

    const start = candidate.indexOf('{key:"renderSsaAss",value:') + '{key:"renderSsaAss",value:'.length;
    const end = candidate.indexOf('},{key:"renderPgs"', start);
    assert.ok(start > 0 && end > start);
    const constructed = [];
    let finishFonts;
    function Renderer(options) { constructed.push(options); }
    const webpack = { e: async () => {}, t: async () => ({ default: Renderer }) };
    const renderer = vm.runInNewContext(`(${candidate.slice(start, end)})`, {
        n: webpack,
        f: { tF: { getApiClient: () => ({
            getUrl: value => value,
            accessToken: () => 'synthetic',
            getNamedConfiguration: () => new Promise(done => { finishFonts = done; }),
            getJSON: async () => []
        }) } },
        k: { appRouter: { baseUrl: () => '' } },
        $: async value => value,
        L: values => values,
        G: () => {}, le: {},
        ne: () => { throw new Error('Prepared captions must not start another request'); }
    });
    player._currentPlayOptions.mediaSource.MediaStreams = [{ Type: 'Video', ReferenceFrameRate: 24 }];
    for (const fallbackFonts of [false, true]) {
        const load = {};
        player._subtitleGuardAssLoad = load;
        const prepared = { ...track, SubtitleGuardContent: content, SubtitleGuardLoad: load };
        const count = constructed.length;
        renderer.call(player, video, prepared, item);
        await new Promise(setImmediate);
        finishFonts({ EnableFallbackFont: fallbackFonts });
        await new Promise(setImmediate);
        assert.equal(constructed.length, count + 1);
        assert.equal(constructed.at(-1).subContent, content);
        assert.equal(constructed.at(-1).subUrl, undefined);
        renderer.call(player, video, prepared, item);
        await new Promise(setImmediate);
        player._subtitleGuardAssLoad = null;
        finishFonts({ EnableFallbackFont: fallbackFonts });
        await new Promise(setImmediate);
        assert.equal(constructed.length, count + 1, 'Late renderer setup must discard a stale track');
    }
    console.log('PASS plugin-owned stock chunk syntax/hash/idempotence; loading/error status; deferred attachment; fetch/renderer cancellation; both font paths; local/external bypass; no automatic retry or playback controls');
}

main().catch(error => { console.error(error); process.exitCode = 1; });