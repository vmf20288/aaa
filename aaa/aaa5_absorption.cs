#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
#endregion

// aaa5_absorption.cs - Absorption indicator for NinjaTrader 8.1.5

namespace NinjaTrader.NinjaScript.Indicators
{
    public class aaa5_absorption : Indicator
    {
        private aaa1_vwap  vwap;
        private aaa4_zones zones;
        private ATR        atr;

        private Dictionary<int, double> buyVol;
        private Dictionary<int, double> sellVol;
        private Dictionary<int, double> delta;
        private Dictionary<int, double> sideVol;
        private Dictionary<int, bool>  flagInZone;
        private Dictionary<int, bool>  flagAgg;
        private Dictionary<int, bool>  flagRange;
        private Dictionary<int, bool>  flagNoBreak;
        private Dictionary<int, bool>  flagAbs;

        private double lastTradePrice;
        private int    lastDirection;
        private double emaSV;
        private Queue<double> smaQueue;
        private double smaSum;

        private struct FailTrack
        {
            public int    BarIdx;
            public bool   IsSupply;
            public double Upper;
            public double Lower;
            public int    Remaining;
        }
        private List<FailTrack> failTracks;

        // ───────────────  PARAMETERS  ───────────────
        [NinjaScriptProperty]
        [Display(Name = "Use Zones Only", Order = 0, GroupName = "Parameters")]
        public bool UseZonesOnly { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Use Anchored VWAP", Order = 1, GroupName = "Parameters")]
        public bool UseAnchoredVWAP { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Vol Factor", Order = 2, GroupName = "Parameters")]
        public double VolFactor { get; set; } = 1.5;

        [NinjaScriptProperty]
        [Display(Name = "Range Factor", Order = 3, GroupName = "Parameters")]
        public double RangeFactor { get; set; } = 0.18;

        [NinjaScriptProperty]
        [Display(Name = "Line Buffer Ticks", Order = 4, GroupName = "Parameters")]
        public int LineBufferTicks { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Rect Buffer Ticks", Order = 5, GroupName = "Parameters")]
        public int RectBufferTicks { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Min Bars Ready", Order = 6, GroupName = "Parameters")]
        public int MinBarsReady { get; set; } = 15;

        [NinjaScriptProperty]
        [Display(Name = "Lookback Full", Order = 7, GroupName = "Parameters")]
        public int LookbackFull { get; set; } = 20;

        [NinjaScriptProperty]
        [Display(Name = "EMA Warm Len", Order = 8, GroupName = "Parameters")]
        public int EmaWarmLen { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "ATR Period", Order = 9, GroupName = "Parameters")]
        public int AtrPeriod { get; set; } = 30;

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", Order = 10, GroupName = "Visibility")]
        public bool ShowVWAP { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 11, GroupName = "Visibility")]
        public bool ShowWeeklyVWAP { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 12, GroupName = "Visibility")]
        public bool ShowAnchoredVWAP { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Zones", Order = 13, GroupName = "Visibility")]
        public bool ShowZones { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show AOI", Order = 14, GroupName = "Visibility")]
        public bool ShowAOI { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Enable Fail Marker", Order = 20, GroupName = "Failure")]
        public bool EnableFailMarker { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Fail Lookahead Bars", Order = 21, GroupName = "Failure")]
        public int FailLookaheadBars { get; set; } = 3;

        [NinjaScriptProperty]
        [Display(Name = "Fail Marker Color", Order = 22, GroupName = "Failure")]
        public System.Windows.Media.Brush FailMarkerColor { get; set; } = Brushes.Gold;

        private float rectHeight = 20f;
        private float bottomMargin = 40f;
        private float topMargin = 20f;
        private SharpDX.Direct2D1.SolidColorBrush brushText;
        private SharpDX.Direct2D1.SolidColorBrush brushFillGray;

        // ───────────────  STATE MACHINE  ───────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Absorption detection indicator.";
                Name        = "aaa5_absorption";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
            }
            else if (State == State.Configure)
            {
                buyVol  = new Dictionary<int, double>();
                sellVol = new Dictionary<int, double>();
                delta   = new Dictionary<int, double>();
                sideVol = new Dictionary<int, double>();
                flagInZone  = new Dictionary<int, bool>();
                flagAgg     = new Dictionary<int, bool>();
                flagRange   = new Dictionary<int, bool>();
                flagNoBreak = new Dictionary<int, bool>();
                flagAbs     = new Dictionary<int, bool>();

                smaQueue = new Queue<double>();
                failTracks = new List<FailTrack>();
            }
            else if (State == State.DataLoaded)
            {
                vwap  = aaa1_vwap(ShowWeeklyVWAP, true, true, ShowVWAP, true, true, UseAnchoredVWAP, DateTime.Today, "00:00");
                zones = aaa4_zones(0.21, 0.32, 0.13, 2, "1", 300, false);
                atr   = ATR(AtrPeriod);
                brushText    = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0f, 0f, 0f, 1f));
                brushFillGray = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0.7f,0.7f,0.7f,0.8f));
            }
            else if (State == State.Terminated)
            {
                brushText?.Dispose();
                brushFillGray?.Dispose();
            }
        }

