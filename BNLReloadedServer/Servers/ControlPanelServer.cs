using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BNLReloadedServer.Database;
using BNLReloadedServer.ProtocolHelpers;
using BNLReloadedServer.Servers;
namespace BNLReloadedServer.ControlPanel;

public sealed class ControlPanelServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly MasterServer? _masterServer;
    private readonly RegionServer _regionServer;
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
        MatchServer matchServer,
        CatalogueStore catalogueStore,
        ServerCatalogue serverCatalogue)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _masterServer = masterServer;
        _regionServer = regionServer;
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
                await ServeFile(ctx, "index.html", "text/html; charset=utf-8");
                return;
            }

            if (method == "GET" && path == "/style.css")
            {
                await ServeFile(ctx, "style.css", "text/css; charset=utf-8");
                return;
            }

            if (method == "GET" && path == "/app.js")
            {
                await ServeFile(ctx, "app.js", "application/javascript; charset=utf-8");
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                await ServeStatus(ctx);
                return;
            }

            if (method == "POST" && path == "/api/refreshCdbLoad")
            {
                await ExecuteAction(ctx, RefreshCatalogue);
                return;
            }

            if (method == "POST" && path == "/api/reset")
            {
                await ServeReset(ctx);
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

            if (method == "GET" && path.StartsWith("/api/cards/"))
            {
                await ServeCard(ctx, Uri.UnescapeDataString(path["/api/cards/".Length..]));
                return;
            }

            if (method == "GET" && path == "/api/logs")
            {
                await ServeLogs(ctx);
                return;
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

    private static readonly string ControlPanelFolderPath = Path.Combine(AppContext.BaseDirectory, "ControlPanel");

    private static async Task ServeFile(HttpListenerContext ctx, string fileName, string contentType)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(ControlPanelFolderPath, fileName));

        ctx.Response.ContentType = contentType;
        var buf = System.Text.Encoding.UTF8.GetBytes(content);
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

    private static async Task ServeReset(HttpListenerContext ctx)
    {
        Console.WriteLine("Control panel: reset requested, shutting down (expecting the service to relaunch)...");
        await WriteJson(ctx, new { message = "Server is shutting down and should be relaunched by the service shortly." });
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            ShutdownSignal.Request();
        });
    }

    private void RefreshCatalogue()
    {
        Console.Write("Control panel: refreshing catalogue...");
        try
        {
            var newCardList = _catalogueStore.Load(Databases.MapDatabase.GetMapCards(), Databases.MapDatabase.GrabExtraMaps());
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
            friends = player.Friends,
            friend_requests_incoming = player.RequestsFromFriends,
            friend_requests_outgoing = player.RequestsFromMe,
            notification_count = player.Notifications.Count,
            last_played_hero = player.LastPlayedHero.HasValue
                ? player.LastPlayedHero.Value.GetCard<BaseTypes.CardUnit>()?.Name?.Text
                    ?? player.LastPlayedHero.Value.GetCard<BaseTypes.CardUnit>()?.Id
                    ?? player.LastPlayedHero.Value.ToString()
                : null,
            badges = player.Badges.ToDictionary(
                b => b.Key.ToString(),
                b => b.Value.Select(key => key.GetCard<BaseTypes.CardBadge>()?.Name?.Text ?? key.GetCard<BaseTypes.CardBadge>()?.Id ?? key.ToString())),
            hero_stats = player.HeroStats.Select(h => new
            {
                hero = h.Hero.GetCard<BaseTypes.CardUnit>()?.Name?.Text ?? h.Hero.GetCard<BaseTypes.CardUnit>()?.Id ?? h.Hero.ToString(),
                wins = h.Wins,
                total_matches = h.TotalMatches
            }),
            match_history = player.MatchHistory
                .OrderByDescending(m => m.MatchEndTime)
                .Select(m => new
                {
                    hero = m.HeroKey.GetCard<BaseTypes.CardUnit>()?.Name?.Text ?? m.HeroKey.GetCard<BaseTypes.CardUnit>()?.Id ?? m.HeroKey.ToString(),
                    map = m.MapKey.GetCard<BaseTypes.CardMap>()?.Name?.Text ?? m.MapKey.GetCard<BaseTypes.CardMap>()?.Id ?? m.MapKey.ToString(),
                    game_mode = m.GameModeKey.GetCard<BaseTypes.CardGameMode>()?.Name ?? m.GameModeKey.GetCard<BaseTypes.CardGameMode>()?.Id ?? m.GameModeKey.ToString(),
                    end_time = m.MatchEndTime,
                    duration_seconds = m.MatchSeconds,
                    is_winner = m.IsWinner,
                    is_backfiller = m.IsBackfiller,
                    is_quit = m.IsQuit,
                    resources_earned = m.ResourcesEarned,
                    blocks_built = m.BlocksBuilt,
                    block_assist = m.BlockAssist,
                    destruction = m.Destruction,
                    objective_damage = m.ObjectiveDamage,
                    kill = m.Kill,
                    death = m.Death,
                    assist = m.Assist
                }),
            time_trial = new
            {
                completed_goal_count = player.TimeTrial.CompletedGoals?.Count ?? 0,
                best_result_count = player.TimeTrial.BestResultTime?.Count ?? 0,
                reset_time = player.TimeTrial.ResetTime
            },
            loadouts = player.HeroLoadouts.Values.Select(DescribeLoadout)
        };

        await WriteJson(ctx, data);
    }

    private static async Task ServeLogs(HttpListenerContext ctx)
    {
        await WriteJson(ctx, new { lines = ConsoleLogBuffer.GetAll() });
    }

    private static async Task ServeCard(HttpListenerContext ctx, string query)
    {
        var card = uint.TryParse(query, out var hash)
            ? Databases.Catalogue.All.FirstOrDefault(c => c.Key.Hash == hash)
            : Databases.Catalogue.All.FirstOrDefault(c => string.Equals(c.Id, query, StringComparison.OrdinalIgnoreCase));

        if (card == null)
        {
            ctx.Response.StatusCode = 404;
            await WriteJson(ctx, new { error = "Card not found" });
            return;
        }

        var json = JsonSerializer.Serialize(card, card.GetType(), JsonHelper.DefaultSerializerSettings);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var buf = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.OutputStream.Close();
    }

    private static object DescribeLoadout(BaseTypes.LobbyLoadout loadout)
    {
        var heroCard = loadout.HeroKey.GetCard<BaseTypes.CardUnit>();
        var heroSkinCard = loadout.SkinKey.GetCard<BaseTypes.CardSkin>();

        return new
        {
            hero = heroCard?.Name?.Text ?? heroCard?.Id ?? loadout.HeroKey.ToString(),
            skin = heroSkinCard?.Name?.Text ?? heroSkinCard?.Id,
            devices = Enumerable.Range(1, 6).Select(slot =>
            {
                if (loadout.Devices == null || !loadout.Devices.TryGetValue(slot, out var deviceKey))
                    return new { slot, name = "(empty)", variant = (string?)null };

                var deviceCard = deviceKey.GetCard<BaseTypes.CardDevice>();
                var groupCard = deviceCard?.GroupKey.GetCard<BaseTypes.CardDeviceGroup>();
                var baseDeviceKey = groupCard?.Devices?.FirstOrDefault();
                var baseDeviceCard = baseDeviceKey.HasValue ? baseDeviceKey.Value.GetCard<BaseTypes.CardDevice>() : null;

                var variantName = deviceCard?.Name?.Text ?? deviceCard?.Id ?? deviceKey.ToString();
                var className = baseDeviceCard?.Name?.Text ?? baseDeviceCard?.Id
                    ?? groupCard?.Name?.Text ?? groupCard?.Id
                    ?? variantName;

                var hasVariant = baseDeviceCard != null && baseDeviceCard.Key != deviceCard?.Key;

                return new { slot, name = className, variant = hasVariant ? variantName : null };
            }),
            perks = new[]
            {
                BaseTypes.PerkSlotType.Defensive,
                BaseTypes.PerkSlotType.Offensive,
                BaseTypes.PerkSlotType.Hero
            }.Select(slotType =>
            {
                var equipped = loadout.Perks?
                    .Select(key => (key, perkCard: key.GetCard<BaseTypes.CardPerk>()))
                    .FirstOrDefault(p => p.perkCard?.SlotType == slotType);

                if (equipped == null || equipped.Value.perkCard == null)
                    return new { slot_type = slotType.ToString(), name = "(empty)", upside = (string?)null, downside = (string?)null };

                var perkCard = equipped.Value.perkCard;
                var description = perkCard.Description?.Text;
                var parts = description?.Split(" / ", 2);

                return new
                {
                    slot_type = slotType.ToString(),
                    name = perkCard.Name?.Text ?? perkCard.Id ?? equipped.Value.key.ToString(),
                    upside = parts?.ElementAtOrDefault(0),
                    downside = parts?.ElementAtOrDefault(1)
                };
            })
        };
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
