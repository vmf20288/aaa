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

// b3.cs - Footprint/absorption indicator for NinjaTrader 8.1.5.1

namespace NinjaTrader.NinjaScript.Indicators
{
    public class b3 : Indicator
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
        private float bottomRectHeight = 18f;
        private float bottomMargin = 20f;

        // ───────────────  PARAMETERS  ───────────────
        [NinjaScriptProperty]
        [Display(Name = "Tamano letra footprint", Order = 0, GroupName = "Parameters")]
        public float TamanoLetraFootprint { get; set; } = 12f;

        private SolidColorBrush brushText;
        private SolidColorBrush brushBorder;
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.DirectWrite.TextFormat bottomTextFormat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "b3";
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
                bottomTextFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 16f);
            }
            else if (State == State.Terminated)
            {
                brushText?.Dispose();
                brushBorder?.Dispose();
                textFormat?.Dispose();
                bottomTextFormat?.Dispose();
            }
        }

        private void DisposeBrushes()
        {
            brushText?.Dispose();   brushText   = null;
            brushBorder?.Dispose(); brushBorder = null;
        }

        private void BuildBrushes()
        {
            DisposeBrushes();
            if (RenderTarget == null)
                return;

            brushText  = new SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 1f));
            brushBorder = new SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 1f));
        }

        protected override void OnRenderTargetChanged()
        {
            base.OnRenderTargetChanged();
            DisposeBrushes();
            BuildBrushes();
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
                // Fallback tick‑rule para datos históricos sin Bid/Ask
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

                levels = new Dictionary<double, LevelStats>();
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
                if (i == CurrentBar)
                    dict = levels;
                else
                    barLevels.TryGetValue(i, out dict);

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

            float yChartBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            float yStart = yChartBottom - bottomMargin - 2 * bottomRectHeight;
            float xLeftLabels = chartControl.GetXByBarIndex(ChartBars, firstBar) - 50f;

            RenderTarget.DrawText("delta", bottomTextFormat, new RectangleF(xLeftLabels, yStart, 50f, bottomRectHeight), brushBorder);
            RenderTarget.DrawText("volumen", bottomTextFormat, new RectangleF(xLeftLabels, yStart + bottomRectHeight, 50f, bottomRectHeight), brushBorder);

            for (int i = firstBar; i <= lastBar; i++)
            {
                BarStats stats;
                bool hasStats;
                if (i == CurrentBar)
                {
                    stats = new BarStats(curBid, curAsk);
                    hasStats = true;
                }
                else
                {
                    hasStats = barTotals.TryGetValue(i, out stats);
                }

                if (!hasStats)
                    continue;

                float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                float xLeft = xCenter - barWidth / 2f;

                var rectDelta = new RectangleF(xLeft, yStart, barWidth, bottomRectHeight);
                var rectVol   = new RectangleF(xLeft, yStart + bottomRectHeight, barWidth, bottomRectHeight);

                RenderTarget.DrawRectangle(rectDelta, brushBorder, 1f);
                RenderTarget.DrawRectangle(rectVol, brushBorder, 1f);

                    string txtDelta = (stats.Ask - stats.Bid).ToString("0");
                    using (var lay = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtDelta, bottomTextFormat, rectDelta.Width, rectDelta.Height))
                    {
                        var m = lay.Metrics;
                        float tx = xLeft + (rectDelta.Width - m.Width) / 2f;
                        float ty = yStart + (bottomRectHeight - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), lay, brushBorder);
                    }

                    string txtVol = (stats.Ask + stats.Bid).ToString("0");
                    using (var lay = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtVol, bottomTextFormat, rectVol.Width, rectVol.Height))
                    {
                        var m = lay.Metrics;
                        float tx = xLeft + (rectVol.Width - m.Width) / 2f;
                        float ty = yStart + bottomRectHeight + (bottomRectHeight - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), lay, brushBorder);
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
        private b3[] cacheb3;
        public b3 b3(float tamanoLetraFootprint)
        {
            return b3(Input, tamanoLetraFootprint);
        }

        public b3 b3(ISeries<double> input, float tamanoLetraFootprint)
        {
            if (cacheb3 != null)
                for (int idx = 0; idx < cacheb3.Length; idx++)
                    if (cacheb3[idx] != null && cacheb3[idx].TamanoLetraFootprint == tamanoLetraFootprint && cacheb3[idx].EqualsInput(input))
                        return cacheb3[idx];
            return CacheIndicator<b3>(new b3(){ TamanoLetraFootprint = tamanoLetraFootprint }, input, ref cacheb3);
        }
    }
}

#endregion

