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
using SharpDX.DirectWrite;
using D2DFactory = SharpDX.Direct2D1.Factory;
using DWFactory  = SharpDX.DirectWrite.Factory;
#endregion

// ──────────────────────────────────────────────────────────────
//  Indicator aaa4_zones  •  Supply/Demand zones + AOI + 4 price levels
// ──────────────────────────────────────────────────────────────
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum BreakMode { Immediate = 1, Reentry = 2 }

    public class aaa4_zones : Indicator
    {
        // ───────────────  USER PARAMETERS  ───────────────
        [Range(0.01, 1.0)]
        [Display(Name = "Size vela base", Order = 1, GroupName = "Parameters",
            Description = "Máx. % que mide el cuerpo de la vela base respecto al cuerpo de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Size wick vela base (AOI)", Order = 2, GroupName = "Parameters",
            Description = "Máx. % que mide la mecha AOI de la vela base respecto al cuerpo de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeWickVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Batalla en wick agresiva", Order = 3, GroupName = "Parameters",
            Description = "Máx. % que mide el wick de la vela agresiva (en su misma dirección) respecto a su cuerpo.")]
        [NinjaScriptProperty]
        public double BatallaWickAgresiva { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "# velas rompe", Order = 4, GroupName = "Parameters",
            Description = "Velas consecutivas que cierran fuera para eliminar zona.")]
        [NinjaScriptProperty]
        public int BreakCandlesNeeded { get; set; }

        [Display(Name = "Break mode", Order = 5, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public BreakMode RotaOption { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Ticks Max Zona", Order = 6, GroupName = "Parameters",
            Description = "Altura máxima en ticks; > ⇒ no se crea zona. Altura real = valor × TickSize.")]
        [NinjaScriptProperty]
        public int TicksMaxZona { get; set; }


        // ───────────────  INTERNAL STATE  ───────────────
        private List<ZoneInfo> zones;
        private List<LLLineInfo> llLines;
        private SolidColorBrush brushFill;
        private SolidColorBrush brushOutline;
        private StrokeStyle strokeStyleDotted;
        private DWFactory textFactory;
        private TextFormat textFormat;
        private Dictionary<int, TextLayout> tfLayouts;
        private readonly object _sync = new object();

        // ───────────────  LIFECYCLE  ───────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "aaa4_zones – Zonas Supply/Demand con parámetros renombrados.";
                Name        = "aaa4_zones";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;

                SizeVelaBase        = 0.21;
                SizeWickVelaBase    = 0.32;
                BatallaWickAgresiva = 0.13;
                BreakCandlesNeeded  = 2;
                RotaOption          = BreakMode.Immediate;
                TicksMaxZona        = 300;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);   // BIP 1
                AddDataSeries(BarsPeriodType.Minute, 10);  // BIP 2
                AddDataSeries(BarsPeriodType.Minute, 15);  // BIP 3
                AddDataSeries(BarsPeriodType.Minute, 30);  // BIP 4
                AddDataSeries(BarsPeriodType.Minute, 45);  // BIP 5
                AddDataSeries(BarsPeriodType.Minute, 60);  // BIP 6
                AddDataSeries(BarsPeriodType.Minute, 90);  // BIP 7
                AddDataSeries(BarsPeriodType.Minute, 120); // BIP 8
                AddDataSeries(BarsPeriodType.Minute, 180); // optional BIP 9
                AddDataSeries(BarsPeriodType.Minute, 240); // optional BIP 10

                zones   = new List<ZoneInfo>();
                llLines = new List<LLLineInfo>();
            }
            else if (State == State.DataLoaded)
            {
                textFactory = new DWFactory();
                textFormat  = new TextFormat(textFactory, "Arial", 12f);
                tfLayouts   = new Dictionary<int, TextLayout>();
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        // ───────────────  BAR-BY-BAR  ───────────────
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

            // Vela base
            double baseOpen   = Opens[bip][1];
            double baseClose  = Closes[bip][1];
            double baseHigh   = Highs[bip][1];
            double baseLow    = Lows[bip][1];
            DateTime baseTime = Times[bip][1];

            // Vela agresiva
            double nextOpen   = Opens[bip][0];
            double nextClose  = Closes[bip][0];
            double nextHigh   = Highs[bip][0];
            double nextLow    = Lows[bip][0];

            double baseBody = Math.Abs(baseClose - baseOpen);
            double nextBody = Math.Abs(nextClose - nextOpen);
            double nextBodyEff = Math.Max(nextBody, TickSize);

            bool baseIsGreen = baseClose > baseOpen;
            bool baseIsRed   = baseClose < baseOpen;
            bool nextIsGreen = nextClose > nextOpen;
            bool nextIsRed   = nextClose < nextOpen;

            // SUPPLY
            if (baseIsGreen && nextIsRed)
            {
                double wickAOI  = baseOpen - baseLow;
                double wickAgg  = Math.Max(nextClose - nextLow, 0);  // solo mecha inferior
                bool condBody   = baseBody < SizeVelaBase      * nextBodyEff;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBodyEff;
                bool condWickAg = (wickAgg / nextBodyEff) <= BatallaWickAgresiva;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, true, bip,
                               Math.Max(nextHigh, baseHigh), baseOpen, baseLow);
            }
            // DEMAND
            else if (baseIsRed && nextIsGreen)
            {
                double wickAOI  = baseHigh - baseOpen;
                double wickAgg  = Math.Max(nextHigh - nextClose, 0); // solo mecha superior
                bool condBody   = baseBody < SizeVelaBase      * nextBodyEff;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBodyEff;
                bool condWickAg = (wickAgg / nextBodyEff) <= BatallaWickAgresiva;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, false, bip,
                               baseOpen, Math.Min(nextLow, baseLow), baseHigh);
            }
        }

        private void CreateZone(DateTime time, bool isSupply, int bip,
                                double topPrice, double bottomPrice, double aoi)
        {
            double zoneTicks = (topPrice - bottomPrice) / TickSize;
            if (zoneTicks > TicksMaxZona) return;

            zones.Add(new ZoneInfo(time, isSupply, bip,
                                   topPrice, bottomPrice, aoi));
            llLines.Add(new LLLineInfo(time, isSupply, aoi, bip));
        }

        // ───────────────  ROMPER ZONAS  ───────────────
        private void CheckBreakZones()
        {
            int bip = BarsInProgress;
            double closeCurrent = Closes[bip][0];

            for (int i = zones.Count - 1; i >= 0; i--)
            {
                ZoneInfo z = zones[i];
                if (z.DataSeries != bip)
                    continue;

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
                else // modo 1
                {
                    z.ConsecutiveBreaks = isOutside
                        ? z.ConsecutiveBreaks + 1
                        : 0;

                    if (z.ConsecutiveBreaks >= BreakCandlesNeeded)
                        RemoveZone(i);
                }
            }
        }

        private void RemoveZone(int idx)
        {
            ZoneInfo z = zones[idx];
            zones.RemoveAt(idx);

            for (int j = llLines.Count - 1; j >= 0; j--)
                if (llLines[j].Time       == z.Time
                 && llLines[j].IsSupply   == z.IsSupply
                 && llLines[j].DataSeries == z.DataSeries)
                    llLines.RemoveAt(j);
        }

        // ───────────────  RENDER  ───────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            EnsureResources();

            List<ZoneInfo> snapZones;
            List<LLLineInfo> snapLines;
            lock (_sync)
            {
                snapZones = new List<ZoneInfo>(zones);
                snapLines = new List<LLLineInfo>(llLines);
            }

            float xRight = ChartPanel.X + ChartPanel.W;

            // Dibujar zonas
            foreach (ZoneInfo z in snapZones)
            {
                float yTop      = chartScale.GetYByValue(z.TopPrice);
                float yBottom   = chartScale.GetYByValue(z.BottomPrice);
                float rectTop   = Math.Min(yTop, yBottom);
                float rectBot   = Math.Max(yTop, yBottom);
                float height    = rectBot - rectTop;
                float xBase     = chartControl.GetXByTime(z.Time);
                float width     = xRight - xBase;
                var rect        = new RectangleF(xBase, rectTop, width, height);

                RenderTarget.FillRectangle(rect, brushFill);
                RenderTarget.DrawRectangle(rect, brushOutline, 1f);

                // Línea discontinua 50 %
                float midPrice = (float)z.Area2;
                float yMid     = chartScale.GetYByValue(midPrice);
                RenderTarget.DrawLine(new Vector2(xBase, yMid),
                                      new Vector2(xRight, yMid),
                                      brushOutline, 1f, strokeStyleDotted);

                // Línea base
                float yBaseOpen = chartScale.GetYByValue(z.IsSupply ? z.BottomPrice : z.TopPrice);
                RenderTarget.DrawLine(new Vector2(xBase, yBaseOpen),
                                      new Vector2(xRight, yBaseOpen),
                                      brushOutline, 1f);

                // Time-frame label
                if (textFactory != null && textFormat != null)
                {
                    string tf = bipToTf(z.DataSeries);
                    if (!tfLayouts.TryGetValue(z.DataSeries, out TextLayout tl))
                    {
                        tl = new TextLayout(textFactory, tf, textFormat, 50, textFormat.FontSize);
                        tfLayouts[z.DataSeries] = tl;
                    }
                    float tx = xRight - tl.Metrics.Width - 5;
                    float ty = z.IsSupply ? (yBaseOpen + 5) : (yBaseOpen - tl.Metrics.Height - 5);
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
                }
            }

            // Dibujar AOI
            foreach (LLLineInfo line in snapLines)
            {
                float y        = chartScale.GetYByValue(line.Price);
                float xBase     = chartControl.GetXByTime(line.Time);
                float width     = xRight - xBase;
                var   p1        = new Vector2(xBase, y);
                var   p2        = new Vector2(xRight, y);

                RenderTarget.DrawLine(p1, p2, brushOutline, 2f);

                if (textFactory != null && textFormat != null)
                {
                    string lbl = "AOI " + bipToTf(line.DataSeries);
                    using var tl = new SharpDX.DirectWrite.TextLayout(textFactory, lbl, textFormat, 100, textFormat.FontSize);
                    float tx = xRight - tl.Metrics.Width - 5;
                    float ty = line.IsSupply ? (y + 5) : (y - tl.Metrics.Height - 5);
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
                }
            }
        }

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

            if (brushFill == null)
                brushFill = new SolidColorBrush(RenderTarget, new Color(0.8f, 0.8f, 0.8f, 0.4f));

            if (brushOutline == null)
                brushOutline = new SolidColorBrush(RenderTarget, new Color(0f, 0f, 0f, 1f));

            if (strokeStyleDotted == null)
            {
                var props = new StrokeStyleProperties { DashStyle = DashStyle.Custom };
                strokeStyleDotted = new StrokeStyle((D2DFactory)RenderTarget.Factory, props, new[] { 2f, 2f });
            }
        }

        private void DisposeResources()
        {
            brushFill?.Dispose();        brushFill = null;
            brushOutline?.Dispose();     brushOutline = null;
            strokeStyleDotted?.Dispose(); strokeStyleDotted = null;
            if (tfLayouts != null)
            {
                foreach (var tl in tfLayouts.Values)
                    tl.Dispose();
                tfLayouts.Clear();
            }
            textFormat?.Dispose();       textFormat = null;
            textFactory?.Dispose();      textFactory = null;
        }

        // ───────────────  PUBLIC API  ───────────────
        public void ForceRebuild()
        {
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

        private string bipToTf(int bip) => BarsArray[bip].BarsPeriod.Value.ToString();

        // ───────────────  INTERNAL CLASSES  ───────────────
        private class ZoneInfo
        {
            public ZoneInfo(DateTime time, bool isSupply, int dataSeries,
                            double topPrice, double bottomPrice, double aoi)
            {
                Time        = time;
                IsSupply    = isSupply;
                DataSeries  = dataSeries;
                TopPrice    = topPrice;
                BottomPrice = bottomPrice;
                AOI         = aoi;
                Area1       = isSupply ? TopPrice    : BottomPrice;
                Area3       = isSupply ? BottomPrice : TopPrice;
                Area2       = (Area1 + Area3) / 2.0;
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
            public int      ConsecutiveBreaks { get; set; } = 0;
            public bool     HasBrokenOnce { get; set; } = false;
        }

        private class LLLineInfo
        {
            public LLLineInfo(DateTime time, bool isSupply, double price, int dataSeries)
            {
                Time       = time;
                IsSupply   = isSupply;
                Price      = price;
                DataSeries = dataSeries;
            }
            public DateTime Time { get; }
            public bool     IsSupply { get; }
            public double   Price { get; }
            public int      DataSeries { get; }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private aaa4_zones[] cacheaaa4_zones;
        public aaa4_zones aaa4_zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            return aaa4_zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona);
        }

        public aaa4_zones aaa4_zones(ISeries<double> input, double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            if (cacheaaa4_zones != null)
                for (int idx = 0; idx < cacheaaa4_zones.Length; idx++)
                    if (cacheaaa4_zones[idx] != null && cacheaaa4_zones[idx].SizeVelaBase == sizeVelaBase && cacheaaa4_zones[idx].SizeWickVelaBase == sizeWickVelaBase && cacheaaa4_zones[idx].BatallaWickAgresiva == batallaWickAgresiva && cacheaaa4_zones[idx].BreakCandlesNeeded == breakCandlesNeeded && cacheaaa4_zones[idx].RotaOption == rotaOption && cacheaaa4_zones[idx].TicksMaxZona == ticksMaxZona && cacheaaa4_zones[idx].EqualsInput(input))
                        return cacheaaa4_zones[idx];
            return CacheIndicator<aaa4_zones>(new aaa4_zones(){ SizeVelaBase = sizeVelaBase, SizeWickVelaBase = sizeWickVelaBase, BatallaWickAgresiva = batallaWickAgresiva, BreakCandlesNeeded = breakCandlesNeeded, RotaOption = rotaOption, TicksMaxZona = ticksMaxZona }, input, ref cacheaaa4_zones);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.aaa4_zones aaa4_zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            return indicator.aaa4_zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona);
        }

        public Indicators.aaa4_zones aaa4_zones(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            return indicator.aaa4_zones(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.aaa4_zones aaa4_zones(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            return indicator.aaa4_zones(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona);
        }

        public Indicators.aaa4_zones aaa4_zones(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, BreakMode rotaOption, int ticksMaxZona)
        {
            return indicator.aaa4_zones(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona);
        }
    }
}

#endregion
