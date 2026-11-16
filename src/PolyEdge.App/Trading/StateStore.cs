using System.Globalization;
using System.Text;
using System.Text.Json;
using PolyEdge.Core;

namespace PolyEdge.App.Trading;

/// <summary>Paper state'i (bakiye + tüm işlemler) JSON'a, işlemleri ve özellikleri CSV'ye yazar.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _g = new();
    private readonly string _dir;
    private string StatePath => Path.Combine(_dir, "state.json");
    private string TradesCsv => Path.Combine(_dir, "trades.csv");
    private string FeaturesCsv => Path.Combine(_dir, "features.csv");
    private string WindowsCsv => Path.Combine(_dir, "windows.csv");

    public StateStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public string Directory_ => _dir;

    public PersistedState? Load()
    {
        lock (_g)
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(StatePath), Json);
        }
    }

    public void Reset()
    {
        lock (_g)
        {
            foreach (var p in new[] { StatePath, TradesCsv, FeaturesCsv, WindowsCsv })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    public void Save(PersistedState s)
    {
        lock (_g)
        {
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, StatePath, overwrite: true);
        }
    }

    public void AppendSettledTrade(PaperTrade t)
    {
        const string header = "id,slug,side,decision_at,filled_at,shares,avg_price,best_ask_at_decision,slippage_usd,cost,fee,gas,redeem_gas,model_prob,final_prob,edge,flow_score,rel_move_pct,won,payout,pnl,roi,settle_source,settled_at";
        var line = string.Join(',',
            t.Id, t.Slug, t.Side, t.DecisionAt.ToString("o"), t.FilledAt.ToString("o"),
            F(t.Shares), F(t.AvgPrice), F(t.BestAskAtDecision), F(t.SlippageUsd), F(t.Cost), F(t.Fee), F(t.Gas), F(t.RedeemGas ?? 0),
            F(t.ModelProb), F(t.FinalProb), F(t.EdgeAtDecision), F(t.FlowScore), F(t.RelMovePct),
            t.Won, F(t.Payout ?? 0), F(t.Pnl ?? 0), F(t.Roi ?? 0), t.SettleSource, t.SettledAt?.ToString("o"));
        Append(TradesCsv, header, line);
    }

    public void AppendFeature(string line)
    {
        const string header = "time,slug,elapsed_s,binance,chainlink,rel_move_pct,sigma,flow_score,whale30_net,cvd15,depth_imb,liq30_net,p_model,p_market,p_final,up_bid,up_ask,down_bid,down_ask,edge_up,edge_down";
        Append(FeaturesCsv, header, line);
    }

    public void AppendWindow(string line)
    {
        const string header = "slug,start_utc,end_utc,chainlink_open,chainlink_close,chainlink_move_bp,chainlink_winner,official_winner,resolution_source,binance_open,bot_skipped,trades";
        Append(WindowsCsv, header, line);
    }

    private void Append(string path, string header, string line)
    {
        lock (_g)
        {
            var exists = File.Exists(path);
            using var sw = new StreamWriter(path, append: true, new UTF8Encoding(false));
            if (!exists) sw.WriteLine(header);
            sw.WriteLine(line);
        }
    }

    public static string F(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    public static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
}
