#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

// b6mintrade.cs - Footprint/absorption indicator for NinjaTrader 8.1.5.1

namespace NinjaTrader.NinjaScript.Indicators
{
    public class b6mintrade : Indicator
    {
        public readonly struct LevelStats
        {
            public double Bid { get; }
            public double Ask { get; }

            public LevelStats(double bid, double ask)
            {
                Bid = bid;
                Ask = ask;
            }

            public LevelStats AddBid(double v) => new LevelStats(Bid + v, Ask);
            public LevelStats AddAsk(double v) => new LevelStats(Bid, Ask + v);
        }

        public readonly struct BarStats
        {
            public double Bid { get; }
            public double Ask { get; }

            public BarStats(double bid, double ask)
            {
                Bid = bid;
                Ask = ask;
            }

            public double Delta  => Ask - Bid;
            public double Volume => Ask + Bid;
        }

        private Dictionary<double, LevelStats> levels;
        private Dictionary<int, Dictionary<double, LevelStats>> barLevels;
        private Dictionary<int, BarStats> barTotals;
        private double curBid;
        private double curAsk;

        public IReadOnlyDictionary<double, LevelStats> CurrentLevels        => levels;
        public IReadOnlyDictionary<int, Dictionary<double, LevelStats>> HistoricalLevels => barLevels;
        public IReadOnlyDictionary<int, BarStats>     BarTotals            => barTotals;
        public double CurrentBidVolume  => curBid;
        public double CurrentAskVolume  => curAsk;

        private double lastTradePrice = double.NaN;
        private double bestBid = double.NaN;
        private double bestAsk = double.NaN;
        private int    lastSide = +1;  // +1 Ask, -1 Bid

        private float rectHeight = 12f;

        private HashSet<double> highlightCurrent;
        private Dictionary<int, HashSet<double>> highlightBars;

        // ───────────────  PARAMETERS  ───────────────
        [NinjaScriptProperty]
        [Display(Name = "Tamano letra footprint", Order = 0, GroupName = "Parameters")]
        public float TamanoLetraFootprint { get; set; } = 12f;

        [NinjaScriptProperty]
        [Display(Name = "Min Trade", Order = 1, GroupName = "Parameters")]
        public int MinTrade { get; set; } = 15;

        [NinjaScriptProperty]
        [Display(Name = "Highlight Trade", Order = 2, GroupName = "Parameters")]
        public int HighlightTrade { get; set; } = 50;

