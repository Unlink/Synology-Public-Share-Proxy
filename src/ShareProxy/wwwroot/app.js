'use strict';
const $ = id => document.getElementById(id);
const icon = name => { const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); const use = document.createElementNS(svg.namespaceURI, 'use'); use.setAttribute('href', '#i-' + name); svg.append(use); svg.setAttribute('aria-hidden', 'true'); return svg; };
const token = /^\/(?:sharing|s)\/([A-Za-z0-9_-]{6,128})\/?$/.exec(location.pathname)?.[1];
const state = { csrf: '', info: null, files: [], selected: new Set(), trail: [], total: 0, ascending: true, queue: [], uploading: false, xhr: null, busy: false, single: false, downloading: false, downloadPoll: null, downloadTimer: null, downloadCookie: '' };
const texts = {
  too_many_files: 'Načítali ste veľa súborov. Otvorte odkaz v novej relácii.',
  too_many_shares: 'V tejto relácii je otvorených priveľa odkazov.',
  unavailable: 'Odkaz bol zrušený, vypršal alebo súbory už nie sú dostupné. Požiadajte vlastníka o nový odkaz.',
  invalid_password: 'Heslo nie je správne. Skúste to znova.',
  password_required: 'Zdieľanie vyžaduje heslo. Otvorte odkaz znova a odomknite ho.',
  request_rejected: 'Relácia sa skončila alebo požiadavka nebola prijatá. Obnovte stránku.',
  account_share_unsupported: 'Tento odkaz vyžaduje účet DSM. Požiadajte vlastníka o verejné zdieľanie.',
  restricted_share_unsupported: 'Tento odkaz má obmedzenie počtu návštev alebo IP adresy. Požiadajte vlastníka o verejný odkaz s časovou expiráciou.',
  operation_rejected: 'Vlastník alebo úložisko túto operáciu nepovolilo. Skontrolujte názov súboru a platnosť odkazu.',
  transfer_too_large: 'Súbor alebo výber prekračuje povolenú veľkosť.',
  size_mismatch: 'Prenos bol prerušený. Skúste súbor odoslať znova.',
  invalid_filename: 'Názov súboru obsahuje nepovolené znaky. Premenujte ho a skúste znova.',
  invalid_uploader: 'Zadajte svoje meno bez lomiek a riadiacich znakov.',
  timeout: 'Prenos alebo úložisko nereaguje. Skúste to znova.',
  rate_limited: 'Príliš veľa požiadaviek. Skúste to znova o minútu.',
  share_busy: 'Práve prebieha iná operácia. Počkajte na jej dokončenie.',
  server_busy: 'Zdieľanie je práve vyťažené. Skúste to o chvíľu.',
  file_not_found: 'Výber už nie je platný. Obnovte zoznam súborov.',
  upload_disabled: 'Nahrávanie je na tomto zdieľaní vypnuté.',
  upload_failed: 'Úložisko nedokázalo uložiť súbor. Skúste to znova alebo kontaktujte vlastníka.',
  password_login_disabled: 'Odomykanie heslom je dočasne vypnuté.',
  upstream_unavailable: 'Zdieľanie sa teraz nedokáže pripojiť k úložisku. Skúste to znova alebo kontaktujte vlastníka.',
  upstream_error: 'Úložisko teraz nedokáže vybaviť požiadavku. Skúste to o chvíľu znova.',
  download_rejected: 'Úložisko neposlalo súbor na stiahnutie. Skúste to znova alebo kontaktujte vlastníka.',
  upstream_redirect_rejected: 'Zdieľanie teraz nie je správne nastavené. Kontaktujte vlastníka odkazu.',
  protocol_error: 'Nepodarilo sa načítať odpoveď úložiska. Skúste to znova alebo kontaktujte vlastníka.',
  network: 'Nepodarilo sa pripojiť. Skontrolujte pripojenie a skúste to znova.'
};
const message = error => (texts[error?.code] || 'Súbory teraz nie sú dostupné. Skúste to o chvíľu znova.')
  + (/^[a-f0-9]{32}$/.test(error?.requestId) && !['invalid_password', 'password_required', 'unavailable'].includes(error.code)
    ? ' Identifikátor chyby: ' + error.requestId + '.' : '');
