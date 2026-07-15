let allPlayers = [];
let currentPlayerId = null;

function showPlayerEdit(id) {
  currentPlayerId = id;
  document.getElementById('view-player').classList.add('active');
  document.getElementById('overlay').classList.add('active');
  document.body.classList.add('modal-open');
  loadPlayer(id);
}

function closePlayerEdit() {
  currentPlayerId = null;
  document.getElementById('view-player').classList.remove('active');
  document.getElementById('overlay').classList.remove('active');
  document.body.classList.remove('modal-open');
}

async function exec(action) {
  const btn = event.target;
  btn.disabled = true;
  const toast = document.getElementById('toast');
  const msg = document.getElementById('toastMsg');
  try {
    const res = await fetch('/api/' + action, { method: 'POST' });
    const data = await res.json();
    toast.className = 'toast ' + (res.ok ? 'success' : 'error');
    msg.textContent = data.message || data.error || 'Done';
  } catch(e) {
    toast.className = 'toast error';
    msg.textContent = e.message;
  }
  toast.style.display = 'block';
  setTimeout(() => { toast.style.display = 'none'; btn.disabled = false; }, 3000);
}

async function loadPlayers() {
  const body = document.getElementById('playersBody');
  const count = document.getElementById('playerCount');
  body.innerHTML = '<tr><td colspan="6" style="color:#888;">Loading...</td></tr>';
  try {
    const res = await fetch('/api/players');
    const data = await res.json();
    allPlayers = data.players || [];
    count.textContent = allPlayers.length + ' player(s) found';
    renderPlayers(allPlayers);
  } catch(e) {
    body.innerHTML = '<tr><td colspan="6" style="color:#f44;">Failed to load players</td></tr>';
    count.textContent = 'Error loading players';
  }
}

function renderPlayers(players) {
  const body = document.getElementById('playersBody');
  if (!players.length) {
    body.innerHTML = '<tr><td colspan="6" style="color:#888;">No players found</td></tr>';
    return;
  }
  body.innerHTML = players.map(p => '<tr class="clickable" onclick="showPlayerEdit(' + p.id + ')">' +
    '<td>' + p.id + '</td>' +
    '<td>' + p.steam_id + '</td>' +
    '<td>' + esc(p.nickname) + '</td>' +
    '<td><span class="role-badge role-' + p.role + '">' + p.role + '</span></td>' +
    '<td>' + esc(p.region || '') + '</td>' +
    '<td><span style="color:' + (p.online ? '#4caf50' : '#888') + '">' + (p.online ? '● Online' : '○ Offline') + '</span></td>' +
    '</tr>').join('');
}

function filterPlayers() {
  const q = document.getElementById('searchInput').value.toLowerCase();
  const filtered = allPlayers.filter(p =>
    p.nickname.toLowerCase().includes(q) ||
    p.id.toString().includes(q) ||
    p.steam_id.toString().includes(q) ||
    (p.region || '').toLowerCase().includes(q)
  );
  renderPlayers(filtered);
}

