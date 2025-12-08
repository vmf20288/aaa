// a21avwap.cs - Anchored VWAP dual module with configurable bands
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Xml.Serialization;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Gui.Tools;
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
        private static readonly BrushConverter brushConverter = new BrushConverter();

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

                Anchor1Date               = DateTime.Today;
                Anchor1Time               = "00:00";
                ShowAnchor1Band1          = true;
                ShowAnchor1Band2          = false;

                Anchor2Date               = DateTime.Today;
                Anchor2Time               = "00:00";
                ShowAnchor2Band1          = false;
                ShowAnchor2Band2          = false;

                Anchor1VwapColor          = Brushes.Blue;
                Anchor1Band1Color         = Brushes.Green;
                Anchor1Band2Color         = Brushes.Green;

                Anchor2VwapColor          = Brushes.Blue;
                Anchor2Band1Color         = Brushes.Green;
                Anchor2Band2Color         = Brushes.Green;

                AddPlot(Anchor1VwapColor,  "AnchoredVWAP1");
                AddPlot(Anchor1Band1Color, "Anchored1+1");
                AddPlot(Anchor1Band1Color, "Anchored1-1");
                AddPlot(Anchor1Band2Color, "Anchored1+2");
                AddPlot(Anchor1Band2Color, "Anchored1-2");

                AddPlot(Anchor2VwapColor,  "AnchoredVWAP2");
                AddPlot(Anchor2Band1Color, "Anchored2+1");
                AddPlot(Anchor2Band1Color, "Anchored2-1");
                AddPlot(Anchor2Band2Color, "Anchored2+2");
                AddPlot(Anchor2Band2Color, "Anchored2-2");
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
                UpdatePlotBrushes();
            }
        }

        protected override void OnBarUpdate()
        {
            if (!ShowAnchored)
            {
                SetAllNan();
                return;
            }

            // Resolve anchor DateTime from current properties in case user modified them
            UpdateAnchorDateTimes();

            ProcessAnchor(ref anchor1, Anchored1, ShowAnchor1Band1, ShowAnchor1Band2, Anchor1VwapColor, Anchor1Band1Color, Anchor1Band2Color, 0);
            ProcessAnchor(ref anchor2, Anchored2, ShowAnchor2Band1, ShowAnchor2Band2, Anchor2VwapColor, Anchor2Band1Color, Anchor2Band2Color, 5);
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

        private void ProcessAnchor(ref AnchorState anchorState, bool enabled, bool showBand1, bool showBand2, Brush vwapColor, Brush band1Color, Brush band2Color, int plotOffset)
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

            UpdatePlotColors(plotOffset, vwapColor, band1Color, band2Color);

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
            if (!TimeSpan.TryParseExact(timePart, "HH\\:mm", CultureInfo.InvariantCulture, out parsedTime))
                parsedTime = TimeSpan.Zero;

            return datePart.Date + parsedTime;
        }

        private void SetAllNan()
        {
            for (int i = 0; i < 10; i++)
                Values[i][0] = double.NaN;
        }

        private void SetAnchorPlotsToNan(int plotOffset)
        {
            for (int i = plotOffset; i < plotOffset + 5; i++)
                Values[i][0] = double.NaN;
        }

        private void UpdatePlotBrushes()
        {
            UpdatePlotColors(0, Anchor1VwapColor, Anchor1Band1Color, Anchor1Band2Color);
            UpdatePlotColors(5, Anchor2VwapColor, Anchor2Band1Color, Anchor2Band2Color);
        }

        private void UpdatePlotColors(int offset, Brush vwapBrush, Brush band1Brush, Brush band2Brush)
        {
            PlotBrushes[offset][0]     = vwapBrush;
            PlotBrushes[offset + 1][0] = band1Brush;
            PlotBrushes[offset + 2][0] = band1Brush;
            PlotBrushes[offset + 3][0] = band2Brush;
            PlotBrushes[offset + 4][0] = band2Brush;
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

        [XmlIgnore]
        [Category("Anchored 1")]
        [Display(Name = "anchored vwap color", Order = 5, GroupName = "Anchored 1")]
        public Brush Anchor1VwapColor { get; set; }

        [Browsable(false)]
        public string Anchor1VwapColorSerializable
        {
            get { return BrushToString(Anchor1VwapColor); }
            set { Anchor1VwapColor = StringToBrush(value); }
        }

        [XmlIgnore]
        [Category("Anchored 1")]
        [Display(Name = "anchored vwap +-1 color", Order = 6, GroupName = "Anchored 1")]
        public Brush Anchor1Band1Color { get; set; }

        [Browsable(false)]
        public string Anchor1Band1ColorSerializable
        {
            get { return BrushToString(Anchor1Band1Color); }
            set { Anchor1Band1Color = StringToBrush(value); }
        }

        [XmlIgnore]
        [Category("Anchored 1")]
        [Display(Name = "anchored vwap +-2 color", Order = 7, GroupName = "Anchored 1")]
        public Brush Anchor1Band2Color { get; set; }

        [Browsable(false)]
        public string Anchor1Band2ColorSerializable
        {
            get { return BrushToString(Anchor1Band2Color); }
            set { Anchor1Band2Color = StringToBrush(value); }
        }

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

        [XmlIgnore]
        [Category("Anchored 2")]
        [Display(Name = "anchored 2 vwap color", Order = 5, GroupName = "Anchored 2")]
        public Brush Anchor2VwapColor { get; set; }

        [Browsable(false)]
        public string Anchor2VwapColorSerializable
        {
            get { return BrushToString(Anchor2VwapColor); }
            set { Anchor2VwapColor = StringToBrush(value); }
        }

        [XmlIgnore]
        [Category("Anchored 2")]
        [Display(Name = "anchored 2 vwap +-1 color", Order = 6, GroupName = "Anchored 2")]
        public Brush Anchor2Band1Color { get; set; }

        [Browsable(false)]
        public string Anchor2Band1ColorSerializable
        {
            get { return BrushToString(Anchor2Band1Color); }
            set { Anchor2Band1Color = StringToBrush(value); }
        }

        [XmlIgnore]
        [Category("Anchored 2")]
        [Display(Name = "anchored vwap 2 +-2 color", Order = 7, GroupName = "Anchored 2")]
        public Brush Anchor2Band2Color { get; set; }

        [Browsable(false)]
        public string Anchor2Band2ColorSerializable
        {
            get { return BrushToString(Anchor2Band2Color); }
            set { Anchor2Band2Color = StringToBrush(value); }
        }

        private static string BrushToString(Brush brush)
        {
            return brush == null ? null : brushConverter.ConvertToString(brush);
        }

        private static Brush StringToBrush(string serialized)
        {
            return string.IsNullOrEmpty(serialized)
                ? null
                : (Brush)brushConverter.ConvertFromString(serialized);
        }

        #endregion
    }
}
