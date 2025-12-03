#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using DWrite = SharpDX.DirectWrite;
#endregion

// ──────────────────────────────────────────────────────────────
//  ENUM GLOBAL  (visible para Indicators, Strategies, etc.)
// ──────────────────────────────────────────────────────────────
namespace NinjaTrader.NinjaScript
{
    public enum BreakMode
    {
        Immediate = 1,
        Reentry   = 2
    }
}

// ──────────────────────────────────────────────────────────────
//  Indicator b8zones  •  Supply/Demand zones + AOI + 4 price levels
// ──────────────────────────────────────────────────────────────
namespace NinjaTrader.NinjaScript.Indicators
{
    public class b8zones : Indicator
    {
        // ───────────────  USER PARAMETERS  ───────────────
        [Range(0.01, 1.0)]
        [Display(Name = "Size vela base", Order = 1, GroupName = "Parameters",
            Description = "% máx. del cuerpo de la vela base respecto al de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Size wick vela base (AOI)", Order = 2, GroupName = "Parameters",
            Description = "% máx. de la mecha AOI respecto al cuerpo de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeWickVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Batalla en wick agresiva", Order = 3, GroupName = "Parameters",
            Description = "% máx. del wick agresivo (en su dirección) respecto a su cuerpo.")]
        [NinjaScriptProperty]
        public double BatallaWickAgresiva { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "# velas rompe", Order = 4, GroupName = "Parameters",
            Description = "Velas consecutivas cerrando fuera que eliminan la zona.")]
        [NinjaScriptProperty]
        public int BreakCandlesNeeded { get; set; }

