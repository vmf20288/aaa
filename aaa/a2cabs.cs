#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.BarsTypes;
#endregion

// a2cabs.cs - Absorcion de ventas basada en Volumetric Bid/Ask

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2cabs : Indicator
    {
        private int bipVol;
        private VolumetricBarsType volBarsType;
        private Series<double> absorptionSeries;
        private readonly List<DateTime> pendingEvents = new List<DateTime>();
        private readonly HashSet<int> absorptionBars = new HashSet<int>();

        [NinjaScriptProperty]
        [Display(Name = "Analysis Time Frame (min)", GroupName = "Parametros", Order = 0)]
        public int AnalysisTimeFrameMin { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Min Total Volume", GroupName = "Parametros", Order = 1)]
        public int MinTotalVolume { get; set; } = 800;

        [NinjaScriptProperty]
        [Display(Name = "Min Bid Share %", GroupName = "Parametros", Order = 2)]
        public double MinBidSharePct { get; set; } = 65.0;

        [NinjaScriptProperty]
        [Display(Name = "Min Close In Range %", GroupName = "Parametros", Order = 3)]
        public double MinCloseInRangePct { get; set; } = 40.0;

        [NinjaScriptProperty]
        [Display(Name = "Max Break Prev Low (ticks)", GroupName = "Parametros", Order = 4)]
        public int MaxBreakPrevLowTicks { get; set; } = 2;

        [NinjaScriptProperty]
        [Display(Name = "Reset Session", GroupName = "Parametros", Order = 5)]
        public bool ResetSession { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Marker Offset Ticks", GroupName = "Visual", Order = 6)]
        public int MarkerOffsetTicks { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Marker Size", GroupName = "Visual", Order = 7)]
        public int MarkerSize { get; set; } = 2;

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Marker Brush", GroupName = "Visual", Order = 8)]
        public System.Windows.Media.Brush MarkerBrush { get; set; } = System.Windows.Media.Brushes.Gray;

        [Browsable(false)]
        public string MarkerBrushSerializable
        {
            get { return Serialize.BrushToString(MarkerBrush); }
            set { MarkerBrush = Serialize.StringToBrush(value); }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> AbsorptionEvent => absorptionSeries;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "a2cabs";
                Description = "Detecta eventos de absorcion de ventas (Order Flow Volumetric).";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                IsSuspendedWhileInactive = true;
            }
            else if (State == State.Configure)
            {
                bipVol = BarsArray.Length;
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, AnalysisTimeFrameMin, VolumetricDeltaType.BidAsk, 1);
            }
            else if (State == State.DataLoaded)
            {
                volBarsType      = BarsArray[bipVol].BarsType as VolumetricBarsType;
                absorptionSeries = new Series<double>(this);
            }
            else if (State == State.Terminated)
            {
                pendingEvents.Clear();
                absorptionBars.Clear();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0)
            {
                if (ResetSession && Bars.IsFirstBarOfSession)
                    ResetSessionState();

                ProcessPendingEvents();

                absorptionSeries[0] = absorptionBars.Contains(CurrentBar) ? 1.0 : 0.0;
                return;
            }

            if (BarsInProgress != bipVol)
                return;

            if (volBarsType == null)
                return;

            int barIndex = CurrentBars[bipVol];
            if (barIndex < 0)
                return;

            var volBar = volBarsType.Volumes[barIndex];

            long volBidBar = 0;
            long volAskBar = 0;

            double high = Highs[bipVol][0];
            double low  = Lows[bipVol][0];

            int priceLevels = Math.Max(1, (int)Math.Round((high - low) / TickSize)) + 1;
            for (int level = 0; level < priceLevels; level++)
            {
                double price = Instrument.MasterInstrument.RoundToTickSize(low + level * TickSize);
                volBidBar += volBar.GetBidVolumeForPrice(price);
                volAskBar += volBar.GetAskVolumeForPrice(price);
            }

            long totalVolume = volBidBar + volAskBar;

            if (totalVolume < MinTotalVolume || totalVolume <= 0)
                return;

            double bidSharePct = 100.0 * volBidBar / totalVolume;
            if (bidSharePct < MinBidSharePct)
                return;

            double close = Closes[bipVol][0];

            double closePosInRange = 50.0;
            if (high != low)
                closePosInRange = 100.0 * (close - low) / (high - low);

            if (closePosInRange < MinCloseInRangePct)
                return;

            if (barIndex > 0)
            {
                double prevLow = Lows[bipVol][1];
                double breakTicks = (prevLow - low) / TickSize;
                if (breakTicks > MaxBreakPrevLowTicks)
                    return;
            }

            pendingEvents.Add(Times[bipVol][0]);
        }

        private void ProcessPendingEvents()
        {
            if (pendingEvents.Count == 0)
                return;

            for (int i = pendingEvents.Count - 1; i >= 0; i--)
            {
                DateTime evTime = pendingEvents[i];
                int targetBar = BarsArray[0].GetBar(evTime);
                if (targetBar < 0 || targetBar > CurrentBar)
                    continue;

                absorptionBars.Add(targetBar);

                double markerPrice = Low[targetBar] - MarkerOffsetTicks * TickSize;
                int barsAgo = CurrentBar - targetBar;
                string tag = $"a2cabs_{targetBar}";
                Draw.Dot(this, tag, false, barsAgo, markerPrice, MarkerBrush);

                pendingEvents.RemoveAt(i);
            }
        }

        private void ResetSessionState()
        {
            pendingEvents.Clear();
            absorptionBars.Clear();
            RemoveDrawObjects();
        }
    }
}
