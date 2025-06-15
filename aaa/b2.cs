#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

// b2.cs - Indicator for zone/vwap touches and big limit orders

namespace NinjaTrader.NinjaScript.Indicators
{
    public class b2 : Indicator
    {
        private aaa1_vwap  vwap;
        private aaa4_zones zones;

        private Dictionary<int, bool> flagTouch;
        private Dictionary<int, bool> flagBig;
        private Dictionary<int, int>  flagLimit;

        private double lastTradePrice;
        private int    lastDirection;

        private float rectHeight = 16f;
        private float topMargin  = 20f;
        private SharpDX.Direct2D1.SolidColorBrush brushText;
        private SharpDX.Direct2D1.SolidColorBrush brushFillGray;
        private SharpDX.Direct2D1.SolidColorBrush brushFillRed;
        private SharpDX.Direct2D1.SolidColorBrush brushFillGreen;

        // ─────────────── PARAMETERS ───────────────
        [NinjaScriptProperty]
        [Display(Name = "Limit Order Min", Order = 0, GroupName = "Parameters")]
        public int LimitOrderMin { get; set; } = 15;

        [NinjaScriptProperty]
        [Display(Name = "Cercania Zona Zones", Order = 1, GroupName = "Parameters")]
        public int ZoneProximityTicks { get; set; } = 10;

