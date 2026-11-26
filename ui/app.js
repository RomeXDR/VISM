// VI Save Manager page. Talks to the host (Program.cs) with web messages.
const $ = (s) => document.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const send = (m) => {
  if (window.chrome?.webview) window.chrome.webview.postMessage(m);
  else window.webkit.messageHandlers.vism.postMessage(JSON.stringify(m));
};
const fmtSize = (b) => b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
let last = null;

function render(m) {
  last = m;
  $('#testTag').hidden = !m.test;
  $('#chooseSaveFolder').hidden = !m.canChooseSaveFolder;
  const savePath = $('#savePath');
  savePath.hidden = !m.canChooseSaveFolder;
  savePath.textContent = m.saveDirectory || 'Select the game’s saves folder to load a save.';
  $('#svNow').innerHTML = m.found
    ? `<div class="sv-row"><div><b>Current game save</b><span>Last written ${esc(m.newest)} · ${fmtSize(m.size)}</span></div>
        ${m.running ? '<span class="sv-run">game running: loading is off until you close it</span>' : ''}
        <button class="ghost small" data-sv="share" data-kind="current" title="Make a zip of it to send to someone">Share</button></div>`
    : `<div class="sv-row"><div><b>No save yet</b><span>Play a VIctory build until the game autosaves, or load a saved spot below.</span></div></div>`;
  const note = $('#svNote');
  note.textContent = m.note || '';
  note.classList.toggle('bad', m.ok === false);
  document.querySelectorAll('[data-sv="fresh"]').forEach((b) => { b.disabled = !!m.running; });

  const row = (s, kind) => `<li class="sv-item${s.auto ? ' auto' : ''}${kind === 'team' ? ' team' : ''}">
      <div class="sv-nm"><b>${esc(s.name)}</b><span>${s.note ? esc(s.note) + ' · ' : ''}game saved ${esc(s.gameTime)} · ${fmtSize(s.size)}</span></div>
      <div class="acts">
        <button class="btn small" data-sv="load" data-kind="${kind}" data-name="${esc(s.name)}" ${m.running ? 'disabled title="Close the game first"' : ''}>Load</button>
        <button class="ghost small" data-sv="share" data-kind="${kind}" data-name="${esc(s.name)}" title="Make a zip of it to send to someone">Share</button>
        ${kind === 'spot' ? `<button class="ghost small danger" data-sv="delete" data-name="${esc(s.name)}">Delete</button>` : ''}
      </div></li>`;
  const mine = m.spots.filter((s) => !s.auto), autos = m.spots.filter((s) => s.auto);
  $('#svList').innerHTML =
    (m.team.length ? `<div class="ogroup">Team saves (come with the app)</div><ul class="sv-list">${m.team.map((s) => row(s, 'team')).join('')}</ul>` : '')
    + `<div class="ogroup">Your spots and imports</div>`
    + (mine.length ? `<ul class="sv-list">${mine.map((s) => row(s, 'spot')).join('')}</ul>` : '<p class="sv-empty">None yet. Name your current save above, or import one.</p>')
    + (autos.length ? `<div class="ogroup">Automatic backups (made before each load, the last 5)</div><ul class="sv-list">${autos.map((s) => row(s, 'spot')).join('')}</ul>` : '');
}

// two clicks for anything that deletes or replaces
function armed(btn, label) {
  if (btn.dataset.armed) return true;
  btn.dataset.armed = '1'; const old = btn.textContent; btn.textContent = label; btn.classList.add('armed');
  setTimeout(() => { if (btn.isConnected) { delete btn.dataset.armed; btn.textContent = old; btn.classList.remove('armed'); } }, 4000);
  return false;
}

document.addEventListener('click', (e) => {
  if (e.target.closest('#importBtn')) { $('#svNote').textContent = 'Pick the zip file(s)…'; return send({ cmd: 'import' }); }
  if (e.target.closest('#openFolder')) return send({ cmd: 'openFolder' });
  if (e.target.closest('#chooseSaveFolder')) return send({ cmd: 'chooseSaveFolder' });
  const b = e.target.closest('[data-sv]');
  if (!b || b.disabled) return;
  const { sv, kind, name } = b.dataset;
  if (sv === 'load' && armed(b, 'Replace current?')) { $('#svNote').textContent = 'Loading…'; send({ cmd: 'load', name, kind }); }
  if (sv === 'delete' && armed(b, 'Sure?')) send({ cmd: 'delete', name });
  if (sv === 'fresh' && armed(b, 'Clear the story?')) { $('#svNote').textContent = 'Clearing…'; send({ cmd: 'fresh' }); }
  if (sv === 'share') send({ cmd: 'share', name, kind });
});
$('#svForm').addEventListener('submit', (e) => {
  e.preventDefault();
  const n = $('#svName').value.trim();
  if (!n) { $('#svNote').textContent = 'Give the save a name first.'; return; }
  send({ cmd: 'snap', name: n });
  $('#svName').value = '';
});
// a newer release on GitHub: a banner with a Download button (Later hides it until the next version)
function showUpdate(u) {
  try { if (localStorage.getItem('skip') === u.version) return; } catch { }
  $('#upTitle').textContent = `Version ${u.version} is out`;
  $('#upNotes').textContent = u.notes ? u.notes.split('\n')[0] : 'Download it, unzip it over the old folder or anywhere new.';
  $('#upGet').onclick = () => send({ cmd: 'openRelease', url: u.url });
  $('#upLater').onclick = () => {
    try { localStorage.setItem('skip', u.version); } catch { }
    send({ cmd: 'skipUpdate', version: u.version });
    $('#update').hidden = true;
  };
  $('#update').hidden = false;
}
window.vismReceive = (message) => {
  if (message?.type === 'saves') render(message);
  if (message?.type === 'update') showUpdate(message);
};
window.chrome?.webview?.addEventListener('message', (e) => window.vismReceive(e.data));
send({ cmd: 'checkUpdate' });
setInterval(() => send({ cmd: 'checkUpdate' }), 6 * 3600 * 1000);
// the game may start or stop, or autosave, while the window is open
setInterval(() => { if (!document.hidden) send({ cmd: 'list' }); }, 5000);
send({ cmd: 'list' });
