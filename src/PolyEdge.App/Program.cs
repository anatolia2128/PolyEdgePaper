using PolyEdge.App;
using PolyEdge.App.Feeds;
using PolyEdge.App.Polymarket;
using PolyEdge.App.Sim;
using PolyEdge.App.Trading;
using PolyEdge.Core;

var builder = WebApplication.CreateBuilder(args);

// Kişisel ayarlar için: appsettings.Local.json (git'e girmez). Ortam değişkeni de olur: Bot__Mode=Simulated
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

T Bind<T>(string section) where T : new() => builder.Configuration.GetSection(section).Get<T>() ?? new T();

var bot = Bind<BotSettings>("Bot");
var feeds = Bind<FeedSettings>("Feeds");
var flow = Bind<FlowSettings>("Flow");
var strat = Bind<StrategySettings>("Strategy");
var exec = Bind<ExecutionSettings>("Execution");

if (!bot.IsSimulated && bot.WindowSeconds != 300)
    throw new InvalidOperationException("Live modda Bot:WindowSeconds 300 olmalı (Polymarket 5 dk marketleri).");

var dataDir = Path.GetFullPath(bot.DataDirectory, builder.Environment.ContentRootPath);
var store = new StateStore(dataDir);
if (bot.ResetState) store.Reset();

var broker = new PaperBroker(exec, bot.StartingBalanceUsd);
var restored = store.Load();
if (restored is not null) broker.Load(restored);
else store.Save(broker.Export());

builder.Services.AddSingleton(bot).AddSingleton(feeds).AddSingleton(flow).AddSingleton(strat).AddSingleton(exec);
builder.Services.AddSingleton(store).AddSingleton(broker).AddSingleton<MarketData>();

if (bot.IsSimulated)
{
    builder.Services.AddSingleton<IPolymarketApi, SimulatedPolymarketApi>();
    builder.Services.AddHostedService<SimulatedFeed>();
}
else
{
    builder.Services.AddSingleton<IPolymarketApi, PolymarketApi>();
    builder.Services.AddHostedService<BinanceFeed>();
    builder.Services.AddHostedService<ChainlinkRtdsFeed>();
}

builder.Services.AddSingleton<WindowLog>();
builder.Services.AddHostedService<WindowRecorderService>();
builder.Services.AddSingleton<BotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotService>());
builder.Services.AddHostedService<SettlementService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (BotService botSvc, PaperBroker br, MarketData d) =>
{
    var now = DateTimeOffset.UtcNow;
    double? Age(DateTimeOffset? t) => t is null ? null : Math.Round((now - t.Value).TotalSeconds, 1);
    var trades = br.AllTrades();
    return Results.Ok(new
    {
        live = botSvc.Live,
        stats = br.Stats(),
        health = new { binanceAgeSec = Age(d.Health.BinanceAt), chainlinkAgeSec = Age(d.Health.ChainlinkAt), bookAgeSec = Age(d.Health.BookAt) },
        open = trades.Where(t => t.IsOpen),
        recent = trades.Where(t => !t.IsOpen).OrderByDescending(t => t.SettledAt).Take(30)
    });
});

app.MapGet("/api/trades", (PaperBroker br) => Results.Ok(br.AllTrades().OrderByDescending(t => t.Id)));

app.Logger.LogInformation("Dashboard: {Urls} | Mod: {Mode} | Veri klasörü: {Dir}", string.Join(", ", app.Urls.DefaultIfEmpty("(launchSettings)")), bot.Mode, dataDir);
app.Run();
