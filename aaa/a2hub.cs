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
//   - Módulo Imbalance (IM):
//       * Detecta y dibuja stacks diagonales de imbalances en Volumetric (Order Flow+),
//         reutilizando la lógica del indicador a2imbalance.
//       * Es independiente del módulo MinTrade y usa el time frame global (en minutos).
//       * Se puede activar/desactivar con la propiedad Global Imbalance (IM) (ON/OFF).
//   - Módulo VolumeCluster (VN):
//       * Detecta clusters de volumen en Volumetric (Order Flow+) dentro de una ventana de ticks
//         y exige un porcentaje concentrado; toma como nivel el precio con mayor volumen (POC local).
//       * Activación inmediata o tras X velas de 1m; invalida por cruce con margen de ticks.
//       * Dibuja rayos horizontales "VolCluster" y expone NivelExpuesto / UltimoNivelActual.
//       * Es independiente de MinTrade e Imbalance y usa el time frame global.
//   - Módulo MultipleNode (X):
//       * Detecta "multiple nodes" de POC en barras Volumétricas (Order Flow+) de GlobalTimeFrameMinutes
//         dentro de una ventana temporal de VentanaMin minutos agrupando POCs a ±TicksAlrededor.
//       * Si el cluster tiene tamaño ≥ 2, dibuja un rayo horizontal etiquetado "multiple node".
//       * Clasifica con el primer cierre de 1m posterior: cierre > nivel → Demand (verde); cierre < nivel → Supply (rojo).
//       * Borra por cierre de 1m con tolerancia direccional (ToleranciaTicks) manteniendo el histórico del nivel.
//   - Módulo UB (Unfinished Business):
//       * Detecta UB en barras Volumétricas (Order Flow+, Delta BidAsk) con el time frame GlobalTimeFrameMinutes compartido.
//       * UB High: si en el precio del High hay volumen en el Bid (Bid@High >= MinOppositeVolume).
//       * UB Low : si en el precio del Low  hay volumen en el Ask (Ask@Low  >= MinOppositeVolume).
//       * Dibuja línea horizontal punteada desde la barra de detección hacia la derecha y texto "unfibusi" encima.
//       * Se borra al producirse trade-through de 1 tick: High/Last > nivel + TickSize (High) o Low/Last < nivel - TickSize (Low).
//   - Módulo Global:
//       * MinTrade (ON/OFF): activa/desactiva completamente el módulo MinTrade (detección y dibujo).
//       * Imbalance (IM) (ON/OFF): interruptor maestro del módulo Imbalance.
//       * VolumeCluster (VN) (ON/OFF): interruptor maestro del módulo VolumeCluster.
//       * MultipleNode (X) (ON/OFF): interruptor maestro del módulo MultipleNode.
//       * UB (ON/OFF): interruptor maestro del módulo Unfinished Business.
//       * Reset session (ON/OFF): controla si se limpian niveles y estado al inicio de cada nueva sesión
//         (ON = igual que ahora, OFF = conserva niveles históricos en el gráfico).
//       * Time frame (min): marco temporal global que usan los módulos basados en velas/Volumetric (IM, VN, X y UB).
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
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using System.Windows.Media;
using System.Windows;

