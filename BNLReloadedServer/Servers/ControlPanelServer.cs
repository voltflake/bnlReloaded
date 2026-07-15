using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BNLReloadedServer.Database;
using BNLReloadedServer.Servers;
namespace BNLReloadedServer.ControlPanel;

public sealed class ControlPanelServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly MasterServer? _masterServer;
    private readonly RegionServer _regionServer;
    private readonly RegionClient _regionClient;
    private readonly MatchServer _matchServer;
    private readonly CatalogueStore _catalogueStore;
    private readonly ServerCatalogue _serverCatalogue;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private Task? _listenTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ControlPanelServer(
        string prefix,
        MasterServer? masterServer,
        RegionServer regionServer,
        RegionClient regionClient,
        MatchServer matchServer,
        CatalogueStore catalogueStore,
        ServerCatalogue serverCatalogue)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _masterServer = masterServer;
        _regionServer = regionServer;
        _regionClient = regionClient;
        _matchServer = matchServer;
        _catalogueStore = catalogueStore;
        _serverCatalogue = serverCatalogue;
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            Console.WriteLine($"Control panel listening on {string.Join(", ", _listener.Prefixes)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to start control panel: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        ((IDisposable)_listener).Dispose();
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(ctx);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Control panel error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (method == "GET" && path == "/")
            {
                await ServeHtml(ctx);
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                await ServeStatus(ctx);
                return;
            }

            if (method == "POST" && path == "/api/restart")
            {
                await ExecuteAction(ctx, RestartServers);
                return;
            }

            if (method == "POST" && path == "/api/refreshCdb")
            {
                await ExecuteAction(ctx, () => RefreshCatalogue(false));
                return;
            }

            if (method == "POST" && path == "/api/refreshCdbLoad")
            {
                await ExecuteAction(ctx, () => RefreshCatalogue(true));
                return;
            }

            if (method == "GET" && path == "/api/players")
            {
                await ServePlayerList(ctx);
                return;
            }

            if (path.StartsWith("/api/players/") && uint.TryParse(path["/api/players/".Length..], out var playerId))
            {
                if (method == "GET")
                {
                    await ServePlayerDetail(ctx, playerId);
                    return;
                }
                if (method == "POST")
                {
                    await HandlePlayerUpdate(ctx, playerId);
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await WriteJson(ctx, new { error = ex.Message });
        }
    }

    private static async Task ServeHtml(HttpListenerContext ctx)
    {
        var html = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BNL Reloaded - Control Panel</title>
<style>
  *, *::before, *::after { box-sizing: border-box; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: #0f0f1a; color: #e0e0e0; margin: 0; padding: 2rem;
  }
  h1 { color: #fff; margin-bottom: 0.5rem; }
  .status { color: #aaa; font-size: 0.9rem; margin-bottom: 2rem; }
  .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 1rem; max-width: 800px; }
  .card {
    background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 8px; padding: 1.5rem;
    transition: border-color 0.2s;
  }
  .card:hover { border-color: #4a4a8a; }
  .card h3 { margin: 0 0 0.5rem; color: #fff; font-size: 1rem; }
  .card p { margin: 0 0 1rem; color: #999; font-size: 0.85rem; }
  button {
    background: #3a3a6a; color: #fff; border: none; border-radius: 4px; padding: 0.6rem 1.2rem;
    font-size: 0.85rem; cursor: pointer; transition: background 0.2s; width: 100%;
  }
  button:hover { background: #5a5a9a; }
  button:disabled { opacity: 0.5; cursor: not-allowed; }
  .toast {
    position: fixed; bottom: 2rem; right: 2rem; background: #1a1a2e; border: 1px solid #3a3a6a;
    border-radius: 8px; padding: 1rem 1.5rem; display: none; z-index: 100;
  }
  .toast.success { border-color: #4caf50; }
  .toast.error { border-color: #f44336; }
  pre { margin: 0; font-size: 0.8rem; color: #ccc; }
  .view { display: none; }
  .view.active { display: block; }
  .back-btn { background: #2a2a4a; margin-bottom: 1rem; width: auto; padding: 0.4rem 1rem; }
  .back-btn:hover { background: #3a3a6a; }
  table { width: 100%; border-collapse: collapse; margin-top: 1rem; }
  th, td { text-align: left; padding: 0.6rem 0.8rem; border-bottom: 1px solid #2a2a4a; }
  th { color: #aaa; font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.05em; }
  tr.clickable { cursor: pointer; transition: background 0.15s; }
  tr.clickable:hover { background: #1a1a2e; }
  .role-badge { display: inline-block; padding: 0.15rem 0.5rem; border-radius: 3px; font-size: 0.75rem; }
  .role-User { background: #2a2a4a; color: #aaa; }
  .role-Moderator { background: #2a4a2a; color: #8f8; }
  .role-Admin { background: #4a3a1a; color: #ff8; }
  .role-Core { background: #4a1a1a; color: #f88; }
  .form-group { margin-bottom: 1rem; }
  .form-group label { display: block; color: #aaa; font-size: 0.8rem; margin-bottom: 0.3rem; }
  .form-group input, .form-group select {
    background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 4px; color: #e0e0e0;
    padding: 0.5rem 0.7rem; font-size: 0.9rem; width: 100%; max-width: 400px;
  }
  .form-group input:focus, .form-group select:focus { outline: none; border-color: #5a5a9a; }
  .form-row { display: flex; gap: 1rem; flex-wrap: wrap; }
  .form-row .form-group { flex: 1; min-width: 150px; }
  .inline-group { display: flex; gap: 1rem; align-items: flex-end; }
  .inline-group .form-group { margin-bottom: 0; }
  .save-btn { background: #4caf50; max-width: 200px; }
  .save-btn:hover { background: #5dbf61; }
  .search-input {
    background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 4px; color: #e0e0e0;
    padding: 0.5rem 0.7rem; font-size: 0.9rem; width: 100%; max-width: 400px; margin-bottom: 1rem;
  }
  .player-count { color: #888; font-size: 0.85rem; margin-bottom: 0.5rem; }
  .checkbox-group input[type="checkbox"] { width: auto; }
</style>
</head>
<body>
<h1>BNL Reloaded</h1>
<div class="status" id="status">Loading...</div>

<div class="view active" id="view-home">
  <div class="cards">
    <div class="card">
      <h3>Restart Server</h3>
      <p>Restart all server connections</p>
      <button onclick="exec('restart')">Restart</button>
    </div>
    <div class="card">
      <h3>Refresh Catalogue</h3>
      <p>Refresh card catalogue from cache</p>
      <button onclick="exec('refreshCdb')">Refresh</button>
    </div>
    <div class="card">
      <h3>Reload &amp; Refresh</h3>
      <p>Reload catalogue from DB then refresh</p>
      <button onclick="exec('refreshCdbLoad')">Reload &amp; Refresh</button>
    </div>
    <div class="card">
      <h3>Player Editor</h3>
      <p>View and edit player data</p>
      <button onclick="showPlayers()">Open</button>
    </div>
  </div>
</div>

<div class="view" id="view-players">
  <button class="back-btn" onclick="showHome()">&larr; Back</button>
  <h2>Players</h2>
  <div class="player-count" id="playerCount">Loading...</div>
  <input class="search-input" id="searchInput" type="text" placeholder="Filter by name, ID, or Steam ID..." oninput="filterPlayers()">
          <table>
            <thead><tr><th>ID</th><th>Steam ID</th><th>Nickname</th><th>Role</th><th>Region</th><th>Status</th></tr></thead>
            <tbody id="playersBody"></tbody>
          </table>
</div>

<div class="view" id="view-player">
  <button class="back-btn" onclick="showPlayers()">&larr; Back</button>
  <h2 id="playerTitle">Edit Player</h2>
  <div style="max-width: 700px;">
    <div class="form-row">
      <div class="form-group">
        <label>Player ID</label>
        <input id="f-id" type="text" readonly>
      </div>
      <div class="form-group">
        <label>Steam ID</label>
        <input id="f-steam" type="text" readonly>
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>Nickname</label>
        <input id="f-nickname" type="text">
      </div>
      <div class="form-group">
        <label>Role</label>
        <select id="f-role">
          <option value="1">User</option>
          <option value="2">Moderator</option>
          <option value="3">Admin</option>
          <option value="4">Core</option>
        </select>
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>Region</label>
        <input id="f-region" type="text" placeholder="e.g. EU, US">
      </div>
      <div class="form-group">
        <label>Looking for Friends</label>
        <input id="f-looking-friends" type="checkbox" class="checkbox-group">
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>Rating Mean</label>
        <input id="f-rating-mean" type="number" step="0.1">
      </div>
      <div class="form-group">
        <label>Rating Deviation</label>
        <input id="f-rating-dev" type="number" step="0.1">
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>League Tier</label>
        <input id="f-league-tier" type="number">
      </div>
      <div class="form-group">
        <label>League Division</label>
        <input id="f-league-div" type="number">
      </div>
      <div class="form-group">
        <label>League Points</label>
        <input id="f-league-pts" type="number">
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>Tutorial Tokens</label>
        <input id="f-tokens" type="number">
      </div>
      <div class="form-group">
        <label>Matchmaker Ban End (ms epoch)</label>
        <input id="f-ban-end" type="text" placeholder="null or unix ms timestamp">
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>Graveyard Permanent</label>
        <select id="f-graveyard-perm">
          <option value="">Same (unchanged)</option>
          <option value="true">True</option>
          <option value="false">False</option>
        </select>
      </div>
      <div class="form-group">
        <label>Graveyard Leave Time (ms epoch)</label>
        <input id="f-graveyard-leave" type="text" placeholder="null or unix ms timestamp">
      </div>
    </div>
    <div class="form-row">
      <div class="form-group">
        <label>&nbsp;</label>
        <button class="save-btn" onclick="savePlayer()">Save Changes</button>
      </div>
    </div>
  </div>
</div>

<div class="toast" id="toast"><pre id="toastMsg"></pre></div>
<script>
let allPlayers = [];
let currentPlayerId = null;

function showHome() {
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.getElementById('view-home').classList.add('active');
  refreshStatus();
}

function showPlayers() {
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.getElementById('view-players').classList.add('active');
  loadPlayers();
}

function showPlayerEdit(id) {
  currentPlayerId = id;
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.getElementById('view-player').classList.add('active');
  loadPlayer(id);
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
    document.getElementById('f-region').value = p.region || '';
    document.getElementById('f-looking-friends').checked = p.looking_for_friends;
    document.getElementById('f-rating-mean').value = p.rating_mean;
    document.getElementById('f-rating-dev').value = p.rating_deviation;
    document.getElementById('f-league-tier').value = p.league ? p.league.tier : '';
    document.getElementById('f-league-div').value = p.league ? p.league.division : '';
    document.getElementById('f-league-pts').value = p.league ? p.league.points : '';
    document.getElementById('f-tokens').value = p.tutorial_tokens;
    document.getElementById('f-ban-end').value = p.matchmaker_ban_end != null ? p.matchmaker_ban_end : '';
    document.getElementById('f-graveyard-perm').value = p.graveyard_permanent === true ? 'true' : p.graveyard_permanent === false ? 'false' : '';
    document.getElementById('f-graveyard-leave').value = p.graveyard_leave_time != null ? p.graveyard_leave_time : '';
  } catch(e) {
    showToast('error', 'Failed to load player: ' + e.message);
  }
}

async function savePlayer() {
  const body = {
    nickname: document.getElementById('f-nickname').value,
    role_id: parseInt(document.getElementById('f-role').value),
    region: document.getElementById('f-region').value || null,
    looking_for_friends: document.getElementById('f-looking-friends').checked,
    rating_mean: parseFloat(document.getElementById('f-rating-mean').value),
    rating_deviation: parseFloat(document.getElementById('f-rating-dev').value),
    tutorial_tokens: parseInt(document.getElementById('f-tokens').value) || 0
  };

  const lt = document.getElementById('f-league-tier').value;
  const ld = document.getElementById('f-league-div').value;
  const lp = document.getElementById('f-league-pts').value;
  if (lt || ld || lp) {
    body.league_tier = parseInt(lt) || 0;
    body.league_division = parseInt(ld) || 0;
    body.league_points = parseInt(lp) || 0;
  }

  const banEnd = document.getElementById('f-ban-end').value.trim();
  body.matchmaker_ban_end = banEnd ? parseInt(banEnd) : null;

  const gp = document.getElementById('f-graveyard-perm').value;
  if (gp === 'true') body.graveyard_permanent = true;
  else if (gp === 'false') body.graveyard_permanent = false;

  const gl = document.getElementById('f-graveyard-leave').value.trim();
  body.graveyard_leave_time = gl ? parseInt(gl) : null;

  try {
    const res = await fetch('/api/players/' + currentPlayerId, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const data = await res.json();
    if (res.ok) {
      showToast('success', 'Player updated');
    } else {
      showToast('error', data.error || 'Update failed');
    }
  } catch(e) {
    showToast('error', e.message);
  }
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
setInterval(refreshStatus, 5000);
refreshStatus();
</script>
</body>
</html>
""";

        ctx.Response.ContentType = "text/html; charset=utf-8";
        var buf = System.Text.Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.OutputStream.Close();
    }

    private async Task ServeStatus(HttpListenerContext ctx)
    {
        var regions = Databases.MasterServerDatabase.GetRegionServers();
        var totalPlayers = regions.Sum(r => Databases.MasterServerDatabase.GetRegionPlayerCount(r.Id!));

        var status = new
        {
            uptime = (DateTime.UtcNow - _startTime).ToString(@"d\.hh\:mm\:ss"),
            is_master = Databases.ConfigDatabase.IsMaster(),
            master_running = _masterServer?.IsStarted ?? false,
            region_running = _regionServer.IsStarted,
            match_running = _matchServer.IsStarted,
            player_count = totalPlayers,
            region_count = regions.Count,
            regions = regions.Select(r => new
            {
                id = r.Id,
                host = r.Host,
                name = r.Info?.Name?.Text,
                players = Databases.MasterServerDatabase.GetRegionPlayerCount(r.Id!)
            })
        };

        await WriteJson(ctx, status);
    }

    private static async Task ExecuteAction(HttpListenerContext ctx, Action action)
    {
        try
        {
            action();
            await WriteJson(ctx, new { message = "OK" });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await WriteJson(ctx, new { error = ex.Message });
        }
    }

    private void RestartServers()
    {
        Console.Write("Control panel: restarting servers...");
        _masterServer?.Restart();
        _regionServer.Restart();
        _regionClient.Disconnect();
        _regionClient.Reconnect();
        _matchServer.Restart();
        Console.WriteLine("Done!");
    }

    private void RefreshCatalogue(bool reload)
    {
        Console.Write("Control panel: refreshing catalogue...");
        try
        {
            var newCardList = reload
                ? _catalogueStore.Load(Databases.MapDatabase.GetMapCards(), Databases.MapDatabase.GrabExtraMaps())
                : CatalogueCache.UpdateCatalogue(CatalogueCache.Load());
            _serverCatalogue.Replicate(newCardList);
            var catalogueReplicator = new Service.ServiceCatalogue(new ServerSender(_regionServer));
            catalogueReplicator.SendReplicate(newCardList);
            Console.WriteLine("Done!");
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Failed - no cache file available");
        }
    }

    private async Task ServePlayerList(HttpListenerContext ctx)
    {
        var onlineIds = Databases.PlayerDatabase.GetAllPlayers().Select(p => p.PlayerId).ToHashSet();
        var allPlayers = await Databases.MasterServerDatabase.GetAllPlayersAsync();
        var players = allPlayers.Select(p => new
        {
            id = p.PlayerId,
            steam_id = p.SteamId,
            nickname = p.Nickname,
            role = p.Role.ToString(),
            role_id = (int)p.Role,
            region = p.Region,
            online = onlineIds.Contains(p.PlayerId)
        });

        await WriteJson(ctx, new { players });
    }

    private async Task ServePlayerDetail(HttpListenerContext ctx, uint playerId)
    {
        var player = Databases.PlayerDatabase.GetPlayerDataNoWait(playerId);
        player ??= await Databases.MasterServerDatabase.GetPlayer(playerId);
        if (player == null)
        {
            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "Player not found" });
            return;
        }

        var data = new
        {
            id = player.PlayerId,
            steam_id = player.SteamId,
            nickname = player.Nickname,
            role = player.Role.ToString(),
            role_id = (int)player.Role,
            region = player.Region,
            online = Databases.PlayerDatabase.GetPlayerDataNoWait(playerId) != null,
            rating_mean = player.Rating.Mean,
            rating_deviation = player.Rating.StandardDeviation,
            league = player.League == null ? null : new
            {
                tier = player.League.Tier,
                division = player.League.Division,
                points = player.League.Points
            },
            progression_level = player.Progression.PlayerProgress?.Level ?? 0,
            tutorial_tokens = player.TutorialTokens,
            looking_for_friends = player.LookingForFriends,
            matchmaker_ban_end = player.MatchmakerBanEnd,
            graveyard_permanent = player.GraveyardPermanent,
            graveyard_leave_time = player.GraveyardLeaveTime,
            friend_count = player.Friends.Count
        };

        await WriteJson(ctx, data);
    }

    private async Task HandlePlayerUpdate(HttpListenerContext ctx, uint playerId)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Try to get the live in-memory player; fall back to DB for offline players
            var livePlayer = Databases.PlayerDatabase.GetPlayerDataNoWait(playerId);
            var dbPlayer = livePlayer ?? await Databases.MasterServerDatabase.GetPlayer(playerId);
            if (dbPlayer == null)
            {
                ctx.Response.StatusCode = 404;
                await WriteJson(ctx, new { error = "Player not found" });
                return;
            }

            // Apply edits to a working copy (always the full PlayerData so we can persist it)
            var updated = dbPlayer;

            if (root.TryGetProperty("nickname", out var nicknameProp))
            {
                updated.Nickname = nicknameProp.GetString() ?? updated.Nickname;
                if (livePlayer != null)
                    Databases.PlayerDatabase.SetPlayerName(playerId, updated.Nickname);
            }

            if (root.TryGetProperty("role_id", out var roleProp))
                updated.Role = (BaseTypes.PlayerRole)roleProp.GetInt32();

            if (root.TryGetProperty("region", out var regionProp))
                updated.Region = regionProp.GetString();

            if (root.TryGetProperty("rating_mean", out var meanProp) || root.TryGetProperty("rating_deviation", out _))
            {
                var mean = root.TryGetProperty("rating_mean", out var m) ? m.GetDouble() : updated.Rating.Mean;
                var dev = root.TryGetProperty("rating_deviation", out var d) ? d.GetDouble() : updated.Rating.StandardDeviation;
                updated.Rating = new Moserware.Skills.Rating(mean, dev);
            }

            if (root.TryGetProperty("league_tier", out _) ||
                root.TryGetProperty("league_division", out _) ||
                root.TryGetProperty("league_points", out _))
            {
                updated.League ??= new BaseTypes.League();
                if (root.TryGetProperty("league_tier", out var lt)) updated.League.Tier = lt.GetInt32();
                if (root.TryGetProperty("league_division", out var ld)) updated.League.Division = ld.GetInt32();
                if (root.TryGetProperty("league_points", out var lp)) updated.League.Points = lp.GetInt32();
            }

            if (root.TryGetProperty("tutorial_tokens", out var tokensProp))
                updated.TutorialTokens = tokensProp.GetInt32();

            if (root.TryGetProperty("looking_for_friends", out var lffProp))
                updated.LookingForFriends = lffProp.GetBoolean();

            if (root.TryGetProperty("matchmaker_ban_end", out var banProp))
                updated.MatchmakerBanEnd = banProp.ValueKind == JsonValueKind.Null ? null : banProp.GetUInt64();

            if (root.TryGetProperty("graveyard_permanent", out var gpProp))
                updated.GraveyardPermanent = gpProp.ValueKind == JsonValueKind.Null ? null : gpProp.GetBoolean();

            if (root.TryGetProperty("graveyard_leave_time", out var gltProp))
                updated.GraveyardLeaveTime = gltProp.ValueKind == JsonValueKind.Null ? null : gltProp.GetUInt64();

            // Persist to the database (works for both online and offline players)
            var saved = await Databases.MasterServerDatabase.UpdatePlayerAsync(playerId, updated);
            if (!saved)
            {
                ctx.Response.StatusCode = 500;
                await WriteJson(ctx, new { error = "Failed to persist changes to database" });
                return;
            }

            await WriteJson(ctx, new { message = "Player updated" });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await WriteJson(ctx, new { error = ex.Message });
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var buf = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.OutputStream.Close();
    }
}
