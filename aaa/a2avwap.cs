#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2avwap : Indicator
    {
        private double anchor1PV;
        private double anchor1V;
        private double anchor1P2V;
        private bool anchor1Active;

        private double anchor2PV;
        private double anchor2V;
        private double anchor2P2V;
        private bool anchor2Active;

        private Brush defaultAnchor1Vwap;
        private Brush defaultAnchor1Band1;
        private Brush defaultAnchor1Band2;
        private Brush defaultAnchor2Vwap;
        private Brush defaultAnchor2Band1;
        private Brush defaultAnchor2Band2;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a2avwap";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsSuspendedWhileInactive = true;

                ShowAnchoredVwap = true;
                Anchored1 = true;
                Anchored2 = false;

                Anchor1Date = DateTime.Today;
                Anchor1Time = "00:00";
                ShowAnchor1Bands1 = true;
                ShowAnchor1Bands2 = false;
                Anchor1VwapBrush = Brushes.Blue;
                Anchor1Band1Brush = Brushes.Green;
                Anchor1Band2Brush = Brushes.Green;

                Anchor2Date = DateTime.Today;
                Anchor2Time = "00:00";
                ShowAnchor2Bands1 = true;
                ShowAnchor2Bands2 = false;
                Anchor2VwapBrush = Brushes.Blue;
                Anchor2Band1Brush = Brushes.Green;
                Anchor2Band2Brush = Brushes.Green;

                AddPlot(Anchor1VwapBrush, "AnchoredVWAP1");
                AddPlot(Anchor1Band1Brush, "Anchored1Plus1");
                AddPlot(Anchor1Band1Brush, "Anchored1Minus1");
                AddPlot(Anchor1Band2Brush, "Anchored1Plus2");
                AddPlot(Anchor1Band2Brush, "Anchored1Minus2");

                AddPlot(Anchor2VwapBrush, "AnchoredVWAP2");
                AddPlot(Anchor2Band1Brush, "Anchored2Plus1");
                AddPlot(Anchor2Band1Brush, "Anchored2Minus1");
                AddPlot(Anchor2Band2Brush, "Anchored2Plus2");
                AddPlot(Anchor2Band2Brush, "Anchored2Minus2");
            }
            else if (State == State.Configure)
            {
                defaultAnchor1Vwap = Anchor1VwapBrush.Clone();
                defaultAnchor1Band1 = Anchor1Band1Brush.Clone();
                defaultAnchor1Band2 = Anchor1Band2Brush.Clone();
                defaultAnchor2Vwap = Anchor2VwapBrush.Clone();
                defaultAnchor2Band1 = Anchor2Band1Brush.Clone();
                defaultAnchor2Band2 = Anchor2Band2Brush.Clone();

                FreezeBrush(defaultAnchor1Vwap);
                FreezeBrush(defaultAnchor1Band1);
                FreezeBrush(defaultAnchor1Band2);
                FreezeBrush(defaultAnchor2Vwap);
                FreezeBrush(defaultAnchor2Band1);
                FreezeBrush(defaultAnchor2Band2);

                Brushes[0] = defaultAnchor1Vwap;
                Brushes[1] = defaultAnchor1Band1;
                Brushes[2] = defaultAnchor1Band1;
                Brushes[3] = defaultAnchor1Band2;
                Brushes[4] = defaultAnchor1Band2;
                Brushes[5] = defaultAnchor2Vwap;
                Brushes[6] = defaultAnchor2Band1;
                Brushes[7] = defaultAnchor2Band1;
                Brushes[8] = defaultAnchor2Band2;
                Brushes[9] = defaultAnchor2Band2;
            }
            else if (State == State.DataLoaded)
            {
                ResetAnchor1();
                ResetAnchor2();
            }
        }

        protected override void OnBarUpdate()
        {
            if (!ShowAnchoredVwap)
            {
                SetAllNan();
                return;
            }

            DateTime anchor1DateTime = BuildAnchorDateTime(Anchor1Date, Anchor1Time);
            DateTime anchor2DateTime = BuildAnchorDateTime(Anchor2Date, Anchor2Time);

            double price = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double volume = Volume[0];

            ProcessModule(ref anchor1Active, ref anchor1PV, ref anchor1V, ref anchor1P2V, anchor1DateTime, Anchored1, ShowAnchor1Bands1, ShowAnchor1Bands2, 0, 1, 2, 3, 4, price, volume);
            ProcessModule(ref anchor2Active, ref anchor2PV, ref anchor2V, ref anchor2P2V, anchor2DateTime, Anchored2, ShowAnchor2Bands1, ShowAnchor2Bands2, 5, 6, 7, 8, 9, price, volume);
        }

        private void ProcessModule(ref bool moduleActive, ref double sumPV, ref double sumV, ref double sumP2V, DateTime anchorDateTime, bool isEnabled, bool showBands1, bool showBands2, int vwapPlot, int plus1Plot, int minus1Plot, int plus2Plot, int minus2Plot, double price, double volume)
        {
            if (!isEnabled)
            {
                Values[vwapPlot][0] = double.NaN;
                Values[plus1Plot][0] = double.NaN;
                Values[minus1Plot][0] = double.NaN;
                Values[plus2Plot][0] = double.NaN;
                Values[minus2Plot][0] = double.NaN;
                return;
            }

            if (!moduleActive && Time[0] >= anchorDateTime)
            {
                sumPV = 0;
                sumV = 0;
                sumP2V = 0;
                moduleActive = true;
            }

            if (!moduleActive)
            {
                Values[vwapPlot][0] = double.NaN;
                Values[plus1Plot][0] = double.NaN;
                Values[minus1Plot][0] = double.NaN;
                Values[plus2Plot][0] = double.NaN;
                Values[minus2Plot][0] = double.NaN;
                return;
            }

            if (volume > 0)
            {
                sumPV += price * volume;
                sumV += volume;
                sumP2V += price * price * volume;
            }

            double vwap = sumV > 0 ? sumPV / sumV : price;
            double variance = sumV > 0 ? (sumP2V / sumV) - (vwap * vwap) : 0;
            if (variance < 0)
                variance = 0;
            double stdDev = Math.Sqrt(variance);

            Values[vwapPlot][0] = vwap;

            if (showBands1)
            {
                Values[plus1Plot][0] = vwap + stdDev;
                Values[minus1Plot][0] = vwap - stdDev;
            }
            else
            {
                Values[plus1Plot][0] = double.NaN;
                Values[minus1Plot][0] = double.NaN;
            }

            if (showBands2)
            {
                Values[plus2Plot][0] = vwap + 2 * stdDev;
                Values[minus2Plot][0] = vwap - 2 * stdDev;
            }
            else
            {
                Values[plus2Plot][0] = double.NaN;
                Values[minus2Plot][0] = double.NaN;
            }
        }

        private DateTime BuildAnchorDateTime(DateTime anchorDate, string anchorTime)
        {
            if (!TimeSpan.TryParseExact(anchorTime ?? "00:00", "HH\\:mm", CultureInfo.InvariantCulture, out TimeSpan ts))
                ts = TimeSpan.Zero;
            return anchorDate.Date + ts;
        }

        private void ResetAnchor1()
        {
            anchor1PV = 0;
            anchor1V = 0;
            anchor1P2V = 0;
            anchor1Active = false;
        }

        private void ResetAnchor2()
        {
            anchor2PV = 0;
            anchor2V = 0;
            anchor2P2V = 0;
            anchor2Active = false;
        }

        private void SetAllNan()
        {
            for (int i = 0; i < Values.Length; i++)
                Values[i][0] = double.NaN;
        }

        private void FreezeBrush(Brush brush)
        {
            if (brush != null && brush.CanFreeze)
                brush.Freeze();
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 0, GroupName = "GLOBAL")]
        public bool ShowAnchoredVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchored 1", Order = 1, GroupName = "GLOBAL")]
        public bool Anchored1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchored 2", Order = 2, GroupName = "GLOBAL")]
        public bool Anchored2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 1 Date", Order = 0, GroupName = "Anchored VWAP 1")]
        public DateTime Anchor1Date { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 1 Time (HH:mm)", Order = 1, GroupName = "Anchored VWAP 1")]
        public string Anchor1Time { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 1 (±1σ)", Order = 2, GroupName = "Anchored VWAP 1")]
        public bool ShowAnchor1Bands1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 2 (±2σ)", Order = 3, GroupName = "Anchored VWAP 1")]
        public bool ShowAnchor1Bands2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 2 Date", Order = 0, GroupName = "Anchored VWAP 2")]
        public DateTime Anchor2Date { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 2 Time (HH:mm)", Order = 1, GroupName = "Anchored VWAP 2")]
        public string Anchor2Time { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 1 (±1σ)", Order = 2, GroupName = "Anchored VWAP 2")]
        public bool ShowAnchor2Bands1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 2 (±2σ)", Order = 3, GroupName = "Anchored VWAP 2")]
        public bool ShowAnchor2Bands2 { get; set; }

        [XmlIgnore]
        [Display(Name = "Anchored VWAP 1 Color", Order = 0, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1VwapBrush { get; set; }

        [Browsable(false)]
        public string Anchor1VwapBrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1VwapBrush); }
            set { Anchor1VwapBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 1 ±1σ Color", Order = 1, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1Band1Brush { get; set; }

        [Browsable(false)]
        public string Anchor1Band1BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1Band1Brush); }
            set { Anchor1Band1Brush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 1 ±2σ Color", Order = 2, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1Band2Brush { get; set; }

        [Browsable(false)]
        public string Anchor1Band2BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1Band2Brush); }
            set { Anchor1Band2Brush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anchored VWAP 2 Color", Order = 0, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2VwapBrush { get; set; }

        [Browsable(false)]
        public string Anchor2VwapBrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2VwapBrush); }
            set { Anchor2VwapBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 2 ±1σ Color", Order = 1, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2Band1Brush { get; set; }

        [Browsable(false)]
        public string Anchor2Band1BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2Band1Brush); }
            set { Anchor2Band1Brush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 2 ±2σ Color", Order = 2, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2Band2Brush { get; set; }

        [Browsable(false)]
        public string Anchor2Band2BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2Band2Brush); }
            set { Anchor2Band2Brush = Serialize.StringToBrush(value); }
        }
        #endregion

        #region NinjaScript generated code. Neither change nor remove.
        public override string DisplayName
        {
            get
            {
                return base.DisplayName + "";
            }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.
namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a2avwap[] cachea2avwap;
        public a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            return a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
        }

        public a2avwap a2avwap(ISeries<double> input, bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            if (cachea2avwap != null)
                for (int idx = 0; idx < cachea2avwap.Length; idx++)
                    if (cachea2avwap[idx] != null && cachea2avwap[idx].ShowAnchoredVwap == showAnchoredVwap && cachea2avwap[idx].Anchored1 == anchored1 && cachea2avwap[idx].Anchored2 == anchored2 && cachea2avwap[idx].Anchor1Date == anchor1Date && cachea2avwap[idx].Anchor1Time == anchor1Time && cachea2avwap[idx].ShowAnchor1Bands1 == showAnchor1Bands1 && cachea2avwap[idx].ShowAnchor1Bands2 == showAnchor1Bands2 && cachea2avwap[idx].Anchor2Date == anchor2Date && cachea2avwap[idx].Anchor2Time == anchor2Time && cachea2avwap[idx].ShowAnchor2Bands1 == showAnchor2Bands1 && cachea2avwap[idx].ShowAnchor2Bands2 == showAnchor2Bands2 && cachea2avwap[idx].EqualsInput(input))
                        return cachea2avwap[idx];
            return CacheIndicator<a2avwap>(new a2avwap() { ShowAnchoredVwap = showAnchoredVwap, Anchored1 = anchored1, Anchored2 = anchored2, Anchor1Date = anchor1Date, Anchor1Time = anchor1Time, ShowAnchor1Bands1 = showAnchor1Bands1, ShowAnchor1Bands2 = showAnchor1Bands2, Anchor2Date = anchor2Date, Anchor2Time = anchor2Time, ShowAnchor2Bands1 = showAnchor2Bands1, ShowAnchor2Bands2 = showAnchor2Bands2 }, input, ref cachea2avwap);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            return indicator.a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
        }

        public Indicators.a2avwap a2avwap(ISeries<double> input, bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            return indicator.a2avwap(input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            return indicator.a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
        }

        public Indicators.a2avwap a2avwap(ISeries<double> input, bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
        {
            return indicator.a2avwap(input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
        }
    }
}
#endregion