        // ───────────────  MARKET DATA  ───────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int barIdx = CurrentBar;
            double price = e.Price;
            double vol = e.Volume;

            if (!buyVol.ContainsKey(barIdx))
            {
                buyVol[barIdx] = sellVol[barIdx] = 0.0;
            }

            double sign;
            if (price > lastTradePrice)
                sign = 1;
            else if (price < lastTradePrice)
                sign = -1;
            else
                sign = lastDirection;

            if (sign >= 0)
                buyVol[barIdx] += vol;
            else
                sellVol[barIdx] += vol;

            if (sign != 0)
                lastDirection = sign > 0 ? 1 : -1;
            lastTradePrice = price;
        }

        // ───────────────  BAR UPDATE  ───────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 1)
                return;

            if (IsFirstTickOfBar)
            {
                int b = CurrentBar - 1;

                double bBuy  = buyVol.ContainsKey(b) ? buyVol[b] : 0.0;
                double bSell = sellVol.ContainsKey(b) ? sellVol[b] : 0.0;
                double bDelta = bBuy - bSell;
                delta[b] = bDelta;

                double sv = Close[b] >= Open[b] ? bBuy : bSell;
                sideVol[b] = sv;

                double baseline;
                if (b < LookbackFull)
                {
                    double alpha = 2.0 / (EmaWarmLen + 1);
                    if (b == 0)
                        emaSV = sv;
                    else
                        emaSV = emaSV + alpha * (sv - emaSV);
                    baseline = emaSV;
                }
                else
                {
                    smaQueue.Enqueue(sv);
                    smaSum += sv;
                    if (smaQueue.Count > LookbackFull)
                        smaSum -= smaQueue.Dequeue();
                    baseline = smaSum / smaQueue.Count;
                }

                bool inZone; bool noBreak; bool isSupply; double upper=0, lower=0;
                CheckZones(b, out inZone, out noBreak, out isSupply, out upper, out lower);
                flagInZone[b]  = inZone;
                flagNoBreak[b] = noBreak;

                bool agg   = sv > baseline * VolFactor;
                bool range = true;
                if (CurrentBar >= AtrPeriod)
                    range = (High[b] - Low[b]) <= atr[b] * RangeFactor;
                flagAgg[b]   = agg;
                flagRange[b] = range;

                bool absorb = inZone && agg && range && noBreak && b >= MinBarsReady;
                flagAbs[b] = absorb;
                if (absorb)
                {
                    System.Windows.Media.Brush col = isSupply ? Brushes.Red : Brushes.Lime;
                    Draw.Diamond(this, "ABS" + b, false, 1, isSupply ? High[b] : Low[b], col);
                    if (EnableFailMarker)
                    {
                        failTracks.Add(new FailTrack { BarIdx = b, IsSupply = isSupply, Upper = upper, Lower = lower, Remaining = FailLookaheadBars });
                    }
                }

                UpdateFailTracks();
            }
        }

        private void UpdateFailTracks()
        {
            for (int i = failTracks.Count - 1; i >= 0; i--)
            {
                var f = failTracks[i];
                if (CurrentBar <= f.BarIdx)
                    continue;
                if (f.Remaining <= 0)
                {
                    failTracks.RemoveAt(i);
                    continue;
                }
                double closePrev = Close[0];
                if (IsFirstTickOfBar)
                    closePrev = Close[1];
                if (f.IsSupply && closePrev > f.Upper)
                {
                    Draw.Text(this, "FAIL" + f.BarIdx + "_" + f.Remaining, false, "R", 0, closePrev, 0, FailMarkerColor, new SimpleFont("Arial", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    failTracks.RemoveAt(i);
                }
                else if (!f.IsSupply && closePrev < f.Lower)
                {
                    Draw.Text(this, "FAIL" + f.BarIdx + "_" + f.Remaining, false, "R", 0, closePrev, 0, FailMarkerColor, new SimpleFont("Arial", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    failTracks.RemoveAt(i);
                }
                else
                {
                    f.Remaining--;
                    failTracks[i] = f;
                    if (f.Remaining <= 0)
                        failTracks.RemoveAt(i);
                }
            }
        }

        // ───────────────  ZONE CHECK  ───────────────
        private void CheckZones(int barIdx, out bool inZone, out bool noBreak, out bool isSupply, out double upper, out double lower)
        {
            inZone = false; noBreak = false; isSupply = false; upper = 0; lower = 0;
            double rectBuf = RectBufferTicks * TickSize;
            double lineBuf = LineBufferTicks * TickSize;

            int count = zones.GetZoneCount();
            for (int i = 0; i < count; i++)
            {
                if (!zones.TryGetZone(i, out bool supply, out double area1, out double area2, out double area3, out double aoi, out int ds))
                    continue;
                double top = Math.Max(area1, area3);
                double bot = Math.Min(area1, area3);
                if (High[barIdx] >= bot - rectBuf && Low[barIdx] <= top + rectBuf)
                {
                    inZone  = true;
                    noBreak = High[barIdx] <= top + rectBuf && Low[barIdx] >= bot - rectBuf;
                    isSupply = supply;
                    upper = top + rectBuf;
                    lower = bot - rectBuf;
                    return;
                }
                if (High[barIdx] >= aoi - lineBuf && Low[barIdx] <= aoi + lineBuf)
                {
                    inZone  = true;
                    noBreak = High[barIdx] <= aoi + lineBuf && Low[barIdx] >= aoi - lineBuf;
                    isSupply = supply;
                    upper = aoi + lineBuf;
                    lower = aoi - lineBuf;
                    return;
                }
            }

            double buf = lineBuf;
            bool touched = false;
            if (ShowVWAP)
            {
                for (int j = 5; j <= 10; j++)
                {
                    double val = vwap.Values[j][barIdx];
                    if (!double.IsNaN(val) && High[barIdx] >= val - buf && Low[barIdx] <= val + buf)
                    {
                        touched = true;
                        noBreak = High[barIdx] <= val + buf && Low[barIdx] >= val - buf;
                        upper = val + buf; lower = val - buf;
                        break;
                    }
                }
            }
            if (!touched && ShowWeeklyVWAP)
            {
                for (int j = 0; j <= 4; j++)
                {
                    double val = vwap.Values[j][barIdx];
                    if (!double.IsNaN(val) && High[barIdx] >= val - buf && Low[barIdx] <= val + buf)
                    {
                        touched = true;
                        noBreak = High[barIdx] <= val + buf && Low[barIdx] >= val - buf;
                        upper = val + buf; lower = val - buf;
                        break;
                    }
                }
            }
            if (!touched && UseAnchoredVWAP && ShowAnchoredVWAP)
            {
                double val = vwap.Values[6][barIdx];
                if (!double.IsNaN(val) && High[barIdx] >= val - buf && Low[barIdx] <= val + buf)
                {
                    touched = true;
                    noBreak = High[barIdx] <= val + buf && Low[barIdx] >= val - buf;
                    upper = val + buf; lower = val - buf;
                }
            }

            if (touched)
            {
                inZone = true;
                isSupply = Close[barIdx] >= Open[barIdx];
            }
            else if (!UseZonesOnly)
            {
                inZone = true;
                noBreak = true;
                isSupply = Close[barIdx] >= Open[barIdx];
                upper = High[barIdx];
                lower = Low[barIdx];
            }
        }

        // ───────────────  RENDER  ───────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartBars == null || RenderTarget == null)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            const int bottomRows = 2;
            float yTopBottom = yBottom - bottomMargin - rectHeight * bottomRows;

            float yChartTop = (float)chartScale.GetYByValue(chartScale.MaxValue);
            const int topRows = 5;
            float yTopTop = yChartTop + topMargin;

            double maxAbs = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (delta.ContainsKey(i))
                    maxAbs = Math.Max(maxAbs, Math.Abs(delta[i]));
            }
            if (maxAbs.Equals(0.0))
                maxAbs = 1.0;

            using (var deltaFmt = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    DrawDeltaCell(i, maxAbs, xLeft, yTopBottom, barWidth, rectHeight, deltaFmt);
                    DrawVolumeCell(i, xLeft, yTopBottom + rectHeight, barWidth, rectHeight, deltaFmt);

                    float yTop = yTopTop;
                    DrawFlagCell(flagInZone, i, xLeft, yTop, barWidth, rectHeight, deltaFmt, "In Zone");
                    yTop += rectHeight;
                    DrawFlagCell(flagAgg, i, xLeft, yTop, barWidth, rectHeight, deltaFmt, "High Vol");
                    yTop += rectHeight;
                    DrawFlagCell(flagRange, i, xLeft, yTop, barWidth, rectHeight, deltaFmt, "Small Rng");
                    yTop += rectHeight;
                    DrawFlagCell(flagNoBreak, i, xLeft, yTop, barWidth, rectHeight, deltaFmt, "No Break");
                    yTop += rectHeight;
                    DrawFlagCell(flagAbs, i, xLeft, yTop, barWidth, rectHeight, deltaFmt, "Absorp");
                }
            }
        }

        private void DrawDeltaCell(int barIdx, double maxAbs, float xLeft, float yTop, float width, float height, SharpDX.DirectWrite.TextFormat fmt)
        {
            double val = delta.ContainsKey(barIdx) ? delta[barIdx] : 0.0;
            float intensity = (float)(Math.Abs(val) / maxAbs);
            intensity = Math.Max(0.2f, Math.Min(1f, intensity));

            SharpDX.Color fillColor = val >= 0 ? new SharpDX.Color(0f, intensity, 0f, intensity)
                                              : new SharpDX.Color(intensity, 0f, 0f, intensity);
            using (var fill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor))
                RenderTarget.FillRectangle(new RectangleF(xLeft, yTop, width, height), fill);
            RenderTarget.DrawRectangle(new RectangleF(xLeft, yTop, width, height), brushText, 1f);

            string txt = val.ToString("0");
            using var tl = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height);
            float tx = xLeft + (width  - tl.Metrics.Width) / 2f;
            float ty = yTop  + (height - tl.Metrics.Height) / 2f;
            RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
        }

        private void DrawVolumeCell(int barIdx, float xLeft, float yTop, float width, float height, SharpDX.DirectWrite.TextFormat fmt)
        {
            double vol = (buyVol.ContainsKey(barIdx) ? buyVol[barIdx] : 0.0) + (sellVol.ContainsKey(barIdx) ? sellVol[barIdx] : 0.0);
            RenderTarget.DrawRectangle(new RectangleF(xLeft, yTop, width, height), brushText, 1f);
            string txt = vol.ToString("0");
            using var tl = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height);
            float tx = xLeft + (width  - tl.Metrics.Width) / 2f;
            float ty = yTop  + (height - tl.Metrics.Height) / 2f;
            RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
        }

        private void DrawFlagCell(Dictionary<int, bool> src, int barIdx, float xLeft, float yTop, float width, float height, SharpDX.DirectWrite.TextFormat fmt, string label)
        {
            bool ok = src.ContainsKey(barIdx) && src[barIdx];
            if (ok)
                RenderTarget.FillRectangle(new RectangleF(xLeft, yTop, width, height), brushFillGray);
            RenderTarget.DrawRectangle(new RectangleF(xLeft, yTop, width, height), brushText, 1f);

            string txt = ok ? label : string.Empty;
            using var tl = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height);
            float tx = xLeft + (width  - tl.Metrics.Width) / 2f;
            float ty = yTop  + (height - tl.Metrics.Height) / 2f;
            RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private aaa5_absorption[] cacheaaa5_absorption;
        public aaa5_absorption aaa5_absorption(bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            return aaa5_absorption(Input, useZonesOnly, useAnchoredVWAP, volFactor, rangeFactor, lineBufferTicks, rectBufferTicks, minBarsReady, lookbackFull, emaWarmLen, atrPeriod, showVWAP, showWeeklyVWAP, showAnchoredVWAP, showZones, showAOI, enableFailMarker, failLookaheadBars, failMarkerColor);
        }

        public aaa5_absorption aaa5_absorption(ISeries<double> input, bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            if (cacheaaa5_absorption != null)
                for (int idx = 0; idx < cacheaaa5_absorption.Length; idx++)
                    if (cacheaaa5_absorption[idx] != null && cacheaaa5_absorption[idx].UseZonesOnly == useZonesOnly && cacheaaa5_absorption[idx].UseAnchoredVWAP == useAnchoredVWAP && cacheaaa5_absorption[idx].VolFactor == volFactor && cacheaaa5_absorption[idx].RangeFactor == rangeFactor && cacheaaa5_absorption[idx].LineBufferTicks == lineBufferTicks && cacheaaa5_absorption[idx].RectBufferTicks == rectBufferTicks && cacheaaa5_absorption[idx].MinBarsReady == minBarsReady && cacheaaa5_absorption[idx].LookbackFull == lookbackFull && cacheaaa5_absorption[idx].EmaWarmLen == emaWarmLen && cacheaaa5_absorption[idx].AtrPeriod == atrPeriod && cacheaaa5_absorption[idx].ShowVWAP == showVWAP && cacheaaa5_absorption[idx].ShowWeeklyVWAP == showWeeklyVWAP && cacheaaa5_absorption[idx].ShowAnchoredVWAP == showAnchoredVWAP && cacheaaa5_absorption[idx].ShowZones == showZones && cacheaaa5_absorption[idx].ShowAOI == showAOI && cacheaaa5_absorption[idx].EnableFailMarker == enableFailMarker && cacheaaa5_absorption[idx].FailLookaheadBars == failLookaheadBars && cacheaaa5_absorption[idx].FailMarkerColor == failMarkerColor && cacheaaa5_absorption[idx].EqualsInput(input))
                        return cacheaaa5_absorption[idx];
            return CacheIndicator<aaa5_absorption>(new aaa5_absorption(){ UseZonesOnly = useZonesOnly, UseAnchoredVWAP = useAnchoredVWAP, VolFactor = volFactor, RangeFactor = rangeFactor, LineBufferTicks = lineBufferTicks, RectBufferTicks = rectBufferTicks, MinBarsReady = minBarsReady, LookbackFull = lookbackFull, EmaWarmLen = emaWarmLen, AtrPeriod = atrPeriod, ShowVWAP = showVWAP, ShowWeeklyVWAP = showWeeklyVWAP, ShowAnchoredVWAP = showAnchoredVWAP, ShowZones = showZones, ShowAOI = showAOI, EnableFailMarker = enableFailMarker, FailLookaheadBars = failLookaheadBars, FailMarkerColor = failMarkerColor }, input, ref cacheaaa5_absorption);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.aaa5_absorption aaa5_absorption(bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            return indicator.aaa5_absorption(Input, useZonesOnly, useAnchoredVWAP, volFactor, rangeFactor, lineBufferTicks, rectBufferTicks, minBarsReady, lookbackFull, emaWarmLen, atrPeriod, showVWAP, showWeeklyVWAP, showAnchoredVWAP, showZones, showAOI, enableFailMarker, failLookaheadBars, failMarkerColor);
        }

        public Indicators.aaa5_absorption aaa5_absorption(ISeries<double> input , bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            return indicator.aaa5_absorption(input, useZonesOnly, useAnchoredVWAP, volFactor, rangeFactor, lineBufferTicks, rectBufferTicks, minBarsReady, lookbackFull, emaWarmLen, atrPeriod, showVWAP, showWeeklyVWAP, showAnchoredVWAP, showZones, showAOI, enableFailMarker, failLookaheadBars, failMarkerColor);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.aaa5_absorption aaa5_absorption(bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            return indicator.aaa5_absorption(Input, useZonesOnly, useAnchoredVWAP, volFactor, rangeFactor, lineBufferTicks, rectBufferTicks, minBarsReady, lookbackFull, emaWarmLen, atrPeriod, showVWAP, showWeeklyVWAP, showAnchoredVWAP, showZones, showAOI, enableFailMarker, failLookaheadBars, failMarkerColor);
        }

        public Indicators.aaa5_absorption aaa5_absorption(ISeries<double> input , bool useZonesOnly, bool useAnchoredVWAP, double volFactor, double rangeFactor, int lineBufferTicks, int rectBufferTicks, int minBarsReady, int lookbackFull, int emaWarmLen, int atrPeriod, bool showVWAP, bool showWeeklyVWAP, bool showAnchoredVWAP, bool showZones, bool showAOI, bool enableFailMarker, int failLookaheadBars, Brush failMarkerColor)
        {
            return indicator.aaa5_absorption(input, useZonesOnly, useAnchoredVWAP, volFactor, rangeFactor, lineBufferTicks, rectBufferTicks, minBarsReady, lookbackFull, emaWarmLen, atrPeriod, showVWAP, showWeeklyVWAP, showAnchoredVWAP, showZones, showAOI, enableFailMarker, failLookaheadBars, failMarkerColor);
        }
    }
}

#endregion