        [Display(Name = "Break mode", Order = 5, GroupName = "Parameters")]
        [NinjaScriptProperty]
        [Browsable(false)]           // Sólo el indicador master la modifica
        public BreakMode RotaOption { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Ticks Max Zona", Order = 6, GroupName = "Parameters",
            Description = "Altura máxima (ticks). Altura real = valor × TickSize.")]
        [NinjaScriptProperty]
        public int TicksMaxZona { get; set; }

        // ───── new properties ─────
        [Range(1, int.MaxValue)]
        [Display(Name = "Agresive vela 5-60 min", Order = 7, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public int AgresiveVela560 { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Agresive vela 90-240 min", Order = 8, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public int AgresiveVela90240 { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Batalla wick con agresive vela 5-240 min", Order = 9, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public double BatallaWickAgresivaLarge { get; set; }

        // ───────────────  INTERNAL STATE  ───────────────
        private readonly object _sync = new object();

        private List<ZoneInfo> zones;
        private List<LLLineInfo> llLines;

        private SolidColorBrush brushFill;
        private SolidColorBrush brushOutline;
        private StrokeStyle     strokeStyleDotted;

        private DWrite.Factory  dwFactory;
        private DWrite.TextFormat textFormat;
        private Dictionary<string, DWrite.TextLayout> tfLayouts;

        // ───────────────  LIFECYCLE  ───────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "b8zones – Zonas Supply/Demand (versión optimizada).";
                Name        = "b8zones";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;

                // valores por defecto
                SizeVelaBase        = 0.21;
                SizeWickVelaBase    = 0.32;
                BatallaWickAgresiva = 0.18;
                BreakCandlesNeeded  = 2;
                RotaOption          = BreakMode.Immediate;
                TicksMaxZona        = 300;
                AgresiveVela560     = 300;
                AgresiveVela90240   = 550;
                BatallaWickAgresivaLarge = 0.32;
            }
            else if (State == State.Configure)
            {
                // BIP 1 .. 10  (ascendente)
                AddDataSeries(BarsPeriodType.Minute,   5);   // 1
                AddDataSeries(BarsPeriodType.Minute,  10);   // 2
                AddDataSeries(BarsPeriodType.Minute,  15);   // 3
                AddDataSeries(BarsPeriodType.Minute,  30);   // 4
                AddDataSeries(BarsPeriodType.Minute,  45);   // 5
                AddDataSeries(BarsPeriodType.Minute,  60);   // 6
                AddDataSeries(BarsPeriodType.Minute,  90);   // 7
                AddDataSeries(BarsPeriodType.Minute, 120);   // 8
                AddDataSeries(BarsPeriodType.Minute, 180);   // 9
                AddDataSeries(BarsPeriodType.Minute, 240);   // 10

                zones   = new List<ZoneInfo>();
                llLines = new List<LLLineInfo>();
            }
            else if (State == State.DataLoaded)
            {
                dwFactory  = new DWrite.Factory();
                textFormat = new DWrite.TextFormat(dwFactory, "Arial", 12f);
                tfLayouts  = new Dictionary<string, DWrite.TextLayout>();
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        // ───────────────  BAR‑BY‑BAR  ───────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0) return;
            if (CurrentBars[BarsInProgress] < 2) return;

            lock (_sync)
            {
                CheckCreateZone();
                CheckBreakZones();
            }
        }

        // ───────────────  CREAR ZONA  ───────────────
        private void CheckCreateZone()
        {
            int bip = BarsInProgress;

            // Vela base (índice 1) y agresiva (índice 0)
            double baseOpen   = Opens[bip][1];
            double baseClose  = Closes[bip][1];
            double baseHigh   = Highs[bip][1];
            double baseLow    = Lows[bip][1];
            DateTime baseTime = Times[bip][1];

            double nextOpen   = Opens[bip][0];
            double nextClose  = Closes[bip][0];
            double nextHigh   = Highs[bip][0];
            double nextLow    = Lows[bip][0];

            double baseBody = Math.Abs(baseClose - baseOpen);
            double nextBodyEff = Math.Max(Math.Abs(nextClose - nextOpen), TickSize);   // evita /0

            bool baseIsGreen = baseClose > baseOpen;
            bool baseIsRed   = baseClose < baseOpen;
            bool nextIsGreen = nextClose > nextOpen;
            bool nextIsRed   = nextClose < nextOpen;

            double nextBodyTicks = Math.Abs(nextClose - nextOpen) / TickSize;
            double wickAggressivePercent = BatallaWickAgresiva;
            if ((bip <= 6 && nextBodyTicks >= AgresiveVela560) || (bip > 6 && nextBodyTicks >= AgresiveVela90240))
                wickAggressivePercent = BatallaWickAgresivaLarge;

            // SUPPLY
            if (baseIsGreen && nextIsRed)
            {
                double wickAOI  = baseOpen - baseLow;
                double wickAgg  = Math.Max(nextClose - nextLow, 0);   // mecha inferior
                bool condBody   = baseBody < SizeVelaBase      * nextBodyEff;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBodyEff;
                bool condWickAg = (wickAgg / nextBodyEff) <= wickAggressivePercent;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, true, bip,
                               Math.Max(nextHigh, baseHigh), baseOpen, baseLow,
                               nextBodyTicks);
            }
            // DEMAND
            else if (baseIsRed && nextIsGreen)
            {
                double wickAOI  = baseHigh - baseOpen;
                double wickAgg  = Math.Max(nextHigh - nextClose, 0);  // mecha superior
                bool condBody   = baseBody < SizeVelaBase      * nextBodyEff;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBodyEff;
                bool condWickAg = (wickAgg / nextBodyEff) <= wickAggressivePercent;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, false, bip,
                               baseOpen, Math.Min(nextLow, baseLow), baseHigh,
                               nextBodyTicks);
            }
        }

        private void CreateZone(DateTime time, bool isSupply, int bip,
                                double topPrice, double bottomPrice, double aoi,
                                double aggressiveTicks)
        {
            double zoneTicks = (topPrice - bottomPrice) / TickSize;
            if (zoneTicks > TicksMaxZona) return;

            var z = new ZoneInfo(time, isSupply, bip, topPrice, bottomPrice, aoi,
                                 aggressiveTicks);
            z.TfLabel = bipToTf(bip);
            zones.Add(z);
            llLines.Add(new LLLineInfo(time, isSupply, aoi, bip));
            ConsolidateCluster(z);
        }

        private void ConsolidateCluster(ZoneInfo zone)
        {
            // Gather overlapping zones with same direction
            List<int> cluster = new List<int>();
            for (int i = 0; i < zones.Count; i++)
            {
                ZoneInfo z = zones[i];
                if (z.IsSupply != zone.IsSupply) continue;
                if (ZonesOverlap(z, zone))
                    cluster.Add(i);
            }
            if (cluster.Count <= 1) return;

            // Determine winner
            int bestIdx = cluster[0];
            foreach (int idx in cluster)
                if (CompareZones(zones[idx], zones[bestIdx]) > 0)
                    bestIdx = idx;

            ZoneInfo winner = zones[bestIdx];
            ZoneInfo? aoiCand = null;

            SortedSet<int> losing = new SortedSet<int>();

            for (int n = cluster.Count - 1; n >= 0; n--)
            {
                int idx = cluster[n];
                if (idx == bestIdx) continue;
                ZoneInfo looser = zones[idx];
                losing.Add(looser.DataSeries);

                if (IsAoiCandidate(winner, looser))
                {
                    if (aoiCand == null || looser.DataSeries > aoiCand.DataSeries)
                        aoiCand = looser;
                }

                RemoveZone(idx);
            }

            if (aoiCand != null)
            {
                llLines.Add(new LLLineInfo(aoiCand.Time, aoiCand.IsSupply, aoiCand.AOI,
                                           aoiCand.DataSeries, true, winner.Time));
                losing.Add(aoiCand.DataSeries);
            }

            if (losing.Count > 0)
            {
                winner.LosingTFs.AddRange(losing);
                string tfList = string.Join(",", SortTfs(losing));
                winner.TfLabel = bipToTf(winner.DataSeries) + " /" + tfList;
            }
            else
                winner.TfLabel = bipToTf(winner.DataSeries);
        }

        private static bool ZonesOverlap(ZoneInfo a, ZoneInfo b)
        {
            double aTop = Math.Max(a.TopPrice, a.BottomPrice);
            double aBot = Math.Min(a.TopPrice, a.BottomPrice);
            double bTop = Math.Max(b.TopPrice, b.BottomPrice);
            double bBot = Math.Min(b.TopPrice, b.BottomPrice);
            return aBot <= bTop && bBot <= aTop;
        }

        private int CompareZones(ZoneInfo a, ZoneInfo b)
        {
            bool aNever = !a.HasBrokenOnce && a.ConsecutiveBreaks == 0;
            bool bNever = !b.HasBrokenOnce && b.ConsecutiveBreaks == 0;
            if (aNever != bNever) return aNever ? 1 : -1;

            if (a.AggressiveTicks != b.AggressiveTicks)
                return a.AggressiveTicks > b.AggressiveTicks ? 1 : -1;

            if (a.DataSeries != b.DataSeries)
                return a.DataSeries > b.DataSeries ? 1 : -1;

            if (a.Time != b.Time)
                return a.Time > b.Time ? 1 : -1;

            double aWidth = Math.Abs(a.TopPrice - a.BottomPrice);
            double bWidth = Math.Abs(b.TopPrice - b.BottomPrice);
            if (aWidth != bWidth)
                return aWidth < bWidth ? 1 : -1;

            return 0;
        }

        private bool IsAoiCandidate(ZoneInfo winner, ZoneInfo cand)
        {
            double diff;
            if (winner.IsSupply)
            {
                if (cand.AOI >= winner.AOI) return false;
                diff = winner.AOI - cand.AOI;
            }
            else
            {
                if (cand.AOI <= winner.AOI) return false;
                diff = cand.AOI - winner.AOI;
            }
            return diff / TickSize >= 15;
        }

        private IEnumerable<string> SortTfs(IEnumerable<int> bips)
        {
            List<int> list = new List<int>(bips);
            list.Sort();
            List<string> tfs = new List<string>();
            foreach (int b in list) tfs.Add(bipToTf(b));
            return tfs;
        }

        // ───────────────  ROMPER ZONAS  ───────────────
        private void CheckBreakZones()
        {
            int bip = BarsInProgress;
            double closeCurrent = Closes[bip][0];

            for (int i = zones.Count - 1; i >= 0; i--)
            {
                ZoneInfo z = zones[i];
                if (z.DataSeries != bip) continue;

                bool isOutside = z.IsSupply
                                 ? closeCurrent > z.TopPrice
                                 : closeCurrent < z.BottomPrice;

                if (RotaOption == BreakMode.Reentry)
                {
                    if (isOutside)
                    {
                        if (!z.HasBrokenOnce)
                            z.HasBrokenOnce = true;
                        else
                            RemoveZone(i);
                    }
                    else
                        z.HasBrokenOnce = false;
                }
                else // Immediate
                {
                    z.ConsecutiveBreaks = isOutside ? z.ConsecutiveBreaks + 1 : 0;
                    if (z.ConsecutiveBreaks >= BreakCandlesNeeded)
                        RemoveZone(i);
                }
            }
        }

        private void RemoveZone(int idx, bool removeLine = true)
        {
            ZoneInfo z = zones[idx];
            zones.RemoveAt(idx);

            if (!removeLine) return;

            for (int j = llLines.Count - 1; j >= 0; j--)
            {
                LLLineInfo line = llLines[j];

                bool isOriginal = line.Time == z.Time &&
                                  line.IsSupply == z.IsSupply &&
                                  line.DataSeries == z.DataSeries;

                bool isInheritedFromZone = line.Inherited &&
                                           line.ParentZoneTime.HasValue &&
                                           line.ParentZoneTime.Value == z.Time;

                if (isOriginal || isInheritedFromZone)
                    llLines.RemoveAt(j);
            }
        }

        // ───────────────  RENDER  ───────────────
        protected override void OnRender(ChartControl cc, ChartScale cs)
        {
            base.OnRender(cc, cs);
            EnsureResources();
            if (RenderTarget == null) return;

            List<ZoneInfo> snapZones;
            List<LLLineInfo> snapLines;
            lock (_sync)
            {
                snapZones = new List<ZoneInfo>(zones);
                snapLines = new List<LLLineInfo>(llLines);
            }

            float xRight = ChartPanel.X + ChartPanel.W;

            // Zonas
            foreach (ZoneInfo z in snapZones)
            {
                float yTop    = cs.GetYByValue(z.TopPrice);
                float yBottom = cs.GetYByValue(z.BottomPrice);
                float rectTop = Math.Min(yTop, yBottom);
                float rectBot = Math.Max(yTop, yBottom);
                float height  = rectBot - rectTop;
                float xBase   = cc.GetXByTime(z.Time);
                var   rect    = new RectangleF(xBase, rectTop, xRight - xBase, height);

                RenderTarget.FillRectangle(rect, brushFill);
                RenderTarget.DrawRectangle(rect, brushOutline, 1f);

                // línea discontinua 50 %
                float yMid = cs.GetYByValue(z.Area2);
                RenderTarget.DrawLine(new Vector2(xBase, yMid),
                                      new Vector2(xRight, yMid),
                                      brushOutline, 1f, strokeStyleDotted);

                // línea base
                float yBase = cs.GetYByValue(z.IsSupply ? z.BottomPrice : z.TopPrice);
                RenderTarget.DrawLine(new Vector2(xBase, yBase),
                                      new Vector2(xRight, yBase),
                                      brushOutline, 1f);

                // etiqueta TF
                var tl = GetOrCreateLayout(z.TfLabel);
                float tx = xRight - tl.Metrics.Width - 5;
                float ty = z.IsSupply ? (yBase + 5) : (yBase - tl.Metrics.Height - 5);
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
            }

            // AOI
            foreach (LLLineInfo l in snapLines)
            {
                float y      = cs.GetYByValue(l.Price);
                float xBase  = cc.GetXByTime(l.Time);

                RenderTarget.DrawLine(new Vector2(xBase, y),
                                      new Vector2(xRight, y),
                                      brushOutline, 2f);

                var tl = l.Inherited
                    ? GetOrCreateLayout("/" + bipToTf(l.DataSeries))
                    : GetOrCreateLayout("AOI " + bipToTf(l.DataSeries));
                float tx = xRight - tl.Metrics.Width - 5;
                float ty = l.IsSupply ? (y + 5) : (y - tl.Metrics.Height - 5);
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
            }
        }

        // ───────────────  RENDER‑TARGET CHANGED  ───────────────
        public override void OnRenderTargetChanged()
        {
            base.OnRenderTargetChanged();
            DisposeResources();
            EnsureResources();
        }

        // ───────────────  RESOURCES  ───────────────
        private void EnsureResources()
        {
            if (RenderTarget == null)
                return;

            // Pinceles y línea discontinua
            brushFill    ??= new SolidColorBrush(RenderTarget, new Color(0.8f, 0.8f, 0.8f, 0.4f));
            brushOutline ??= new SolidColorBrush(RenderTarget, new Color(0f, 0f, 0f, 1f));

            if (strokeStyleDotted == null)
            {
                var props = new StrokeStyleProperties { DashStyle = DashStyle.Custom };
                strokeStyleDotted = new StrokeStyle(RenderTarget.Factory, props, new[] { 2f, 2f });
            }

            // Fabrica y formato de texto (re‑crearlos si se perdieron)
            if (dwFactory == null || textFormat == null)
            {
                dwFactory  = new DWrite.Factory();
                textFormat = new DWrite.TextFormat(dwFactory, "Arial", 12f);
                tfLayouts  = new Dictionary<string, DWrite.TextLayout>();
            }
        }

        private void DisposeResources()
        {
            brushFill?.Dispose();        brushFill = null;
            brushOutline?.Dispose();     brushOutline = null;
            strokeStyleDotted?.Dispose(); strokeStyleDotted = null;

            if (tfLayouts != null)
            {
                foreach (var tl in tfLayouts.Values) tl.Dispose();
            }
            tfLayouts = null;            // ← marca para recrear
            textFormat?.Dispose();       textFormat = null;
            dwFactory?.Dispose();        dwFactory = null;
        }

        // ───────────────  TEXT‑LAYOUT CACHE  ───────────────
        private DWrite.TextLayout GetOrCreateLayout(int bip, string prefix = "")
        {
            if (tfLayouts == null)
                tfLayouts = new Dictionary<string, DWrite.TextLayout>();

            string key = prefix + bip;

            if (!tfLayouts.TryGetValue(key, out var tl))
            {
                string tf = prefix + bipToTf(bip);
                tl = new DWrite.TextLayout(dwFactory, tf, textFormat, 100, textFormat.FontSize);
                tfLayouts[key] = tl;
            }
            return tl;
        }

        private DWrite.TextLayout GetOrCreateLayout(string text)
        {
            if (tfLayouts == null)
                tfLayouts = new Dictionary<string, DWrite.TextLayout>();

            if (!tfLayouts.TryGetValue(text, out var tl))
            {
                tl = new DWrite.TextLayout(dwFactory, text, textFormat, 100, textFormat.FontSize);
                tfLayouts[text] = tl;
            }
            return tl;
        }

        // ───────────────  PUBLIC API  ───────────────
        public void ForceRebuild()
        {
            lock (_sync)
            {
                zones.Clear();
                llLines.Clear();
            }
            ChartControl?.InvalidateVisual();
        }

        public int GetZoneCount() => zones.Count;

        public bool TryGetZone(int index,
                               out bool   isSupply,
                               out double area1,
                               out double area2,
                               out double area3,
                               out double aoi,
                               out int    dataSeries)
        {
            lock (_sync)
            {
                if (index < 0 || index >= zones.Count)
                {
                    isSupply = false; area1 = area2 = area3 = aoi = 0; dataSeries = 0;
                    return false;
                }
                ZoneInfo z = zones[index];
                isSupply   = z.IsSupply;
                area1      = z.Area1;
                area2      = z.Area2;
                area3      = z.Area3;
                aoi        = z.AOI;
                dataSeries = z.DataSeries;
                return true;
            }
        }

        private string bipToTf(int bip) => bip switch
        {
            1 => "5",   2 => "10",  3 => "15", 4 => "30", 5 => "45",
            6 => "60",  7 => "90",  8 => "120", 9 => "180", 10 => "240",
            _ => BarsPeriod.Value.ToString()
        };

        // ───────────────  INTERNAL CLASSES  ───────────────
        private class ZoneInfo
        {
            public ZoneInfo(DateTime time, bool isSupply, int dataSeries,
                            double topPrice, double bottomPrice, double aoi,
                            double aggressiveTicks)
            {
                Time            = time;
                IsSupply        = isSupply;
                DataSeries      = dataSeries;
                TopPrice        = topPrice;
                BottomPrice     = bottomPrice;
                AOI             = aoi;
                AggressiveTicks = aggressiveTicks;

                Area1       = isSupply ? TopPrice    : BottomPrice;
                Area3       = isSupply ? BottomPrice : TopPrice;
                Area2       = (Area1 + Area3) / 2.0;
                LosingTFs   = new List<int>();
            }

            public DateTime Time { get; }
            public bool     IsSupply { get; }
            public int      DataSeries { get; }
            public double   TopPrice { get; }
            public double   BottomPrice { get; }
            public double   AOI { get; }
            public double   Area1 { get; }
            public double   Area2 { get; }
            public double   Area3 { get; }

            public double   AggressiveTicks { get; }
            public List<int> LosingTFs { get; }
            public string    TfLabel { get; set; }

            public int  ConsecutiveBreaks { get; set; }
            public bool HasBrokenOnce     { get; set; }
        }

        private class LLLineInfo
        {
            public LLLineInfo(DateTime time, bool isSupply, double price, int dataSeries,
                             bool inherited = false, DateTime? parentZoneTime = null)
            {
                Time           = time;
                IsSupply       = isSupply;
                Price          = price;
                DataSeries     = dataSeries;
                Inherited      = inherited;
                ParentZoneTime = parentZoneTime;
            }

            public DateTime  Time { get; }
            public bool      IsSupply { get; }
            public double    Price { get; }
            public int       DataSeries { get; }
            public bool      Inherited { get; set; }
            public DateTime? ParentZoneTime { get; }
        }
    }

}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
{
private b8zones[] cacheb8zones;
public b8zones b8zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
return b8zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, agresiveVela560, agresiveVela90240, batallaWickAgresivaLarge);
}

