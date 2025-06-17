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

// b41_mintrade.cs - Footprint/absorption indicator with minimum trade filter for NinjaTrader 8.1.5.1

namespace NinjaTrader.NinjaScript.Indicators
{
    public class b41_mintrade : Indicator
    {
        private class LevelStats
        {
            public double Bid;
            public double Ask;
        }

        private class BarStats
        {
            public double Bid;
            public double Ask;
        }

        private Dictionary<double, LevelStats> levels;
        private Dictionary<int, Dictionary<double, LevelStats>> barLevels;
        private Dictionary<int, BarStats> barTotals;
        private double curBid;
        private double curAsk;

        private double lastTradePrice;
        private int    lastDirection;

        private float rectHeight = 12f;
        private float bottomMargin = 20f;

        // ───────────────  PARAMETERS  ───────────────
        [NinjaScriptProperty]
        [Display(Name = "Delta por nivel precio", Order = 0, GroupName = "Parameters")]
        public bool DeltaPorNivelPrecio { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Min Trade", Order = 1, GroupName = "Parameters")]
        public int MinTrade { get; set; } = 15;

        private bool lastDeltaPorNivelPrecio;

        private SolidColorBrush brushAsk;
        private SolidColorBrush brushBid;
        private SolidColorBrush brushEven;
        private SolidColorBrush brushText;
        private SolidColorBrush brushBorder;
        private SharpDX.DirectWrite.TextFormat textFormat;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "b41_mintrade";
                Description             = "Footprint/absorption indicator";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DrawOnPricePanel        = false;
                DisplayInDataBox        = false;
                PaintPriceMarkers       = false;
                DeltaPorNivelPrecio     = false;
            }
            else if (State == State.Configure)
            {
                levels    = new Dictionary<double, LevelStats>();
                barLevels = new Dictionary<int, Dictionary<double, LevelStats>>();
                barTotals = new Dictionary<int, BarStats>();
                curBid    = 0;
                curAsk    = 0;
            }
            else if (State == State.DataLoaded)
            {
                lastDeltaPorNivelPrecio = DeltaPorNivelPrecio;
                BuildBrushes();
                textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f);
            }
            else if (State == State.Terminated)
            {
                brushAsk?.Dispose();
                brushBid?.Dispose();
                brushEven?.Dispose();
                brushText?.Dispose();
                brushBorder?.Dispose();
                textFormat?.Dispose();
            }
        }

        private void BuildBrushes()
        {
            brushAsk?.Dispose();
            brushBid?.Dispose();
            brushEven?.Dispose();
            brushText?.Dispose();
            brushBorder?.Dispose();

            if (RenderTarget == null)
                return;

            if (DeltaPorNivelPrecio)
            {
                brushAsk  = new SolidColorBrush(RenderTarget, new Color4(0f, 1f, 0f, 0.4f));
                brushBid  = new SolidColorBrush(RenderTarget, new Color4(1f, 0f, 0f, 0.4f));
                brushEven = new SolidColorBrush(RenderTarget, new Color4(0.5f, 0.5f, 0.5f, 0.2f));
                brushText = new SolidColorBrush(RenderTarget, new Color4(1f, 1f, 1f, 1f));
                brushBorder = new SolidColorBrush(RenderTarget, new Color4(0f,0f,0f,1f));
            }
            else
            {
                var transparent = new Color4(0f, 0f, 0f, 0f);
                brushAsk  = new SolidColorBrush(RenderTarget, transparent);
                brushBid  = new SolidColorBrush(RenderTarget, transparent);
                brushEven = new SolidColorBrush(RenderTarget, transparent);
                brushText = new SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 1f));
                brushBorder = new SolidColorBrush(RenderTarget, new Color4(0f,0f,0f,1f));
            }
        }

        // ─────────────── MARKET DATA ───────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            double price       = e.Price;
            double vol         = e.Volume;

            if (vol < MinTrade)
                return;

            double priceRounded = Instrument.MasterInstrument.RoundToTickSize(price);

            int sign;
            if (price > lastTradePrice)      sign = 1;
            else if (price < lastTradePrice) sign = -1;
            else                              sign = lastDirection;

            if (!levels.TryGetValue(priceRounded, out LevelStats ls))
            {
                ls = new LevelStats();
                levels[priceRounded] = ls;
            }

            if (sign >= 0)
            {
                ls.Ask += vol;
                curAsk  += vol;
            }
            else
            {
                ls.Bid += vol;
                curBid += vol;
            }

            if (sign != 0)
                lastDirection = sign;
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

                Print($"Bar {b}  TotalAsk={curAsk}  TotalBid={curBid}");
                barTotals[b] = new BarStats { Ask = curAsk, Bid = curBid };

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

            if (brushAsk == null || lastDeltaPorNivelPrecio != DeltaPorNivelPrecio)
            {
                BuildBrushes();
                lastDeltaPorNivelPrecio = DeltaPorNivelPrecio;
            }

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

                    if (DeltaPorNivelPrecio)
                    {
                        SolidColorBrush fill = brushEven;
                        if (ls.Ask > ls.Bid)      fill = brushAsk;
                        else if (ls.Bid > ls.Ask) fill = brushBid;

                        RenderTarget.FillRectangle(rect, fill);
                    }

                    RenderTarget.DrawRectangle(rect, brushText, 1f);

                    if (DeltaPorNivelPrecio)
                    {
                        double delta = ls.Ask - ls.Bid;
                        string txt = delta.ToString("0");
                        using (var layout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, textFormat, rect.Width, rect.Height))
                        {
                            var m = layout.Metrics;
                            float tx = xLeft + (rect.Width  - m.Width)  / 2f;
                            float ty = y     + (rect.Height - m.Height) / 2f;
                            RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushText);
                        }
                    }
                    else
                    {
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

            float yChartBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            float yStart = yChartBottom - bottomMargin - 2 * rectHeight;
            float xLeftLabels = chartControl.GetXByBarIndex(ChartBars, firstBar) - 50f;

            using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f))
            {
                RenderTarget.DrawText("delta", fmt, new RectangleF(xLeftLabels, yStart, 50f, rectHeight), brushBorder);
                RenderTarget.DrawText("volumen", fmt, new RectangleF(xLeftLabels, yStart + rectHeight, 50f, rectHeight), brushBorder);

                for (int i = firstBar; i <= lastBar; i++)
                {
                    BarStats stats = null;
                    if (i == CurrentBar)
                        stats = new BarStats { Ask = curAsk, Bid = curBid };
                    else
                        barTotals.TryGetValue(i, out stats);

                    if (stats == null)
                        continue;

                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft = xCenter - barWidth / 2f;

                    var rectDelta = new RectangleF(xLeft, yStart, barWidth, rectHeight);
                    var rectVol   = new RectangleF(xLeft, yStart + rectHeight, barWidth, rectHeight);

                    RenderTarget.DrawRectangle(rectDelta, brushBorder, 1f);
                    RenderTarget.DrawRectangle(rectVol, brushBorder, 1f);

                    string txtDelta = (stats.Ask - stats.Bid).ToString("0");
                    using (var lay = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtDelta, textFormat, rectDelta.Width, rectDelta.Height))
                    {
                        var m = lay.Metrics;
                        float tx = xLeft + (rectDelta.Width - m.Width) / 2f;
                        float ty = yStart + (rectHeight - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), lay, brushBorder);
                    }

                    string txtVol = (stats.Ask + stats.Bid).ToString("0");
                    using (var lay = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txtVol, textFormat, rectVol.Width, rectVol.Height))
                    {
                        var m = lay.Metrics;
                        float tx = xLeft + (rectVol.Width - m.Width) / 2f;
                        float ty = yStart + rectHeight + (rectHeight - m.Height) / 2f;
                        RenderTarget.DrawTextLayout(new Vector2(tx, ty), lay, brushBorder);
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
        private b41_mintrade[] cacheb41_mintrade;
        public b41_mintrade b41_mintrade(bool deltaPorNivelPrecio, int minTrade)
        {
            return b41_mintrade(Input, deltaPorNivelPrecio, minTrade);
        }

        public b41_mintrade b41_mintrade(ISeries<double> input, bool deltaPorNivelPrecio, int minTrade)
        {
            if (cacheb41_mintrade != null)
                for (int idx = 0; idx < cacheb41_mintrade.Length; idx++)
                    if (cacheb41_mintrade[idx] != null && cacheb41_mintrade[idx].DeltaPorNivelPrecio == deltaPorNivelPrecio && cacheb41_mintrade[idx].MinTrade == minTrade && cacheb41_mintrade[idx].EqualsInput(input))
                        return cacheb41_mintrade[idx];
            return CacheIndicator<b41_mintrade>(new b41_mintrade(){ DeltaPorNivelPrecio = deltaPorNivelPrecio, MinTrade = minTrade }, input, ref cacheb41_mintrade);
        }
    }
}

#endregion

