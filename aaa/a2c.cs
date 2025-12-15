#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media; // Brush / Brushes
using SharpDX;
using SharpDX.DirectWrite;
using D2D1 = SharpDX.Direct2D1;
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
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
        private const int AbsBaselineLookbackBars = 14;

        private int volBip = -1;
        private VolumetricBarsType volBarsType;
        private double volTickSize = 0;

        private int divBip = -1;
        private VolumetricBarsType divVolBarsType;
        private readonly List<double> cumDeltaDiv = new List<double>();

        private readonly List<long> histMaxAsk = new List<long>();
        private readonly List<long> histMaxBid = new List<long>();
        private readonly List<long> histHVNTotal = new List<long>();

        private readonly HashSet<string> emittedSignals = new HashSet<string>();
        private readonly HashSet<string> absorptionEmittedSignals = new HashSet<string>();
        private readonly Dictionary<int, int> orderLimitSignalsByBar = new Dictionary<int, int>();
        private readonly Dictionary<int, int> absorptionSignalsByBar = new Dictionary<int, int>();
        private readonly Dictionary<int, int> divergenceSignalsByBar = new Dictionary<int, int>();

        // --- NUEVO: filas inferiores Vol / Δ / CΔ (5m volumetric internal)
        private readonly Dictionary<int, long> volTotalByBar = new Dictionary<int, long>();
        private readonly Dictionary<int, long> deltaByBar = new Dictionary<int, long>();
        private readonly Dictionary<int, long> cumDeltaByBar = new Dictionary<int, long>();

        private long cumDeltaRunningVol = 0;
        private DateTime cumDeltaTradingDay = DateTime.MinValue;
        private static readonly TimeSpan CumDeltaResetTime = new TimeSpan(4, 0, 0);
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
                ShowHistory = true;

                MultiplyVolume = 2.5;
                MinContracts = 100;

                AbsMultiplyVolume = 2.0;
                AbsMinContracts = 150;
                AbsMinSideShare = 0.25;

                DivTimeFrameMinutes = 1;
                DivLookbackBars = 15;
                DivMinPriceMoveTicks = 12;
                DivMinDeltaMove = 600;
            }
            else if (State == State.Configure)
            {
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, TimeFrameMinutes, VolumetricDeltaType.BidAsk, 1);
                volBip = BarsArray.Length - 1;

                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, DivTimeFrameMinutes, VolumetricDeltaType.BidAsk, 1);
                divBip = BarsArray.Length - 1;
            }
            else if (State == State.DataLoaded)
            {
                volBarsType = BarsArray[volBip].BarsType as VolumetricBarsType;
                if (volBarsType != null)
                    volTickSize = BarsArray[volBip].Instrument.MasterInstrument.TickSize;

                divVolBarsType = BarsArray[divBip].BarsType as VolumetricBarsType;
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

            if (BarsInProgress == divBip)
            {
                ProcessDivergenceSeries();
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

            // NUEVO: actualizar Vol/Δ/CΔ del bar cerrado (independiente de señales)
            UpdateVolDeltaRows(volBarIndex, volBarTime, true);

            if (!TryGetMaxVolumes(volBarIndex, out long maxBid, out double maxBidPrice, out long maxAsk, out double maxAskPrice))
                return;

            UpdateBaseline(maxBid, maxAsk);

            bool hvnFound = TryGetHighVolumeNode(volBarIndex, out long hvnTotal, out double priceHVN, out long bidHVN, out long askHVN);

            if (hvnFound)
                UpdateAbsorptionBaseline(hvnTotal);

            if (!ShowHistory && isHistorical)
                return;

            if (hvnFound)
                EvaluateAndStoreAbsorption(volBarTime, volBarIndex, hvnTotal, priceHVN, bidHVN, askHVN, isHistorical);

            EvaluateAndDraw(volBarTime, volBarIndex, maxBid, maxBidPrice, maxAsk, maxAskPrice, isHistorical);
        }

        private void HandleLiveBar(int volBarIndex, DateTime volBarTime)
        {
            if (volBarIndex < 0)
                return;

            // NUEVO: actualizar Vol/Δ/CΔ del bar vivo (intrabar)
            UpdateVolDeltaRows(volBarIndex, volBarTime, false);

            if (!TryGetMaxVolumes(volBarIndex, out long maxBid, out double maxBidPrice, out long maxAsk, out double maxAskPrice))
                return;

            EvaluateAndDraw(volBarTime, volBarIndex, maxBid, maxBidPrice, maxAsk, maxAskPrice, false);

            if (TryGetHighVolumeNode(volBarIndex, out long hvnTotal, out double priceHVN, out long bidHVN, out long askHVN))
                EvaluateAndStoreAbsorption(volBarTime, volBarIndex, hvnTotal, priceHVN, bidHVN, askHVN, false);
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

            double tmpAskPrice;
            double tmpBidPrice;

            maxAsk = volBarsType.Volumes[volBarIndex].GetMaximumVolume(true, out tmpAskPrice);
            maxBid = volBarsType.Volumes[volBarIndex].GetMaximumVolume(false, out tmpBidPrice);

            if (maxAsk > 0)
                maxAskPrice = Instrument.MasterInstrument.RoundToTickSize(tmpAskPrice);

            if (maxBid > 0)
                maxBidPrice = Instrument.MasterInstrument.RoundToTickSize(tmpBidPrice);

            return maxBid > 0 || maxAsk > 0;
        }

        private bool TryGetHighVolumeNode(int volBarIndex, out long totalHVN, out double priceHVN, out long bidHVN, out long askHVN)
        {
            totalHVN = 0;
            priceHVN = double.NaN;
            bidHVN = 0;
            askHVN = 0;

            if (volBarIndex < 0 || volBarsType == null)
                return false;

            int barsAgo = CurrentBars[volBip] - volBarIndex;
            if (barsAgo < 0 || barsAgo >= BarsArray[volBip].Count)
                return false;

            double tmpPrice;
            long maxCombined = volBarsType.Volumes[volBarIndex].GetMaximumVolume(null, out tmpPrice);

            if (maxCombined <= 0 || double.IsNaN(tmpPrice))
                return false;

            priceHVN = Instrument.MasterInstrument.RoundToTickSize(tmpPrice);

            bidHVN = volBarsType.Volumes[volBarIndex].GetBidVolumeForPrice(priceHVN);
            askHVN = volBarsType.Volumes[volBarIndex].GetAskVolumeForPrice(priceHVN);

            totalHVN = bidHVN + askHVN;

            return totalHVN > 0 && !double.IsNaN(priceHVN);
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

        private void EvaluateAndStoreAbsorption(DateTime volBarTime, int volBarIndex, long hvnTotal, double priceHVN, long bidHVN, long askHVN, bool isHistorical)
        {
            double baselineHVN = ComputeMedian(histHVNTotal);

            if (baselineHVN <= 0)
                return;

            double thresholdAbs = Math.Max(AbsMultiplyVolume * baselineHVN, AbsMinContracts);

            if (hvnTotal < thresholdAbs)
                return;

            double bidShare = hvnTotal > 0 ? (double)bidHVN / hvnTotal : 0.0;
            double askShare = hvnTotal > 0 ? (double)askHVN / hvnTotal : 0.0;
            double minShare = Math.Min(bidShare, askShare);

            if (minShare < AbsMinSideShare)
                return;

            if (isHistorical && !ShowHistory)
                return;

            int primaryBar = BarsArray[0].GetBar(volBarTime);
            if (primaryBar < 0 || primaryBar > CurrentBars[0])
                return;

            int side = 0;
            if (askHVN > bidHVN)
                side = 1;
            else if (bidHVN > askHVN)
                side = -1;

            absorptionSignalsByBar[primaryBar] = side;

            string key = $"{volBarTime.Ticks}:{side}";
            if (!absorptionEmittedSignals.Contains(key))
            {
                int barsAgo = CurrentBars[0] - primaryBar;
                bool pointingUp = side <= 0;
                Brush brush = isHistorical ? Brushes.LightGray : (side > 0 ? Brushes.OrangeRed : Brushes.ForestGreen);
                string tag = $"a2c_ABS_{volBarTime.Ticks}";

                if (pointingUp)
                    Draw.TriangleUp(this, tag, false, barsAgo, priceHVN, brush);
                else
                    Draw.TriangleDown(this, tag, false, barsAgo, priceHVN, brush);

                absorptionEmittedSignals.Add(key);
            }
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
            orderLimitSignalsByBar[primaryBar] = isBidSide ? 1 : -1;
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

        private void UpdateAbsorptionBaseline(long hvnTotal)
        {
            if (hvnTotal <= 0)
                return;

            histHVNTotal.Add(hvnTotal);

            if (histHVNTotal.Count > AbsBaselineLookbackBars)
                histHVNTotal.RemoveAt(0);
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
            histHVNTotal.Clear();
            emittedSignals.Clear();
            absorptionEmittedSignals.Clear();
            orderLimitSignalsByBar.Clear();
            absorptionSignalsByBar.Clear();
            divergenceSignalsByBar.Clear();
            cumDeltaDiv.Clear();

            // NUEVO: limpiar filas Vol/Δ/CΔ
            volTotalByBar.Clear();
            deltaByBar.Clear();
            cumDeltaByBar.Clear();
            cumDeltaRunningVol = 0;
            cumDeltaTradingDay = DateTime.MinValue;

            RemoveDrawObjects();
        }
        #endregion

        #region Vol/Delta/CumDelta helpers (5m series)
        private DateTime GetCumDeltaTradingDay(DateTime barTime)
        {
            // Reset a las 4:00 (según la hora del gráfico)
            return (barTime.TimeOfDay < CumDeltaResetTime) ? barTime.Date.AddDays(-1) : barTime.Date;
        }

        private void EnsureCumDeltaSession(DateTime barTime)
        {
            DateTime day = GetCumDeltaTradingDay(barTime);
            if (cumDeltaTradingDay != day)
            {
                cumDeltaTradingDay = day;
                cumDeltaRunningVol = 0;
            }
        }

        private void UpdateVolDeltaRows(int volBarIndex, DateTime volBarTime, bool isClosedBar)
        {
            if (volBarsType == null || volBip < 0)
                return;

            if (volBarIndex < 0 || volBarIndex >= BarsArray[volBip].Count)
                return;

            // Reset 4:00
            EnsureCumDeltaSession(volBarTime);

            // Total Volume (de la serie Volumes[])
            long totalVol = 0;
            int barsAgo = CurrentBars[volBip] - volBarIndex;
            if (barsAgo >= 0 && barsAgo < BarsArray[volBip].Count)
            {
                double v = Volumes[volBip][barsAgo];
                totalVol = (long)Math.Round(v, MidpointRounding.AwayFromZero);
            }

            // Bar Delta (Order Flow Volumetric)
            long barDelta;
            try
            {
                barDelta = volBarsType.Volumes[volBarIndex].BarDelta;
            }
            catch
            {
                // Si por alguna razón la propiedad no está accesible en tu build, no romper el indicador.
                return;
            }

            long barCumDelta;
            if (isClosedBar)
            {
                cumDeltaRunningVol += barDelta;
                barCumDelta = cumDeltaRunningVol;
            }
            else
            {
                barCumDelta = cumDeltaRunningVol + barDelta;
            }

            // Mapear al bar primario (chart)
            int primaryBar = BarsArray[0].GetBar(volBarTime);
            if (primaryBar < 0 || primaryBar > CurrentBars[0])
                return;

            volTotalByBar[primaryBar] = totalVol;
            deltaByBar[primaryBar] = barDelta;
            cumDeltaByBar[primaryBar] = barCumDelta;
        }

        private static string FormatDeltaRounded(long delta)
        {
            // Δ: redondear a 50 y mostrar en centenas con .0 / .5
            // Ej: -310 -> -3.0 ; -325 -> -3.5 ; 90 -> 1.0 (según redondeo)
            double rounded = Math.Round(delta / 50.0, 0, MidpointRounding.AwayFromZero) * 50.0;
            double scaled = rounded / 100.0;

            if (Math.Abs(scaled) < 1e-9)
                return "0";

            return scaled.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string FormatVolumeK(long volume)
        {
            // Vol: miles sin decimal y redondeado (SIN "k")
            long k = (long)Math.Round(volume / 1000.0, 0, MidpointRounding.AwayFromZero);
            return k.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string FormatCumDeltaK(long cumDelta)
        {
            // CΔ: miles con 1 decimal y "k" (ej 1.5k, -0.5k, 0.1k)
            double k = cumDelta / 1000.0;
            k = Math.Round(k, 1, MidpointRounding.AwayFromZero);
            return k.ToString("0.0", CultureInfo.InvariantCulture);
        }
        #endregion

        #region Lógica de módulo Divergence
        private void ProcessDivergenceSeries()
        {
            if (divVolBarsType == null)
                return;

            if (CurrentBars[divBip] < 0)
                return;

            if (ResetSession && BarsArray[divBip] != null && BarsArray[divBip].IsFirstBarOfSession && IsFirstTickOfBar)
                ClearSessionState();

            if (!IsFirstTickOfBar)
                return;

            int closedIndex = CurrentBars[divBip] - 1;
            if (closedIndex < 0)
                return;

            double tickSize = BarsArray[divBip].Instrument.MasterInstrument.TickSize;
            double low = Lows[divBip][1];
            double high = Highs[divBip][1];

            if (high < low)
            {
                double t = high;
                high = low;
                low = t;
            }

            int priceLevels = Math.Max(1, (int)Math.Round((high - low) / tickSize)) + 1;
            long totalBid = 0;
            long totalAsk = 0;

            for (int level = 0; level < priceLevels; level++)
            {
                double price = Instrument.MasterInstrument.RoundToTickSize(low + level * tickSize);
                totalBid += divVolBarsType.Volumes[closedIndex].GetBidVolumeForPrice(price);
                totalAsk += divVolBarsType.Volumes[closedIndex].GetAskVolumeForPrice(price);
            }

            long deltaBar = totalAsk - totalBid;

            double cumDeltaValue = deltaBar;
            if (closedIndex > 0 && closedIndex - 1 < cumDeltaDiv.Count)
                cumDeltaValue = cumDeltaDiv[closedIndex - 1] + deltaBar;

            while (cumDeltaDiv.Count <= closedIndex)
                cumDeltaDiv.Add(0);

            cumDeltaDiv[closedIndex] = cumDeltaValue;

            if (closedIndex + 1 < DivLookbackBars)
                return;

            int barsAgoCurrent = CurrentBars[divBip] - closedIndex;
            int barsAgoPrev = barsAgoCurrent + DivLookbackBars - 1;

            double priceNow = Closes[divBip][barsAgoCurrent];
            double pricePrev = Closes[divBip][barsAgoPrev];

            int prevIndex = closedIndex - (DivLookbackBars - 1);
            if (prevIndex < 0 || prevIndex >= cumDeltaDiv.Count)
                return;

            double deltaNow = cumDeltaDiv[closedIndex];
            double deltaPrev = cumDeltaDiv[prevIndex];

            double priceChange = priceNow - pricePrev;
            double deltaChange = deltaNow - deltaPrev;

            if (Math.Abs(priceChange) < DivMinPriceMoveTicks * tickSize)
                return;

            if (Math.Abs(deltaChange) < DivMinDeltaMove)
                return;

            int side = 0;
            if (priceChange < 0 && deltaChange > 0)
                side = 1;
            else if (priceChange > 0 && deltaChange < 0)
                side = -1;

            if (side == 0)
                return;

            DateTime barTime = Times[divBip][barsAgoCurrent];
            int primaryBar = BarsArray[0].GetBar(barTime);
            if (primaryBar < 0 || primaryBar > CurrentBars[0])
                return;

            divergenceSignalsByBar[primaryBar] = side;
        }
        #endregion

        #region Render
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (chartControl == null || chartScale == null || ChartBars == null || Bars == null)
                return;

            ChartPanel panel = ChartPanel;
            if (panel == null)
                return;

            // Confirmaciones (igual que antes)
            float rowHeight = 18f;

            // SOLO filas inferiores (VOL/Δ/CΔ) más altas
            float lowerRowHeight = 24f;

            float topMargin = 4f;
            float bottomMargin = 2f;

            int upperRows = 4; // Order Limit, Absorption, (vacía), Divergence
            int lowerRows = 3; // Volume, Delta, Cum delta
            int totalRows = upperRows + lowerRows;

            float totalHeight = rowHeight * upperRows + lowerRowHeight * lowerRows + topMargin + bottomMargin;

            float bottom = panel.Y + panel.H;
            float top = bottom - totalHeight;

            SharpDX.RectangleF backgroundRect = new SharpDX.RectangleF(panel.X, top, panel.W, totalHeight);

            var rt = RenderTarget;
            if (rt == null)
                return;

            using (var bgBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(10, 10, 10, 200)))
            using (var gridBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(200, 200, 200, 120)))
            // Línea divisoria debajo de Divergence: más gruesa + apenas más visible
            using (var dividerBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(220, 220, 220, 160)))
            using (var textBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(200, 200, 200, 180)))
            using (var volTextBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(170, 170, 170, 180)))
            using (var deltaPosBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(60, 200, 120, 190)))
            using (var deltaNegBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(220, 80, 80, 190)))
            using (var cumPosBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(60, 180, 110, 170)))
            using (var cumNegBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(200, 70, 70, 170)))
            using (var textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f))
            // Texto VOL/Δ/CΔ más grande
            using (var valueFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 16f))
            {
                // Centrar valores en su celda (ancho = centro-a-centro)
                valueFormat.TextAlignment = TextAlignment.Center;
                valueFormat.ParagraphAlignment = ParagraphAlignment.Center;

                rt.FillRectangle(backgroundRect, bgBrush);

                float firstLineY = top + topMargin;
                float lastLineY = bottom - bottomMargin;

                float GetRowTop(int rowIndex)
                {
                    if (rowIndex <= 0)
                        return firstLineY;

                    if (rowIndex < upperRows)
                        return firstLineY + rowHeight * rowIndex;

                    return firstLineY + rowHeight * upperRows + lowerRowHeight * (rowIndex - upperRows);
                }

                // Bordes superior e inferior
                rt.DrawLine(new SharpDX.Vector2(panel.X, firstLineY), new SharpDX.Vector2(panel.X + panel.W, firstLineY), gridBrush, 1f);

                // Separadores horizontales internos
                for (int i = 1; i < totalRows; i++)
                {
                    float y = GetRowTop(i);

                    if (i == upperRows) // debajo de Divergence (entre row 4 y row 5 visualmente)
                        rt.DrawLine(new SharpDX.Vector2(panel.X, y), new SharpDX.Vector2(panel.X + panel.W, y), dividerBrush, 2f);
                    else
                        rt.DrawLine(new SharpDX.Vector2(panel.X, y), new SharpDX.Vector2(panel.X + panel.W, y), gridBrush, 1f);
                }

                rt.DrawLine(new SharpDX.Vector2(panel.X, lastLineY), new SharpDX.Vector2(panel.X + panel.W, lastLineY), gridBrush, 1f);

                // Labels derecha (igual estilo que antes)
                void DrawRightLabel(string label, float rowTopY, float rowH)
                {
                    SharpDX.RectangleF r = new SharpDX.RectangleF(panel.X, rowTopY, panel.W, rowH);
                    using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, label, textFormat, r.Width, rowH))
                    {
                        float textX = panel.X + panel.W - layout.Metrics.Width - 6f;
                        float textY = rowTopY + (rowH - layout.Metrics.Height) / 2f;
                        rt.DrawTextLayout(new SharpDX.Vector2(textX, textY), layout, textBrush);
                    }
                }

                // Row 1
                DrawRightLabel("Order Limit", GetRowTop(0), rowHeight);
                // Row 2
                DrawRightLabel("Absorption", GetRowTop(1), rowHeight);
                // Row 3 se deja vacía (NO tocar)
                // Row 4
                DrawRightLabel("Divergence", GetRowTop(3), rowHeight);

                // Row 5..7 nuevas
                DrawRightLabel("Volume", GetRowTop(4), lowerRowHeight);
                DrawRightLabel("Delta", GetRowTop(5), lowerRowHeight);
                DrawRightLabel("Cum delta", GetRowTop(6), lowerRowHeight);

                int startBar = Math.Max(ChartBars.FromIndex, 0);
                int endBar = Math.Min(ChartBars.ToIndex, Bars.Count - 1);

                float triangleSize = 5f;
                float row1Middle = firstLineY + rowHeight * 0.5f;
                float row2Middle = firstLineY + rowHeight * 1.5f;
                float row4Middle = firstLineY + rowHeight * 3.5f;

                // Rects de valores (topY por fila) - filas inferiores más altas
                float rowVolTop = GetRowTop(4);
                float rowDelTop = GetRowTop(5);
                float rowCumTop = GetRowTop(6);

                for (int barIndex = startBar; barIndex <= endBar; barIndex++)
                {
                    float x = (float)chartControl.GetXByBarIndex(ChartBars, barIndex);

                    // --- Triángulos existentes (sin cambios)
                    if (orderLimitSignalsByBar.TryGetValue(barIndex, out int olSide))
                    {
                        bool isBidSide = olSide > 0;
                        SharpDX.Color color = isBidSide ? new SharpDX.Color(34, 139, 34) : new SharpDX.Color(255, 102, 0);
                        DrawTriangle(rt, x, row1Middle, triangleSize, isBidSide, color);
                    }

                    if (absorptionSignalsByBar.TryGetValue(barIndex, out int absSide))
                    {
                        bool pointingUp = absSide < 0;
                        SharpDX.Color color = absSide > 0 ? new SharpDX.Color(255, 102, 0) : new SharpDX.Color(34, 139, 34);
                        DrawTriangle(rt, x, row2Middle, triangleSize, pointingUp, color);
                    }

                    if (divergenceSignalsByBar.TryGetValue(barIndex, out int divSide))
                    {
                        bool pointingUp = divSide > 0;
                        SharpDX.Color color = divSide > 0 ? new SharpDX.Color(34, 139, 34) : new SharpDX.Color(255, 102, 0);
                        DrawTriangle(rt, x, row4Middle, triangleSize, pointingUp, color);
                    }

                    // --- Ancho (centro a centro)
                    float halfWidth = 6f;
                    if (barIndex < endBar)
                    {
                        float xNext = (float)chartControl.GetXByBarIndex(ChartBars, barIndex + 1);
                        halfWidth = Math.Max(2f, (xNext - x) * 0.5f);
                    }
                    else if (barIndex > startBar)
                    {
                        float xPrev = (float)chartControl.GetXByBarIndex(ChartBars, barIndex - 1);
                        halfWidth = Math.Max(2f, (x - xPrev) * 0.5f);
                    }

                    float cellW = halfWidth * 2f;
                    float cellX = x - halfWidth;

                    // --- VOL / Δ / CΔ (texto)
                    if (volTotalByBar.TryGetValue(barIndex, out long vTotal))
                    {
                        string txt = FormatVolumeK(vTotal);
                        var rect = new SharpDX.RectangleF(cellX, rowVolTop, cellW, lowerRowHeight);
                        rt.DrawText(txt, valueFormat, rect, volTextBrush);
                    }

                    if (deltaByBar.TryGetValue(barIndex, out long dBar))
                    {
                        string txt = FormatDeltaRounded(dBar);
                        var rect = new SharpDX.RectangleF(cellX, rowDelTop, cellW, lowerRowHeight);

                        if (dBar > 0) rt.DrawText(txt, valueFormat, rect, deltaPosBrush);
                        else if (dBar < 0) rt.DrawText(txt, valueFormat, rect, deltaNegBrush);
                        else rt.DrawText(txt, valueFormat, rect, textBrush);
                    }

                    if (cumDeltaByBar.TryGetValue(barIndex, out long cdBar))
                    {
                        string txt = FormatCumDeltaK(cdBar);
                        var rect = new SharpDX.RectangleF(cellX, rowCumTop, cellW, lowerRowHeight);

                        if (cdBar > 0) rt.DrawText(txt, valueFormat, rect, cumPosBrush);
                        else if (cdBar < 0) rt.DrawText(txt, valueFormat, rect, cumNegBrush);
                        else rt.DrawText(txt, valueFormat, rect, volTextBrush);
                    }
                }
            }
        }

        private void DrawTriangle(D2D1.RenderTarget rt, float centerX, float centerY, float size, bool pointingUp, SharpDX.Color color)
        {
            using (var brush = new D2D1.SolidColorBrush(rt, color))
            using (var geometry = new D2D1.PathGeometry(rt.Factory))
            {
                using (D2D1.GeometrySink sink = geometry.Open())
                {
                    Vector2 p1 = new Vector2(centerX, pointingUp ? centerY - size : centerY + size);
                    Vector2 p2 = new Vector2(centerX - size, pointingUp ? centerY + size : centerY - size);
                    Vector2 p3 = new Vector2(centerX + size, pointingUp ? centerY + size : centerY - size);

                    sink.BeginFigure(p1, D2D1.FigureBegin.Filled);
                    sink.AddLine(p2);
                    sink.AddLine(p3);
                    sink.EndFigure(D2D1.FigureEnd.Closed);
                    sink.Close();
                }

                rt.FillGeometry(geometry, brush);
            }
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

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Absorption - multiplicar volumen", Description = "Factor multiplicador del baseline de HVN", Order = 0, GroupName = "Absorption")]
        public double AbsMultiplyVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Absorption - min contratos", Description = "Mínimo absoluto de contratos en el HVN", Order = 1, GroupName = "Absorption")]
        public int AbsMinContracts { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "Absorption - min % por lado", Description = "Mínimo porcentaje que debe tener cada lado (Bid/Ask) del total en el HVN", Order = 2, GroupName = "Absorption")]
        public double AbsMinSideShare { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Div - TimeFrame (min)", Description = "Timeframe en minutos para la serie rápida de divergencia (por defecto 1)", Order = 0, GroupName = "Divergence")]
        public int DivTimeFrameMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(3, 100)]
        [Display(Name = "Div - Lookback barras", Description = "Número de barras rápidas usadas para medir la pendiente precio/delta", Order = 1, GroupName = "Divergence")]
        public int DivLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Div - min movimiento precio (ticks)", Description = "Mínimo movimiento absoluto de precio en la ventana para considerar divergencia", Order = 2, GroupName = "Divergence")]
        public int DivMinPriceMoveTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Div - min movimiento cum delta", Description = "Mínimo movimiento absoluto de cumulative delta en la ventana para considerar divergencia", Order = 3, GroupName = "Divergence")]
        public int DivMinDeltaMove { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a2c[] cachea2c;
		public a2c a2c(int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			return a2c(Input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
		}

		public a2c a2c(ISeries<double> input, int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			if (cachea2c != null)
				for (int idx = 0; idx < cachea2c.Length; idx++)
					if (cachea2c[idx] != null && cachea2c[idx].TimeFrameMinutes == timeFrameMinutes && cachea2c[idx].ResetSession == resetSession && cachea2c[idx].ShowHistory == showHistory && cachea2c[idx].MultiplyVolume == multiplyVolume && cachea2c[idx].MinContracts == minContracts && cachea2c[idx].AbsMultiplyVolume == absMultiplyVolume && cachea2c[idx].AbsMinContracts == absMinContracts && cachea2c[idx].AbsMinSideShare == absMinSideShare && cachea2c[idx].DivTimeFrameMinutes == divTimeFrameMinutes && cachea2c[idx].DivLookbackBars == divLookbackBars && cachea2c[idx].DivMinPriceMoveTicks == divMinPriceMoveTicks && cachea2c[idx].DivMinDeltaMove == divMinDeltaMove && cachea2c[idx].EqualsInput(input))
						return cachea2c[idx];
			return CacheIndicator<a2c>(new a2c(){ TimeFrameMinutes = timeFrameMinutes, ResetSession = resetSession, ShowHistory = showHistory, MultiplyVolume = multiplyVolume, MinContracts = minContracts, AbsMultiplyVolume = absMultiplyVolume, AbsMinContracts = absMinContracts, AbsMinSideShare = absMinSideShare, DivTimeFrameMinutes = divTimeFrameMinutes, DivLookbackBars = divLookbackBars, DivMinPriceMoveTicks = divMinPriceMoveTicks, DivMinDeltaMove = divMinDeltaMove }, input, ref cachea2c);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a2c a2c(int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			return indicator.a2c(Input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
		}

		public Indicators.a2c a2c(ISeries<double> input , int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			return indicator.a2c(input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a2c a2c(int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			return indicator.a2c(Input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
		}

		public Indicators.a2c a2c(ISeries<double> input , int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
		{
			return indicator.a2c(input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
		}
	}
}

#endregion
