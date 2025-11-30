#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2c : Indicator
    {
        #region Constantes y campos privados
        private const int BaselineLookbackBars = 14;

        private int volBip = -1;
        private VolumetricBarsType volBarsType;
        private double volTickSize = 0;

        private readonly List<long> histMaxAsk = new List<long>();
        private readonly List<long> histMaxBid = new List<long>();

        private readonly HashSet<string> emittedSignals = new HashSet<string>();
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a2c";
                Description = "Confirmación por limit orders usando barras volumétricas.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DrawOnPricePanel = true;
                DisplayInDataBox = false;
                IsSuspendedWhileInactive = true;

                TimeFrameMinutes = 5;
                ResetSession = true;
                ShowHistory = false;

                MultiplyVolume = 2.5;
                MinContracts = 100;
            }
            else if (State == State.Configure)
            {
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, TimeFrameMinutes, VolumetricDeltaType.BidAsk, 1);
                volBip = BarsArray.Length - 1;
            }
            else if (State == State.DataLoaded)
            {
                volBarsType = BarsArray[volBip].BarsType as VolumetricBarsType;
                if (volBarsType != null)
                    volTickSize = BarsArray[volBip].Instrument.MasterInstrument.TickSize;
            }
            else if (State == State.Terminated)
            {
                ClearSessionState();
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == volBip)
            {
                ProcessVolumetricSeries();
                return;
            }

            if (BarsInProgress == 0)
            {
                if (ResetSession && Bars != null && Bars.IsFirstBarOfSession)
                    ClearSessionState();
            }
        }
        #endregion

        #region Procesado de barras volumétricas
        private void ProcessVolumetricSeries()
        {
            if (volBarsType == null || volTickSize.ApproxCompare(0) == 0)
                return;

            if (CurrentBars[volBip] < 0)
                return;

            if (ResetSession && BarsArray[volBip] != null && BarsArray[volBip].IsFirstBarOfSession && IsFirstTickOfBar)
                ClearSessionState();

            bool isHistorical = State == State.Historical;
            bool isRealtimeLike = State == State.Realtime || State == State.Transition;

            if (isHistorical)
            {
                if (!IsFirstTickOfBar || CurrentBars[volBip] < 1)
                    return;

                int closedIndex = CurrentBars[volBip] - 1;
                DateTime closedTime = Times[volBip][1];
                HandleClosedBar(closedIndex, closedTime, true);
                return;
            }

            if (!isRealtimeLike)
                return;

            if (IsFirstTickOfBar && CurrentBars[volBip] > 0)
            {
                int closedIndex = CurrentBars[volBip] - 1;
                DateTime closedTime = Times[volBip][1];
                HandleClosedBar(closedIndex, closedTime, false);
            }

            int liveIndex = CurrentBars[volBip];
            DateTime liveTime = Times[volBip][0];
            HandleLiveBar(liveIndex, liveTime);
        }
        #endregion

        #region Lógica de módulo Limit Order
        private void HandleClosedBar(int volBarIndex, DateTime volBarTime, bool isHistorical)
        {
            if (volBarIndex < 0)
                return;

            if (!TryGetMaxVolumes(volBarIndex, out long maxBid, out double maxBidPrice, out long maxAsk, out double maxAskPrice))
                return;

            UpdateBaseline(maxBid, maxAsk);

            if (!ShowHistory && isHistorical)
                return;

            EvaluateAndDraw(volBarTime, volBarIndex, maxBid, maxBidPrice, maxAsk, maxAskPrice, isHistorical);
        }

        private void HandleLiveBar(int volBarIndex, DateTime volBarTime)
        {
            if (volBarIndex < 0)
                return;

            if (!TryGetMaxVolumes(volBarIndex, out long maxBid, out double maxBidPrice, out long maxAsk, out double maxAskPrice))
                return;

            EvaluateAndDraw(volBarTime, volBarIndex, maxBid, maxBidPrice, maxAsk, maxAskPrice, false);
        }

        private bool TryGetMaxVolumes(int volBarIndex, out long maxBid, out double maxBidPrice, out long maxAsk, out double maxAskPrice)
        {
            maxBid = 0;
            maxAsk = 0;
            maxBidPrice = double.NaN;
            maxAskPrice = double.NaN;

            if (volBarIndex < 0 || volBarsType == null)
                return false;

            int barsAgo = CurrentBars[volBip] - volBarIndex;
            if (barsAgo < 0 || barsAgo >= BarsArray[volBip].Count)
                return false;

            double low = Lows[volBip][barsAgo];
            double high = Highs[volBip][barsAgo];

            if (high < low)
            {
                double t = high;
                high = low;
                low = t;
            }

            int priceLevels = Math.Max(1, (int)Math.Round((high - low) / volTickSize)) + 1;

            for (int level = 0; level < priceLevels; level++)
            {
                double price = Instrument.MasterInstrument.RoundToTickSize(low + level * volTickSize);
                long bidVol = volBarsType.Volumes[volBarIndex].GetBidVolumeForPrice(price);
                long askVol = volBarsType.Volumes[volBarIndex].GetAskVolumeForPrice(price);

                if (bidVol > maxBid)
                {
                    maxBid = bidVol;
                    maxBidPrice = price;
                }

                if (askVol > maxAsk)
                {
                    maxAsk = askVol;
                    maxAskPrice = price;
                }
            }

            return maxBid > 0 || maxAsk > 0;
        }

        private void EvaluateAndDraw(DateTime volBarTime, int volBarIndex, long maxBid, double maxBidPrice, long maxAsk, double maxAskPrice, bool isHistorical)
        {
            double baselineBid = ComputeMedian(histMaxBid);
            double baselineAsk = ComputeMedian(histMaxAsk);

            double thresholdBid = Math.Max(MultiplyVolume * baselineBid, MinContracts);
            double thresholdAsk = Math.Max(MultiplyVolume * baselineAsk, MinContracts);

            if (maxBid >= thresholdBid && !double.IsNaN(maxBidPrice))
                TryDrawSignal(volBarTime, volBarIndex, maxBidPrice, true, isHistorical);

            if (maxAsk >= thresholdAsk && !double.IsNaN(maxAskPrice))
                TryDrawSignal(volBarTime, volBarIndex, maxAskPrice, false, isHistorical);
        }

        private void TryDrawSignal(DateTime volBarTime, int volBarIndex, double price, bool isBidSide, bool isHistorical)
        {
            string key = $"{volBarTime.Ticks}:{(isBidSide ? "B" : "A")}";
            if (emittedSignals.Contains(key))
                return;

            int primaryBar = BarsArray[0].GetBar(volBarTime);
            if (primaryBar < 0 || primaryBar > CurrentBars[0])
                return;

            int barsAgo = CurrentBars[0] - primaryBar;
            Brush brush = isHistorical ? Brushes.LightGray : (isBidSide ? Brushes.ForestGreen : Brushes.OrangeRed);
            string tag = $"a2c_LO_{(isBidSide ? "BID" : "ASK")}_{volBarTime.Ticks}";

            if (isBidSide)
                Draw.TriangleUp(this, tag, false, barsAgo, price, brush);
            else
                Draw.TriangleDown(this, tag, false, barsAgo, price, brush);

            emittedSignals.Add(key);
        }

        private void UpdateBaseline(long maxBid, long maxAsk)
        {
            if (maxBid > 0)
            {
                histMaxBid.Add(maxBid);
                if (histMaxBid.Count > BaselineLookbackBars)
                    histMaxBid.RemoveAt(0);
            }

            if (maxAsk > 0)
            {
                histMaxAsk.Add(maxAsk);
                if (histMaxAsk.Count > BaselineLookbackBars)
                    histMaxAsk.RemoveAt(0);
            }
        }

        private double ComputeMedian(List<long> data)
        {
            if (data.Count == 0)
                return 0;

            var sorted = data.OrderBy(x => x).ToList();
            int count = sorted.Count;
            int mid = count / 2;

            if (count % 2 == 1)
                return sorted[mid];

            return 0.5 * (sorted[mid - 1] + sorted[mid]);
        }

        private void ClearSessionState()
        {
            histMaxAsk.Clear();
            histMaxBid.Clear();
            emittedSignals.Clear();
            RemoveDrawObjects();
        }
        #endregion

        #region Propiedades
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "TimeFrameMinutes", Description = "Timeframe en minutos para la serie volumétrica interna", Order = 0, GroupName = "General")]
        public int TimeFrameMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ResetSession", Description = "Si true, resetea estado al inicio de sesión", Order = 1, GroupName = "General")]
        public bool ResetSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ShowHistory", Description = "Mostrar eventos históricos", Order = 2, GroupName = "General")]
        public bool ShowHistory { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Multiplicar volumen", Description = "Factor multiplicador del baseline", Order = 0, GroupName = "Limit Order")]
        public double MultiplyVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min contratos", Description = "Mínimo absoluto de contratos para señal", Order = 1, GroupName = "Limit Order")]
        public int MinContracts { get; set; }
        #endregion
    }
}