using NinjaTrader.Cbi;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui;              // DashStyleHelper
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;        // SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
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

        // --- Volumetric shared + Imbalance (IM) ---
        private int                   volBip            = -1; // serie Volumetric compartida (IM/VN)
        private VolumetricBarsType    volBarsType       = null;
        private double                volTickSize       = 0;
        private readonly Dictionary<string, StackLine> activeLines = new Dictionary<string, StackLine>();

        // v6: radio fijo de proximidad para dedupe por lado (en ticks)
        private const int ProximityTicksFixed = 10;

        // --- Unfinished Business (UB) ---
        #region Unfinished Business (UB)
        private class UBLevel
        {
            public double   Price;
            public bool     IsHigh;
            public DateTime DetectedTime;
            public string   TagLine;
            public string   TagText;
        }

        private readonly List<UBLevel>               ubLevels     = new List<UBLevel>();
        private readonly Dictionary<string, UBLevel> ubLevelsByKey = new Dictionary<string, UBLevel>();
        private SimpleFont                           ubTextFont;
        private readonly Brush                       ubBrushHigh   = Brushes.Red;
        private readonly Brush                       ubBrushLow    = Brushes.Green;
        private const int                            ubLineWidth   = 2;
        private const int                            ubTextOffsetTicks = 1;

        private static readonly MethodInfo ubDrawLineTimeWithStyle;
        private static readonly MethodInfo ubDrawLineTimeWidthOnly;
        private static readonly MethodInfo ubDrawLineTimeBasic;
        private static readonly object     ubDashStyleDashValue;
        #endregion

        static a2hub()
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
                        ubDrawLineTimeWithStyle = method;
                        try
                        {
                            ubDashStyleDashValue = Enum.Parse(parameters[8].ParameterType, "Dash");
                        }
                        catch
                        {
                            ubDashStyleDashValue = null;
                        }
                    }
                }
                else if (parameters.Length == 9 && parameters[3].ParameterType == typeof(DateTime) && parameters[5].ParameterType == typeof(DateTime) && parameters[8].ParameterType == typeof(int))
                {
                    ubDrawLineTimeWidthOnly = method;
                }
                else if (parameters.Length == 8 && parameters[3].ParameterType == typeof(DateTime) && parameters[5].ParameterType == typeof(DateTime))
                {
                    ubDrawLineTimeBasic = method;
                }
            }
        }

        // --- VolumeCluster (VN) ---
        private class ClusterZone
        {
            public DateTime FormationTime;  // cierre de la barra volumétrica con cluster
            public double   Level;          // precio con mayor volumen dentro del rango
            public double   RangeHigh;      // límite superior del rango
            public double   RangeLow;       // límite inferior del rango
            public long     ClusterVolume;  // suma de volumen del rango
            public long     BarTotalVolume; // volumen total de la barra
            public bool     Active;         // ya activada
            public bool     IsSupport;      // true soporte, false resistencia
            public string   Tag;            // tag de dibujo
            public int      M1ClosesSeen;   // cierres 1m contados desde formación
            public DateTime ActivationTime; // tiempo de activación
            public DateTime InvalidationTime; // tiempo de invalidación por precio
        }

        private readonly List<ClusterZone> vnPending    = new List<ClusterZone>();
        private readonly List<ClusterZone> vnActive     = new List<ClusterZone>();
        private readonly List<ClusterZone> vnDrawQueue  = new List<ClusterZone>();
        private readonly List<ClusterZone> vnHistorical = new List<ClusterZone>();

        private readonly Brush vnSoporteBrush     = Brushes.LimeGreen;
        private readonly Brush vnResistenciaBrush = Brushes.Red;
        private const int      vnGrosorLineaFijo  = 2;

        private Series<double> vnNivelExpuesto;
        private double         vnUltimoNivelActual = double.NaN;

        // --- MultipleNode (X) ---
        #region MultipleNode (X) – campos y estructuras
        private enum NodeState { Pending = 0, Demand = 1, Supply = 2 }

        private class Level
        {
            public string   Tag;
            public double   Price;
            public DateTime StartTime;
            public DateTime NextMinuteCloseTime;
            public NodeState State;
            public bool     Active;
            public DateTime? InvalidationTime;
        }

        private readonly List<Level> multipleNodeActiveLevels = new List<Level>();
        private int                   multipleNodeUniqueId    = 0;
        private string                multipleNodeTagPrefix;
        private SimpleFont            multipleNodeLabelFont;
        private double                multipleNodeTickSize;

        private NinjaTrader.NinjaScript.LinePriceMode multipleNodeModoPrecio = NinjaTrader.NinjaScript.LinePriceMode.Average;
        private bool                                              multipleNodeBorrarSoloSiCierre = true;
        private NinjaTrader.NinjaScript.CloseSource               multipleNodeCierreParaBorrar   = NinjaTrader.NinjaScript.CloseSource.OneMinute;

        private double MultipleNodeDedupTolerance => Math.Max(multipleNodeTickSize, (MultipleNodeTicksAlrededor * multipleNodeTickSize) / 2.0);
        private double MultipleNodeCloseTouchEps   => multipleNodeTickSize * 0.1;
        #endregion

        private class StackLine
        {
            public string TagRay { get; set; }
            public string TagText { get; set; }
            public double Price { get; set; } // precio medio actual del stack (para invalidación)
            public bool IsAskStack { get; set; } // true = ASK (soporte, verde); false = BID (resistencia, roja)
            public DateTime BarTime { get; set; } // tiempo de la barra volumétrica donde se originó
            public double RunStartPrice { get; set; } // precio de inicio del stack (para reusar tag intrabar)
            public int OriginPrimaryIndex { get; set; } // índice de la barra primaria donde se creó
        }

        // ----------------- Propiedades expuestas -----------------
        [NinjaScriptProperty]
        [Display(Name = "MinTrade (ON/OFF)", Order = 0, GroupName = "Global")]
        public bool MinTradeModuleOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset session (ON/OFF)", Order = 1, GroupName = "Global")]
        public bool ResetSessionOnNewSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance (IM) (ON/OFF)", Order = 2, GroupName = "Global")]
        public bool ImbalanceModuleOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VolumeCluster (VN) (ON/OFF)", Order = 3, GroupName = "Global")]
        public bool VolumeClusterModuleOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MultipleNode (X) (ON/OFF)", Order = 4, GroupName = "Global")]
        public bool MultipleNodeModuleOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UB (ON/OFF)", Order = 5, GroupName = "Global")]
        public bool UBModuleOn { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Time frame (min)", Order = 6, GroupName = "Global")]
        public int GlobalTimeFrameMinutes { get; set; }

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

        [NinjaScriptProperty]
        [Display(Name = "Stack imbalance", GroupName = "Imbalance (IM)", Order = 2)]
        [Range(1, 50)]
        public int StackImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance ratio (e.g. 3.0 = 300%)", GroupName = "Imbalance (IM)", Order = 3)]
        [Range(1.0, double.MaxValue)]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min delta para imbalance", GroupName = "Imbalance (IM)", Order = 4)]
        [Range(0, int.MaxValue)]
        public int MinDeltaImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Tolerancia borrar (ticks)", GroupName = "Imbalance (IM)", Order = 5)]
        [Range(0, 1000)]
        public int ToleranciaBorrarTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro de supervivencia", GroupName = "Imbalance (IM)", Order = 6)]
        public bool FiltroSupervivencia { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Porcentaje concentrado (%)", GroupName = "VolumeCluster (VN)", Order = 0)]
        public int PorcentajeConcentrado { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ticks alrededor (ancho del rango)", GroupName = "VolumeCluster (VN)", Order = 1)]
        public int TicksAlrededor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Min de velas para confirmación (1m)", GroupName = "VolumeCluster (VN)", Order = 2)]
        public int MinVelasConfirmacion { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Margen de ticks: borrar", GroupName = "VolumeCluster (VN)", Order = 3)]
        public int MargenTicksBorre { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Dentro de cuántos minutos", GroupName = "MultipleNode (X)", Order = 1)]
        public int MultipleNodeVentanaMin { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Ticks alrededor", GroupName = "MultipleNode (X)", Order = 2)]
        public int MultipleNodeTicksAlrededor { get; set; }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Tolerancia ticks (cierre 1m)", GroupName = "MultipleNode (X)", Order = 3)]
        public int MultipleNodeToleranciaTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "MinVolume (opuesto)", GroupName = "UB", Order = 1)]
        public int MinOppositeVolume { get; set; }

        // Salidas públicas (último evento)
        [Browsable(false), XmlIgnore] public Series<double> LastMinTradeVolume => volumeSeries;
        [Browsable(false), XmlIgnore] public Series<double> LastMinTradePrice  => priceSeries;

        [Browsable(false), XmlIgnore] public Series<double> NivelExpuesto => vnNivelExpuesto;
        [Browsable(false)] public double UltimoNivelActual => vnUltimoNivelActual;

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

            ClearAllStackLines();

            UbClearAllLevels();

            ResetVolumeClusterModule();

            if (volBip >= 0 && volBip < BarsArray.Length)
            {
                volBarsType = BarsArray[volBip].BarsType as VolumetricBarsType;
                if (volBarsType != null)
                    volTickSize = BarsArray[volBip].Instrument.MasterInstrument.TickSize;
            }
            else
            {
                volBarsType = null;
                volTickSize = 0;
            }

            if (volumeSeries != null)
                volumeSeries[0] = double.NaN;
            if (priceSeries != null)
                priceSeries[0]  = double.NaN;

            if (vnNivelExpuesto != null)
                vnNivelExpuesto[0] = double.NaN;

            if (multipleNodeActiveLevels.Count > 0)
            {
                foreach (var lvl in multipleNodeActiveLevels)
                    DeleteMultipleNodeLevel(lvl);
                multipleNodeActiveLevels.Clear();
            }

            multipleNodeUniqueId = 0;
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

                MinTradeModuleOn         = true;
                ResetSessionOnNewSession = true;
                ImbalanceModuleOn        = true;
                VolumeClusterModuleOn    = true;
                MultipleNodeModuleOn     = true;
                UBModuleOn               = true;
                GlobalTimeFrameMinutes   = 5;
                MinTrade                = 25;
                ToleranciaTicks         = 8;
                ClusterWindowMs         = 300;
                DriftTicks              = 2;
                UseAlMenos              = true;
                AlMenosMinTrade         = 10;
                UseMinPrint             = true;
                MinPrintVol             = 2;
                StackImbalance          = 3;
                ImbalanceRatio          = 3.0;
                MinDeltaImbalance       = 0;
                ToleranciaBorrarTicks   = 6;
                FiltroSupervivencia     = false;
                PorcentajeConcentrado   = 25;
                TicksAlrededor          = 6;
                MinVelasConfirmacion    = 0;
                MargenTicksBorre        = 6;
                MultipleNodeVentanaMin       = 25;
                MultipleNodeTicksAlrededor   = 6;
                MultipleNodeToleranciaTicks  = 6;
                MinOppositeVolume            = 1;

                ubTextFont = new SimpleFont("Arial", 12) { Bold = true };

                AddPlot(Brushes.Transparent, "LastMinTradeVolume");
                AddPlot(Brushes.Transparent, "LastMinTradePrice");
            }
            else if (State == State.Configure)
            {
                // Serie 1: 1 minuto (clasificación/invalidación por CIERRE 1m)
                AddDataSeries(BarsPeriodType.Minute, 1);
                // Serie 2: 1 tick (detección y clúster)
                AddDataSeries(BarsPeriodType.Tick, 1);
                // Serie Volumetric compartida para Imbalance (IM) y VolumeCluster (VN)
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, GlobalTimeFrameMinutes, VolumetricDeltaType.BidAsk, 1);
                volBip = BarsArray.Length - 1;
            }
            else if (State == State.DataLoaded)
            {
                volumeSeries = Values[0];
                priceSeries  = Values[1];

                if (volBip >= 0 && volBip < BarsArray.Length)
                {
                    volBarsType = BarsArray[volBip].BarsType as VolumetricBarsType;
                    if (volBarsType != null)
                        volTickSize = BarsArray[volBip].Instrument.MasterInstrument.TickSize;
                    else
                        volTickSize = 0;
                }

                vnNivelExpuesto = new Series<double>(this);

                multipleNodeTickSize  = Instrument.MasterInstrument.TickSize;
                multipleNodeTagPrefix = $"a2MN_{Instrument.MasterInstrument.Name}";
                multipleNodeLabelFont = new SimpleFont("Arial", 10);

                if (ubTextFont == null)
                    ubTextFont = new SimpleFont("Arial", 12) { Bold = true };
            }
        }
        #endregion

        #region OnMarketData (tiempo real, inferir lado)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0)
                return;

            if (UBModuleOn && ubLevels.Count > 0 && e.MarketDataType == MarketDataType.Last)
                UbHandleMarketDataLast(e.Price);

            if (!MinTradeModuleOn)
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

            // Serie primaria: solo reseteamos salidas numéricas y procesamos módulos sobre BIP 0
            if (BarsInProgress == 0)
            {
                if (ResetSessionOnNewSession && Bars.IsFirstBarOfSession && IsFirstTickOfBar)
                    ResetForNewSession();

                if (ImbalanceModuleOn)
                {
                    EnsureSurvivalCutoffTime();

                    if (activeLines.Count > 0)
                        CheckInvalidations();
                }

                if (vnNivelExpuesto != null)
                    vnNivelExpuesto[0] = VolumeClusterModuleOn ? vnUltimoNivelActual : double.NaN;

                if (VolumeClusterModuleOn && vnDrawQueue.Count > 0)
                    VnProcessDrawQueue();

                if (UBModuleOn && ubLevels.Count > 0)
                {
                    ExtendUbLinesToRightEdge(Time[0]);
                    UbTestAndRemoveCompletedLevels(High[0], Low[0]);
                }

                volumeSeries[0] = double.NaN;
                priceSeries[0]  = double.NaN;
                return;
            }

            // --- CIERRE 1 MINUTO: clasificar e invalidar ---
            if (BarsInProgress == 1)
            {
                if (IsFirstTickOfBar && CurrentBars[1] >= 1)
                {
                    int    closedIdx = CurrentBars[1] - 1; // índice de la barra 1m que acaba de cerrar
                    double close1m   = Closes[1][1];

                    if (MinTradeModuleOn)
                    {
                        if (levels.Count > 0)
                        {
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
                        }
                    }

                    if (MultipleNodeModuleOn && multipleNodeTickSize > 0)
                    {
                        DateTime lastCloseTime  = Times[1][1];
                        double   lastClosePrice = Closes[1][1];
                        double   prevClose      = (CurrentBars[1] > 1) ? Closes[1][2] : lastClosePrice;

                        foreach (var lvl in multipleNodeActiveLevels.Where(x => x.Active && x.State == NodeState.Pending && lastCloseTime >= x.NextMinuteCloseTime).ToList())
                        {
                            if (lastClosePrice > lvl.Price) { lvl.State = NodeState.Demand; RecolorMultipleNodeLevel(lvl, Brushes.LimeGreen); }
                            else if (lastClosePrice < lvl.Price) { lvl.State = NodeState.Supply; RecolorMultipleNodeLevel(lvl, Brushes.Red); }
                        }

                        if (multipleNodeBorrarSoloSiCierre && multipleNodeCierreParaBorrar == NinjaTrader.NinjaScript.CloseSource.OneMinute)
                        {
                            double tol = Math.Max(0, MultipleNodeToleranciaTicks) * multipleNodeTickSize;

                            foreach (var lvl in multipleNodeActiveLevels.Where(x => x.Active).ToList())
                            {
                                if (lvl.State == NodeState.Pending)
                                {
                                    if (CloseTouchesOrCrossesMultipleNode(lastClosePrice, prevClose, lvl.Price))
                                        InvalidateMultipleNodeLevel(lvl, Times[1][1]);
                                    continue;
                                }

                                bool broken = false;
                                if (lvl.State == NodeState.Demand)
                                    broken = (lastClosePrice < lvl.Price - tol);
                                else if (lvl.State == NodeState.Supply)
                                    broken = (lastClosePrice > lvl.Price + tol);

                                if (broken)
                                    InvalidateMultipleNodeLevel(lvl, Times[1][1]);
                            }
                        }
                    }
                }

                if (VolumeClusterModuleOn && CurrentBars[1] >= 0)
                    VnProcessConfirmationsYVigencias();

                return;
            }

            if (volBip >= 0 && BarsInProgress == volBip)
            {
                if (!ImbalanceModuleOn && !VolumeClusterModuleOn && !MultipleNodeModuleOn && !UBModuleOn)
                    return;

                if (volBarsType == null || CurrentBars[volBip] < 0 || volTickSize <= 0)
                    return;

                bool isHistorical   = State == State.Historical;
                bool isRealtimeLike = State == State.Realtime || State == State.Transition;

                int      volBarIndex;
                DateTime volBarTime;
                double   low, high;

                if (isHistorical)
                {
                    if (!IsFirstTickOfBar || CurrentBars[volBip] < 1)
                        return;

                    volBarIndex = CurrentBars[volBip] - 1;
                    volBarTime  = Times[volBip][1];
                    low         = Lows[volBip][1];
                    high        = Highs[volBip][1];
                }
                else if (isRealtimeLike)
                {
                    volBarIndex = CurrentBars[volBip];
                    volBarTime  = Times[volBip][0];
                    low         = Lows[volBip][0];
                    high        = Highs[volBip][0];
                }
                else
                {
                    return;
                }

                if (high < low)
                {
                    double t = high;
                    high = low;
                    low  = t;
                }

                int volumesCount = volBarsType.Volumes?.Count ?? 0;
                if (volBarIndex < 0 || volBarIndex >= volumesCount)
                    return;

                if (ImbalanceModuleOn)
                {
                    DetectAndDrawStacks(volBarIndex, volBarTime, low, high, true);
                    DetectAndDrawStacks(volBarIndex, volBarTime, low, high, false);
                }

                if (VolumeClusterModuleOn)
                    VnDetectCluster(volBarIndex, volBarTime, low, high);

                if (MultipleNodeModuleOn)
                    MultipleNodeDetect(volBarIndex, volBarTime);

                if (UBModuleOn)
                {
                    var volumes = volBarsType.Volumes[volBarIndex];
                    long bidAtHigh = volumes.GetBidVolumeForPrice(high);
                    long askAtLow  = volumes.GetAskVolumeForPrice(low);

                    bool ubHigh = bidAtHigh >= MinOppositeVolume;
                    bool ubLow  = askAtLow  >= MinOppositeVolume;

                    if (ubHigh)
                        AddUbLevel(Instrument.MasterInstrument.RoundToTickSize(high), true, volBarTime);

                    if (ubLow)
                        AddUbLevel(Instrument.MasterInstrument.RoundToTickSize(low), false, volBarTime);

                    if (ubLevels.Count > 0)
                        UbTestAndRemoveCompletedLevels(high, low);
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

        #region Unfinished Business (UB) helpers
        private void AddUbLevel(double price, bool isHigh, DateTime detectedTime)
        {
            string key = BuildUbKey(price, isHigh);
            if (ubLevelsByKey.ContainsKey(key))
                return;

            var lvl = new UBLevel
            {
                Price        = price,
                IsHigh       = isHigh,
                DetectedTime = detectedTime,
                TagLine      = $"a2unfibusi_line_{(isHigh ? "H" : "L")}_{price.ToString("0.#####", CultureInfo.InvariantCulture)}_{detectedTime:yyyyMMddHHmmss}",
                TagText      = $"a2unfibusi_txt_{(isHigh ? "H" : "L")}_{price.ToString("0.#####", CultureInfo.InvariantCulture)}_{detectedTime:yyyyMMddHHmmss}"
            };

            ubLevels.Add(lvl);
            ubLevelsByKey[key] = lvl;

            DateTime endTime = (CurrentBars[0] >= 0) ? Times[0][0] : detectedTime;
            DrawUbLine(lvl, detectedTime, endTime);
            DrawUbText(lvl);
        }

        private void DrawUbLine(UBLevel lvl, DateTime startTime, DateTime endTime)
        {
            Brush b = lvl.IsHigh ? ubBrushHigh : ubBrushLow;
            if (ubDrawLineTimeWithStyle != null && ubDashStyleDashValue != null)
            {
                ubDrawLineTimeWithStyle.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b, ubDashStyleDashValue, ubLineWidth });
                return;
            }

            if (ubDrawLineTimeWidthOnly != null)
            {
                ubDrawLineTimeWidthOnly.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b, ubLineWidth });
                return;
            }

            if (ubDrawLineTimeBasic != null)
                ubDrawLineTimeBasic.Invoke(null, new object[] { this, lvl.TagLine, false, startTime, lvl.Price, endTime, lvl.Price, b });
        }

        private void DrawUbText(UBLevel lvl)
        {
            double y = lvl.Price + ubTextOffsetTicks * TickSize;
            Brush  b = lvl.IsHigh ? ubBrushHigh : ubBrushLow;

            Draw.Text(this, lvl.TagText, false, "unfibusi", lvl.DetectedTime, y, 0, b, ubTextFont, System.Windows.TextAlignment.Left, null, null, 0);
        }

        private void ExtendUbLinesToRightEdge(DateTime endTime)
        {
            foreach (var lvl in ubLevels)
                DrawUbLine(lvl, lvl.DetectedTime, endTime);
        }

        private void UbTestAndRemoveCompletedLevels(double upThreshold, double downThreshold)
        {
            if (!UBModuleOn || ubLevels.Count == 0)
                return;

            double upThr   = Instrument.MasterInstrument.RoundToTickSize(upThreshold);
            double downThr = Instrument.MasterInstrument.RoundToTickSize(downThreshold);

            var toRemove = new List<UBLevel>();
            foreach (var lvl in ubLevels)
            {
                if (IsUbLevelCompleted(lvl, upThr, downThr))
                    toRemove.Add(lvl);
            }

            foreach (var lvl in toRemove)
                UbRemoveLevel(lvl);
        }

        private void UbHandleMarketDataLast(double price)
        {
            if (!UBModuleOn || ubLevels.Count == 0)
                return;

            double last = Instrument.MasterInstrument.RoundToTickSize(price);
            UbTestAndRemoveCompletedLevels(last, last);
        }

        private void UbRemoveLevel(UBLevel lvl)
        {
            RemoveDrawObjectSafe(lvl.TagLine);
            RemoveDrawObjectSafe(lvl.TagText);

            ubLevels.Remove(lvl);
            string key = BuildUbKey(lvl.Price, lvl.IsHigh);
            if (ubLevelsByKey.ContainsKey(key))
                ubLevelsByKey.Remove(key);
        }

        private void UbClearAllLevels()
        {
            foreach (var lvl in ubLevels.ToList())
                UbRemoveLevel(lvl);
        }

        private bool IsUbLevelCompleted(UBLevel lvl, double upThr, double downThr)
            => lvl.IsHigh ? upThr >= lvl.Price + TickSize : downThr <= lvl.Price - TickSize;

        private static string BuildUbKey(double price, bool isHigh)
            => $"{(isHigh ? "H" : "L")}@{price.ToString("0.#####", CultureInfo.InvariantCulture)}";

        #endregion

        #region MultipleNode (X) helpers
        private void MultipleNodeDetect(int volBarIndex, DateTime volBarTime)
        {
            if (volBarsType == null || volBarIndex < 0 || multipleNodeTickSize <= 0)
                return;

            int barrasVentana = Math.Max(1, MultipleNodeVentanaMin / Math.Max(1, GlobalTimeFrameMinutes));
            int disponibles   = Math.Min(barrasVentana, volBarIndex + 1);

            var pocs = new List<double>(disponibles);
            for (int i = 0; i < disponibles; i++)
            {
                int absIndex = volBarIndex - i;
                if (absIndex < 0) break;

                double pocPrice;
                volBarsType.Volumes[absIndex].GetMaximumVolume(null, out pocPrice);
                if (!double.IsNaN(pocPrice) && pocPrice > 0)
                    pocs.Add(Instrument.MasterInstrument.RoundToTickSize(pocPrice));
            }

            var clusters = GetMultipleNodeClusterLevels(pocs);
            foreach (double levelPrice in clusters)
            {
                if (!HasActiveMultipleNodeLevelNear(levelPrice))
                    CreateMultipleNodeLevel(levelPrice, volBarTime);
            }
        }

        private List<double> GetMultipleNodeClusterLevels(List<double> pocs)
        {
            var result = new List<double>();
            if (pocs == null || pocs.Count < 2)
                return result;

            pocs.Sort();
            double tol = MultipleNodeTicksAlrededor * multipleNodeTickSize;

            List<double> cluster = new List<double> { pocs[0] };
            double        clusterStart = pocs[0];

            for (int i = 1; i < pocs.Count; i++)
            {
                double p = pocs[i];
                if (p - clusterStart <= tol)
                    cluster.Add(p);
                else
                {
                    if (cluster.Count >= 2)
                        result.Add(ClusterMultipleNodePrice(cluster));
                    cluster.Clear();
                    cluster.Add(p);
                    clusterStart = p;
                }
            }

            if (cluster.Count >= 2)
                result.Add(ClusterMultipleNodePrice(cluster));

            return result;
        }

        private double ClusterMultipleNodePrice(List<double> cluster)
        {
            if (cluster == null || cluster.Count == 0)
                return 0;

            return multipleNodeModoPrecio == NinjaTrader.NinjaScript.LinePriceMode.Average
                ? Instrument.MasterInstrument.RoundToTickSize(cluster.Average())
                : Instrument.MasterInstrument.RoundToTickSize(0.5 * (cluster.First() + cluster.Last()));
        }

        private bool HasActiveMultipleNodeLevelNear(double price)
        {
            foreach (var lvl in multipleNodeActiveLevels)
                if (lvl.Active && Math.Abs(lvl.Price - price) <= MultipleNodeDedupTolerance)
                    return true;
            return false;
        }

        private void CreateMultipleNodeLevel(double price, DateTime startTime)
        {
            var lvl = new Level
            {
                Price               = Instrument.MasterInstrument.RoundToTickSize(price),
                StartTime           = startTime,
                NextMinuteCloseTime = startTime.AddMinutes(1),
                State               = NodeState.Pending,
                Active              = true,
                Tag                 = $"{multipleNodeTagPrefix}_{++multipleNodeUniqueId}_{(long)Math.Round(price / multipleNodeTickSize)}"
            };

            multipleNodeActiveLevels.Add(lvl);
            DrawMultipleNodeLevel(lvl, Brushes.Gold);
        }

        private void DrawMultipleNodeLevel(Level lvl, Brush brush)
        {
            Draw.Ray(this, lvl.Tag, lvl.StartTime, lvl.Price, lvl.StartTime.AddMinutes(1), lvl.Price, brush, DashStyleHelper.Solid, 2);

            Draw.Text(this, lvl.Tag + "_label", false, "multiple node",
                      lvl.StartTime, lvl.Price + 2 * multipleNodeTickSize, 0,
                      brush, multipleNodeLabelFont, TextAlignment.Left,
                      Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void RecolorMultipleNodeLevel(Level lvl, Brush brush)
        {
            DrawMultipleNodeLevel(lvl, brush);
        }

        private void InvalidateMultipleNodeLevel(Level lvl, DateTime endTime)
        {
            RemoveDrawObjectSafe(lvl.Tag);
            RemoveDrawObjectSafe(lvl.Tag + "_label");

            Brush brush = Brushes.Gold;
            if (lvl.State == NodeState.Demand)
                brush = Brushes.LimeGreen;
            else if (lvl.State == NodeState.Supply)
                brush = Brushes.Red;

            Draw.Line(this, lvl.Tag + "_hist", false, lvl.StartTime, lvl.Price, endTime, lvl.Price, brush, DashStyleHelper.Solid, 2);

            Draw.Text(this, lvl.Tag + "_hist_label", false, "multiple node",
                      lvl.StartTime, lvl.Price + 2 * multipleNodeTickSize, 0,
                      brush, multipleNodeLabelFont, TextAlignment.Left,
                      Brushes.Transparent, Brushes.Transparent, 0);

            lvl.InvalidationTime = endTime;
            lvl.Active           = false;
        }

        private void DeleteMultipleNodeLevel(Level lvl)
        {
            RemoveDrawObjectSafe(lvl.Tag);
            RemoveDrawObjectSafe(lvl.Tag + "_label");
            RemoveDrawObjectSafe(lvl.Tag + "_hist");
            RemoveDrawObjectSafe(lvl.Tag + "_hist_label");
            lvl.Active = false;
        }

        private bool CloseTouchesOrCrossesMultipleNode(double lastClose, double prevClose, double levelPrice)
        {
            if (Math.Abs(lastClose - levelPrice) <= MultipleNodeCloseTouchEps)
                return true;

            bool crossDown = lastClose < levelPrice && prevClose > levelPrice;
            bool crossUp   = lastClose > levelPrice && prevClose < levelPrice;
            return crossDown || crossUp;
        }
        #endregion

        #region Imbalance (IM) module
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

        private int GetPrimaryIndex(DateTime barTime)
        {
            int idxOnPrimary = BarsArray[0].GetBar(barTime);
            if (idxOnPrimary < 0)
            {
                if (CurrentBars[0] >= 0)
                    idxOnPrimary = CurrentBars[0];
                else
                    idxOnPrimary = -1;
            }

            return idxOnPrimary;
        }

        private void CreateOrUpdateStackLine(DateTime barTime, double runStartPrice, int runCount, bool isAskSide)
        {
            if (CurrentBars[0] < 1)
                return;

            double midPrice = runStartPrice + ((runCount - 1) * 0.5) * volTickSize;

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

            string tagRay = $"a2imbalance_v6_{side}_{barTime:yyyyMMddHHmmss}_{Instrument.FullName}_{runStartPrice.ToString("0.########")}";
            string tagText = $"{tagRay}_text";

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

            if (startBarsAgo <= endBarsAgo)
            {
                if (CurrentBars[0] >= 1)
                    startBarsAgo = 1;
                else
                    return;
            }

            Brush brush = isAskSide ? Brushes.LimeGreen : Brushes.Red;

            var ray = Draw.Ray(this, tagRay, startBarsAgo, midPrice, endBarsAgo, midPrice, brush);
            if (ray != null && ray.Stroke != null)
                ray.Stroke.Width = 2;

            Draw.Text(this, tagText, "Stack Imbalance", startBarsAgo, midPrice, brush);

            if (!activeLines.TryGetValue(tagRay, out var infoNew))
            {
                infoNew = new StackLine
                {
                    TagRay = tagRay,
                    TagText = tagText,
                    Price = midPrice,
                    IsAskStack = isAskSide,
                    BarTime = barTime,
                    RunStartPrice = runStartPrice,
                    OriginPrimaryIndex = GetPrimaryIndex(barTime)
                };
                activeLines[tagRay] = infoNew;
            }
            else
            {
                infoNew.Price = midPrice;
            }
        }

        private void CheckInvalidations()
        {
            if (activeLines.Count == 0)
                return;

            bool useVolForInvalidation = volTickSize > 0 && volBip >= 0 && CurrentBars.Length > volBip && CurrentBars[volBip] >= 0;
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
                    if (currentLow <= info.Price - tol)
                        toFinalize.Add(kvp.Key);
                }
                else
                {
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

            int originIndex = info.OriginPrimaryIndex;
            if (originIndex < 0)
                originIndex = GetPrimaryIndex(info.BarTime);

            int barsPassed = Math.Max(0, CurrentBars[0] - originIndex);
            if (FiltroSupervivencia && barsPassed <= 1)
            {
                RemoveStackLine(tagRay);
                return;
            }

            int startBarsAgo = BarsAgoOnPrimary(info.BarTime);
            int endBarsAgo = 0;

            if (startBarsAgo <= endBarsAgo && CurrentBars[0] >= 1)
                startBarsAgo = 1;

            Brush brush = info.IsAskStack ? Brushes.LimeGreen : Brushes.Red;

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

        private void EnsureSurvivalCutoffTime()
        {
            // No-op
        }
        #endregion

        #region VolumeCluster (VN) module
        private void VnDetectCluster(int volBarIndex, DateTime volBarTime, double low, double high)
        {
            var volRow = volBarsType.Volumes[volBarIndex];

            long barTotalVol = volRow.TotalVolume;
            if (barTotalVol <= 0)
                return;

            double highLocal = high;
            double lowLocal  = low;

            int niveles = Math.Max(1, (int)Math.Round((highLocal - lowLocal) / TickSize)) + 1;
            int ventana = Math.Max(1, TicksAlrededor);
            if (niveles < ventana)
                return;

            long umbral = (long)Math.Ceiling(barTotalVol * (PorcentajeConcentrado / 100.0));

            long mejorSuma   = 0;
            int  mejorInicio = -1;

            for (int start = 0; start <= niveles - ventana; start++)
            {
                long suma = 0;
                for (int k = 0; k < ventana; k++)
                {
                    double price = Instrument.MasterInstrument.RoundToTickSize(highLocal - (start + k) * TickSize);
                    suma += volRow.GetTotalVolumeForPrice(price);
                }
                if (suma > mejorSuma)
                {
                    mejorSuma   = suma;
                    mejorInicio = start;
                }
            }

            if (mejorInicio < 0 || mejorSuma < umbral)
                return;

            double rangoHigh = Instrument.MasterInstrument.RoundToTickSize(highLocal - mejorInicio * TickSize);
            double rangoLow  = Instrument.MasterInstrument.RoundToTickSize(rangoHigh - (ventana - 1) * TickSize);

            long   maxV  = -1;
            double nivel = (rangoHigh + rangoLow) * 0.5;
            for (int k = 0; k < ventana; k++)
            {
                double p = Instrument.MasterInstrument.RoundToTickSize(rangoHigh - k * TickSize);
                long v   = volRow.GetTotalVolumeForPrice(p);
                if (v > maxV)
                {
                    maxV  = v;
                    nivel = p;
                }
            }
            nivel = Instrument.MasterInstrument.RoundToTickSize(nivel);

            var cz = new ClusterZone
            {
                FormationTime    = volBarTime,
                Level            = nivel,
                RangeHigh        = rangoHigh,
                RangeLow         = rangoLow,
                ClusterVolume    = mejorSuma,
                BarTotalVolume   = barTotalVol,
                Active           = false,
                IsSupport        = false,
                Tag              = $"a2vc_{volBarTime:yyyyMMdd_HHmmss}_{Math.Round(nivel / TickSize)}",
                M1ClosesSeen     = 0,
                ActivationTime   = DateTime.MinValue,
                InvalidationTime = DateTime.MinValue
            };

            if (MinVelasConfirmacion == 0)
            {
                double volClose = Closes[volBip][0];
                if (volClose > cz.Level)
                {
                    cz.IsSupport      = true;
                    cz.Active         = true;
                    cz.ActivationTime = cz.FormationTime;
                    vnActive.Add(cz);
                    vnDrawQueue.Add(cz);
                    vnUltimoNivelActual = cz.Level;
                }
                else if (volClose < cz.Level)
                {
                    cz.IsSupport      = false;
                    cz.Active         = true;
                    cz.ActivationTime = cz.FormationTime;
                    vnActive.Add(cz);
                    vnDrawQueue.Add(cz);
                    vnUltimoNivelActual = cz.Level;
                }
            }
            else
            {
                vnPending.Add(cz);
            }
        }

        private void VnProcessConfirmationsYVigencias()
        {
            DateTime m1CloseTime = Times[1][0];
            double   m1Close     = Closes[1][0];
            double   m1High      = Highs[1][0];
            double   m1Low       = Lows[1][0];

            if (MinVelasConfirmacion > 0)
            {
                for (int i = vnPending.Count - 1; i >= 0; i--)
                {
                    var cz = vnPending[i];

                    if (m1CloseTime <= cz.FormationTime)
                        continue;

                    cz.M1ClosesSeen++;

                    if (cz.M1ClosesSeen >= MinVelasConfirmacion)
                    {
                        if (m1Close > cz.Level)
                        {
                            cz.IsSupport      = true;
                            cz.Active         = true;
                            cz.ActivationTime = m1CloseTime;
                            vnActive.Add(cz);
                            vnDrawQueue.Add(cz);
                            vnUltimoNivelActual = cz.Level;
                        }
                        else if (m1Close < cz.Level)
                        {
                            cz.IsSupport      = false;
                            cz.Active         = true;
                            cz.ActivationTime = m1CloseTime;
                            vnActive.Add(cz);
                            vnDrawQueue.Add(cz);
                            vnUltimoNivelActual = cz.Level;
                        }
                        vnPending.RemoveAt(i);
                    }
                }
            }

            for (int i = vnActive.Count - 1; i >= 0; i--)
            {
                var cz = vnActive[i];

                double invalidateBelow = Instrument.MasterInstrument.RoundToTickSize(cz.Level - MargenTicksBorre * TickSize);
                double invalidateAbove = Instrument.MasterInstrument.RoundToTickSize(cz.Level + MargenTicksBorre * TickSize);

                bool invalidaSoporte     = cz.IsSupport  && m1Low  <= invalidateBelow;
                bool invalidaResistencia = !cz.IsSupport && m1High >= invalidateAbove;

                if (invalidaSoporte || invalidaResistencia)
                {
                    cz.InvalidationTime = m1CloseTime;
                    VnRemoveFromDrawQueue(cz.Tag);
                    VnConvertirEnHistorico(cz);
                    vnActive.RemoveAt(i);
                    VnRefreshUltimoNivel();
                }
            }
        }

        private void VnProcessDrawQueue()
        {
            for (int j = vnDrawQueue.Count - 1; j >= 0; j--)
            {
                var cz = vnDrawQueue[j];
                vnDrawQueue.RemoveAt(j);

                if (!VnIsActiveTag(cz.Tag))
                {
                    RemoveDrawObjectSafe(cz.Tag);
                    RemoveDrawObjectSafe(cz.Tag + "_lbl");
                    continue;
                }

                VnDibujarLineaHorizontalEnPrincipal(cz);
            }
        }

        private void VnDibujarLineaHorizontalEnPrincipal(ClusterZone cz)
        {
            Brush color = cz.IsSupport ? vnSoporteBrush : vnResistenciaBrush;

            int startBarsAgo = VnBarsAgoFromTimeOnPrimary(cz.FormationTime);
            if (startBarsAgo <= 0) startBarsAgo = 1;
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;
            startBarsAgo = Math.Min(startBarsAgo, primaryCurrentBar);
            int endBarsAgo = Math.Max(0, startBarsAgo - 1);

            RemoveDrawObjectSafe(cz.Tag);
            RemoveDrawObjectSafe(cz.Tag + "_lbl");
            RemoveDrawObjectSafe(cz.Tag + "_hist");
            RemoveDrawObjectSafe(cz.Tag + "_lbl_hist");

            var ray = Draw.Ray(this, cz.Tag, startBarsAgo, cz.Level, endBarsAgo, cz.Level, color);

            double yLabel = Instrument.MasterInstrument.RoundToTickSize(cz.Level + 2 * TickSize);
            Draw.Text(this, cz.Tag + "_lbl", "VolCluster", startBarsAgo, yLabel, color);

            VnTryStyleRayWithReflection(ray, color, vnGrosorLineaFijo);
        }

        private void VnConvertirEnHistorico(ClusterZone cz)
        {
            Brush color = cz.IsSupport ? vnSoporteBrush : vnResistenciaBrush;

            RemoveDrawObjectSafe(cz.Tag);
            RemoveDrawObjectSafe(cz.Tag + "_lbl");
            RemoveDrawObjectSafe(cz.Tag + "_hist");
            RemoveDrawObjectSafe(cz.Tag + "_lbl_hist");

            int startBarsAgo = VnBarsAgoFromTimeOnPrimary(cz.FormationTime);
            if (startBarsAgo <= 0) startBarsAgo = 1;
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;
            startBarsAgo = Math.Min(startBarsAgo, primaryCurrentBar);
            int endBarsAgo = VnBarsAgoFromTimeOnPrimary(cz.InvalidationTime);
            if (endBarsAgo < 0) endBarsAgo = 0;
            endBarsAgo = Math.Min(endBarsAgo, primaryCurrentBar);

            var line = Draw.Line(this, cz.Tag + "_hist", startBarsAgo, cz.Level, endBarsAgo, cz.Level, color);
            double yLabel = Instrument.MasterInstrument.RoundToTickSize(cz.Level + 2 * TickSize);
            Draw.Text(this, cz.Tag + "_lbl_hist", "VolCluster", startBarsAgo, yLabel, color);
            VnTryStyleRayWithReflection(line, color, vnGrosorLineaFijo);

            if (!vnHistorical.Contains(cz))
                vnHistorical.Add(cz);
        }

        private void VnTryStyleRayWithReflection(object ray, Brush color, int width)
        {
            try
            {
                if (ray == null) return;
                var t = ray.GetType();

                var outlineBrushProp = t.GetProperty("OutlineBrush");
                outlineBrushProp?.SetValue(ray, color, null);

                var dashProp = t.GetProperty("DashStyleHelper");
                if (dashProp != null && dashProp.PropertyType.IsEnum)
                {
                    object dot = Enum.Parse(dashProp.PropertyType, "Dot", true);
                    dashProp.SetValue(ray, dot, null);
                }

                var widthProp = t.GetProperty("Width");
                if (widthProp != null && widthProp.PropertyType == typeof(int))
                    widthProp.SetValue(ray, width, null);

                var strokeProp = t.GetProperty("Stroke");
                if (strokeProp != null)
                {
                    var strokeType = strokeProp.PropertyType;
                    object strokeObj = null;

                    var ctor2 = strokeType.GetConstructor(new Type[] { typeof(Brush), typeof(int) });
                    if (ctor2 != null)
                        strokeObj = ctor2.Invoke(new object[] { color, width });
                    else
                    {
                        var dashType = dashProp?.PropertyType;
                        if (dashType != null)
                        {
                            var ctor3 = strokeType.GetConstructor(new Type[] { typeof(Brush), dashType, typeof(int) });
                            if (ctor3 != null)
                            {
                                object dot = Enum.Parse(dashType, "Dot", true);
                                strokeObj = ctor3.Invoke(new object[] { color, dot, width });
                            }
                        }
                    }

                    if (strokeObj != null)
                        strokeProp.SetValue(ray, strokeObj, null);
                }
            }
            catch
            {
                // Silencioso
            }
        }

        private int VnBarsAgoFromTimeOnPrimary(DateTime time)
        {
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;

            int idxOnPrimary = Bars.GetBar(time);
            if (idxOnPrimary < 0)
                return primaryCurrentBar;

            int barsAgo = primaryCurrentBar - idxOnPrimary;
            if (barsAgo < 0)
                barsAgo = 0;
            if (barsAgo > primaryCurrentBar)
                barsAgo = primaryCurrentBar;
            return barsAgo;
        }

        private void VnRefreshUltimoNivel()
        {
            vnUltimoNivelActual = (vnActive.Count > 0) ? vnActive[vnActive.Count - 1].Level : double.NaN;
        }

        private void VnRemoveFromDrawQueue(string tag)
        {
            for (int j = vnDrawQueue.Count - 1; j >= 0; j--)
                if (vnDrawQueue[j].Tag == tag) vnDrawQueue.RemoveAt(j);
        }

        private void VnRemoveActiveByTag(string tag)
        {
            for (int i = vnActive.Count - 1; i >= 0; i--)
                if (vnActive[i].Tag == tag) vnActive.RemoveAt(i);
            VnRefreshUltimoNivel();
        }

        private bool VnIsActiveTag(string tag)
        {
            for (int i = 0; i < vnActive.Count; i++)
                if (vnActive[i].Tag == tag) return true;
            return false;
        }

        private void ResetVolumeClusterModule()
        {
            var tags = new HashSet<string>();
            foreach (var cz in vnPending)
                tags.Add(cz.Tag);
            foreach (var cz in vnActive)
                tags.Add(cz.Tag);
            foreach (var cz in vnDrawQueue)
                tags.Add(cz.Tag);
            foreach (var cz in vnHistorical)
            {
                tags.Add(cz.Tag);
                tags.Add(cz.Tag + "_hist");
                tags.Add(cz.Tag + "_lbl_hist");
            }

            foreach (var tag in tags)
            {
                RemoveDrawObjectSafe(tag);
                RemoveDrawObjectSafe(tag + "_lbl");
                RemoveDrawObjectSafe(tag + "_hist");
                RemoveDrawObjectSafe(tag + "_lbl_hist");
            }

            vnPending.Clear();
            vnActive.Clear();
            vnDrawQueue.Clear();
            vnHistorical.Clear();

            vnUltimoNivelActual = double.NaN;
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
public a2hub a2hub(bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
return a2hub(Input, minTradeModuleOn, resetSessionOnNewSession, imbalanceModuleOn, volumeClusterModuleOn, multipleNodeModuleOn, uBModuleOn, globalTimeFrameMinutes, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, filtroSupervivencia, porcentajeConcentrado, ticksAlrededor, minVelasConfirmacion, margenTicksBorre, multipleNodeVentanaMin, multipleNodeTicksAlrededor, multipleNodeToleranciaTicks, minOppositeVolume);
}

public a2hub a2hub(ISeries<double> input, bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
if (cachea2hub != null)
for (int idx = 0; idx < cachea2hub.Length; idx++)
if (cachea2hub[idx] != null && cachea2hub[idx].MinTradeModuleOn == minTradeModuleOn && cachea2hub[idx].ResetSessionOnNewSession == resetSessionOnNewSession && cachea2hub[idx].ImbalanceModuleOn == imbalanceModuleOn && cachea2hub[idx].VolumeClusterModuleOn == volumeClusterModuleOn && cachea2hub[idx].MultipleNodeModuleOn == multipleNodeModuleOn && cachea2hub[idx].UBModuleOn == uBModuleOn && cachea2hub[idx].GlobalTimeFrameMinutes == globalTimeFrameMinutes && cachea2hub[idx].MinTrade == minTrade && cachea2hub[idx].ToleranciaTicks == toleranciaTicks && cachea2hub[idx].ClusterWindowMs == clusterWindowMs && cachea2hub[idx].DriftTicks == driftTicks && cachea2hub[idx].UseAlMenos == useAlMenos && cachea2hub[idx].AlMenosMinTrade == alMenosMinTrade && cachea2hub[idx].UseMinPrint == useMinPrint && cachea2hub[idx].MinPrintVol == minPrintVol && cachea2hub[idx].StackImbalance == stackImbalance && cachea2hub[idx].ImbalanceRatio.ApproxCompare(imbalanceRatio) == 0 && cachea2hub[idx].MinDeltaImbalance == minDeltaImbalance && cachea2hub[idx].ToleranciaBorrarTicks == toleranciaBorrarTicks && cachea2hub[idx].FiltroSupervivencia == filtroSupervivencia && cachea2hub[idx].PorcentajeConcentrado == porcentajeConcentrado && cachea2hub[idx].TicksAlrededor == ticksAlrededor && cachea2hub[idx].MinVelasConfirmacion == minVelasConfirmacion && cachea2hub[idx].MargenTicksBorre == margenTicksBorre && cachea2hub[idx].MultipleNodeVentanaMin == multipleNodeVentanaMin && cachea2hub[idx].MultipleNodeTicksAlrededor == multipleNodeTicksAlrededor && cachea2hub[idx].MultipleNodeToleranciaTicks == multipleNodeToleranciaTicks && cachea2hub[idx].MinOppositeVolume == minOppositeVolume && cachea2hub[idx].EqualsInput(input))
return cachea2hub[idx];
return CacheIndicator<a2hub>(new a2hub(){ MinTradeModuleOn = minTradeModuleOn, ResetSessionOnNewSession = resetSessionOnNewSession, ImbalanceModuleOn = imbalanceModuleOn, VolumeClusterModuleOn = volumeClusterModuleOn, MultipleNodeModuleOn = multipleNodeModuleOn, UBModuleOn = uBModuleOn, GlobalTimeFrameMinutes = globalTimeFrameMinutes, MinTrade = minTrade, ToleranciaTicks = toleranciaTicks, ClusterWindowMs = clusterWindowMs, DriftTicks = driftTicks, UseAlMenos = useAlMenos, AlMenosMinTrade = alMenosMinTrade, UseMinPrint = useMinPrint, MinPrintVol = minPrintVol, StackImbalance = stackImbalance, ImbalanceRatio = imbalanceRatio, MinDeltaImbalance = minDeltaImbalance, ToleranciaBorrarTicks = toleranciaBorrarTicks, FiltroSupervivencia = filtroSupervivencia, PorcentajeConcentrado = porcentajeConcentrado, TicksAlrededor = ticksAlrededor, MinVelasConfirmacion = minVelasConfirmacion, MargenTicksBorre = margenTicksBorre, MultipleNodeVentanaMin = multipleNodeVentanaMin, MultipleNodeTicksAlrededor = multipleNodeTicksAlrededor, MultipleNodeToleranciaTicks = multipleNodeToleranciaTicks, MinOppositeVolume = minOppositeVolume }, input, ref cachea2hub);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.a2hub a2hub(bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
return indicator.a2hub(Input, minTradeModuleOn, resetSessionOnNewSession, imbalanceModuleOn, volumeClusterModuleOn, multipleNodeModuleOn, uBModuleOn, globalTimeFrameMinutes, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, filtroSupervivencia, porcentajeConcentrado, ticksAlrededor, minVelasConfirmacion, margenTicksBorre, multipleNodeVentanaMin, multipleNodeTicksAlrededor, multipleNodeToleranciaTicks, minOppositeVolume);
}

public Indicators.a2hub a2hub(ISeries<double> input , bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
return indicator.a2hub(input, minTradeModuleOn, resetSessionOnNewSession, imbalanceModuleOn, volumeClusterModuleOn, multipleNodeModuleOn, uBModuleOn, globalTimeFrameMinutes, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, filtroSupervivencia, porcentajeConcentrado, ticksAlrededor, minVelasConfirmacion, margenTicksBorre, multipleNodeVentanaMin, multipleNodeTicksAlrededor, multipleNodeToleranciaTicks, minOppositeVolume);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.a2hub a2hub(bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
return indicator.a2hub(Input, minTradeModuleOn, resetSessionOnNewSession, imbalanceModuleOn, volumeClusterModuleOn, multipleNodeModuleOn, uBModuleOn, globalTimeFrameMinutes, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, filtroSupervivencia, porcentajeConcentrado, ticksAlrededor, minVelasConfirmacion, margenTicksBorre, multipleNodeVentanaMin, multipleNodeTicksAlrededor, multipleNodeToleranciaTicks, minOppositeVolume);
}

public Indicators.a2hub a2hub(ISeries<double> input , bool minTradeModuleOn, bool resetSessionOnNewSession, bool imbalanceModuleOn, bool volumeClusterModuleOn, bool multipleNodeModuleOn, bool uBModuleOn, int globalTimeFrameMinutes, int minTrade, int toleranciaTicks, int clusterWindowMs, int driftTicks, bool useAlMenos, int alMenosMinTrade, bool useMinPrint, int minPrintVol, int stackImbalance, double imbalanceRatio, int minDeltaImbalance, int toleranciaBorrarTicks, bool filtroSupervivencia, int porcentajeConcentrado, int ticksAlrededor, int minVelasConfirmacion, int margenTicksBorre, int multipleNodeVentanaMin, int multipleNodeTicksAlrededor, int multipleNodeToleranciaTicks, int minOppositeVolume)
{
return indicator.a2hub(input, minTradeModuleOn, resetSessionOnNewSession, imbalanceModuleOn, volumeClusterModuleOn, multipleNodeModuleOn, uBModuleOn, globalTimeFrameMinutes, minTrade, toleranciaTicks, clusterWindowMs, driftTicks, useAlMenos, alMenosMinTrade, useMinPrint, minPrintVol, stackImbalance, imbalanceRatio, minDeltaImbalance, toleranciaBorrarTicks, filtroSupervivencia, porcentajeConcentrado, ticksAlrededor, minVelasConfirmacion, margenTicksBorre, multipleNodeVentanaMin, multipleNodeTicksAlrededor, multipleNodeToleranciaTicks, minOppositeVolume);
}
}
}

#endregion
