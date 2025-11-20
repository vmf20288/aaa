#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media; // Brush / Brushes
using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools; // Stroke
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2imbalance : Indicator
    {
        #region Campos privados
        private int volBip = -1;
        private VolumetricBarsType volBarsType;
        private double volTickSize = 0;
        private readonly Dictionary<string, StackLine> activeLines = new Dictionary<string, StackLine>(); // tag -> info

        // v6: radio fijo de proximidad para dedupe por lado (en ticks)
        private const int ProximityTicksFixed = 10;
        #endregion

        #region Clases auxiliares
        private class StackLine
        {
            public string TagRay { get; set; }
            public string TagText { get; set; }
            public double Price { get; set; } // precio medio actual del stack (para invalidación)
            public bool IsAskStack { get; set; } // true = ASK (soporte, verde); false = BID (resistencia, roja)
            public DateTime BarTime { get; set; } // tiempo de la barra volumétrica donde se originó
            public double RunStartPrice { get; set; } // precio de inicio del stack (para reusar tag intrabar)
        }
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a2imbalance";
                Description = "Marca stacks de imbalances diagonales en Volumetric (Order Flow+).";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DrawOnPricePanel = true;
                DisplayInDataBox = false;
                IsSuspendedWhileInactive = true;
                TimeFrameVelas = 5; // minutos
                ImbalanceRatio = 3.0; // 300%
                MinDeltaImbalance = 0; // delta mínimo diagonal
                StackImbalance = 3; // niveles consecutivos
                ToleranciaBorrarTicks = 6; // ticks
                UsarVolumetricoInvalidacion = false;
            }
            else if (State == State.Configure)
            {
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, TimeFrameVelas, VolumetricDeltaType.BidAsk, 1);
                volBip = BarsArray.Length - 1;
            }
            else if (State == State.DataLoaded)
            {
                volBarsType = BarsArray[volBip].BarsType as VolumetricBarsType;
                if (volBarsType != null)
                    volTickSize = BarsArray[volBip].Instrument.MasterInstrument.TickSize;
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // 1) Escaneo INTRABAR de stacks en la serie Volumetric (barra en formación)
            if (BarsInProgress == volBip)
            {
                if (volBarsType == null || CurrentBars[volBip] < 0 || volTickSize <= 0)
                    return;

                bool isHistorical = State == State.Historical;
                bool isRealtimeLike = State == State.Realtime || State == State.Transition;

                int volBarIndex;
                DateTime volBarTime;
                double low, high;

                if (isHistorical)
                {
                    // Solo procesar barras cerradas (on bar close). Se usa la barra anterior (barsAgo=1) en el primer tick de la siguiente.
                    if (!IsFirstTickOfBar || CurrentBars[volBip] < 1)
                        return;

                    volBarIndex = CurrentBars[volBip] - 1;
                    volBarTime = Times[volBip][1];
                    low = Lows[volBip][1];
                    high = Highs[volBip][1];
                }
                else if (isRealtimeLike)
                {
                    // Escaneo intrabar en tiempo real/transición sobre la barra en formación (barsAgo=0)
                    volBarIndex = CurrentBars[volBip];
                    volBarTime = Times[volBip][0];
                    low = Lows[volBip][0];
                    high = Highs[volBip][0];
                }
                else
                {
                    return;
                }

                if (high < low)
                {
                    double t = high;
                    high = low;
                    low = t;
                }

                DetectAndDrawStacks(volBarIndex, volBarTime, low, high, true); // ASK (soporte)
                DetectAndDrawStacks(volBarIndex, volBarTime, low, high, false); // BID (resistencia)
                return;
            }

            // 2) Borrado por sesión e invalidez en la serie primaria
            if (BarsInProgress == 0)
            {
                if (Bars != null && Bars.IsFirstBarOfSession)
                    ClearAllStackLines();

                if (activeLines.Count > 0)
                    CheckInvalidations();

                return;
            }
        }
        #endregion

        #region Detección de stacks y dibujo
        private void DetectAndDrawStacks(int volBarIndex, DateTime volBarTime, double low, double high, bool checkAskSide)
        {
            int runCount = 0;
            double runStartPrice = double.NaN;
            double step = volTickSize;

            // v5: límites para asegurar existencia de la diagonal vecina
            double start = checkAskSide ? (low + step) : low; // BUY (ASK) arranca en low+step
            double end = checkAskSide ? high : (high - step); // SELL (BID) termina en high-step
            if (end < start - 1e-10)
                return;

            for (double p = start; p <= end + 1e-10; p += step)
            {
                long dominant, opposite;
                if (checkAskSide)
                {
                    dominant = volBarsType.Volumes[volBarIndex].GetAskVolumeForPrice(p);
                    opposite = volBarsType.Volumes[volBarIndex].GetBidVolumeForPrice(p - step);
                }
                else
                {
                    dominant = volBarsType.Volumes[volBarIndex].GetBidVolumeForPrice(p);
                    opposite = volBarsType.Volumes[volBarIndex].GetAskVolumeForPrice(p + step);
                }

                bool isImb = IsDiagonalImbalanceSafe(dominant, opposite);
                if (isImb)
                {
                    if (runCount == 0)
                        runStartPrice = p;
                    runCount++;
                }
                else
                {
                    if (runCount >= StackImbalance && !double.IsNaN(runStartPrice))
                        CreateOrUpdateStackLine(volBarTime, runStartPrice, runCount, checkAskSide);
                    runCount = 0;
                    runStartPrice = double.NaN;
                }
            }

            if (runCount >= StackImbalance && !double.IsNaN(runStartPrice))
                CreateOrUpdateStackLine(volBarTime, runStartPrice, runCount, checkAskSide);
        }

        // v5: opposite==0 -> delta mínimo; opposite>0 -> ratio + delta
        private bool IsDiagonalImbalanceSafe(long dominant, long opposite)
        {
            if (dominant <= 0)
                return false;

            if (opposite <= 0)
                return dominant >= MinDeltaImbalance;

            bool ratioOk = (double)dominant >= ImbalanceRatio * (double)opposite;
            bool deltaOk = Math.Abs(dominant - opposite) >= MinDeltaImbalance;
            return ratioOk && deltaOk;
        }

        // Helper: DateTime (bar Volumétrico) -> barsAgo en serie primaria (BIP 0)
        private int BarsAgoOnPrimary(DateTime anchorTime)
        {
            int idxOnPrimary = BarsArray[0].GetBar(anchorTime);
            if (idxOnPrimary < 0)
                return CurrentBars[0]; // si no existe, anclar en el extremo izquierdo visible

            int barsAgo = CurrentBars[0] - idxOnPrimary;
            if (barsAgo < 0)
                barsAgo = 0; // por seguridad si el primario va "adelantado"
            return barsAgo;
        }

        // v6: Crea o ACTUALIZA la línea del stack intrabar (misma barra y mismo runStartPrice/lado)
        // + DEDUPE por LADO en zona fija ±10 ticks ANTES de dibujar
        private void CreateOrUpdateStackLine(DateTime barTime, double runStartPrice, int runCount, bool isAskSide)
        {
            // No dibujar si la serie primaria aún no tiene al menos 1 bar a la izquierda
            if (CurrentBars[0] < 1)
                return;

            // Punto medio del stack (tu lógica original)
            double midPrice = runStartPrice + ((runCount - 1) * 0.5) * volTickSize;

            // v6: DEDUPE por lado con zona fija ±10 ticks alrededor del nuevo midPrice
            double proxTol = ProximityTicksFixed * TickSize;
            if (activeLines.Count > 0)
            {
                var toRemoveProx = new List<string>();
                foreach (var kvp in activeLines)
                {
                    var info = kvp.Value;
                    if (info.IsAskStack == isAskSide && Math.Abs(info.Price - midPrice) <= proxTol)
                        toRemoveProx.Add(kvp.Key);
                }

                foreach (var oldTag in toRemoveProx)
                    RemoveStackLine(oldTag);
            }

            string side = isAskSide ? "ASK" : "BID";

            // Tag estable por barra y stack: ancla en runStartPrice (no midPrice) para actualizar intrabar
            string tagRay = $"a2imbalance_v6_{side}_{barTime:yyyyMMddHHmmss}_{Instrument.FullName}_{runStartPrice.ToString("0.########")}";
            string tagText = $"{tagRay}_text";

            // (opcional) limpieza adicional del MISMO stack en la misma barra (si quedó algo por timing)
            double tolStart = volTickSize * 0.25;
            var toRemoveSameStack = new List<string>();
            foreach (var kvp in activeLines)
            {
                var info = kvp.Value;
                if (info.IsAskStack == isAskSide && info.BarTime == barTime && Math.Abs(info.RunStartPrice - runStartPrice) <= tolStart && kvp.Key != tagRay)
                    toRemoveSameStack.Add(kvp.Key);
            }

            foreach (var oldTag in toRemoveSameStack)
                RemoveStackLine(oldTag);

            int startBarsAgo = BarsAgoOnPrimary(barTime);
            int endBarsAgo = 0; // extender hacia la derecha

            // HOTFIX horizontal: si ambos anclajes caen en el mismo bar (startBarsAgo <= 0), fuerza 1 bar a la izquierda
            if (startBarsAgo <= endBarsAgo)
            {
                if (CurrentBars[0] >= 1)
                    startBarsAgo = 1;
                else
                    return;
            }

            Brush brush = isAskSide ? Brushes.LimeGreen : Brushes.Red;

            // Re-dibujo (crear o actualizar el mismo tag) — dos puntos con la MISMA Y => HORIZONTAL
            var ray = Draw.Ray(this, tagRay, startBarsAgo, midPrice, endBarsAgo, midPrice, brush);
            if (ray != null && ray.Stroke != null)
                ray.Stroke.Width = 2;

            Draw.Text(this, tagText, "Stack Imbalance", startBarsAgo, midPrice, brush);

            // Memoria/estado del stack
            if (!activeLines.TryGetValue(tagRay, out var infoNew))
            {
                infoNew = new StackLine
                {
                    TagRay = tagRay,
                    TagText = tagText,
                    Price = midPrice,
                    IsAskStack = isAskSide,
                    BarTime = barTime,
                    RunStartPrice = runStartPrice
                };
                activeLines[tagRay] = infoNew;
            }
            else
            {
                infoNew.Price = midPrice; // actualizar precio (para invalidaciones) si ya existía
            }
        }
        #endregion

        #region Borrado por invalidez y por sesión
        private void CheckInvalidations()
        {
            if (activeLines.Count == 0)
                return;

            bool useVolForInvalidation = UsarVolumetricoInvalidacion && volTickSize > 0 && volBip >= 0 && CurrentBars.Length > volBip && CurrentBars[volBip] >= 0;
            double tickSize = useVolForInvalidation ? volTickSize : TickSize;
            double tol = ToleranciaBorrarTicks * tickSize;

            double currentHigh = useVolForInvalidation ? Highs[volBip][0] : High[0];
            double currentLow = useVolForInvalidation ? Lows[volBip][0] : Low[0];

            var toFinalize = new List<string>();
            foreach (var kvp in activeLines)
            {
                var info = kvp.Value;
                if (info.IsAskStack)
                {
                    // soporte: borrar si rompe por debajo (Price - tol)
                    if (currentLow <= info.Price - tol)
                        toFinalize.Add(kvp.Key);
                }
                else
                {
                    // resistencia: borrar si rompe por encima (Price + tol)
                    if (currentHigh >= info.Price + tol)
                        toFinalize.Add(kvp.Key);
                }
            }

            foreach (var tag in toFinalize)
                FinalizeStackLine(tag);
        }

        private void FinalizeStackLine(string tagRay)
        {
            if (!activeLines.TryGetValue(tagRay, out var info))
                return;

            int startBarsAgo = BarsAgoOnPrimary(info.BarTime);
            int endBarsAgo = 0;

            if (startBarsAgo <= endBarsAgo && CurrentBars[0] >= 1)
                startBarsAgo = 1;

            Brush brush = info.IsAskStack ? Brushes.LimeGreen : Brushes.Red;

            // reemplazar el Ray infinito por una línea fija hasta el bar que invalida
            RemoveDrawObjectSafe(info.TagRay);
            string finalTag = $"{info.TagRay}_final";
            var line = Draw.Line(this, finalTag, startBarsAgo, info.Price, endBarsAgo, info.Price, brush);
            if (line != null && line.Stroke != null)
                line.Stroke.Width = 2;

            RemoveDrawObjectSafe(info.TagText);
            activeLines.Remove(tagRay);
        }

        private void ClearAllStackLines()
        {
            if (activeLines.Count == 0)
                return;

            foreach (var kvp in activeLines)
            {
                RemoveDrawObjectSafe(kvp.Value.TagRay);
                RemoveDrawObjectSafe(kvp.Value.TagText);
            }

            activeLines.Clear();
        }

        private void RemoveStackLine(string tagRay)
        {
            if (!activeLines.TryGetValue(tagRay, out var info))
                return;

            RemoveDrawObjectSafe(info.TagRay);
            RemoveDrawObjectSafe(info.TagText);
            activeLines.Remove(tagRay);
        }

        private void RemoveDrawObjectSafe(string tag)
        {
            try
            {
                RemoveDrawObject(tag);
            }
            catch
            {
                /* ignorar */
            }
        }
        #endregion

        #region Propiedades (parámetros)
        [NinjaScriptProperty]
        [Display(Name = "Time frame velas (min)", GroupName = "Parámetros", Order = 1)]
        [Range(1, int.MaxValue)]
        public int TimeFrameVelas { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stack imbalance", GroupName = "Parámetros", Order = 2)]
        [Range(1, 50)]
        public int StackImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance ratio (e.g. 3.0 = 300%)", GroupName = "Parámetros", Order = 3)]
        [Range(1.0, double.MaxValue)]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min delta para imbalance", GroupName = "Parámetros", Order = 4)]
        [Range(0, int.MaxValue)]
        public int MinDeltaImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tolerancia borrar (ticks)", GroupName = "Parámetros", Order = 5)]
        [Range(0, 1000)]
        public int ToleranciaBorrarTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar Volumetric para invalidación", GroupName = "Parámetros", Order = 6)]
        public bool UsarVolumetricoInvalidacion { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.
namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a2imbalance[] cachea2imbalance;

        public a2imbalance a2imbalance(int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            return a2imbalance(Input, timeFrameVelas, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, usarVolumetricoInvalidacion);
        }

        public a2imbalance a2imbalance(ISeries<double> input, int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            if (cachea2imbalance != null)
                for (int idx = 0; idx < cachea2imbalance.Length; idx++)
                    if (cachea2imbalance[idx] != null && cachea2imbalance[idx].TimeFrameVelas == timeFrameVelas && cachea2imbalance[idx].StackImbalance == stackImbalance && cachea2imbalance[idx].ImbalanceRatio == imbalanceRatio && cachea2imbalance[idx].MinDeltaImbalance == minDeltaImbalance && cachea2imbalance[idx].ToleranciaBorrarTicks == toleranciaBorrarTicks && cachea2imbalance[idx].UsarVolumetricoInvalidacion == usarVolumetricoInvalidacion && cachea2imbalance[idx].EqualsInput(input))
                        return cachea2imbalance[idx];

            return CacheIndicator<a2imbalance>(new a2imbalance()
            {
                TimeFrameVelas = timeFrameVelas,
                StackImbalance = stackImbalance,
                ImbalanceRatio = imbalanceRatio,
                MinDeltaImbalance = minDeltaImbalance,
                ToleranciaBorrarTicks = toleranciaBorrarTicks,
                UsarVolumetricoInvalidacion = usarVolumetricoInvalidacion
            }, input, ref cachea2imbalance);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a2imbalance a2imbalance(int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            return indicator.a2imbalance(Input, timeFrameVelas, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, usarVolumetricoInvalidacion);
        }

        public Indicators.a2imbalance a2imbalance(ISeries<double> input, int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            return indicator.a2imbalance(input, timeFrameVelas, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, usarVolumetricoInvalidacion);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a2imbalance a2imbalance(int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            return indicator.a2imbalance(Input, timeFrameVelas, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, usarVolumetricoInvalidacion);
        }

        public Indicators.a2imbalance a2imbalance(ISeries<double> input, int timeFrameVelas, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool usarVolumetricoInvalidacion)
        {
            return indicator.a2imbalance(input, timeFrameVelas, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, usarVolumetricoInvalidacion);
        }
    }
}
#endregion