const size = value => { if (value == null) return '—'; if (!value) return '0 B'; const n = Math.min(Math.floor(Math.log(value) / Math.log(1024)), 4); return new Intl.NumberFormat('sk', { maximumFractionDigits: n ? 1 : 0 }).format(value / 1024 ** n) + ' ' + ['B', 'KB', 'MB', 'GB', 'TB'][n]; };
const date = seconds => seconds ? new Intl.DateTimeFormat('sk', { day: 'numeric', month: 'numeric', year: 'numeric' }).format(new Date(seconds * 1000)) : '—';
function view(name) { for (const id of ['home', 'loading', 'password', 'error', 'share']) $(id).hidden = id !== name; document.body.classList.toggle('single-mode', name === 'share' && state.single); }
function notice(text, failure = false) { $('notice').textContent = text; $('notice').classList.toggle('error-text', failure); }
async function api(path, config = {}) {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...config });
  if (!response.ok) { let data; try { data = await response.json(); } catch { data = { code: 'network' }; } throw { ...data, status: response.status }; }
  return response.status === 204 ? null : response.json();
}
function showError(error) {
  if (error?.code === 'password_required') { view('password'); $('share-password').focus(); return; }
  $('error-title').textContent = [404, 410].includes(error?.status) ? 'Odkaz už nie je dostupný.' : 'Súbory teraz nie sú dostupné.';
  $('error-message').textContent = message(error); view('error');
}
async function loadShare() {
  view('loading');
  try {
    state.info = await api('/api/shares/' + token);
    $('share-title').textContent = state.info.title; document.title = state.info.title + ' · File Share';
    $('description').textContent = state.info.description;
    $('mode-label').textContent = state.info.mode === 'upload' ? 'ŽIADOSŤ O SÚBORY' : 'ZDIEĽANÉ SÚBORY';
    const expires = state.info.expiresAt;
    $('expiry-text').textContent = expires ? 'Dostupné do ' + (/^\d{4}-\d{2}-\d{2}$/.test(expires) ? new Intl.DateTimeFormat('sk').format(new Date(expires + 'T12:00:00')) : expires) : 'Bez časového obmedzenia';
    state.single = state.info.mode === 'download' && state.info.files.length === 1 && !state.info.files[0].isDirectory;
    $('single-file-view').hidden = !state.single;
    $('download-view').hidden = state.info.mode !== 'download' || state.single; $('upload-view').hidden = state.info.mode !== 'upload';
    $('upload-limit').textContent = 'Maximálna veľkosť jedného súboru: ' + size(state.info.maxUploadBytes);
    state.trail = []; state.selected.clear();
    if (state.single) {
      const file = state.info.files[0]; state.files = [file]; state.total = 1;
      $('single-file-name').textContent = file.name;
      $('single-file-meta').textContent = [file.size == null ? '' : 'Veľkosť: ' + size(file.size), file.modified == null ? '' : date(file.modified)].filter(Boolean).join(' · ');
      $('single-file-meta').hidden = !$('single-file-meta').textContent;
      $('single-file-expiry').textContent = $('expiry-text').textContent;
    } else if (state.info.mode === 'download') await loadFiles(false);
    view('share');
  } catch (error) { showError(error); }
}
function listBusy(busy, append = false) {
  state.busy = busy; $('list-loading').hidden = !busy;
  $('list-loading-label').textContent = append ? 'Načítavam ďalšie súbory…' : 'Načítavam priečinok…';
  $('download-view').setAttribute('aria-busy', String(busy));
  $('download-view').classList.toggle('is-loading', busy);
  for (const id of ['refresh', 'load-more', 'root-nav', 'search', 'sort-name', 'select-all']) $(id).disabled = busy;
  for (const button of $('breadcrumbs').querySelectorAll('button')) button.disabled = busy;
  for (const control of $('files').querySelectorAll('button,input')) control.disabled = busy;
  updateSelection();
}
async function loadFiles(append, nextTrail = state.trail) {
  if (state.busy) return;
  listBusy(true, append);
  try {
    const folder = nextTrail.at(-1)?.id;
    const query = new URLSearchParams({ offset: append ? state.files.length : 0 });
    if (folder) query.set('folder', folder);
    const result = await api('/api/shares/' + token + '/files?' + query);
    state.files = append ? [...state.files, ...result.files] : result.files; state.total = result.total; state.trail = nextTrail;
    if (!append) state.selected.clear();
    renderFiles(); renderBreadcrumbs(); notice('');
  } catch (error) { if (!$('share').hidden) notice(message(error), true); else throw error; }
  finally { listBusy(false); }
}
function visibleFiles() {
  const needle = $('search').value.toLocaleLowerCase('sk');
  return state.files.filter(f => f.name.toLocaleLowerCase('sk').includes(needle)).sort((a, b) => Number(b.isDirectory) - Number(a.isDirectory) || (state.ascending ? 1 : -1) * a.name.localeCompare(b.name, 'sk', { numeric: true }));
}
function symbol(file) {
  const wrapper = document.createElement('span');
  wrapper.className = 'file-symbol ' + (file.isDirectory ? 'folder' : file.name.split('.').at(-1).toLowerCase());
  wrapper.append(icon(file.isDirectory ? 'folder' : 'file')); return wrapper;
}
function renderFiles() {
  const files = visibleFiles(); $('files').replaceChildren();
  for (const file of files) {
    const tr = document.createElement('tr'); tr.classList.toggle('selected', state.selected.has(file.id));
    const cbCell = document.createElement('td'); cbCell.className = 'check-col';
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = state.selected.has(file.id); checkbox.setAttribute('aria-label', 'Vybrať ' + file.name);
    checkbox.addEventListener('change', () => { checkbox.checked ? state.selected.add(file.id) : state.selected.delete(file.id); tr.classList.toggle('selected', checkbox.checked); updateSelection(); }); cbCell.append(checkbox);
    const nameCell = document.createElement('td'); const rowName = document.createElement('div'); rowName.className = 'file-name'; rowName.append(symbol(file));
    const name = document.createElement('button'); name.type = 'button'; name.textContent = file.name;
    name.addEventListener('click', async () => { if (state.busy) return; if (file.isDirectory) { $('search').value = ''; await loadFiles(false, [...state.trail, { id: file.id, name: file.name }]); } else download([file.id]); });
    rowName.append(name); nameCell.append(rowName);
    const sizeCell = document.createElement('td'); sizeCell.className = 'size-col'; sizeCell.textContent = file.isDirectory ? 'Priečinok' : size(file.size);
    const dateCell = document.createElement('td'); dateCell.className = 'date-col'; dateCell.textContent = date(file.modified);
    const actionCell = document.createElement('td'); actionCell.className = 'action-col';
    const action = document.createElement('button'); action.className = 'icon-button'; action.setAttribute('aria-label', 'Stiahnuť ' + file.name); action.append(icon('download')); action.addEventListener('click', () => download([file.id])); actionCell.append(action);
    tr.append(cbCell, nameCell, sizeCell, dateCell, actionCell); $('files').append(tr);
  }
  $('empty').hidden = files.length > 0;
  $('empty').textContent = $('search').value ? 'Nenašli sa žiadne zodpovedajúce súbory.' : 'V tomto priečinku nie sú žiadne súbory.';
  const plural = state.files.length === 1 ? 'položka' : state.files.length >= 2 && state.files.length <= 4 ? 'položky' : 'položiek';
  $('file-count').textContent = state.files.length < state.total ? state.files.length + ' z ' + state.total + ' položiek' : state.files.length + ' ' + plural;
  $('load-more').hidden = state.files.length >= state.total;
  $('download-all').disabled = !state.files.length || state.files.length < state.total;
  $('download-all').title = state.files.length < state.total ? 'Najprv načítajte všetky položky.' : '';
  updateSelection();
}
function updateSelection() {
  const files = visibleFiles(); const chosen = files.filter(f => state.selected.has(f.id)).length;
  $('select-all').checked = files.length > 0 && chosen === files.length; $('select-all').indeterminate = chosen > 0 && chosen < files.length;
  $('download-selected').disabled = !state.selected.size || state.busy || state.downloading;
  $('download-all').disabled = !state.files.length || state.files.length < state.total || state.busy || state.downloading;
  $('single-download').disabled = state.downloading;
  $('selection-count').textContent = state.selected.size ? 'Vybraté: ' + state.selected.size : 'Nič nie je vybraté';
}
function renderBreadcrumbs() {
  const root = document.createElement('button'); root.append(icon('folder'), document.createTextNode(state.info.title));
  root.addEventListener('click', () => { $('search').value = ''; loadFiles(false, []); }); $('breadcrumbs').replaceChildren(root);
  state.trail.forEach((folder, index) => { const button = document.createElement('button'); button.textContent = folder.name; button.addEventListener('click', () => { $('search').value = ''; loadFiles(false, state.trail.slice(0, index + 1)); }); $('breadcrumbs').append(icon('arrow'), button); });
}
function finishDownload(started = false) {
  clearInterval(state.downloadPoll); clearTimeout(state.downloadTimer);
  if (state.downloadCookie) document.cookie = state.downloadCookie + '=; Max-Age=0; Path=/; SameSite=Strict' + (location.protocol === 'https:' ? '; Secure' : '');
  state.downloadCookie = ''; state.downloading = false;
  document.body.classList.remove('download-pending');
  $('single-download').lastElementChild.textContent = 'Stiahnuť';
  $('download-status').classList.toggle('complete', started);
  $('download-status').hidden = !started;
  if (started) {
    $('download-status-title').textContent = 'Sťahovanie začalo';
    $('download-status-text').textContent = 'Priebeh nájdete v sťahovaniach prehliadača.';
    state.downloadTimer = setTimeout(() => { $('download-status').hidden = true; }, 5000);
  }
  updateSelection();
}
function download(ids) {
  if (state.busy || state.downloading) return;
  if (ids.length > 100) { notice('Vyberte najviac 100 položiek naraz. Väčší priečinok môžete stiahnuť cez jeho názov v nadradenom priečinku.', true); return; }
  if (!ids.length) return;
  clearTimeout(state.downloadTimer);
  const receipt = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');
  state.downloadCookie = 'download-ready-' + receipt; state.downloading = true;
  document.body.classList.add('download-pending'); updateSelection(); notice('');
  $('single-download').lastElementChild.textContent = 'Pripravujem…';
  $('download-status').hidden = false; $('download-status').classList.remove('complete');
  $('download-status-title').textContent = 'Pripravujem sťahovanie…';
  $('download-status-text').textContent = 'Čakám na súbor z úložiska. Väčší priečinok môže chvíľu trvať.';
  state.downloadPoll = setInterval(() => {
    if (document.cookie.split(';').some(cookie => cookie.trim() === state.downloadCookie + '=1')) finishDownload(true);
  }, 250);
  state.downloadTimer = setTimeout(() => { finishDownload(); notice('Úložisko stále pripravuje sťahovanie. Skontrolujte aj sťahovania prehliadača.'); }, 120000);
  const form = document.createElement('form'); form.method = 'POST'; form.action = '/api/shares/' + token + '/download'; form.target = 'download-frame'; form.hidden = true;
  for (const [key, value] of Object.entries({ csrf: state.csrf, ids: JSON.stringify(ids), receipt })) { const input = document.createElement('input'); input.type = 'hidden'; input.name = key; input.value = value; form.append(input); }
  document.body.append(form); form.submit(); form.remove();
}
$('download-frame').addEventListener('load', () => { try { const text = $('download-frame').contentDocument?.body?.textContent; if (text && state.downloading) { const error = JSON.parse(text); if (error.code) { finishDownload(); notice(message(error), true); } } } catch { /* attachment response is managed by the browser */ } });
$('single-download').addEventListener('click', () => download(state.files.map(f => f.id)));
$('select-all').addEventListener('change', e => { for (const file of visibleFiles()) e.target.checked ? state.selected.add(file.id) : state.selected.delete(file.id); renderFiles(); });
$('search').addEventListener('input', renderFiles);
$('sort-name').addEventListener('click', () => { state.ascending = !state.ascending; $('sort-name').lastElementChild.textContent = state.ascending ? '↑' : '↓'; renderFiles(); });
$('download-selected').addEventListener('click', () => download([...state.selected]));
$('download-all').addEventListener('click', () => download(state.files.map(f => f.id)));
$('refresh').addEventListener('click', () => loadFiles(false)); $('load-more').addEventListener('click', () => loadFiles(true));
$('root-nav').addEventListener('click', () => { $('search').value = ''; loadFiles(false, []); });
$('retry').addEventListener('click', loadShare);
$('show-password').addEventListener('click', () => { const show = $('share-password').type === 'password'; $('share-password').type = show ? 'text' : 'password'; $('show-password').textContent = show ? 'Skryť' : 'Zobraziť'; $('show-password').setAttribute('aria-label', show ? 'Skryť heslo' : 'Zobraziť heslo'); $('show-password').setAttribute('aria-pressed', show); });
$('password-form').addEventListener('submit', async event => {
  event.preventDefault(); $('unlock').disabled = true; $('password-error').textContent = ''; $('share-password').removeAttribute('aria-invalid');
  try { await api('/api/shares/' + token + '/unlock', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Share-CSRF': state.csrf }, body: JSON.stringify({ password: $('share-password').value }) }); $('share-password').value = ''; await loadShare(); }
  catch (error) { $('password-error').textContent = message(error); $('share-password').setAttribute('aria-invalid', 'true'); $('share-password').focus(); }
  finally { $('unlock').disabled = false; }
});
$('open-form').addEventListener('submit', event => { event.preventDefault(); try { const value = $('share-link').value.trim(); const match = /^[A-Za-z0-9_-]{6,128}$/.test(value) ? value : /^\/(?:sharing|s|fsdownload)\/([A-Za-z0-9_-]{6,128})(?:\/|$)/.exec(new URL(value, location.origin).pathname)?.[1]; if (!match) throw Error(); location.assign('/sharing/' + match); } catch { $('home-error').textContent = 'Vložte platný odkaz na zdieľanie.'; } });

function queueFiles(files) {
  if (state.uploading) { notice('Počkajte na dokončenie aktuálneho nahrávania.', true); return; }
  for (const file of files) {
    if (state.queue.length >= 100) { notice('Naraz možno vybrať najviac 100 súborov.', true); break; }
    if (state.queue.some(item => item.file.name === file.name && item.file.size === file.size && item.file.lastModified === file.lastModified)) continue;
    state.queue.push({ file, status: file.size > state.info.maxUploadBytes ? 'too-large' : 'ready', progress: 0, code: file.size > state.info.maxUploadBytes ? 'transfer_too_large' : '' });
  }
  renderQueue();
}
function renderQueue() {
  $('queue').replaceChildren(); $('queue-empty').hidden = state.queue.length > 0; $('queue-count').textContent = state.queue.length;
  state.queue.forEach((item, index) => {
    const li = document.createElement('li'); li.append(symbol({ name: item.file.name, isDirectory: false }));
    const details = document.createElement('div'); details.className = 'queue-file'; const name = document.createElement('strong'); name.textContent = item.file.name;
    const status = document.createElement('small'); status.textContent = size(item.file.size) + ' · ' + ({ ready: 'Pripravené na odoslanie', uploading: 'Nahrávanie ' + item.progress + ' %', verifying: 'Potvrdzujem uloženie…', done: 'Úspešne odoslané', cancelled: 'Zrušené', failed: message(item), 'too-large': message(item) }[item.status]);
    status.classList.toggle('failure', ['failed', 'too-large'].includes(item.status)); details.append(name, status);
    if (['uploading', 'verifying'].includes(item.status)) { const progress = document.createElement('progress'); progress.max = 100; progress.value = item.progress; progress.setAttribute('aria-label', 'Priebeh nahrávania ' + item.file.name); details.append(progress); }
    const remove = document.createElement('button'); remove.className = 'icon-button'; remove.append(icon('close')); remove.setAttribute('aria-label', (item.status === 'uploading' || item.status === 'verifying' ? 'Zrušiť prenos ' : 'Odstrániť ') + item.file.name);
    remove.disabled = state.uploading && !['uploading', 'verifying'].includes(item.status);
    remove.addEventListener('click', () => { if (['uploading', 'verifying'].includes(item.status)) state.xhr?.abort(); else { state.queue.splice(index, 1); renderQueue(); } });
    li.append(details, remove); $('queue').append(li);
  });
  const ready = state.queue.some(item => ['ready', 'failed', 'cancelled'].includes(item.status));
  $('start-upload').disabled = state.uploading || !ready; $('clear-queue').disabled = state.uploading; $('choose-files').disabled = state.uploading;
  $('uploader').disabled = state.uploading;
  $('upload-summary').textContent = state.uploading ? 'Prebieha nahrávanie. Nezatvárajte stránku.' : state.queue.length ? 'Odoslané: ' + state.queue.filter(item => item.status === 'done').length + ' / ' + state.queue.length : 'Súbory budú odoslané vlastníkovi.';
}
function upload(item) {
  return new Promise(resolve => {
    const xhr = new XMLHttpRequest(); state.xhr = xhr; item.status = 'uploading'; item.error = ''; item.requestId = null; renderQueue();
    xhr.open('POST', '/api/shares/' + token + '/upload'); xhr.timeout = 1800000;
    xhr.setRequestHeader('Content-Type', 'application/octet-stream'); xhr.setRequestHeader('X-Share-CSRF', state.csrf);
    xhr.setRequestHeader('X-File-Name', encodeURIComponent(item.file.name)); xhr.setRequestHeader('X-Uploader-Name', encodeURIComponent($('uploader').value.trim()));
    xhr.upload.addEventListener('progress', event => { if (event.lengthComputable) { item.progress = Math.round(event.loaded / event.total * 100); if (item.progress === 100) item.status = 'verifying'; renderQueue(); } });
    xhr.onload = () => { if (xhr.status >= 200 && xhr.status < 300) item.status = 'done'; else { item.status = 'failed'; try { const error = JSON.parse(xhr.responseText); item.code = error.code; item.requestId = error.requestId; } catch { item.code = 'network'; item.requestId = null; } } state.xhr = null; renderQueue(); resolve(); };
    xhr.onerror = () => { item.status = 'failed'; item.code = 'network'; state.xhr = null; renderQueue(); resolve(); };
    xhr.ontimeout = () => { item.status = 'failed'; item.code = 'timeout'; state.xhr = null; renderQueue(); resolve(); };
    xhr.onabort = () => { item.status = 'cancelled'; state.xhr = null; renderQueue(); resolve(); };
    xhr.send(item.file);
  });
}
$('start-upload').addEventListener('click', async () => {
  if (!$('uploader').value.trim()) { $('uploader').setAttribute('aria-invalid', 'true'); $('uploader').focus(); notice('Pred odoslaním vyplňte svoje meno.', true); return; }
  $('uploader').removeAttribute('aria-invalid'); state.uploading = true; notice('');
  const pending = state.queue.filter(item => ['ready', 'failed', 'cancelled'].includes(item.status));
  for (const item of pending) await upload(item);
  state.uploading = false; renderQueue(); notice(state.queue.every(item => item.status === 'done') ? 'Hotovo. Všetky súbory boli úspešne odoslané.' : 'Nahrávanie sa skončilo. Skontrolujte stav jednotlivých súborov.');
});
$('choose-files').addEventListener('click', () => $('file-picker').click());
$('file-picker').addEventListener('change', event => { queueFiles(event.target.files); event.target.value = ''; });
$('clear-queue').addEventListener('click', () => { state.queue = []; renderQueue(); });
let dragDepth = 0;
$('dropzone').addEventListener('dragenter', event => { event.preventDefault(); dragDepth++; $('dropzone').classList.add('drag'); });
$('dropzone').addEventListener('dragover', event => event.preventDefault());
$('dropzone').addEventListener('dragleave', event => { event.preventDefault(); if (!--dragDepth) $('dropzone').classList.remove('drag'); });
$('dropzone').addEventListener('drop', event => { event.preventDefault(); dragDepth = 0; $('dropzone').classList.remove('drag'); queueFiles(event.dataTransfer.files); });
window.addEventListener('dragover', event => event.preventDefault()); window.addEventListener('drop', event => event.preventDefault());
window.addEventListener('beforeunload', event => { if (state.uploading) { event.preventDefault(); event.returnValue = ''; } });
(async () => {
  try { const session = await api('/api/session'); state.csrf = session.csrf; if (token) await loadShare(); else view('home'); }
  catch (error) { showError(error); }
})();
