// a21avwap.cs - Anchored VWAP dual module with configurable bands
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a21avwap : Indicator
    {
        private struct AnchorState
        {
            public double SumPV;
            public double SumP2V;
            public double SumV;
            public int    StartIndex;
            public DateTime AnchorDateTime;
        }

        private AnchorState anchor1;
        private AnchorState anchor2;
        private double      sessionSumPV;
        private double      sessionSumP2V;
        private double      sessionSumV;
        private double      weeklySumPV;
        private double      weeklySumP2V;
        private double      weeklySumV;
        private DateTime    currentWeekStart;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                      = "a21avwap";
                Calculate                 = Calculate.OnBarClose;
                IsOverlay                 = true;
                IsSuspendedWhileInactive  = true;

                BarsRequiredToPlot        = 0;

                ShowAnchored              = true;
                Anchored1                 = true;
                Anchored2                 = false;
                VwapSessionEnabled        = false;
                VwapWeeklyEnabled         = true;

                Anchor1Date               = DateTime.Today;
                Anchor1Time               = "00:00";
                ShowAnchor1Band1          = true;
                ShowAnchor1Band2          = false;

                Anchor2Date               = DateTime.Today;
                Anchor2Time               = "00:00";
                ShowAnchor2Band1          = false;
                ShowAnchor2Band2          = false;

                ShowSessionBand1          = true;
                ShowSessionBand2          = false;

                ShowWeeklyBand1           = true;
                ShowWeeklyBand2           = false;

                AddPlot(Brushes.DodgerBlue, "AnchoredVWAP1");
                AddPlot(Brushes.DodgerBlue, "Anchored1+1");
                AddPlot(Brushes.DodgerBlue, "Anchored1-1");
                AddPlot(Brushes.DodgerBlue, "Anchored1+2");
                AddPlot(Brushes.DodgerBlue, "Anchored1-2");

                AddPlot(Brushes.DodgerBlue, "AnchoredVWAP2");
                AddPlot(Brushes.DodgerBlue, "Anchored2+1");
                AddPlot(Brushes.DodgerBlue, "Anchored2-1");
                AddPlot(Brushes.DodgerBlue, "Anchored2+2");
                AddPlot(Brushes.DodgerBlue, "Anchored2-2");

                AddPlot(Brushes.DodgerBlue, "SessionVWAP");
                AddPlot(Brushes.DodgerBlue, "Session+1");
                AddPlot(Brushes.DodgerBlue, "Session-1");
                AddPlot(Brushes.DodgerBlue, "Session+2");
                AddPlot(Brushes.DodgerBlue, "Session-2");

                AddPlot(Brushes.DodgerBlue, "WeeklyVWAP");
                AddPlot(Brushes.DodgerBlue, "Weekly+1");
                AddPlot(Brushes.DodgerBlue, "Weekly-1");
                AddPlot(Brushes.DodgerBlue, "Weekly+2");
                AddPlot(Brushes.DodgerBlue, "Weekly-2");

                Plots[0].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[1].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[2].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[3].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[4].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[5].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[6].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[7].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[8].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[9].DashStyleHelper  = DashStyleHelper.Dash;
                Plots[10].DashStyleHelper = DashStyleHelper.Dot;
                Plots[11].DashStyleHelper = DashStyleHelper.Dot;
                Plots[12].DashStyleHelper = DashStyleHelper.Dot;
                Plots[13].DashStyleHelper = DashStyleHelper.Dot;
                Plots[14].DashStyleHelper = DashStyleHelper.Dot;
                Plots[15].DashStyleHelper = DashStyleHelper.Solid;
                Plots[16].DashStyleHelper = DashStyleHelper.Solid;
                Plots[17].DashStyleHelper = DashStyleHelper.Solid;
                Plots[18].DashStyleHelper = DashStyleHelper.Solid;
                Plots[19].DashStyleHelper = DashStyleHelper.Solid;

                Plots[0].Width  = 2;
                Plots[1].Width  = 1;
                Plots[2].Width  = 1;
                Plots[3].Width  = 1;
                Plots[4].Width  = 1;
                Plots[5].Width  = 2;
                Plots[6].Width  = 1;
                Plots[7].Width  = 1;
                Plots[8].Width  = 1;
                Plots[9].Width  = 1;
                Plots[10].Width = 2;
                Plots[11].Width = 1;
                Plots[12].Width = 1;
                Plots[13].Width = 1;
                Plots[14].Width = 1;
                Plots[15].Width = 2;
                Plots[16].Width = 1;
                Plots[17].Width = 1;
                Plots[18].Width = 1;
                Plots[19].Width = 1;
            }
            else if (State == State.DataLoaded)
            {
                // Ajustar la fecha por defecto a la del primer bar disponible si el usuario no la cambió
                DateTime firstBarDate = Bars.Count > 0 ? Bars.GetTime(0).Date : DateTime.Today;

                if (Anchor1Date.Date == DateTime.Today)
                    Anchor1Date = firstBarDate;

                if (Anchor2Date.Date == DateTime.Today)
                    Anchor2Date = firstBarDate;

                anchor1 = InitializeAnchor(Anchor1Date, Anchor1Time);
                anchor2 = InitializeAnchor(Anchor2Date, Anchor2Time);
                sessionSumPV   = sessionSumP2V = sessionSumV = 0;
                weeklySumPV    = weeklySumP2V = weeklySumV = 0;
                currentWeekStart = DateTime.MinValue;
            }
        }

        protected override void OnBarUpdate()
        {
            if (!ShowAnchored)
            {
                SetAnchorPlotsToNan(0);
                SetAnchorPlotsToNan(5);
            }
            else
            {
                // Resolve anchor DateTime from current properties in case user modified them
                UpdateAnchorDateTimes();

                ProcessAnchor(ref anchor1, Anchored1, ShowAnchor1Band1, ShowAnchor1Band2, 0);
                ProcessAnchor(ref anchor2, Anchored2, ShowAnchor2Band1, ShowAnchor2Band2, 5);
            }

            ProcessSessionVwap();
            ProcessWeeklyVwap();
        }

        private AnchorState InitializeAnchor(DateTime anchorDate, string anchorTime)
        {
            AnchorState state = new AnchorState
            {
                SumPV         = 0,
                SumP2V        = 0,
                SumV          = 0,
                StartIndex    = -1,
                AnchorDateTime = BuildAnchorDateTime(anchorDate, anchorTime)
            };

            return state;
        }

        private void UpdateAnchorDateTimes()
        {
            DateTime newAnchor1 = BuildAnchorDateTime(Anchor1Date, Anchor1Time);
            if (newAnchor1 != anchor1.AnchorDateTime)
            {
                anchor1 = InitializeAnchor(Anchor1Date, Anchor1Time);
            }

            DateTime newAnchor2 = BuildAnchorDateTime(Anchor2Date, Anchor2Time);
            if (newAnchor2 != anchor2.AnchorDateTime)
            {
                anchor2 = InitializeAnchor(Anchor2Date, Anchor2Time);
            }
        }

        private void ProcessAnchor(ref AnchorState anchorState, bool enabled, bool showBand1, bool showBand2, int plotOffset)
        {
            if (!enabled)
            {
                SetAnchorPlotsToNan(plotOffset);
                return;
            }

            EnsureStartIndex(ref anchorState);

            if (anchorState.StartIndex == int.MaxValue || CurrentBar < anchorState.StartIndex)
            {
                SetAnchorPlotsToNan(plotOffset);
                return;
            }

            if (CurrentBar == anchorState.StartIndex)
            {
                anchorState.SumPV  = 0;
                anchorState.SumP2V = 0;
                anchorState.SumV   = 0;
            }

            double priceBar = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double volBar   = Volume[0];

            anchorState.SumPV  += priceBar * volBar;
            anchorState.SumP2V += priceBar * priceBar * volBar;
            anchorState.SumV   += volBar;

            if (Math.Abs(anchorState.SumV) <= double.Epsilon)
            {
                SetAnchorPlotsToNan(plotOffset);
                return;
            }

            double vwap     = anchorState.SumPV / anchorState.SumV;
            double variance = (anchorState.SumP2V / anchorState.SumV) - vwap * vwap;
            double stdDev   = variance > 0 ? Math.Sqrt(variance) : 0;

            Values[plotOffset][0] = vwap;
            Values[plotOffset + 1][0] = showBand1 ? vwap + stdDev : double.NaN;
            Values[plotOffset + 2][0] = showBand1 ? vwap - stdDev : double.NaN;
            Values[plotOffset + 3][0] = showBand2 ? vwap + 2 * stdDev : double.NaN;
            Values[plotOffset + 4][0] = showBand2 ? vwap - 2 * stdDev : double.NaN;
        }

        private void EnsureStartIndex(ref AnchorState anchorState)
        {
            DateTime anchorTime = anchorState.AnchorDateTime;

            // Determine start index lazily
            if (anchorState.StartIndex == -1 || anchorState.StartIndex == int.MaxValue)
            {
                for (int idx = 0; idx <= CurrentBar; idx++)
                {
                    if (Time[idx] >= anchorTime)
                    {
                        anchorState.StartIndex = idx;
                        break;
                    }
                }

                if (anchorState.StartIndex == -1)
                    anchorState.StartIndex = int.MaxValue;
            }
        }

        private DateTime BuildAnchorDateTime(DateTime datePart, string timePart)
        {
            TimeSpan parsedTime;
            if (TimeSpan.TryParseExact(timePart, "HH\\:mm", CultureInfo.InvariantCulture, out parsedTime))
                return datePart.Date + parsedTime;

            if (TimeSpan.TryParseExact(timePart, "HH\\:mm\\:ss", CultureInfo.InvariantCulture, out parsedTime))
                return datePart.Date + parsedTime;

            if (TimeSpan.TryParse(timePart, CultureInfo.InvariantCulture, out parsedTime))
                return datePart.Date + parsedTime;

            return datePart.Date;
        }

        private void SetAllNan()
        {
            for (int i = 0; i < 20; i++)
                Values[i][0] = double.NaN;
        }

        private void SetAnchorPlotsToNan(int plotOffset)
        {
            for (int i = plotOffset; i < plotOffset + 5; i++)
                Values[i][0] = double.NaN;
        }

        private void ProcessSessionVwap()
        {
            if (!VwapSessionEnabled)
            {
                SetAnchorPlotsToNan(10);
                return;
            }

            if (Bars.IsFirstBarOfSession)
            {
                sessionSumPV  = 0;
                sessionSumP2V = 0;
                sessionSumV   = 0;
            }

            double priceBar = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double volBar   = Volume[0];

            sessionSumPV  += priceBar * volBar;
            sessionSumP2V += priceBar * priceBar * volBar;
            sessionSumV   += volBar;

            if (Math.Abs(sessionSumV) <= double.Epsilon)
            {
                SetAnchorPlotsToNan(10);
                return;
            }

            double vwap     = sessionSumPV / sessionSumV;
            double variance = (sessionSumP2V / sessionSumV) - vwap * vwap;
            double stdDev   = variance > 0 ? Math.Sqrt(variance) : 0;

            Values[10][0] = vwap;
            Values[11][0] = ShowSessionBand1 ? vwap + stdDev : double.NaN;
            Values[12][0] = ShowSessionBand1 ? vwap - stdDev : double.NaN;
            Values[13][0] = ShowSessionBand2 ? vwap + 2 * stdDev : double.NaN;
            Values[14][0] = ShowSessionBand2 ? vwap - 2 * stdDev : double.NaN;
        }

        private void ProcessWeeklyVwap()
        {
            if (!VwapWeeklyEnabled)
            {
                SetAnchorPlotsToNan(15);
                return;
            }

            DateTime barTime   = Time[0];
            DateTime barDate   = barTime.Date;
            int      daysSinceSunday = (int)barDate.DayOfWeek;
            DateTime sundayDate      = barDate.AddDays(-daysSinceSunday);
            DateTime weekStart       = sundayDate.AddHours(18);

            if (barTime < weekStart)
                weekStart = sundayDate.AddDays(-7).AddHours(18);

            if (weekStart != currentWeekStart)
            {
                weeklySumPV     = 0;
                weeklySumP2V    = 0;
                weeklySumV      = 0;
                currentWeekStart = weekStart;
            }

            double priceBar = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double volBar   = Volume[0];

            weeklySumPV  += priceBar * volBar;
            weeklySumP2V += priceBar * priceBar * volBar;
            weeklySumV   += volBar;

            if (Math.Abs(weeklySumV) <= double.Epsilon)
            {
                SetAnchorPlotsToNan(15);
                return;
            }

            double vwap     = weeklySumPV / weeklySumV;
            double variance = (weeklySumP2V / weeklySumV) - vwap * vwap;
            double stdDev   = variance > 0 ? Math.Sqrt(variance) : 0;

            Values[15][0] = vwap;
            Values[16][0] = ShowWeeklyBand1 ? vwap + stdDev : double.NaN;
            Values[17][0] = ShowWeeklyBand1 ? vwap - stdDev : double.NaN;
            Values[18][0] = ShowWeeklyBand2 ? vwap + 2 * stdDev : double.NaN;
            Values[19][0] = ShowWeeklyBand2 ? vwap - 2 * stdDev : double.NaN;
        }

        #region Properties

        [Category("Global")]
        [Display(Name = "show anchored", Order = 1, GroupName = "Global")]
        public bool ShowAnchored { get; set; }

        [Category("Global")]
        [Display(Name = "anchored 1", Order = 2, GroupName = "Global")]
        public bool Anchored1 { get; set; }

        [Category("Global")]
        [Display(Name = "anchored 2", Order = 3, GroupName = "Global")]
        public bool Anchored2 { get; set; }

        [Category("Global")]
        [Display(Name = "vwap session", Order = 4, GroupName = "Global")]
        public bool VwapSessionEnabled { get; set; }

        [Category("Global")]
        [Display(Name = "vwap weekly", Order = 5, GroupName = "Global")]
        public bool VwapWeeklyEnabled { get; set; }

        [Category("Anchored 1")]
        [Display(Name = "fecha", Order = 1, GroupName = "Anchored 1")]
        public DateTime Anchor1Date { get; set; }

        [Category("Anchored 1")]
        [Display(Name = "hora", Order = 2, GroupName = "Anchored 1")]
        public string Anchor1Time { get; set; }

        [Category("Anchored 1")]
        [Display(Name = "show anchored banda +-1", Order = 3, GroupName = "Anchored 1")]
        public bool ShowAnchor1Band1 { get; set; }

        [Category("Anchored 1")]
        [Display(Name = "show banda +-2", Order = 4, GroupName = "Anchored 1")]
        public bool ShowAnchor1Band2 { get; set; }

        [Category("Anchored 2")]
        [Display(Name = "fecha", Order = 1, GroupName = "Anchored 2")]
        public DateTime Anchor2Date { get; set; }

        [Category("Anchored 2")]
        [Display(Name = "hora", Order = 2, GroupName = "Anchored 2")]
        public string Anchor2Time { get; set; }

        [Category("Anchored 2")]
        [Display(Name = "show anchored banda +-1", Order = 3, GroupName = "Anchored 2")]
        public bool ShowAnchor2Band1 { get; set; }

        [Category("Anchored 2")]
        [Display(Name = "show banda +-2", Order = 4, GroupName = "Anchored 2")]
        public bool ShowAnchor2Band2 { get; set; }

        [Category("vwap session")]
        [Display(Name = "show band +-1", Order = 1, GroupName = "vwap session")]
        public bool ShowSessionBand1 { get; set; }

        [Category("vwap session")]
        [Display(Name = "show band +-2", Order = 2, GroupName = "vwap session")]
        public bool ShowSessionBand2 { get; set; }

        [Category("vwap weekly")]
        [Display(Name = "show band +-1", Order = 1, GroupName = "vwap weekly")]
        public bool ShowWeeklyBand1 { get; set; }

        [Category("vwap weekly")]
        [Display(Name = "show band +-2", Order = 2, GroupName = "vwap weekly")]
        public bool ShowWeeklyBand2 { get; set; }

        #endregion
    }
}