        [NinjaScriptProperty]
        [Display(Name = "Cercania VWAPs", Order = 2, GroupName = "Parameters")]
        public int VWAPProximityTicks { get; set; } = 15;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Detects touches of zones/VWAP and big limit orders.";
                Name        = "b2";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
            }
            else if (State == State.Configure)
            {
                flagTouch = new Dictionary<int, bool>();
                flagBig   = new Dictionary<int, bool>();
                flagLimit = new Dictionary<int, int>();
            }
            else if (State == State.DataLoaded)
            {
                vwap  = aaa1_vwap(true, true, true, true, true, true, true, DateTime.Today, "00:00");
                zones = aaa4_zones(0.21, 0.32, 0.13, 2, "1", 300, false);
                brushText      = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0f,0f,0f,1f));
                brushFillGray  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0.7f,0.7f,0.7f,0.8f));
                brushFillRed   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(1f,0f,0f,0.5f));
                brushFillGreen = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0f,1f,0f,0.5f));
            }
            else if (State == State.Terminated)
            {
                brushText?.Dispose();
                brushFillGray?.Dispose();
                brushFillRed?.Dispose();
                brushFillGreen?.Dispose();
            }
        }

        // ─────────────── MARKET DATA ───────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int barIdx = CurrentBar;
            double price = e.Price;
            double vol   = e.Volume;

            int sign;
            if (price > lastTradePrice)      sign = 1;
            else if (price < lastTradePrice) sign = -1;
            else                              sign = lastDirection;

            bool nearZone = IsPriceNearZone(price, out bool supply);
            bool nearVWAP = IsPriceNearVWAP(price);
            bool nearArea = nearZone || nearVWAP;

            if (nearArea)
                flagTouch[barIdx] = true;

            if (vol >= LimitOrderMin && nearArea)
            {
                flagBig[barIdx] = true;
                if (sign > 0 && (nearVWAP || supply))
                    flagLimit[barIdx] = 1; // red
                else if (sign < 0 && (nearVWAP || !supply))
                    flagLimit[barIdx] = -1; // green
            }

            if (sign != 0)
                lastDirection = sign;
            lastTradePrice = price;
        }

        private bool IsPriceNearZone(double price, out bool supply)
        {
            double buf = ZoneProximityTicks * TickSize;
            int count = zones.GetZoneCount();
            for (int i = 0; i < count; i++)
            {
                if (!zones.TryGetZone(i, out bool isSupply, out double a1, out double a2, out double a3, out double aoi, out int ds))
                    continue;
                double top = Math.Max(a1, a3) + buf;
                double bot = Math.Min(a1, a3) - buf;
                if (price <= top && price >= bot)
                {
                    supply = isSupply;
                    return true;
                }
                if (price <= aoi + buf && price >= aoi - buf)
                {
                    supply = isSupply;
                    return true;
                }
            }
            supply = false;
            return false;
        }

        private bool IsPriceNearVWAP(double price)
        {
            double buf = VWAPProximityTicks * TickSize;
            for (int j = 0; j <= 10; j++)
            {
                double val = vwap.Values[j][0];
                if (!double.IsNaN(val) && Math.Abs(price - val) <= buf)
                    return true;
            }
            return false;
        }

        // ─────────────── RENDER ───────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartBars == null || RenderTarget == null)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yChartTop = (float)chartScale.GetYByValue(chartScale.MaxValue);
            float yTop = yChartTop + topMargin;
            float xLeftLabels = chartControl.GetXByBarIndex(ChartBars, firstBar) - 50f;

            using (var fmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f))
            {
                RenderTarget.DrawText("toco area", fmt, new RectangleF(xLeftLabels, yTop, 80f, rectHeight), brushText);
                RenderTarget.DrawText("big order", fmt, new RectangleF(xLeftLabels, yTop + rectHeight, 80f, rectHeight), brushText);
                RenderTarget.DrawText("order limit", fmt, new RectangleF(xLeftLabels, yTop + 2*rectHeight, 80f, rectHeight), brushText);

                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft = xCenter - barWidth / 2f;

                    DrawFlag(flagTouch.ContainsKey(i) && flagTouch[i], xLeft, yTop, barWidth, rectHeight, brushFillGray, fmt);
                    DrawFlag(flagBig.ContainsKey(i) && flagBig[i], xLeft, yTop + rectHeight, barWidth, rectHeight, brushFillGray, fmt);
                    int limVal = flagLimit.ContainsKey(i) ? flagLimit[i] : 0;
                    SharpDX.Direct2D1.SolidColorBrush fill = limVal > 0 ? brushFillRed : (limVal < 0 ? brushFillGreen : null);
                    DrawFlag(limVal != 0, xLeft, yTop + 2*rectHeight, barWidth, rectHeight, fill, fmt);
                }
            }
        }

        private void DrawFlag(bool on, float xLeft, float yTop, float width, float height, SharpDX.Direct2D1.SolidColorBrush fill, SharpDX.DirectWrite.TextFormat fmt)
        {
            if (on && fill != null)
                RenderTarget.FillRectangle(new RectangleF(xLeft, yTop, width, height), fill);
            RenderTarget.DrawRectangle(new RectangleF(xLeft, yTop, width, height), brushText, 1f);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private b2[] cacheb2;
        public b2 b2(int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            return b2(Input, limitOrderMin, zoneProximityTicks, vWAPProximityTicks);
        }

        public b2 b2(ISeries<double> input, int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            if (cacheb2 != null)
                for (int idx = 0; idx < cacheb2.Length; idx++)
                    if (cacheb2[idx] != null && cacheb2[idx].LimitOrderMin == limitOrderMin && cacheb2[idx].ZoneProximityTicks == zoneProximityTicks && cacheb2[idx].VWAPProximityTicks == vWAPProximityTicks && cacheb2[idx].EqualsInput(input))
                        return cacheb2[idx];
            return CacheIndicator<b2>(new b2(){ LimitOrderMin = limitOrderMin, ZoneProximityTicks = zoneProximityTicks, VWAPProximityTicks = vWAPProximityTicks }, input, ref cacheb2);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.b2 b2(int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            return indicator.b2(Input, limitOrderMin, zoneProximityTicks, vWAPProximityTicks);
        }

        public Indicators.b2 b2(ISeries<double> input , int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            return indicator.b2(input, limitOrderMin, zoneProximityTicks, vWAPProximityTicks);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.b2 b2(int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            return indicator.b2(Input, limitOrderMin, zoneProximityTicks, vWAPProximityTicks);
        }

        public Indicators.b2 b2(ISeries<double> input , int limitOrderMin, int zoneProximityTicks, int vWAPProximityTicks)
        {
            return indicator.b2(input, limitOrderMin, zoneProximityTicks, vWAPProximityTicks);
        }
    }
}

#endregion
