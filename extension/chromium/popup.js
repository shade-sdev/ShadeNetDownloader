const state = document.getElementById('state');
const enabled = document.getElementById('enabled');
const note = document.getElementById('note');
const connect = document.getElementById('connect');

function render(status) {
  enabled.checked = status.localEnabled;

  if (status.connected) {
    state.textContent = status.appVersion ? `Connected · ShadeNet ${status.appVersion}` : 'Connected';
    state.className = 'ok';
    connect.hidden = true;
  } else {
    state.textContent = 'Not connected';
    state.className = 'bad';
    connect.hidden = false;
  }

  let text = '';
  if (status.connected && !status.appEnabled) text = 'Capturing is turned off in the ShadeNet app.';
  else if (!status.connected && status.lastError) text = status.lastError;
  note.textContent = text;
  note.hidden = text === '';
}

function ask(message) {
  return chrome.runtime.sendMessage(message).then(render, () => {
    state.textContent = 'The extension is not responding.';
    state.className = 'bad';
  });
}

enabled.addEventListener('change', () => ask({ type: 'set-enabled', value: enabled.checked }));
connect.addEventListener('click', () => {
  connect.disabled = true;
  state.textContent = 'Connecting…';
  state.className = 'muted';
  ask({ type: 'connect' }).finally(() => { connect.disabled = false; });
});

const media = document.getElementById('media');
const mediaList = document.getElementById('mediaList');

function length(seconds) {
  if (!seconds) return '';
  const s = Math.round(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rest = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${rest}` : `${m}:${rest}`;
}

function renderMedia(result) {
  const items = result?.items ?? [];
  media.hidden = items.length === 0;
  mediaList.replaceChildren();

  for (const item of items) {
    const row = document.createElement('li');
    const what = document.createElement('div');
    what.className = 'what';
    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = item.title || new URL(item.url).hostname;
    name.title = item.url;
    const meta = document.createElement('div');
    meta.className = 'meta';
    const facts = [item.kind === 'tracks' ? 'Playing now' : item.kind === 'page' ? 'YouTube video' : 'Video stream'];
    if ((item.kind === 'tracks' || item.kind === 'page') && item.quality) facts.push(item.quality);
    else if (item.maxHeight) facts.push(`up to ${item.maxHeight}p`);
    if (item.duration) facts.push(length(item.duration));
    if (item.live) facts.push('live: recorded until you stop it');
    if (item.protected) facts.push('protected: not supported');
    meta.textContent = facts.join(' · ');
    what.append(name, meta);

    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = item.live ? 'Record' : 'Download';
    button.disabled = item.protected || (item.kind === 'tracks' && item.live);
    button.addEventListener('click', async () => {
      button.disabled = true;
      const answer = await chrome.runtime.sendMessage({ type: 'download-media', url: item.url }).catch(() => null);
      button.textContent = answer?.accepted ? 'Sent ✓' : 'Failed';
      if (!answer?.accepted) setTimeout(() => { button.textContent = item.live ? 'Record' : 'Download'; button.disabled = false; }, 2500);
    });

    row.append(what, button);
    mediaList.append(row);
  }
}

ask({ type: 'status' });
chrome.runtime.sendMessage({ type: 'list-media' }).then(renderMedia, () => {});
