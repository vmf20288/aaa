#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Xml.Serialization;
using System.Windows;              // <-- Necesario para TextAlignment
using System.Windows.Media;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;       // <-- SimpleFont, etc.
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ---------------------------------------------------------------------------------------
// MinTrade (v2) — Cálculo tick a tick independiente del marco temporal del gráfico
// ---------------------------------------------------------------------------------------
// Qué hace:
//   - Detecta un trade individual con volumen >= MinTrade (por defecto 25 contratos).
//   - Dibuja una línea horizontal hacia la derecha desde el momento del trade.
//   - Encima de la línea coloca el texto: "MinTrade (VOL)".
//   - El texto es ROJO si el trade ocurrió en el BID y VERDE si ocurrió en el ASK.
//   - Expone dos Series para que otros indicadores las consulten:
//       * LastMinTradeVolume  -> volumen del último trade que cumplió la condición (en el tick actual), o NaN.
//       * LastMinTradePrice   -> precio de ese trade (en el tick actual), o NaN.
// Cómo garantiza tick a tick e independencia del timeframe/agregación:
//   - Agrega una serie secundaria de 1-Tick (AddDataSeries(BarsPeriodType.Tick, 1)) y
//     realiza TODA la lógica en BarsInProgress == 1 (la serie de 1 tick).
//   - Ancla los dibujos por DateTime del tick (Times[1][0]) para que no dependan del timeframe del chart.
//
// Versión interna (comentario): v2
// Cambios v2:
//   - Se agregó `using System.Windows;` para resolver TextAlignment.
//   - Se calificó completamente `DashStyleHelper` como `NinjaTrader.Gui.DashStyleHelper`.
// ---------------------------------------------------------------------------------------

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum TradeSide
    {
        Unknown = 0,
        Bid = 1,
        Ask = 2
    }

    public class MinTrade : Indicator
    {
        // ====== VERSIONADO INTERNO (no modifica el nombre del indicador) ======
        private const string __VERSION__ = "v2";

        // Buffer para mapear lado del último trade (por OnMarketData)
        private double lastBid = double.NaN;
        private double lastAsk = double.NaN;

        private struct TradeRec
        {
            public double Price;
            public long Volume;
            public TradeSide Side;
        }

        private List<TradeRec> tradeBuffer;
        private const int MaxTradeBuffer = 512;

        // Series expuestas (se usarán Values[0] y Values[1] con plots transparentes)
        private Series<double> _volSeries;
        private Series<double> _priceSeries;

        // ==================== PROPIEDADES DE USUARIO ====================
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MinTrade", Order = 1, GroupName = "Parámetros")]
        public int MinTradeThreshold { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "MinTrade"; // Mantener nombre limpio; la versión va en comentario
                Description              = "Detecta trades individuales >= MinTrade y etiqueta el precio con una línea/Texto (tick a tick).";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;

                MinTradeThreshold = 25; // default modificable

                // Plots transparentes para exponer valores a otros indicadores
                AddPlot(Brushes.Transparent, "LastMinTradeVolume");
                AddPlot(Brushes.Transparent, "LastMinTradePrice");
            }
            else if (State == State.Configure)
            {
                // Serie secundaria de 1 tick => cálculo SIEMPRE tick a tick, independiente del chart
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                tradeBuffer  = new List<TradeRec>(MaxTradeBuffer);
                _volSeries   = Values[0];
                _priceSeries = Values[1];
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Mantenemos Bid/Ask para inferir el lado del trade
            if (e.MarketDataType == MarketDataType.Bid)
            {
                lastBid = e.Price;
            }
            else if (e.MarketDataType == MarketDataType.Ask)
            {
                lastAsk = e.Price;
            }
            else if (e.MarketDataType == MarketDataType.Last)
            {
                TradeSide side = TradeSide.Unknown;

                if (!double.IsNaN(lastBid) && Math.Abs(e.Price - lastBid) <= TickSize * 0.5)
                    side = TradeSide.Bid;
                else if (!double.IsNaN(lastAsk) && Math.Abs(e.Price - lastAsk) <= TickSize * 0.5)
                    side = TradeSide.Ask;

                // Guardamos en buffer para que OnBarUpdate (serie 1-tick) recupere el lado
                tradeBuffer.Add(new TradeRec { Price = e.Price, Volume = e.Volume, Side = side });
                if (tradeBuffer.Count > MaxTradeBuffer)
                    tradeBuffer.RemoveAt(0);
            }
        }

        protected override void OnBarUpdate()
        {
            // Procesar EXCLUSIVAMENTE la serie 1-Tick (BarsInProgress == 1) para ser 100% tick a tick
            if (BarsInProgress != 1)
                return;

            long vol = (long)Volume[0];

            if (vol >= MinTradeThreshold)
            {
                double   price = Close[0];
                DateTime tTick = Times[1][0];

                // Buscar el lado (si hay datos de market data). Si no, queda Unknown.
                TradeSide side = FindSide(price, vol);

                // Exponer valores para otros indicadores (en el tick del disparo)
                _volSeries[0]   = vol;
                _priceSeries[0] = price;

                // Dibujos:
                string tagBase = string.Format(CultureInfo.InvariantCulture,
                    "MinTrade_{0}_{1}_{2}_{3}",
                    Instrument?.MasterInstrument?.Name ?? "Instr",
                    tTick.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
                    price,
                    vol);

                // Línea horizontal extendida hacia la derecha (Ray)
                Brush lineBrush = Brushes.DimGray;
                Draw.Ray(this, tagBase + "_L",
                         false,                    // isAutoScale
                         0, price,
                         -1, price,
                         lineBrush, NinjaTrader.Gui.DashStyleHelper.Solid, 1);

                // Texto encima de la línea
                string etiqueta = $"MinTrade ({vol})";
                Brush  textBrush = side == TradeSide.Bid ? Brushes.Red
                                  : side == TradeSide.Ask ? Brushes.Green
                                  : Brushes.Gray;

                double yText = price + (2 * TickSize);
                Draw.Text(this, tagBase + "_T",
                          true,                       // isAutoScale
                          etiqueta,
                          tTick, yText, 0,
                          textBrush,
                          new SimpleFont("Arial", 12),
                          TextAlignment.Center,
                          Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                // Si no hay disparo en este tick, exponemos NaN para facilitar lectura aguas arriba
                _volSeries[0]   = double.NaN;
                _priceSeries[0] = double.NaN;
            }
        }

        private TradeSide FindSide(double price, long volume)
        {
            // Busca en el buffer el trade más reciente con mismo precio (±0.5 tick) y volumen exacto
            for (int i = tradeBuffer.Count - 1; i >= 0; i--)
            {
                var tr = tradeBuffer[i];
                if (tr.Volume == volume && Math.Abs(tr.Price - price) <= TickSize * 0.5)
                {
                    tradeBuffer.RemoveAt(i);
                    return tr.Side;
                }
            }
            return TradeSide.Unknown;
        }

        #region Series expuestas para otros indicadores
        [Browsable(false), XmlIgnore]
        public Series<double> LastMinTradeVolume
        {
            get { return _volSeries; }
        }

        [Browsable(false), XmlIgnore]
        public Series<double> LastMinTradePrice
        {
            get { return _priceSeries; }
        }
        #endregion

        #region (Opcional) Debug
        private void TraceVersion()
        {
            Print(string.Format("MinTrade {0} cargado. Umbral actual: {1}", __VERSION__, MinTradeThreshold));
        }
        #endregion
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private MinTrade[] cacheMinTrade;
        public MinTrade MinTrade(int minTradeThreshold)
        {
            return MinTrade(Input, minTradeThreshold);
        }

        public MinTrade MinTrade(ISeries<double> input, int minTradeThreshold)
        {
            if (cacheMinTrade != null)
                for (int idx = 0; idx < cacheMinTrade.Length; idx++)
                    if (cacheMinTrade[idx] != null && cacheMinTrade[idx].MinTradeThreshold == minTradeThreshold && cacheMinTrade[idx].EqualsInput(input))
                        return cacheMinTrade[idx];
            return CacheIndicator<MinTrade>(new MinTrade(){ MinTradeThreshold = minTradeThreshold }, input, ref cacheMinTrade);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.MinTrade MinTrade(int minTradeThreshold)
        {
            return indicator.MinTrade(Input, minTradeThreshold);
        }

        public Indicators.MinTrade MinTrade(ISeries<double> input , int minTradeThreshold)
        {
            return indicator.MinTrade(input, minTradeThreshold);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.MinTrade MinTrade(int minTradeThreshold)
        {
            return indicator.MinTrade(Input, minTradeThreshold);
        }

        public Indicators.MinTrade MinTrade(ISeries<double> input , int minTradeThreshold)
        {
            return indicator.MinTrade(input, minTradeThreshold);
        }
    }
}

#endregion
