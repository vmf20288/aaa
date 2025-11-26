// a2hub (v6)
// ---------------------------------------------------------------------------------------
// README (solo lectura interna)
//
// Nombre de clase/archivo: a2hub   (NO cambiar el nombre de la clase/archivo)
//
// Descripción (v6):
//   - Detecta "MinTrade" a partir de un CLÚSTER de prints dentro de una ventana temporal
//     (ClusterWindowMs) con drift de precio (± DriftTicks).
//   - Filtros del clúster:
//       * MinPrint (ON/OFF): solo suma prints >= MinPrintVol.
//       * "Al menos" (ON/OFF): exige >= AlMenosMinTrade en algún print del clúster.
//   - Emite el nivel en cuanto el clúster alcanza MinTrade (y cumple filtros).
//   - Línea GRIS al nacer (Neutral), VERDE si Demand/Soporte (primer cierre 1m > nivel),
//     ROJA si Supply/Resistencia (primer cierre 1m < nivel). Sin tolerancia de confirmación.
//   - Invalidación por CIERRE 1m con Tolerancia (ticks): Supply rompe arriba; Demand rompe abajo.
//     Al invalidar: se corta la extensión, se conserva el color, y la etiqueta "MinTrade (VOL)"
//     queda siempre visible (rojo BID / verde ASK).
//   - **Anti‑dup (v6):** si en ≤ 300 ms y a ±1 tick aparece otro nivel del mismo lado,
//     se suma su volumen al nivel existente y NO se crea otra línea (la etiqueta se actualiza).
//   - Módulo Global:
//       * MinTrade (ON/OFF): activa/desactiva completamente el módulo MinTrade (detección y dibujo).
//       * Reset session (ON/OFF): controla si se limpian niveles y estado al inicio de cada nueva sesión
//         (ON = igual que ahora, OFF = conserva niveles históricos en el gráfico).
//
// Parámetros expuestos (solo los acordados):
//   - MinTrade (int)                 -> umbral de volumen del clúster para generar el nivel.
//   - Tolerancia (ticks) (int)       -> DEFAULT 8; solo afecta invalidación tras clasificar.
//   - Tiempo clúster (ms) (int)      -> ClusterWindowMs (DEFAULT 300).
//   - Drift (± ticks) (int)          -> DriftTicks (DEFAULT 2).
//   - Usar 'al menos' (bool)         -> UseAlMenos (DEFAULT true).
//   - Al menos (vol) (int)           -> AlMenosMinTrade (DEFAULT 10).
//   - Usar MinPrint (bool)           -> UseMinPrint (DEFAULT true).
//   - MinPrint (vol) (int)           -> MinPrintVol (DEFAULT 2).
//
// Changelog interno:
//   - v1: Detección básica con dibujo hacia adelante.
//   - v2: Fallback barsAgo + hotfix horizontal.
//   - v3: Revisión estructural, colores por lado, estado Neutral -> Confirmado.
//   - v4: 1m para clasificar/invalidar; propiedad Tolerancia (ticks).
//   - v5: Clúster (ventana ms + drift) con MinPrint/AlMenos; emisión inmediata por clúster.
//   - v6: **Anti‑dup fijo** (≤ 300 ms, ±1 tick, mismo lado): fusiona volumen y no crea otra línea.
//
// ---------------------------------------------------------------------------------------

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Xml.Serialization;
using System.Windows.Media;