public b8zones b8zones(ISeries<double> input, double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
if (cacheb8zones != null)
for (int idx = 0; idx < cacheb8zones.Length; idx++)
if (cacheb8zones[idx] != null && cacheb8zones[idx].SizeVelaBase == sizeVelaBase && cacheb8zones[idx].SizeWickVelaBase == sizeWickVelaBase && cacheb8zones[idx].BatallaWickAgresiva == batallaWickAgresiva && cacheb8zones[idx].BreakCandlesNeeded == breakCandlesNeeded && cacheb8zones[idx].RotaOption == rotaOption && cacheb8zones[idx].TicksMaxZona == ticksMaxZona && cacheb8zones[idx].AgresiveVela560 == agresiveVela560 && cacheb8zones[idx].AgresiveVela90240 == agresiveVela90240 && cacheb8zones[idx].BatallaWickAgresivaLarge == batallaWickAgresivaLarge && cacheb8zones[idx].EqualsInput(input))
return cacheb8zones[idx];
return CacheIndicator<b8zones>(new b8zones(){ SizeVelaBase = sizeVelaBase, SizeWickVelaBase = sizeWickVelaBase, BatallaWickAgresiva = batallaWickAgresiva, BreakCandlesNeeded = breakCandlesNeeded, RotaOption = rotaOption, TicksMaxZona = ticksMaxZona, AgresiveVela560 = agresiveVela560, AgresiveVela90240 = agresiveVela90240, BatallaWickAgresivaLarge = batallaWickAgresivaLarge }, input, ref cacheb8zones);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.b8zones b8zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
return indicator.b8zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, agresiveVela560, agresiveVela90240, batallaWickAgresivaLarge);
}

public Indicators.b8zones b8zones(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
return indicator.b8zones(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, agresiveVela560, agresiveVela90240, batallaWickAgresivaLarge);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.b8zones b8zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
return indicator.b8zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, agresiveVela560, agresiveVela90240, batallaWickAgresivaLarge);
}

public Indicators.b8zones b8zones(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona, int agresiveVela560, int agresiveVela90240, double batallaWickAgresivaLarge)
{
return indicator.b8zones(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, agresiveVela560, agresiveVela90240, batallaWickAgresivaLarge);
}
}
}

#endregion
