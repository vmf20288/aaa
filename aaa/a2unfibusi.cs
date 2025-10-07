#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX.DirectWrite;
#endregion

// ======================================================================================================
// a2unfibusi  —  v1
//
// README (solo lectura):
// ------------------------------------------------------------------------------------------------------
// Qué hace
//   - Detecta "Unfinished Business" (UB) en barras volumétricas (Order Flow Volumetric, Delta Bid/Ask).
//   - UB en High: si en el precio del High hay volumen en el Bid (Bid@High >= MinOppositeVolume).
//   - UB en Low : si en el precio del Low  hay volumen en el Ask (Ask@Low  >= MinOppositeVolume).
//   - Al detectar, dibuja una línea horizontal PUNTEADA desde la barra de detección hasta la derecha del gráfico,
//     y etiqueta "unfibusi" encima de la línea.
//   - Borra la línea cuando el precio "completa" el nivel con 1 tick de trade-through
//     (UB High: High >= nivel + TickSize; UB Low: Low <= nivel - TickSize).
//
// Cómo calcula (independiente del gráfico):
//   - SIEMPRE añade una serie interna Volumétrica (Order Flow +) con:
//       BarsPeriodType = Minute, valor = FrameBaseTimeMinutes, DeltaType = BidAsk, ticksPerLevel = 1.
//   - El cálculo/detección de UB SIEMPRE usa esa serie interna, aunque el gráfico esté en 1–3–5 min,
//     4 ticks, etc. Es decir, el "marco de cálculo" es fijo (por defecto, 5 min).
//
// Parámetros esenciales:
//   - FrameBaseTimeMinutes (default 5): timeframe base de CÁLCULO.
//   - MinOppositeVolume (default 10): umbral de volumen "opuesto" en el extremo para marcar UB.
//   - ResetAtSessionStart (default true): borra todos los niveles al iniciar la sesión.
//
// Requisitos:
//   - Datos que soporten Order Flow Volumetric (licencia Order Flow+).
//   - ticksPerLevel se fija en 1 internamente (NT8 no admite 0).
//
// Notas de uso:
//   - Los niveles UB suelen actuar como "imanes": el precio tiende a volver a testearlos.
//   - Úsalos como objetivos/confirmaciones; no como setup aislado.
// ------------------------------------------------------------------------------------------------------
// Próximas mejoras (futuras v2, v3, ...):
//   - Opción "StrictBothSides" (requerir volumen en ambos lados en el extremo).
//   - Colores/estilos configurables por propiedades.
//   - Persistencia entre sesiones o expiración por antigüedad.
// ======================================================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2unfibusi : Indicator
    {
        // -------------------------
        // Tipos y estado interno
        // -------------------------
        private class UBLevel
        {
            public double   Price;
            public bool     IsHigh;         // true = UB en High ; false = UB en Low
            public DateTime DetectedTime;   // tiempo de la barra (serie de cálculo)
            public string   TagLine;        // tag del Draw.Line
            public string   TagText;        // tag del Draw.Text
        }

        private readonly List<UBLevel> levels = new List<UBLevel>();
        private readonly Dictionary<string, UBLevel> levelsByKey = new Dictionary<string, UBLevel>();
        private int computeSeriesIndex = -1;            // índice de la serie volumétrica interna
        private SimpleFont textFont;

        // Colores/estilo (fijos en v1 para simplificar)
        private Brush brushHigh = Brushes.Red;
        private Brush brushLow  = Brushes.Green;
        private const NinjaTrader.Gui.Tools.DashStyleHelper dashStyle = NinjaTrader.Gui.Tools.DashStyleHelper.Dash;

        // -------------------------
        // Propiedades (usuario)
        // -------------------------

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Frame base time (min)", Description = "Timeframe base de CÁLCULO, en minutos (volumétrico interno).", Order = 1, GroupName = "a2unfibusi")]
        public int FrameBaseTimeMinutes { get; set; } = 5;

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MinVolume (opuesto)", Description = "Volumen mínimo en el lado opuesto en el extremo (Bid@High / Ask@Low).", Order = 2, GroupName = "a2unfibusi")]
        public int MinOppositeVolume { get; set; } = 10;

        [NinjaScriptProperty]
        [Display(Name = "Reset al inicio de sesión", Description = "Borra niveles al comenzar una nueva sesión.", Order = 3, GroupName = "a2unfibusi")]
        public bool ResetAtSessionStart { get; set; } = true;

        [Range(1, 10)]
        [Display(Name = "Grosor línea", Description = "Ancho de la línea punteada.", Order = 10, GroupName = "Estilo")]
        public int LineWidth { get; set; } = 2;

        [Range(1, 10)]
        [Display(Name = "Offset texto (ticks)", Description = "Separación vertical (en ticks) para el texto 'unfibusi'.", Order = 11, GroupName = "Estilo")]
        public int TextOffsetTicks { get; set; } = 1;

        // -------------------------
        // Ciclo de vida
        // -------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name       = "a2unfibusi";
                Calculate  = Calculate.OnBarClose;   // sólido para backtest; si quieres intrabar cambia a OnEachTick
                IsOverlay  = true;
                IsSuspendedWhileInactive = true;

                // Valores por defecto ya inicializados arriba
                textFont = new SimpleFont("Arial", 12) { Bold = true };
            }
            else if (State == State.Configure)
            {
                // Añade SIEMPRE una serie Volumétrica interna (cálculo fijo):
                // Volumetric Delta = BidAsk ; ticksPerLevel = 1 (0 no es válido)
                int tf = Math.Max(1, FrameBaseTimeMinutes);
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, tf, VolumetricDeltaType.BidAsk, 1);
                // El índice de esta serie será el último (tras agregar):
                computeSeriesIndex = BarsArray.Length - 1;
            }
            else if (State == State.DataLoaded)
            {
                if (textFont == null)
                    textFont = new SimpleFont("Arial", 12) { Bold = true };
            }
        }

        protected override void OnBarUpdate()
        {
            // Seguridad: necesitamos que existan ambas series (primaria e interna)
            if (CurrentBar < 0 || computeSeriesIndex < 1 || BarsArray == null || BarsArray.Length <= computeSeriesIndex)
                return;

            // 1) Reset al inicio de sesión (sobre la serie de CÁLCULO)
            if (BarsInProgress == computeSeriesIndex && ResetAtSessionStart && Bars.IsFirstBarOfSession)
                ClearAllLevels();

            // 2) Detección de UB SOLO en la serie de CÁLCULO (volumétrica interna)
            if (BarsInProgress == computeSeriesIndex)
                DetectUnfinishedBusiness();

            // 3) Extender líneas hasta el borde derecho usando el tiempo de la serie PRIMARIA
            if (BarsInProgress == 0)
                ExtendLinesToRightEdge(Time[0]);   // endTime = última barra visible del primario

            // 4) Comprobación de "completado" (1 tick de trade-through) tanto en la serie primaria
            //    como en la serie de cálculo, para reaccionar en cualquiera de las dos actualizaciones.
            TestAndRemoveCompletedLevels(BarsInProgress);
        }

        // -------------------------
        // Detección UB (serie cálculo)
        // -------------------------
        private void DetectUnfinishedBusiness()
        {
            // Asegurar que tenemos un VolumetricBarsType en la serie de cálculo
            var volType = BarsArray[computeSeriesIndex].BarsSeries.BarsType as VolumetricBarsType;
            if (volType == null)
                return;

            int cb = CurrentBars[computeSeriesIndex];
            if (cb < 0)
                return;

            var volumes = volType.Volumes[cb];
            double hi = Highs[computeSeriesIndex][0];
            double lo = Lows[computeSeriesIndex][0];

            // Volumen "opuesto" en los extremos:
            long bidAtHigh = volumes.GetBidVolumeForPrice(hi);
            long askAtLow  = volumes.GetAskVolumeForPrice(lo);

            bool ubHigh = bidAtHigh >= MinOppositeVolume;
            bool ubLow  = askAtLow  >= MinOppositeVolume;

            DateTime detectTime = Times[computeSeriesIndex][0];

            if (ubHigh)
                AddUbLevel(Instrument.MasterInstrument.RoundToTickSize(hi), true, detectTime);

            if (ubLow)
                AddUbLevel(Instrument.MasterInstrument.RoundToTickSize(lo), false, detectTime);
        }

        // -------------------------
        // Gestión de niveles
        // -------------------------
        private void AddUbLevel(double price, bool isHigh, DateTime detectedTime)
        {
            string key = BuildKey(price, isHigh);
            if (levelsByKey.ContainsKey(key))
                return; // Ya existe ese nivel activo

            var lvl = new UBLevel
            {
                Price        = price,
                IsHigh       = isHigh,
                DetectedTime = detectedTime,
                TagLine      = $"a2unfibusi_line_{(isHigh ? "H" : "L")}_{price.ToString("0.#####", CultureInfo.InvariantCulture)}_{detectedTime:yyyyMMddHHmmss}",
                TagText      = $"a2unfibusi_txt_{(isHigh ? "H" : "L")}_{price.ToString("0.#####", CultureInfo.InvariantCulture)}_{detectedTime:yyyyMMddHHmmss}"
            };

            levels.Add(lvl);
            levelsByKey[key] = lvl;

            // Dibuja inmediatamente con extremo derecho en la última barra del primario (si existe)
            DateTime endTime = (CurrentBars[0] >= 0) ? Times[0][0] : detectedTime;
            DrawLevelLine(lvl, detectedTime, endTime);
            DrawLevelText(lvl);
        }

        private void DrawLevelLine(UBLevel lvl, DateTime startTime, DateTime endTime)
        {
            Brush b = lvl.IsHigh ? brushHigh : brushLow;
            Draw.Line(this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b, dashStyle, LineWidth);
        }

        private void DrawLevelText(UBLevel lvl)
        {
            // Texto "unfibusi" ligeramente por encima de la línea
            double y = lvl.Price + TextOffsetTicks * TickSize;
            Brush  b = lvl.IsHigh ? brushHigh : brushLow;

            Draw.Text(this, lvl.TagText, false, "unfibusi",
                      lvl.DetectedTime, y, 0, b, textFont, TextAlignment.Left, null, null, 0);
        }

        private void ExtendLinesToRightEdge(DateTime endTime)
        {
            // Redibuja con el mismo tag para "mover" el extremo derecho a la última barra del primario
            foreach (var lvl in levels)
                DrawLevelLine(lvl, lvl.DetectedTime, endTime);
        }

        private void TestAndRemoveCompletedLevels(int seriesIndexUpdated)
        {
            if (levels.Count == 0)
                return;

            // High/Low de la serie que acaba de actualizarse
            double hi, lo;

            if (seriesIndexUpdated == computeSeriesIndex)
            {
                if (CurrentBars[computeSeriesIndex] < 0) return;
                hi = Highs[computeSeriesIndex][0];
                lo = Lows[computeSeriesIndex][0];
            }
            else
            {
                if (CurrentBar < 0) return; // serie primaria
                hi = High[0];
                lo = Low[0];
            }

            // Trade-through de 1 tick
            double upThr   = Instrument.MasterInstrument.RoundToTickSize(hi);
            double downThr = Instrument.MasterInstrument.RoundToTickSize(lo);

            // Lista temporal para eliminar sin modificar durante el foreach
            var toRemove = new List<UBLevel>();

            foreach (var lvl in levels)
            {
                bool completed = lvl.IsHigh
                                 ? upThr   >= lvl.Price + TickSize
                                 : downThr <= lvl.Price - TickSize;

                if (completed)
                    toRemove.Add(lvl);
            }

            foreach (var lvl in toRemove)
                RemoveLevel(lvl);
        }

        private void RemoveLevel(UBLevel lvl)
        {
            RemoveDrawObject(lvl.TagLine);
            RemoveDrawObject(lvl.TagText);

            levels.Remove(lvl);
            string key = BuildKey(lvl.Price, lvl.IsHigh);
            if (levelsByKey.ContainsKey(key))
                levelsByKey.Remove(key);
        }

        private void ClearAllLevels()
        {
            foreach (var lvl in levels.ToList())
                RemoveLevel(lvl);
        }

        private static string BuildKey(double price, bool isHigh)
            => $"{(isHigh ? "H" : "L")}@{price.ToString("0.#####", CultureInfo.InvariantCulture)}";
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
{
private a2unfibusi[] cachea2unfibusi;
public a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
return a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume, resetAtSessionStart);
}