async function loadPlayer(id) {
  document.getElementById('playerTitle').textContent = 'Edit Player #' + id;
  try {
    const res = await fetch('/api/players/' + id);
    if (!res.ok) throw new Error('Player not found');
    const p = await res.json();
    document.getElementById('f-id').value = p.id;
    document.getElementById('f-steam').value = p.steam_id;
    document.getElementById('f-nickname').value = p.nickname;
    document.getElementById('f-role').value = p.role_id;
    document.getElementById('f-mmr').value = p.rating_mean;
    document.getElementById('f-rating-dev').value = p.rating_deviation;

    const now = Date.now();
    const mmStatus = document.getElementById('mmBanStatus');
    const mmBanned = p.matchmaker_ban_end != null && p.matchmaker_ban_end > now;
    mmStatus.textContent = mmBanned ? 'Banned until ' + new Date(p.matchmaker_ban_end).toLocaleString() : 'Not banned';
    mmStatus.className = 'ban-status ' + (mmBanned ? 'banned' : 'clear');

    const gyStatus = document.getElementById('gyBanStatus');
    const gyPermanent = p.graveyard_permanent === true;
    const gyBanned = gyPermanent || (p.graveyard_leave_time != null && p.graveyard_leave_time > now);
    gyStatus.textContent = gyPermanent ? 'Permanently banned'
      : gyBanned ? 'Banned until ' + new Date(p.graveyard_leave_time).toLocaleString()
      : 'Not banned';
    gyStatus.className = 'ban-status ' + (gyBanned ? 'banned' : 'clear');

    document.getElementById('f-mm-ban-amount').value = '';
    document.getElementById('f-gy-ban-amount').value = '';
    document.getElementById('f-gy-permanent').checked = false;
  } catch(e) {
    showToast('error', 'Failed to load player: ' + e.message);
  }
}

async function updatePlayer(body, successMsg) {
  try {
    const res = await fetch('/api/players/' + currentPlayerId, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const data = await res.json();
    if (res.ok) {
      showToast('success', successMsg || data.message || 'Updated');
      loadPlayer(currentPlayerId);
    } else {
      showToast('error', data.error || 'Update failed');
    }
  } catch(e) {
    showToast('error', e.message);
  }
}

const UNIT_MS = { minutes: 60 * 1000, hours: 60 * 60 * 1000, days: 24 * 60 * 60 * 1000 };

function durationMs(amountId, unitId) {
  const amount = parseFloat(document.getElementById(amountId).value);
  if (!amount || amount <= 0) return null;
  return amount * UNIT_MS[document.getElementById(unitId).value];
}

function saveRoleMmr() {
  updatePlayer({
    role_id: parseInt(document.getElementById('f-role').value),
    rating_mean: parseFloat(document.getElementById('f-mmr').value),
    rating_deviation: parseFloat(document.getElementById('f-rating-dev').value)
  }, 'Role & MMR updated');
}

function banMatchmaking() {
  const ms = durationMs('f-mm-ban-amount', 'f-mm-ban-unit');
  if (ms == null) { showToast('error', 'Enter a ban duration'); return; }
  updatePlayer({ matchmaker_ban_end: Date.now() + ms }, 'Player banned from matchmaking');
}

function unbanMatchmaking() {
  updatePlayer({ matchmaker_ban_end: null }, 'Matchmaking ban lifted');
}

function banGraveyard() {
  const permanent = document.getElementById('f-gy-permanent').checked;
  const ms = permanent ? null : durationMs('f-gy-ban-amount', 'f-gy-ban-unit');
  if (!permanent && ms == null) { showToast('error', 'Enter a ban duration or mark permanent'); return; }
  updatePlayer({
    graveyard_permanent: permanent,
    graveyard_leave_time: permanent ? null : Date.now() + ms
  }, 'Player sent to graveyard');
}

function unbanGraveyard() {
  updatePlayer({ graveyard_permanent: false, graveyard_leave_time: null }, 'Graveyard ban lifted');
}

function showToast(type, msg) {
  const toast = document.getElementById('toast');
  const el = document.getElementById('toastMsg');
  toast.className = 'toast ' + type;
  el.textContent = msg;
  toast.style.display = 'block';
  setTimeout(() => { toast.style.display = 'none'; }, 3000);
}

function esc(s) {
  const d = document.createElement('div');
  d.textContent = s;
  return d.innerHTML;
}

async function refreshStatus() {
  try {
    const res = await fetch('/api/status');
    const data = await res.json();
    document.getElementById('status').textContent =
      'Running since ' + data.uptime + ' | Players: ' + data.player_count + ' | Regions: ' + data.region_count;
  } catch { /* ignore */ }
}
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && currentPlayerId != null) closePlayerEdit();
});

setInterval(refreshStatus, 5000);
refreshStatus();
loadPlayers();
