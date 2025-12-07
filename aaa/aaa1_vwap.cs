// aaa1_vwap.cs - Triple VWAP indicator updated for NinjaTrader 8.1.5
// Derived from original a1.cs indicator for NinjaTrader 8.1.4
// This version uses Calculate.OnEachTick and supports Weekly, Session, and Anchored VWAP.

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class aaa1_vwap : Indicator
    {
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 0, GroupName = "Weekly VWAP")]
        public bool ShowWeekly { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 1 (±1σ)", Order = 1, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands1 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 2 (±2σ)", Order = 2, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands2 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 0, GroupName = "Session VWAP")]
        public bool ShowSession { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 1 (±1σ)", Order = 1, GroupName = "Session VWAP")]
        public bool ShowSessionBands1 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 2 (±2σ)", Order = 2, GroupName = "Session VWAP")]
        public bool ShowSessionBands2 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 0, GroupName = "Anchored VWAP")]
        public bool ShowAnchored { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 1 (±1σ)", Order = 3, GroupName = "Anchored VWAP")]
        public bool ShowAnchoredBands1 { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 2 (±2σ)", Order = 4, GroupName = "Anchored VWAP")]
        public bool ShowAnchoredBands2 { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Date", Order = 1, GroupName = "Anchored VWAP")]
        public DateTime AnchorDate { get; set; } = DateTime.Today;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Time (HH:mm)", Order = 2, GroupName = "Anchored VWAP")]
        [RegularExpression("^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Formato HH:mm")]
        public string AnchorTime
        {
            get => anchorTime;
            set => anchorTime = NormalizeAnchorTime(value);
        }

        internal static string NormalizeAnchorTime(string time)
        {
            return string.IsNullOrWhiteSpace(time) ? "00:00" : time;
        }

        private double wSumPV, wSumV, wSumVarPV;
        private double sSumPV, sSumV, sSumVarPV;
        private double aSumPV, aSumV;
        private double aSumP2V;
        private bool anchorActive;
        private double prevVol;
        private string anchorTime = "00:00";

        private DateTime AnchorDateTime => DateTime.TryParse(AnchorTime, out var t)
            ? AnchorDate.Date.Add(t.TimeOfDay)
            : AnchorDate.Date;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "aaa1_vwap";
                IsOverlay = true;
                Calculate = Calculate.OnEachTick;

                AddPlot(Brushes.Blue, "Weekly VWAP");      // 0
                AddPlot(Brushes.Green, "+1σ");             // 1
                AddPlot(Brushes.Green, "-1σ");             // 2
                AddPlot(Brushes.Green, "+2σ");             // 3
                AddPlot(Brushes.Green, "-2σ");             // 4
                AddPlot(Brushes.Blue, "Session VWAP");     // 5
                AddPlot(Brushes.Yellow, "Anchored VWAP");  // 6
                AddPlot(Brushes.Purple, "Session +1σ");    // 7
                AddPlot(Brushes.Purple, "Session -1σ");    // 8
                AddPlot(Brushes.Purple, "Session +2σ");    // 9
                AddPlot(Brushes.Purple, "Session -2σ");    //10
                AddPlot(Brushes.Orange, "Anch +1σ");   // 11
                AddPlot(Brushes.Orange, "Anch -1σ");   // 12
                AddPlot(Brushes.Orange, "Anch +2σ");   // 13
                AddPlot(Brushes.Orange, "Anch -2σ");   // 14
            }
        }

        protected override void OnBarUpdate()
        {
            if (IsFirstTickOfBar)
            {
                prevVol = 0;

                if (Bars.IsFirstBarOfSession)
                {
                    sSumPV = sSumV = sSumVarPV = 0;

                    if (Time[0].DayOfWeek == DayOfWeek.Sunday)
                        wSumPV = wSumV = wSumVarPV = 0;
                }
            }

            double cumVol = Volume[0];
            double deltaVol = cumVol - prevVol;
            prevVol = cumVol;
            if (deltaVol <= 0)
                return;

            double price = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;

            wSumPV += price * deltaVol;
            wSumV += deltaVol;
            double wVWAP = wSumPV / wSumV;
            wSumVarPV += deltaVol * Math.Pow(price - wVWAP, 2);
            double wSigma = Math.Sqrt(wSumVarPV / wSumV);

            sSumPV += price * deltaVol;
            sSumV += deltaVol;
            double sVWAP = sSumPV / sSumV;
            sSumVarPV += deltaVol * Math.Pow(price - sVWAP, 2);
            double sSigma = Math.Sqrt(sSumVarPV / sSumV);

            DateTime anchorDT = AnchorDateTime;
            if (ShowAnchored)
            {
                if (!anchorActive && Time[0] >= anchorDT)
                {
                    aSumPV = aSumV = aSumP2V = 0;
                    anchorActive = true;
                }
                if (anchorActive)
                {
                    aSumPV += price * deltaVol;
                    aSumV += deltaVol;
                    aSumP2V += price * price * deltaVol;
                }
            }
            double aVWAP  = aSumV == 0 ? price : aSumPV / aSumV;
            double aSigma = aSumV == 0 ? 0     : Math.Sqrt((aSumP2V / aSumV) - aVWAP * aVWAP);

            if (ShowWeekly)
            {
                Values[0][0] = wVWAP;
                if (ShowWeeklyBands1)
                {
                    Values[1][0] = wVWAP + wSigma;
                    Values[2][0] = wVWAP - wSigma;
                    Values[3][0] = ShowWeeklyBands2 ? wVWAP + 2 * wSigma : double.NaN;
                    Values[4][0] = ShowWeeklyBands2 ? wVWAP - 2 * wSigma : double.NaN;
                }
                else
                    for (int i = 1; i <= 4; i++)
                        Values[i][0] = double.NaN;
            }
            else
                for (int i = 0; i <= 4; i++)
                    Values[i][0] = double.NaN;

            if (ShowSession)
            {
                Values[5][0] = sVWAP;
                if (ShowSessionBands1)
                {
                    Values[7][0] = sVWAP + sSigma;
                    Values[8][0] = sVWAP - sSigma;
                    Values[9][0] = ShowSessionBands2 ? sVWAP + 2 * sSigma : double.NaN;
                    Values[10][0] = ShowSessionBands2 ? sVWAP - 2 * sSigma : double.NaN;
                }
                else
                    for (int i = 7; i <= 10; i++)
                        Values[i][0] = double.NaN;
            }
            else
            {
                Values[5][0] = double.NaN;
                for (int i = 7; i <= 10; i++)
                    Values[i][0] = double.NaN;
            }

            if (ShowAnchored && anchorActive)
            {
                Values[6][0]  = aVWAP;

                Values[11][0] = ShowAnchoredBands1 ? aVWAP + aSigma     : double.NaN;
                Values[12][0] = ShowAnchoredBands1 ? aVWAP - aSigma     : double.NaN;
                Values[13][0] = ShowAnchoredBands2 ? aVWAP + 2 * aSigma : double.NaN;
                Values[14][0] = ShowAnchoredBands2 ? aVWAP - 2 * aSigma : double.NaN;
            }
            else
            {
                Values[6][0]  = double.NaN;
                Values[11][0] = Values[12][0] = Values[13][0] = Values[14][0] = double.NaN;
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private aaa1_vwap[] cacheaaa1_vwap;
        public aaa1_vwap aaa1_vwap(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            string anchorTimeSafe = aaa1_vwap.NormalizeAnchorTime(anchorTime);

            return aaa1_vwap(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showSessionBands1, showSessionBands2, showAnchored, anchorDate, anchorTimeSafe, showAnchoredBands1, showAnchoredBands2);
        }

        public aaa1_vwap aaa1_vwap(ISeries<double> input, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            string anchorTimeSafe = aaa1_vwap.NormalizeAnchorTime(anchorTime);

            if (cacheaaa1_vwap != null)
                for (int idx = 0; idx < cacheaaa1_vwap.Length; idx++)
                    if (cacheaaa1_vwap[idx] != null && cacheaaa1_vwap[idx].ShowWeekly == showWeekly && cacheaaa1_vwap[idx].ShowWeeklyBands1 == showWeeklyBands1 && cacheaaa1_vwap[idx].ShowWeeklyBands2 == showWeeklyBands2 && cacheaaa1_vwap[idx].ShowSession == showSession && cacheaaa1_vwap[idx].ShowSessionBands1 == showSessionBands1 && cacheaaa1_vwap[idx].ShowSessionBands2 == showSessionBands2 && cacheaaa1_vwap[idx].ShowAnchored == showAnchored && cacheaaa1_vwap[idx].AnchorDate == anchorDate && cacheaaa1_vwap[idx].AnchorTime == anchorTimeSafe && cacheaaa1_vwap[idx].ShowAnchoredBands1 == showAnchoredBands1 && cacheaaa1_vwap[idx].ShowAnchoredBands2 == showAnchoredBands2 && cacheaaa1_vwap[idx].EqualsInput(input))
                        return cacheaaa1_vwap[idx];
            return CacheIndicator<aaa1_vwap>(new aaa1_vwap() { ShowWeekly = showWeekly, ShowWeeklyBands1 = showWeeklyBands1, ShowWeeklyBands2 = showWeeklyBands2, ShowSession = showSession, ShowSessionBands1 = showSessionBands1, ShowSessionBands2 = showSessionBands2, ShowAnchored = showAnchored, AnchorDate = anchorDate, AnchorTime = anchorTimeSafe, ShowAnchoredBands1 = showAnchoredBands1, ShowAnchoredBands2 = showAnchoredBands2 }, input, ref cacheaaa1_vwap);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.aaa1_vwap aaa1_vwap(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            return indicator.aaa1_vwap(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showSessionBands1, showSessionBands2, showAnchored, anchorDate, anchorTime, showAnchoredBands1, showAnchoredBands2);
        }

        public Indicators.aaa1_vwap aaa1_vwap(ISeries<double> input, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            return indicator.aaa1_vwap(input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showSessionBands1, showSessionBands2, showAnchored, anchorDate, anchorTime, showAnchoredBands1, showAnchoredBands2);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.aaa1_vwap aaa1_vwap(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            return indicator.aaa1_vwap(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showSessionBands1, showSessionBands2, showAnchored, anchorDate, anchorTime, showAnchoredBands1, showAnchoredBands2);
        }

        public Indicators.aaa1_vwap aaa1_vwap(ISeries<double> input, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showSessionBands1, bool showSessionBands2, bool showAnchored, DateTime anchorDate, string anchorTime, bool showAnchoredBands1, bool showAnchoredBands2)
        {
            return indicator.aaa1_vwap(input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showSessionBands1, showSessionBands2, showAnchored, anchorDate, anchorTime, showAnchoredBands1, showAnchoredBands2);
        }
    }
}

#endregion
