#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
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

        private static readonly MethodInfo drawLineTimeWithStyle;
        private static readonly MethodInfo drawLineTimeWidthOnly;
        private static readonly MethodInfo drawLineTimeBasic;
        private static readonly object     dashStyleDashValue;

        static a2unfibusi()
        {
            foreach (var method in typeof(Draw).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Line")
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length == 10 && parameters[3].ParameterType == typeof(DateTime) && parameters[5].ParameterType == typeof(DateTime))
                {
                    if (parameters[8].ParameterType.IsEnum)
                    {
                        drawLineTimeWithStyle = method;
                        try
                        {
                            dashStyleDashValue = Enum.Parse(parameters[8].ParameterType, "Dash");
                        }
                        catch
                        {
                            dashStyleDashValue = null;
                        }
                    }
                }
                else if (parameters.Length == 9 && parameters[3].ParameterType == typeof(DateTime) && parameters[5].ParameterType == typeof(DateTime) && parameters[8].ParameterType == typeof(int))
                {
                    drawLineTimeWidthOnly = method;
                }
                else if (parameters.Length == 8 && parameters[3].ParameterType == typeof(DateTime) && parameters[5].ParameterType == typeof(DateTime))
                {
                    drawLineTimeBasic = method;
                }
            }
        }

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
        public int MinOppositeVolume { get; set; } = 1;

        [Browsable(false), XmlIgnore]
        public bool ResetAtSessionStart { get; set; } = true;

        [Browsable(false), XmlIgnore]
        public int LineWidth { get; set; } = 1;

        [Browsable(false), XmlIgnore]
        public int TextOffsetTicks { get; set; } = 1;

        [XmlIgnore]
        [Display(Name = "Color linea", GroupName = "a2unfibusi", Order = 3)]
        public Brush ColorLinea { get; set; } = Brushes.Gold;

        [Browsable(false)]
        public string ColorLineaSerializable
        {
            get { return Serialize.BrushToString(ColorLinea); }
            set { ColorLinea = Serialize.StringToBrush(value); }
        }

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
            if (computeSeriesIndex < 0 || BarsArray == null || BarsArray.Length <= computeSeriesIndex)
                return;

            // 1) Reset al inicio de sesión (sobre la serie de CÁLCULO)
            if (BarsInProgress == computeSeriesIndex && BarsArray[computeSeriesIndex].IsFirstBarOfSession)
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

            // Dibuja inmediatamente con extremo derecho extendido hacia la derecha del gráfico
            DateTime baseEndTime = (CurrentBars[0] >= 0) ? Times[0][0] : detectedTime;
            DateTime endTime     = baseEndTime.AddDays(365);
            DrawLevelLine(lvl, detectedTime, endTime);
            DrawLevelText(lvl);
        }

        private void DrawLevelLine(UBLevel lvl, DateTime startTime, DateTime endTime)
        {
            Brush b = ColorLinea;
            if (drawLineTimeWithStyle != null && dashStyleDashValue != null)
            {
                drawLineTimeWithStyle.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b, dashStyleDashValue, LineWidth });
                return;
            }

            if (drawLineTimeWidthOnly != null)
            {
                drawLineTimeWidthOnly.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b, LineWidth });
                return;
            }

            if (drawLineTimeBasic != null)
            {
                drawLineTimeBasic.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b });
            }
        }

        private void DrawLevelText(UBLevel lvl)
        {
            // Texto "UB" ligeramente por encima de la línea
            double y = lvl.Price + TextOffsetTicks * TickSize;
            Brush  b = ColorLinea;

            Draw.Text(this, lvl.TagText, false, "UB",
                      lvl.DetectedTime, y, 0, b, textFont, System.Windows.TextAlignment.Left, null, null, 0);
        }

        private void ExtendLinesToRightEdge(DateTime lastPrimaryBarTime)
        {
            // Extiende el extremo derecho un poco más allá de la última barra del primario
            DateTime endTime = lastPrimaryBarTime.AddDays(365);

            // Redibuja con el mismo tag para "mover" el extremo derecho
            foreach (var lvl in levels)
                DrawLevelLine(lvl, lvl.DetectedTime, endTime);
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || levels.Count == 0)
                return;

            double last = Instrument.MasterInstrument.RoundToTickSize(e.Price);
            var toRemove = new List<UBLevel>();

            foreach (var lvl in levels)
            {
                if (IsLevelCompleted(lvl, last, last))
                    toRemove.Add(lvl);
            }

            foreach (var lvl in toRemove)
                RemoveLevel(lvl);
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
                if (IsLevelCompleted(lvl, upThr, downThr))
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

        private bool IsLevelCompleted(UBLevel lvl, double upThr, double downThr)
            => lvl.IsHigh
                   ? upThr   >= lvl.Price + TickSize
                   : downThr <= lvl.Price - TickSize;

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
		public a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume)
		{
			return a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume);
		}

		public a2unfibusi a2unfibusi(ISeries<double> input, int frameBaseTimeMinutes, int minOppositeVolume)
		{
			if (cachea2unfibusi != null)
				for (int idx = 0; idx < cachea2unfibusi.Length; idx++)
					if (cachea2unfibusi[idx] != null && cachea2unfibusi[idx].FrameBaseTimeMinutes == frameBaseTimeMinutes && cachea2unfibusi[idx].MinOppositeVolume == minOppositeVolume && cachea2unfibusi[idx].EqualsInput(input))
						return cachea2unfibusi[idx];
			return CacheIndicator<a2unfibusi>(new a2unfibusi(){ FrameBaseTimeMinutes = frameBaseTimeMinutes, MinOppositeVolume = minOppositeVolume }, input, ref cachea2unfibusi);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume)
		{
			return indicator.a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume);
		}

		public Indicators.a2unfibusi a2unfibusi(ISeries<double> input , int frameBaseTimeMinutes, int minOppositeVolume)
		{
			return indicator.a2unfibusi(input, frameBaseTimeMinutes, minOppositeVolume);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a2unfibusi a2unfibusi(int frameBaseTimeMinutes, int minOppositeVolume)
		{
			return indicator.a2unfibusi(Input, frameBaseTimeMinutes, minOppositeVolume);
		}

		public Indicators.a2unfibusi a2unfibusi(ISeries<double> input , int frameBaseTimeMinutes, int minOppositeVolume)
		{
			return indicator.a2unfibusi(input, frameBaseTimeMinutes, minOppositeVolume);
		}
	}
}

#endregion