public a2unfibusi a2unfibusi(ISeries<double> input, int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
if (cachea2unfibusi != null)
for (int idx = 0; idx < cachea2unfibusi.Length; idx++)
if (cachea2unfibusi[idx] != null && cachea2unfibusi[idx].FrameBaseTimeMinutes == frameBaseTimeMinutes && cachea2unfibusi[idx].MinOppositeVolume == minOppositeVolume && cachea2unfibusi[idx].ResetAtSessionStart == resetAtSessionStart && cachea2unfibusi[idx].EqualsInput(input))
return cachea2unfibusi[idx];
return CacheIndicator<a2unfibusi>(new a2unfibusi(){ FrameBaseTimeMinutes = frameBaseTimeMinutes, MinOppositeVolume = minOppositeVolume, ResetAtSessionStart = resetAtSessionStart }, input, ref cachea2unfibusi);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
return indicator.a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume, resetAtSessionStart);
}

public Indicators.a2unfibusi a2unfibusi(ISeries<double> input , int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
return indicator.a2unfibusi(input, frameBaseTimeMinutes, minOppositeVolume, resetAtSessionStart);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
return indicator.a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume, resetAtSessionStart);
}

public Indicators.a2unfibusi a2unfibusi(ISeries<double> input , int frameBaseTimeMinutes, int minOppositeVolume, bool resetAtSessionStart)
{
return indicator.a2unfibusi(input, frameBaseTimeMinutes, minOppositeVolume, resetAtSessionStart);
}
}
}

#endregion
