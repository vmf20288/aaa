#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
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

                long total = bidVol + askVol;

                if (total > totalHVN)
                {
                    totalHVN = total;
                    priceHVN = price;
                    bidHVN = bidVol;
                    askHVN = askVol;
                }
            }

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
            RemoveDrawObjects();
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

            float rowHeight = 18f;
            float topMargin = 4f;
            float bottomMargin = 2f;
            float totalHeight = rowHeight * 4f + topMargin + bottomMargin;

            float bottom = panel.Y + panel.H;
            float top = bottom - totalHeight;

            SharpDX.RectangleF backgroundRect = new SharpDX.RectangleF(panel.X, top, panel.W, totalHeight);

            var rt = RenderTarget;
            if (rt == null)
                return;

            using (var bgBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(10, 10, 10, 200)))
            using (var gridBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(200, 200, 200, 120)))
            using (var textBrush = new D2D1.SolidColorBrush(rt, new SharpDX.Color(200, 200, 200, 180)))
            using (var textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f))
            {
                rt.FillRectangle(backgroundRect, bgBrush);

                float firstLineY = top + topMargin;
                float lastLineY = bottom - bottomMargin;

                rt.DrawLine(new SharpDX.Vector2(panel.X, firstLineY), new SharpDX.Vector2(panel.X + panel.W, firstLineY), gridBrush, 1f);

                for (int i = 1; i < 4; i++)
                {
                    float y = firstLineY + rowHeight * i;
                    rt.DrawLine(new SharpDX.Vector2(panel.X, y), new SharpDX.Vector2(panel.X + panel.W, y), gridBrush, 1f);
                }

                rt.DrawLine(new SharpDX.Vector2(panel.X, lastLineY), new SharpDX.Vector2(panel.X + panel.W, lastLineY), gridBrush, 1f);

                SharpDX.RectangleF orderLimitRect = new SharpDX.RectangleF(panel.X, firstLineY, panel.W, rowHeight);
                string orderLimitLabel = "Order Limit";
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, orderLimitLabel, textFormat, orderLimitRect.Width, rowHeight))
                {
                    float textX = panel.X + panel.W - layout.Metrics.Width - 6f;
                    float textY = firstLineY + (rowHeight - layout.Metrics.Height) / 2f;
                    rt.DrawTextLayout(new SharpDX.Vector2(textX, textY), layout, textBrush);
                }

                SharpDX.RectangleF absorptionRect = new SharpDX.RectangleF(panel.X, firstLineY + rowHeight, panel.W, rowHeight);
                string absorptionLabel = "Absorption";
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, absorptionLabel, textFormat, absorptionRect.Width, rowHeight))
                {
                    float textX = panel.X + panel.W - layout.Metrics.Width - 6f;
                    float textY = absorptionRect.Y + (rowHeight - layout.Metrics.Height) / 2f;
                    rt.DrawTextLayout(new SharpDX.Vector2(textX, textY), layout, textBrush);
                }

                SharpDX.RectangleF divergenceRect = new SharpDX.RectangleF(panel.X, firstLineY + rowHeight * 3f, panel.W, rowHeight);
                string divergenceLabel = "Divergence";
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, divergenceLabel, textFormat, divergenceRect.Width, rowHeight))
                {
                    float textX = panel.X + panel.W - layout.Metrics.Width - 6f;
                    float textY = divergenceRect.Y + (rowHeight - layout.Metrics.Height) / 2f;
                    rt.DrawTextLayout(new SharpDX.Vector2(textX, textY), layout, textBrush);
                }

                int startBar = Math.Max(ChartBars.FromIndex, 0);
                int endBar = Math.Min(ChartBars.ToIndex, Bars.Count - 1);

                float triangleSize = 5f;
                float row1Middle = firstLineY + rowHeight / 2f;
                float row2Top = firstLineY + rowHeight * 1f;
                float row2Middle = row2Top + rowHeight / 2f;
                float row4Top = firstLineY + rowHeight * 3f;
                float row4Middle = row4Top + rowHeight / 2f;

                for (int barIndex = startBar; barIndex <= endBar; barIndex++)
                {
                    if (!orderLimitSignalsByBar.TryGetValue(barIndex, out int side))
                        continue;

                    float x = (float)chartControl.GetXByBarIndex(ChartBars, barIndex);
                    bool isBidSide = side > 0;
                    SharpDX.Color color = isBidSide ? new SharpDX.Color(34, 139, 34) : new SharpDX.Color(255, 102, 0);

                    DrawTriangle(rt, x, row1Middle, triangleSize, isBidSide, color);
                }

                for (int barIndex = startBar; barIndex <= endBar; barIndex++)
                {
                    if (!absorptionSignalsByBar.TryGetValue(barIndex, out int side))
                        continue;

                    float x = (float)chartControl.GetXByBarIndex(ChartBars, barIndex);
                    bool isBidSideAbs = side < 0;
                    bool pointingUp = isBidSideAbs;
                    SharpDX.Color color = side > 0 ? new SharpDX.Color(255, 102, 0) : new SharpDX.Color(34, 139, 34);

                    DrawTriangle(rt, x, row2Middle, triangleSize, pointingUp, color);
                }

                for (int barIndex = startBar; barIndex <= endBar; barIndex++)
                {
                    if (!divergenceSignalsByBar.TryGetValue(barIndex, out int side))
                        continue;

                    float x = (float)chartControl.GetXByBarIndex(ChartBars, barIndex);
                    bool pointingUp = side > 0;
                    SharpDX.Color color = side > 0 ? new SharpDX.Color(34, 139, 34) : new SharpDX.Color(255, 102, 0);

                    DrawTriangle(rt, x, row4Middle, triangleSize, pointingUp, color);
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

            return CacheIndicator<a2c>(new a2c()
            {
                TimeFrameMinutes = timeFrameMinutes,
                ResetSession = resetSession,
                ShowHistory = showHistory,
                MultiplyVolume = multiplyVolume,
                MinContracts = minContracts,
                AbsMultiplyVolume = absMultiplyVolume,
                AbsMinContracts = absMinContracts,
                AbsMinSideShare = absMinSideShare,
                DivTimeFrameMinutes = divTimeFrameMinutes,
                DivLookbackBars = divLookbackBars,
                DivMinPriceMoveTicks = divMinPriceMoveTicks,
                DivMinDeltaMove = divMinDeltaMove
            }, input, ref cachea2c);
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

        public Indicators.a2c a2c(ISeries<double> input, int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
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

        public Indicators.a2c a2c(ISeries<double> input, int timeFrameMinutes, bool resetSession, bool showHistory, double multiplyVolume, int minContracts, double absMultiplyVolume, int absMinContracts, double absMinSideShare, int divTimeFrameMinutes, int divLookbackBars, int divMinPriceMoveTicks, int divMinDeltaMove)
        {
            return indicator.a2c(input, timeFrameMinutes, resetSession, showHistory, multiplyVolume, minContracts, absMultiplyVolume, absMinContracts, absMinSideShare, divTimeFrameMinutes, divLookbackBars, divMinPriceMoveTicks, divMinDeltaMove);
        }
    }
}
#endregion