        private SolidColorBrush brushText;
        private SolidColorBrush highlightBrush;
        private SharpDX.DirectWrite.TextFormat textFormat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "b6mintrade";
                Description             = "Footprint/absorption indicator";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DrawOnPricePanel        = false;
                DisplayInDataBox        = false;
                PaintPriceMarkers       = false;
            }
            else if (State == State.Configure)
            {
                levels    = new Dictionary<double, LevelStats>();
                barLevels = new Dictionary<int, Dictionary<double, LevelStats>>();
                barTotals = new Dictionary<int, BarStats>();
                highlightBars = new Dictionary<int, HashSet<double>>();
                highlightCurrent = new HashSet<double>();
                curBid    = 0;
                curAsk    = 0;
                bestBid   = double.NaN;
                bestAsk   = double.NaN;
                lastSide  = +1;
                lastTradePrice = double.NaN;
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", TamanoLetraFootprint);
            }
            else if (State == State.Terminated)
            {
                brushText?.Dispose();
                highlightBrush?.Dispose();
                textFormat?.Dispose();
            }
        }

        private void DisposeBrushes()
        {
            brushText?.Dispose();   brushText   = null;
            highlightBrush?.Dispose(); highlightBrush = null;
        }

        private void BuildBrushes()
        {
            DisposeBrushes();
            if (RenderTarget == null)
                return;

            brushText     = new SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 1f));
            highlightBrush = new SolidColorBrush(RenderTarget, new Color4(1f, 0.85f, 0f, 0.35f));
        }

        public override void OnRenderTargetChanged()
        {
            base.OnRenderTargetChanged();
            BuildBrushes();   // BuildBrushes se encarga de DisposeBrushes()
        }

        // ─────────────── MARKET DATA ───────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                bestBid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                bestAsk = e.Price;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            double price       = e.Price;
            double vol         = e.Volume;
            double priceRounded = Instrument.MasterInstrument.RoundToTickSize(price);

            if (vol < MinTrade)
                return;

            if (vol >= HighlightTrade)
                highlightCurrent.Add(priceRounded);

            // ─── Determinar lado Bid / Ask ──────────────────────────────────────────────
            bool insideReady = !double.IsNaN(bestBid) && !double.IsNaN(bestAsk);

            int side;
            if (insideReady)
            {
                bool atBid = price <= bestBid + TickSize * 1e-4;
                bool atAsk = price >= bestAsk - TickSize * 1e-4;

                if      (atAsk && !atBid) side = +1;      // compra agresiva
                else if (atBid && !atAsk) side = -1;      // venta agresiva
                else                      side = lastSide;   // dentro del spread
            }
            else
            {
                // Fallback tick-rule para datos históricos sin Bid/Ask
                if      (double.IsNaN(lastTradePrice) || price > lastTradePrice) side = +1;
                else if (price < lastTradePrice)                                 side = -1;
                else                                                             side = lastSide;
            }

            if (!levels.TryGetValue(priceRounded, out LevelStats ls))
                ls = new LevelStats(0, 0);

            ls = (side == +1) ? ls.AddAsk(vol) : ls.AddBid(vol);
            levels[priceRounded] = ls;

            if (side == +1)
                curAsk += vol;
            else
                curBid += vol;

            // ─── Guardar estado para el siguiente tick ───────────────────────────────
            lastSide       = side;
            lastTradePrice = price;
        }

        // ─────────────── BAR UPDATE ───────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 1)
                return;

            if (IsFirstTickOfBar)
            {
                int b = CurrentBar - 1;
                barLevels[b] = levels;
                barTotals[b] = new BarStats(curBid, curAsk);
                highlightBars[b] = highlightCurrent;

                levels = new Dictionary<double, LevelStats>();
                highlightCurrent = new HashSet<double>();
                curBid  = 0;
                curAsk  = 0;
            }
        }

        // ─────────────── RENDER ───────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartBars == null || RenderTarget == null)
                return;

            if (brushText == null)
                BuildBrushes();

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            for (int i = firstBar; i <= lastBar; i++)
            {
                Dictionary<double, LevelStats> dict = null;
                HashSet<double> highlights = null;
                if (i == CurrentBar)
                {
                    dict = levels;
                    highlights = highlightCurrent;
                }
                else
                {
                    barLevels.TryGetValue(i, out dict);
                    highlightBars.TryGetValue(i, out highlights);
                }

                if (dict == null)
                    continue;

                float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                float xLeft   = xCenter - barWidth / 2f;

                foreach (var kv in SortedByPriceDesc(dict))
                {
                    double price = kv.Key;
                    LevelStats ls = kv.Value;

                    float y = (float)chartScale.GetYByValue(price) - rectHeight / 2f;
                    var rect = new RectangleF(xLeft, y, barWidth, rectHeight);

                    if (highlights != null && highlights.Contains(price))
                        RenderTarget.FillRectangle(rect, highlightBrush);

                    string txtBid = ls.Bid.ToString("0");
                    string txtAsk = ls.Ask.ToString("0");
                    float half = rect.Width / 2f;

                    using (var layB = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtBid, textFormat, half, rect.Height))
                    {
                        var m = layB.Metrics;
                        float tx = xLeft + (half - m.Width) / 2f;
                        float ty = y + (rect.Height - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), layB, brushText);
                    }

                    using (var layA = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtAsk, textFormat, half, rect.Height))
                    {
                        var m = layA.Metrics;
                        float tx = xLeft + half + (half - m.Width) / 2f;
                        float ty = y + (rect.Height - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), layA, brushText);
                    }
                }
            }

        }

        private IEnumerable<KeyValuePair<double, LevelStats>> SortedByPriceDesc(Dictionary<double, LevelStats> src)
        {
            var list = new List<KeyValuePair<double, LevelStats>>(src);
            list.Sort((a,b) => b.Key.CompareTo(a.Key));
            return list;
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private b6mintrade[] cacheb6MinTrade;
        public b6mintrade b6MinTrade(float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            return b6MinTrade(Input, tamanoLetraFootprint, minTrade, highlightTrade);
        }

        public b6mintrade b6MinTrade(ISeries<double> input, float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            if (cacheb6MinTrade != null)
                for (int idx = 0; idx < cacheb6MinTrade.Length; idx++)
                    if (cacheb6MinTrade[idx] != null && cacheb6MinTrade[idx].TamanoLetraFootprint == tamanoLetraFootprint && cacheb6MinTrade[idx].MinTrade == minTrade && cacheb6MinTrade[idx].HighlightTrade == highlightTrade && cacheb6MinTrade[idx].EqualsInput(input))
                        return cacheb6MinTrade[idx];
            return CacheIndicator<b6mintrade>(new b6mintrade(){ TamanoLetraFootprint = tamanoLetraFootprint, MinTrade = minTrade, HighlightTrade = highlightTrade }, input, ref cacheb6MinTrade);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.b6mintrade b6MinTrade(float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            return indicator.b6MinTrade(Input, tamanoLetraFootprint, minTrade, highlightTrade);
        }

        public Indicators.b6mintrade b6MinTrade(ISeries<double> input , float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            return indicator.b6MinTrade(input, tamanoLetraFootprint, minTrade, highlightTrade);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.b6mintrade b6MinTrade(float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            return indicator.b6MinTrade(Input, tamanoLetraFootprint, minTrade, highlightTrade);
        }

        public Indicators.b6mintrade b6MinTrade(ISeries<double> input , float tamanoLetraFootprint, int minTrade, int highlightTrade)
        {
            return indicator.b6MinTrade(input, tamanoLetraFootprint, minTrade, highlightTrade);
        }
    }
}

#endregion