using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui;              // DashStyleHelper
using NinjaTrader.Gui.Tools;        // SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2hub : Indicator
    {
        // Lado del trade (anidado para evitar colisiones)
        public enum MinTradeSide { Unknown = 0, Bid = 1, Ask = 2 }

        // Estado del nivel
        private enum LevelState { Neutral = 0, Demand = 1, Supply = 2, Invalid = 3 }

        private const string InternalName    = "a2hub";
        private const string InternalVersion = "v6";

        // --- Constantes Anti-dup (fijas, sin propiedades) ---
        private const int AntiDupWindowMs = 300; // tiempo máximo entre niveles para fusionar
        private const int AntiDupTicks    = 1;   // tolerancia de precio ±1 tick

        // --- Estructura del nivel dibujado ---
        private class MinTradeLevel
        {
            public string TagLineActive;    // Ray activo (extendiendo)
            public string TagLineFrozen;    // Línea fija al invalidar
            public string TagText;          // Texto "MinTrade (VOL)"
            public double Price;            // Nivel horizontal (precio ancla del clúster)
            public long Volume;             // Volumen total ACUMULADO del nivel (considerando fusiones)
            public MinTradeSide Side;       // Lado del clúster (para el color del texto)
            public DateTime TickTime;       // Tiempo de INICIO del nivel (del clúster original)
            public int EventMinuteIndex;    // Índice de la barra 1m que contiene TickTime
            public LevelState State;        // Neutral / Demand / Supply / Invalid
            public bool Classified;         // ¿Ya fue clasificado?
            public bool Invalidated;        // ¿Ya fue invalidado (cortado)?
        }

        // --- Series (salida pública) ---
        private Series<double> volumeSeries;
        private Series<double> priceSeries;

        // --- L1 para inferir lado por tick ---
        private double       bestBid       = double.NaN;
        private double       bestAsk       = double.NaN;
        private double       prevTickPrice = double.NaN;
        private MinTradeSide prevTickSide  = MinTradeSide.Unknown;
        private const double QuoteToleranceTicks = 0.5;

        // --- Niveles activos (dibujos) ---
        private readonly Dictionary<string, MinTradeLevel> levels = new Dictionary<string, MinTradeLevel>();

        // --- Estado del CLÚSTER en construcción ---
        private bool         clusterActive = false;
        private DateTime     clusterStartTime = DateTime.MinValue;
        private double       clusterAnchorPrice = double.NaN;
        private double       clusterSumIncluded = 0.0;      // suma que cuenta (tras MinPrint si ON)
        private double       clusterSumAskIncluded = 0.0;   // suma incluida por lado
        private double       clusterSumBidIncluded = 0.0;
        private bool         clusterHasAtLeast = false;     // hubo algún print >= AlMenosMinTrade
        private bool         clusterEmitted = false;        // ¿ya emitimos un nivel por este clúster?
        private double       driftPriceTol = 0.0;           // tolerancia de precio del clúster
        private int          clusterStartMinuteIndex = 0;   // barra 1m donde arrancó el clúster

        // ----------------- Propiedades expuestas -----------------
        [NinjaScriptProperty]
        [Display(Name = "MinTrade (ON/OFF)", Order = 0, GroupName = "Global")]
        public bool MinTradeModuleOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset session (ON/OFF)", Order = 1, GroupName = "Global")]
        public bool ResetSessionOnNewSession { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MinTrade", Order = 0, GroupName = "MinTrade")]
        public int MinTrade { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Tolerancia (ticks)", Order = 1, GroupName = "MinTrade")]
        public int ToleranciaTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Tiempo clúster (ms)", Order = 2, GroupName = "MinTrade")]
        public int ClusterWindowMs { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Drift (± ticks)", Order = 3, GroupName = "MinTrade")]
        public int DriftTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar 'al menos' (ON/OFF)", Order = 4, GroupName = "MinTrade")]
        public bool UseAlMenos { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Al menos (vol)", Order = 5, GroupName = "MinTrade")]
        public int AlMenosMinTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar MinPrint (ON/OFF)", Order = 6, GroupName = "MinTrade")]
        public bool UseMinPrint { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MinPrint (vol)", Order = 7, GroupName = "MinTrade")]
        public int MinPrintVol { get; set; }

        // Salidas públicas (último evento)
        [Browsable(false), XmlIgnore] public Series<double> LastMinTradeVolume => volumeSeries;
        [Browsable(false), XmlIgnore] public Series<double> LastMinTradePrice  => priceSeries;

        [Browsable(false)] public string       Version                    => InternalVersion;
        [Browsable(false)] public MinTradeSide CurrentMinTradeSide        { get; private set; } = MinTradeSide.Unknown;
        [Browsable(false)] public DateTime     CurrentMinTradeTime        { get; private set; } = DateTime.MinValue;
        [Browsable(false)] public double       CurrentMinTradeVolumeValue { get; private set; } = double.NaN;
        [Browsable(false)] public double       CurrentMinTradePriceValue  { get; private set; } = double.NaN;
        [Browsable(false)] public double       LastDetectedMinTradeVolume { get; private set; } = double.NaN;
        [Browsable(false)] public double       LastDetectedMinTradePrice  { get; private set; } = double.NaN;
        [Browsable(false)] public MinTradeSide LastDetectedMinTradeSide   { get; private set; } = MinTradeSide.Unknown;
        [Browsable(false)] public DateTime     LastDetectedMinTradeTime   { get; private set; } = DateTime.MinValue;

        private void ResetForNewSession()
        {
            if (levels.Count > 0)
            {
                foreach (var lv in levels.Values)
                {
                    RemoveDrawObjectSafe(lv.TagLineActive);
                    RemoveDrawObjectSafe(lv.TagLineFrozen);
                    RemoveDrawObjectSafe(lv.TagText);
                }
                levels.Clear();
            }

            clusterActive           = false;
            clusterStartTime        = DateTime.MinValue;
            clusterAnchorPrice      = double.NaN;
            clusterSumIncluded      = 0.0;
            clusterSumAskIncluded   = 0.0;
            clusterSumBidIncluded   = 0.0;
            clusterHasAtLeast       = false;
            clusterEmitted          = false;
            driftPriceTol           = 0.0;
            clusterStartMinuteIndex = 0;

            bestBid       = double.NaN;
            bestAsk       = double.NaN;
            prevTickPrice = double.NaN;
            prevTickSide  = MinTradeSide.Unknown;

            CurrentMinTradeSide        = MinTradeSide.Unknown;
            CurrentMinTradeTime        = DateTime.MinValue;
            CurrentMinTradeVolumeValue = double.NaN;
            CurrentMinTradePriceValue  = double.NaN;
            LastDetectedMinTradeVolume = double.NaN;
            LastDetectedMinTradePrice  = double.NaN;
            LastDetectedMinTradeSide   = MinTradeSide.Unknown;
            LastDetectedMinTradeTime   = DateTime.MinValue;

            if (volumeSeries != null)
                volumeSeries[0] = double.NaN;
            if (priceSeries != null)
                priceSeries[0]  = double.NaN;
        }

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = InternalName;
                Description              = "MinTrade por clúster (ventana ms + drift), línea neutral/confirmada e invalidación por cierre 1m con tolerancia. Anti-dup por defecto.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;

                MinTradeModuleOn       = true;
                ResetSessionOnNewSession = true;
                MinTrade               = 25;
                ToleranciaTicks        = 8;
                ClusterWindowMs        = 300;
                DriftTicks             = 2;
                UseAlMenos             = true;
                AlMenosMinTrade        = 10;
                UseMinPrint            = true;
                MinPrintVol            = 2;

                AddPlot(Brushes.Transparent, "LastMinTradeVolume");
                AddPlot(Brushes.Transparent, "LastMinTradePrice");
            }
            else if (State == State.Configure)
            {
                // Serie 1: 1 minuto (clasificación/invalidación por CIERRE 1m)
                AddDataSeries(BarsPeriodType.Minute, 1);
                // Serie 2: 1 tick (detección y clúster)
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                volumeSeries = Values[0];
                priceSeries  = Values[1];
            }
        }
        #endregion

        #region OnMarketData (tiempo real, inferir lado)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!MinTradeModuleOn)
                return;

            if (BarsInProgress != 0)
                return;

            if (e.MarketDataType == MarketDataType.Bid) bestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask) bestAsk = e.Price;
        }
        #endregion

        #region OnBarUpdate (BIP 0 primario / BIP 1 = 1m / BIP 2 = 1 tick)
        protected override void OnBarUpdate()
        {
            if (CurrentBars[BarsInProgress] < 0)
                return;

            // Serie primaria: solo reseteamos salidas numéricas
            if (BarsInProgress == 0)
            {
                if (ResetSessionOnNewSession && Bars.IsFirstBarOfSession && IsFirstTickOfBar)
                    ResetForNewSession();

                volumeSeries[0] = double.NaN;
                priceSeries[0]  = double.NaN;
                return;
            }

            // --- CIERRE 1 MINUTO: clasificar e invalidar ---
            if (BarsInProgress == 1)
            {
                if (!MinTradeModuleOn)
                    return;

                if (!IsFirstTickOfBar || CurrentBars[1] < 1)
                    return;

                int    closedIdx = CurrentBars[1] - 1; // índice de la barra 1m que acaba de cerrar
                double close1m   = Closes[1][1];

                if (levels.Count == 0)
                    return;

                var keys = new List<string>(levels.Keys);
                foreach (var key in keys)
                {
                    if (!levels.TryGetValue(key, out var lv)) continue;

                    // 1) CLASIFICACIÓN: primer cierre 1m posterior al evento
                    if (!lv.Classified && closedIdx >= lv.EventMinuteIndex)
                    {
                        if (close1m > lv.Price)
                        {
                            lv.State      = LevelState.Demand;
                            lv.Classified = true;
                            UpdateActiveRayColor(lv, Brushes.Green);
                        }
                        else if (close1m < lv.Price)
                        {
                            lv.State      = LevelState.Supply;
                            lv.Classified = true;
                            UpdateActiveRayColor(lv, Brushes.Red);
                        }
                        // Igual al nivel -> Neutral, evaluará siguientes cierres 1m
                    }

                    // 2) INVALIDACIÓN tras clasificar
                    if (lv.Classified && !lv.Invalidated)
                    {
                        double tol = Math.Max(0, ToleranciaTicks) * TickSize;

                        bool invalidate =
                            (lv.State == LevelState.Supply  && close1m >= lv.Price + tol) ||
                            (lv.State == LevelState.Demand  && close1m <= lv.Price - tol);

                        if (invalidate)
                        {
                            lv.Invalidated = true;
                            lv.State       = LevelState.Invalid;
                            FreezeLineAtCurrent(lv); // corta extensión
                        }
                    }
                }

                return;
            }

            // --- DETECCIÓN TICK A TICK (clúster) ---
            if (BarsInProgress == 2)
            {
                if (!MinTradeModuleOn)
                    return;

                if (CurrentBars[0] < 0)
                    return;

                double   price    = Close[0];
                double   volume   = Volume[0];
                DateTime tickTime = Times[2][0];

                MinTradeSide side = ResolveSide(price);

                // Preparar tolerancia de drift (precio)
                double ts = TickSize > 0 ? TickSize : (Instrument?.MasterInstrument?.TickSize ?? 0.0);
                if (ts <= 0) ts = Math.Max(Math.Abs(price) * 1e-6, 1e-4);
                double curDriftTol = Math.Max(0, DriftTicks) * ts;

                // Si no hay clúster activo, arrancar uno
                if (!clusterActive)
                {
                    StartCluster(tickTime, price, curDriftTol);
                }
                else
                {
                    // ¿Sigue dentro de ventana y drift?
                    double elapsedMs = (tickTime - clusterStartTime).TotalMilliseconds;
                    if (elapsedMs > ClusterWindowMs || Math.Abs(price - clusterAnchorPrice) > driftPriceTol)
                    {
                        // Nuevo clúster
                        StartCluster(tickTime, price, curDriftTol);
                    }
                }

                // Incluir este print en el clúster
                IncludeTickInCluster(side, volume);

                // ¿Alcanzó condiciones para emitir?
                TryEmitCluster();

                // Contexto de lado
                prevTickPrice = price;
                if (side != MinTradeSide.Unknown)
                    prevTickSide = side;

                return;
            }
        }
        #endregion

        #region Clúster helpers
        private void StartCluster(DateTime t, double price, double curDriftTol)
        {
            clusterActive            = true;
            clusterStartTime         = t;
            clusterAnchorPrice       = price;
            clusterSumIncluded       = 0.0;
            clusterSumAskIncluded    = 0.0;
            clusterSumBidIncluded    = 0.0;
            clusterHasAtLeast        = false;
            clusterEmitted           = false;
            driftPriceTol            = curDriftTol;
            clusterStartMinuteIndex  = Math.Max(0, BarsArray[1].GetBar(t));
        }

        private void IncludeTickInCluster(MinTradeSide side, double vol)
        {
            if (UseAlMenos && vol >= AlMenosMinTrade)
                clusterHasAtLeast = true;

            double add = vol;
            if (UseMinPrint && vol < MinPrintVol)
                add = 0.0;

            clusterSumIncluded += add;

            if (side == MinTradeSide.Ask) clusterSumAskIncluded += add;
            else if (side == MinTradeSide.Bid) clusterSumBidIncluded += add;
        }

        private void TryEmitCluster()
        {
            if (clusterEmitted)
                return;

            if (clusterSumIncluded < MinTrade)
                return;

            if (UseAlMenos && !clusterHasAtLeast)
                return;

            // Lado dominante por volumen incluido
            MinTradeSide domSide = MinTradeSide.Unknown;
            if (clusterSumAskIncluded > clusterSumBidIncluded) domSide = MinTradeSide.Ask;
            else if (clusterSumBidIncluded > clusterSumAskIncluded) domSide = MinTradeSide.Bid;
            else domSide = prevTickSide; // empate: último lado conocido

            long volTotal = (long)Math.Round(clusterSumIncluded, MidpointRounding.AwayFromZero);
            EmitLevel(clusterStartTime, clusterAnchorPrice, volTotal, domSide);

            clusterEmitted = true;
        }

        // --- ANTI‑DUP: fusión si hay un nivel existente cercano en tiempo y precio del mismo lado ---
        private bool TryAntiDupMerge(DateTime eventTime, double price, long volume, MinTradeSide side)
        {
            if (levels.Count == 0)
                return false;

            // Tolerancia de precio para anti-dup (±1 tick)
            double ts = TickSize > 0 ? TickSize : (Instrument?.MasterInstrument?.TickSize ?? 0.0);
            if (ts <= 0) ts = 1e-4;
            double priceTol = Math.Max(1, AntiDupTicks) * ts;

            MinTradeLevel target = null;
            double bestDt = double.MaxValue;

            foreach (var kvp in levels)
            {
                var lv = kvp.Value;
                if (lv.Invalidated)                 continue;                // no fusionar con niveles cortados
                if (lv.Side != side)                continue;                // mismo lado
                double dt = (eventTime - lv.TickTime).TotalMilliseconds;
                if (dt < 0 || dt > AntiDupWindowMs) continue;                // debe ser "posterior" y dentro de ventana
                if (Math.Abs(price - lv.Price) > priceTol) continue;         // ±1 tick

                if (dt < bestDt) { bestDt = dt; target = lv; }
            }

            if (target == null)
                return false;

            // Fusionar: sumar volumen y actualizar etiqueta; NO crear otra línea
            target.Volume += volume;

            // Actualizar la etiqueta con el nuevo total
            DrawEventText(target);

            // Refrescar salidas (opcional: reportamos el total acumulado del nivel)
            volumeSeries[0] = target.Volume;
            priceSeries[0]  = target.Price;

            CurrentMinTradeVolumeValue = target.Volume;
            CurrentMinTradePriceValue  = target.Price;
            CurrentMinTradeSide        = side;
            CurrentMinTradeTime        = eventTime;

            LastDetectedMinTradeVolume = target.Volume;
            LastDetectedMinTradePrice  = target.Price;
            LastDetectedMinTradeSide   = side;
            LastDetectedMinTradeTime   = eventTime;

            return true;
        }

        private void EmitLevel(DateTime eventTime, double price, long volume, MinTradeSide side)
        {
            // Anti‑dup: intentar fusionar con nivel existente (≤ 300 ms, ±1 tick, mismo lado)
            if (TryAntiDupMerge(eventTime, price, volume, side))
                return;

            string instrumentName = Instrument?.MasterInstrument?.Name ?? "Instrument";
            string tagBase = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}_{2}_{3}",
                InternalName,
                instrumentName,
                eventTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
                price.ToString("0.########", CultureInfo.InvariantCulture));

            var lv = new MinTradeLevel
            {
                TagLineActive    = tagBase + "_line",
                TagLineFrozen    = tagBase + "_frozen",
                TagText          = tagBase + "_text",
                Price            = price,
                Volume           = volume,
                Side             = side,
                TickTime         = eventTime,
                EventMinuteIndex = Math.Max(0, BarsArray[1].GetBar(eventTime)),
                State            = LevelState.Neutral,
                Classified       = false,
                Invalidated      = false
            };
            levels[tagBase] = lv;

            // Dibujo inicial: Ray NEUTRAL (gris)
            DrawActiveRay(lv, Brushes.DimGray);

            // Texto del evento (color por lado)
            DrawEventText(lv);

            // Salidas públicas (último evento)
            volumeSeries[0] = volume;
            priceSeries[0]  = price;

            CurrentMinTradeVolumeValue = volume;
            CurrentMinTradePriceValue  = price;
            CurrentMinTradeSide        = side;
            CurrentMinTradeTime        = eventTime;

            LastDetectedMinTradeVolume = volume;
            LastDetectedMinTradePrice  = price;
            LastDetectedMinTradeSide   = side;
            LastDetectedMinTradeTime   = eventTime;
        }
        #endregion

        #region Dibujo / Resoluciones
        private MinTradeSide ResolveSide(double price)
        {
            double tolerance = TickSize > 0 ? TickSize * QuoteToleranceTicks : 0.0;

            if (!double.IsNaN(bestAsk) && Math.Abs(price - bestAsk) <= tolerance) return MinTradeSide.Ask;
            if (!double.IsNaN(bestBid) && Math.Abs(price - bestBid) <= tolerance) return MinTradeSide.Bid;

            if (!double.IsNaN(prevTickPrice))
            {
                if (price > prevTickPrice) return MinTradeSide.Ask;
                if (price < prevTickPrice) return MinTradeSide.Bid;
                if (prevTickSide != MinTradeSide.Unknown) return prevTickSide;
            }
            return MinTradeSide.Unknown;
        }

        private int GetPrimaryBarsAgo(DateTime anchorTime)
        {
            if (BarsArray == null || BarsArray.Length == 0 || BarsArray[0] == null)
                return int.MinValue;

            int currentPrimary = CurrentBars[0];
            if (currentPrimary < 0)
                return int.MinValue;

            int idxOnPrimary = BarsArray[0].GetBar(anchorTime);
            if (idxOnPrimary < 0)
                return currentPrimary; // fallback: extremo izquierdo cargado

            int barsAgo = currentPrimary - idxOnPrimary;
            if (barsAgo < 0) barsAgo = 0;
            return barsAgo;
        }

        private void DrawActiveRay(MinTradeLevel lv, Brush brush)
        {
            int startBarsAgo = GetPrimaryBarsAgo(lv.TickTime);
            if (startBarsAgo == int.MinValue) return;

            int endBarsAgo = 0;
            if (startBarsAgo <= endBarsAgo)
            {
                if (CurrentBars[0] >= 1) startBarsAgo = 1;
                else return;
            }

            var ray = Draw.Ray(this, lv.TagLineActive, startBarsAgo, lv.Price, endBarsAgo, lv.Price, brush);
            if (ray != null && ray.Stroke != null)
                ray.Stroke.Width = 2;
        }

        private void UpdateActiveRayColor(MinTradeLevel lv, Brush brush)
        {
            int startBarsAgo = GetPrimaryBarsAgo(lv.TickTime);
            if (startBarsAgo == int.MinValue) return;

            int endBarsAgo = 0;
            if (startBarsAgo <= endBarsAgo)
            {
                if (CurrentBars[0] >= 1) startBarsAgo = 1;
                else return;
            }

            var ray = Draw.Ray(this, lv.TagLineActive, startBarsAgo, lv.Price, endBarsAgo, lv.Price, brush);
            if (ray != null && ray.Stroke != null)
                ray.Stroke.Width = 2;
        }

        private void DrawEventText(MinTradeLevel lv)
        {
            Brush textBrush = Brushes.Gray;
            if (lv.Side == MinTradeSide.Bid) textBrush = Brushes.Red;
            else if (lv.Side == MinTradeSide.Ask) textBrush = Brushes.Green;

            double ts = TickSize > 0 ? TickSize : (Instrument?.MasterInstrument?.TickSize ?? 0.0);
            if (ts <= 0) ts = Math.Max(Math.Abs(lv.Price) * 1e-6, 1e-4);

            double yText = lv.Price + (2 * ts);

            Draw.Text(this,
                      lv.TagText,
                      $"MinTrade ({lv.Volume})",
                      GetPrimaryBarsAgo(lv.TickTime),
                      yText,
                      textBrush);
        }

        private void FreezeLineAtCurrent(MinTradeLevel lv)
        {
            RemoveDrawObjectSafe(lv.TagLineActive);

            Brush segBrush = Brushes.DimGray;
            if (lv.State == LevelState.Supply) segBrush = Brushes.Red;
            else if (lv.State == LevelState.Demand) segBrush = Brushes.Green;

            int startBarsAgo = GetPrimaryBarsAgo(lv.TickTime);
            if (startBarsAgo == int.MinValue) return;

            int endBarsAgo = 0;
            if (startBarsAgo <= endBarsAgo)
            {
                if (CurrentBars[0] >= 1) startBarsAgo = 1;
                else return;
            }

            // Firma con isAutoScale explícito para NT8
            var line = Draw.Line(this, lv.TagLineFrozen, false, startBarsAgo, lv.Price, endBarsAgo, lv.Price, segBrush, DashStyleHelper.Solid, 2);
            if (line != null && line.Stroke != null)
                line.Stroke.Width = 2;
        }

        private void RemoveDrawObjectSafe(string tag)
        {
            try { RemoveDrawObject(tag); } catch { /* ignore */ }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
{
private a2hub[] cachea2hub;
public a2hub a2hub(int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
return a2hub(Input, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol);
}

public a2hub a2hub(ISeries<double> input, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
if (cachea2hub != null)
for (int idx = 0; idx < cachea2hub.Length; idx++)
if (cachea2hub[idx] != null && cachea2hub[idx].MinTrade == minTrade && cachea2hub[idx].ToleranciaTicks == toleranciaTicks && cachea2hub[idx].ClusterWindowMs == clusterWindowMs && cachea2hub[idx].DriftTicks == driftTicks && cachea2hub[idx].UseAlMenos == useAlMenos && cachea2hub[idx].AlMenosMinTrade == alMenosMinTrade && cachea2hub[idx].UseMinPrint == useMinPrint && cachea2hub[idx].MinPrintVol == minPrintVol && cachea2hub[idx].EqualsInput(input))
return cachea2hub[idx];
return CacheIndicator<a2hub>(new a2hub(){ MinTrade = minTrade, ToleranciaTicks = toleranciaTicks, ClusterWindowMs = clusterWindowMs, DriftTicks = driftTicks, UseAlMenos = useAlMenos, AlMenosMinTrade = alMenosMinTrade, UseMinPrint = useMinPrint, MinPrintVol = minPrintVol }, input, ref cachea2hub);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.a2hub a2hub(int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
return indicator.a2hub(Input, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol);
}

public Indicators.a2hub a2hub(ISeries<double> input , int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
return indicator.a2hub(input, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.a2hub a2hub(int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
return indicator.a2hub(Input, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol);
}

public Indicators.a2hub a2hub(ISeries<double> input , int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol)
{
return indicator.a2hub(input, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol);
}
}
}

#endregion
