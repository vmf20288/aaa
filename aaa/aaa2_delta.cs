// aaa2_delta.cs - Delta indicator updated for NinjaTrader 8.1.5
// Derived from original a6 indicator for NinjaTrader 8.1.4
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators
{
    // Delta indicator using tick rule and volume
    public class aaa2_delta : Indicator
    {
        // --- fields ---------------------------------------------------------
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta2;  // tick rule
        private Dictionary<int, double> volume;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat textFormat;
        private bool lastBackgroundWhite;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // --- properties -----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Background White", Order = 1, GroupName = "Parameters")]
        public bool BackgroundWhite { get; set; }

        // --- state machine --------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "aaa2_delta";
                Description             = "Delta (tick rule) and volume";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
                BackgroundWhite         = false;
            }
            else if (State == State.Configure)
            {
                delta2 = new Dictionary<int, double>();
                volume = new Dictionary<int, double>();
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                lastBackgroundWhite = BackgroundWhite;
            }
            else if (State == State.Terminated)
            {
                DisposeGraphics();
            }
        }

        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();
            textFormat?.Dispose();
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();

            Color4 cText = BackgroundWhite ? new Color4(0, 0, 0, 1f)
                                           : new Color4(1, 1, 1, 1f);
            Color4 cVol  = new Color4(1, 1, 1, 1f);

            if (RenderTarget != null)
            {
                brushGeneral     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushVolumeWhite = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cVol);
            }
        }

        // --- market data ----------------------------------------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int barIdx   = CurrentBar;
            double price = e.Price;
            double vol   = e.Volume;

            if (!delta2.ContainsKey(barIdx))
            {
                delta2[barIdx] = volume[barIdx] = 0.0;
            }

            // Method 2: tick rule
            double sign2;
            if (price > lastTradePrice)
                sign2 = 1;
            else if (price < lastTradePrice)
                sign2 = -1;
            else
                sign2 = lastDirection;
            delta2[barIdx] += vol * sign2;
            if (sign2 != 0)
                lastDirection = sign2 > 0 ? 1 : -1;
            lastTradePrice = price;

            volume[barIdx] += vol;
        }

        // --- render ---------------------------------------------------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            if (lastBackgroundWhite != BackgroundWhite)
            {
                BuildBrushes();
                lastBackgroundWhite = BackgroundWhite;
            }

            if (Math.Abs(textFormat.FontSize - FontSizeProp) > 0.1f)
            {
                textFormat.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            const int rowCount = 2; // delta + volume
            float yTop    = yBottom - bottomMargin - rectHeight * rowCount;

            double max2 = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (delta2.ContainsKey(i)) max2 = Math.Max(max2, Math.Abs(delta2[i]));
            }
            if (max2.Equals(0.0)) max2 = 1.0;

            int deltaFontSize = FontSizeProp + 8;
            using (var deltaFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", deltaFontSize))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    DrawCell(delta2, i, max2, xLeft, yTop,             barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawVolumeCell(i, xLeft, yTop + rectHeight,     barWidth, rectHeight, deltaFormat);
                }
            }
        }

        private void DrawCell(Dictionary<int, double> src, int barIndex, double maxAbs, float xLeft, float yTop, float width, float height, TextFormat fmt, SharpDX.Direct2D1.Brush textBrush)
        {
            double value = src.ContainsKey(barIndex) ? src[barIndex] : 0.0;
            float intensity = (float)(Math.Abs(value) / maxAbs);
            intensity = Math.Max(0.2f, Math.Min(1f, intensity));

            Color4 fillColor = value >= 0
                ? new Color4(0f, intensity, 0f, intensity)
                : new Color4(intensity, 0f, 0f, intensity);

            var rect = new RectangleF(xLeft, yTop, width, height);
            using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor))
                RenderTarget.FillRectangle(rect, fillBrush);

            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);

            string txt = value.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, textBrush);
            }
        }

        private void DrawVolumeCell(int barIndex, float xLeft, float yTop, float width, float height, TextFormat fmt)
        {
            double vol = volume.ContainsKey(barIndex) ? volume[barIndex] : 0.0;
            var rect = new RectangleF(xLeft, yTop, width, height);
            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);
            string txt = vol.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushVolumeWhite);
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private aaa2_delta[] cacheaaa2_delta;
        public aaa2_delta aaa2_delta(int fontSizeProp, bool backgroundWhite)
        {
            return aaa2_delta(Input, fontSizeProp, backgroundWhite);
        }

        public aaa2_delta aaa2_delta(ISeries<double> input, int fontSizeProp, bool backgroundWhite)
        {
            if (cacheaaa2_delta != null)
                for (int idx = 0; idx < cacheaaa2_delta.Length; idx++)
                    if (cacheaaa2_delta[idx] != null && cacheaaa2_delta[idx].FontSizeProp == fontSizeProp && cacheaaa2_delta[idx].BackgroundWhite == backgroundWhite && cacheaaa2_delta[idx].EqualsInput(input))
                        return cacheaaa2_delta[idx];
            return CacheIndicator<aaa2_delta>(new aaa2_delta(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite }, input, ref cacheaaa2_delta);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.aaa2_delta aaa2_delta(int fontSizeProp, bool backgroundWhite)
        {
            return indicator.aaa2_delta(Input, fontSizeProp, backgroundWhite);
        }

        public Indicators.aaa2_delta aaa2_delta(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
        {
            return indicator.aaa2_delta(input, fontSizeProp, backgroundWhite);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.aaa2_delta aaa2_delta(int fontSizeProp, bool backgroundWhite)
        {
            return indicator.aaa2_delta(Input, fontSizeProp, backgroundWhite);
        }

        public Indicators.aaa2_delta aaa2_delta(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
        {
            return indicator.aaa2_delta(input, fontSizeProp, backgroundWhite);
        }
    }
}

#endregion
