#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;              // TextAlignment
using System.Windows.Media;
using System.Xml.Serialization;    // XmlIgnore
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;       // SimpleFont
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =====================================================================================================
// README - a2multiplenodes  (v6)
// -----------------------------------------------------------------------------------------------------
// Cambios v6 (interno; el nombre del indicador NO cambia):
//   • Se fija y oculta en propiedades: 
//       - "Borrar sólo si cierre toca" = ON
//       - "Cierre para borrar" = OneMinute
//     (ya no aparecen en el panel de Propiedades).
//   • Se fija y oculta en propiedades: "Modo precio línea" = Average.
//   • Se añade parámetro visible: "Tolerancia ticks (cierre 1m)" = 6 (por defecto).
//       - Nuevo borrado por cierre (sólo para 1m) con umbral direccional:
//           · DEMAND: sólo borra si el CIERRE de 1m queda > Tolerancia por DEBAJO del nivel 
//                     (p. ej., más de 6 ticks por debajo).
//           · SUPPLY: sólo borra si el CIERRE de 1m queda > Tolerancia por ENCIMA del nivel 
//                     (p. ej., más de 6 ticks por encima).
//         Nota: Mientras el nodo esté "Pending", se mantiene el criterio anterior (tocar/cruzar por cierre).
//
// Resto del comportamiento (detección de múltiple nodo, expiraciones, mechas si se desactiva el modo cierre, etc.)
// permanece SIN cambios.
//
// ¿Qué hace? (resumen)
//   • Detecta “multiple nodes” (≥2 POC dentro de ±ticks) con barras Volumétricas (Order Flow+) de X min (5 por defecto)
//     dentro de una ventana de Y min (25 por defecto), siempre tick a tick (ticksPerLevel=1).
//   • Dibuja un RAY horizontal desde la barra que lo formó hacia la derecha con etiqueta "multiple node".
//   • Clasificación tras el primer cierre de 1m posterior:
//       - Cierre > nivel -> DEMAND (verde)
//       - Cierre < nivel -> SUPPLY (rojo)
//     Mientras no se clasifica, se mantiene amarillo.
//   • Borrado configurable internamente (fijo en esta versión): por CIERRE de 1m con tolerancia direccional (ver arriba).
// =====================================================================================================

namespace NinjaTrader.NinjaScript
{
    // Enums en el namespace padre para que el diseñador de propiedades las resuelva sin problemas
    public enum LinePriceMode { Average, Midpoint }
    public enum CloseSource { Primary, OneMinute, ThirtySeconds }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2multiplenodes : Indicator
    {
        #region Nested types
        private enum NodeState { Pending = 0, Demand = 1, Supply = 2 }

        private class Level
        {
            public string Tag;
            public double Price;
            public DateTime StartTime;
            public DateTime NextMinuteCloseTime; // para clasificación (1m)
            public NodeState State;
            public bool Active;
            public DateTime? InvalidationTime;
            public int HitCount;
            public DateTime? LastClusterTime;
        }
        #endregion

        #region Inputs (properties)
        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Time frame velas (min)", GroupName = "Parámetros", Order = 1)]
        public int VelasTimeFrameMin { get; set; } = 5;

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Dentro de cuántos minutos", GroupName = "Parámetros", Order = 2)]
        public int VentanaMin { get; set; } = 25;

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Ticks alrededor", GroupName = "Parámetros", Order = 3)]
        public int TicksAlrededor { get; set; } = 6;

        // ---- (OCULTO) Modo precio línea: fijo en Average
        [Browsable(false)]
        [XmlIgnore]
        public NinjaTrader.NinjaScript.LinePriceMode ModoPrecio { get; set; } = NinjaTrader.NinjaScript.LinePriceMode.Average;

        // ---- Gestión de niveles (visibles)
        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Tolerancia ticks (cierre 1m)", GroupName = "Gestión de niveles", Order = 12)]
        public int ToleranciaTicks { get; set; } = 6;

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Color linea", GroupName = "Gestión de niveles", Order = 12)]
        public Brush ColorLinea { get; set; } = Brushes.Gold;

        [Browsable(false)]
        public string ColorLineaSerializable
        {
            get { return Serialize.BrushToString(ColorLinea); }
            set { ColorLinea = Serialize.StringToBrush(value); }
        }

        // ---- (OCULTO) Borrado sólo por cierre: fijo en ON y OneMinute
        [Browsable(false)]
        [XmlIgnore]
        public bool BorrarSoloSiCierre { get; set; } = true;

        [Browsable(false)]
        [XmlIgnore]
        public NinjaTrader.NinjaScript.CloseSource CierreParaBorrar { get; set; } = NinjaTrader.NinjaScript.CloseSource.OneMinute;

        [NinjaScriptProperty]
        [Display(Name = "Show history", GroupName = "Gestión de niveles", Order = 14)]
        public bool ShowHistory { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Restart session (limpiar al inicio)", GroupName = "Gestión de niveles", Order = 13)]
        public bool RestartSession { get; set; } = true;
        #endregion

        #region State / fields
        private int bipVol = -1;          // Volumétrico X min
        private int bipOneMinute = -1;    // 1m
        private int bipThirtySec = -1;    // 30s

        private double tickSize;
        private readonly List<Level> activeLevels = new List<Level>();
        private int uniqueId = 0;
        private string tagPrefix;
        private SimpleFont labelFont;

        private double DedupTolerance => Math.Max(tickSize, (TicksAlrededor * tickSize) / 2.0);
        private double CloseTouchEps   => tickSize * 0.1; // tolerancia para considerar "cierre toca" ≈ exacto
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "a2multiplenodes";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;

                // Fijamos defaults (y los ocultos se mantienen ocultos)
                VelasTimeFrameMin = 5;
                VentanaMin        = 15;
                TicksAlrededor    = 6;

                // Ocultos (fijos)
                ModoPrecio         = NinjaTrader.NinjaScript.LinePriceMode.Average;
                BorrarSoloSiCierre = true;
                CierreParaBorrar   = NinjaTrader.NinjaScript.CloseSource.OneMinute;

                // Visible
                ToleranciaTicks   = 6;
                ColorLinea        = Brushes.Gold;
                ShowHistory       = true;
                RestartSession    = true;
            }
            else if (State == State.Configure)
            {
                // Serie Volumétrica interna (tick a tick con ticksPerLevel=1)
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, VelasTimeFrameMin, VolumetricDeltaType.BidAsk, 1);
                bipVol = 1;

                // Series auxiliares de cierre
                AddDataSeries(BarsPeriodType.Minute, 1);
                bipOneMinute = 2;

                AddDataSeries(BarsPeriodType.Second, 30);
                bipThirtySec = 3;
            }
            else if (State == State.DataLoaded)
            {
                tickSize  = Instrument.MasterInstrument.TickSize;
                tagPrefix = $"a2MN_{Instrument.MasterInstrument.Name}";
                labelFont = new SimpleFont("Arial", 10);
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (bipVol < 0 || bipOneMinute < 0 || bipThirtySec < 0)
                return;

            // --- 1) Serie Volumétrica Xmin: detectar clusters POC
            if (BarsInProgress == bipVol)
            {
                var vbt = BarsArray[bipVol].BarsType as VolumetricBarsType;
                if (vbt == null || CurrentBar < 0)
                    return;

                int barrasVentana = Math.Max(1, VentanaMin / Math.Max(1, VelasTimeFrameMin));
                int disponibles   = Math.Min(barrasVentana, CurrentBar + 1);

                var pocs = new List<double>(disponibles);
                for (int i = 0; i < disponibles; i++)
                {
                    int absIndex = CurrentBar - i;
                    if (absIndex < 0) break;

                    double pocPrice;
                    // volumen combinado Bid+Ask (null)
                    vbt.Volumes[absIndex].GetMaximumVolume(null, out pocPrice);
                    if (!double.IsNaN(pocPrice) && pocPrice > 0)
                        pocs.Add(Instrument.MasterInstrument.RoundToTickSize(pocPrice));
                }

                var clusters = GetClusterLevels(pocs);
                foreach (double levelPrice in clusters)
                {
                    var existing = activeLevels.FirstOrDefault(x => x.InvalidationTime == null && Math.Abs(x.Price - levelPrice) <= DedupTolerance);

                    if (existing != null)
                    {
                        if (existing.LastClusterTime != Times[bipVol][0])
                        {
                            existing.HitCount++;
                            existing.LastClusterTime = Times[bipVol][0];
                        }

                        if (!existing.Active)
                        {
                            existing.Active = true;
                            existing.StartTime = Times[bipVol][0];
                            existing.NextMinuteCloseTime = existing.StartTime.AddMinutes(1);
                            existing.State = NodeState.Pending;
                        }

                        RecolorLevel(existing, GetStateBrush(existing.State));
                    }
                    else
                    {
                        CreateLevel(levelPrice, Times[bipVol][0]);
                    }
                }

                // Cancelar niveles pendientes que ya no estén soportados por un cluster actual
                foreach (var lvl in activeLevels.Where(x => x.Active && x.State == NodeState.Pending).ToList())
                {
                    bool hasCluster = clusters.Any(c => Math.Abs(c - lvl.Price) <= DedupTolerance);
                    if (!hasCluster)
                        DeleteLevel(lvl);
                }
            }
            // --- 2) Serie 1m: clasificar y (si aplica) borrar por cierre=1m (con tolerancia direccional v6)
            else if (BarsInProgress == bipOneMinute)
            {
                if (CurrentBar < 1)
                    return;

                if (IsFirstTickOfBar)
                {
                    DateTime lastCloseTime  = Times[bipOneMinute][1];
                    double   lastClosePrice = Closes[bipOneMinute][1];

                    // Clasificación del nodo pendiente usando el primer cierre 1m posterior
                    foreach (var lvl in activeLevels.Where(x => x.Active && x.State == NodeState.Pending && lastCloseTime >= x.NextMinuteCloseTime).ToList())
                    {
                        if (lastClosePrice > lvl.Price) { lvl.State = NodeState.Demand; RecolorLevel(lvl, Brushes.LimeGreen); }
                        else if (lastClosePrice < lvl.Price) { lvl.State = NodeState.Supply; RecolorLevel(lvl, Brushes.Red); }
                        // Si es exactamente igual, se mantiene Pending (amarillo)
                    }

                    // Borrado por cierre 1m
                    if (BorrarSoloSiCierre && CierreParaBorrar == NinjaTrader.NinjaScript.CloseSource.OneMinute)
                    {
                        double c1 = Closes[bipOneMinute][1];
                        double c2 = (CurrentBars[bipOneMinute] > 1) ? Closes[bipOneMinute][2] : c1;

                        double tol = Math.Max(0, ToleranciaTicks) * tickSize;

                        foreach (var lvl in activeLevels.Where(x => x.Active).ToList())
                        {
                            if (lvl.State == NodeState.Pending)
                            {
                                // Comportamiento previo: borra si "toca exacto" o cruza por cierre
                                if (CloseTouchesOrCrosses(c1, c2, lvl.Price))
                                    InvalidateLevel(lvl, Times[bipOneMinute][1]);
                                continue;
                            }

                            // v6: Umbral direccional por estado
                            bool broken = false;
                            if (lvl.State == NodeState.Demand)
                                broken = (c1 < lvl.Price - tol);      // cierre > tolerancia por debajo del nivel
                            else if (lvl.State == NodeState.Supply)
                                broken = (c1 > lvl.Price + tol);      // cierre > tolerancia por encima del nivel

                            if (broken)
                                InvalidateLevel(lvl, Times[bipOneMinute][1]);
                        }
                    }
                }
            }
            // --- 3) Serie 30s: borrado por cierre=30s (no aplica en v6 por estar fijado OneMinute, pero se deja por compatibilidad)
            else if (BarsInProgress == bipThirtySec)
            {
                if (!BorrarSoloSiCierre || CierreParaBorrar != NinjaTrader.NinjaScript.CloseSource.ThirtySeconds || CurrentBar < 1)
                    return;

                if (IsFirstTickOfBar)
                {
                    double c1 = Closes[bipThirtySec][1];
                    double c2 = (CurrentBars[bipThirtySec] > 1) ? Closes[bipThirtySec][2] : c1;

                    foreach (var lvl in activeLevels.Where(x => x.Active).ToList())
                        if (CloseTouchesOrCrosses(c1, c2, lvl.Price))
                            InvalidateLevel(lvl, Times[bipThirtySec][1]);
                }
            }
            // --- 4) Serie primaria: limpieza de sesión y borrado por toque (o por cierre=primario)
            else if (BarsInProgress == 0)
            {
                if (CurrentBar < 0)
                    return;

                // Limpiar al inicio de sesión si está activado
                if (RestartSession && IsFirstTickOfBar && Bars.IsFirstBarOfSession)
                {
                    foreach (var lvl in activeLevels.ToList())
                        DeleteLevel(lvl);

                    activeLevels.Clear();
                }

                foreach (var lvl in activeLevels.Where(x => x.Active).ToList())
                {
                    if (!BorrarSoloSiCierre)
                    {
                        // Modo clásico: borrar si High/Low toca/cruza
                        if (High[0] >= lvl.Price && Low[0] <= lvl.Price)
                            InvalidateLevel(lvl, Times[0][0]);
                    }
                    else if (CierreParaBorrar == NinjaTrader.NinjaScript.CloseSource.Primary && IsFirstTickOfBar)
                    {
                        // Borrar por CIERRE usando el timeframe primario (no se usa en v6 por estar fijado a 1m)
                        double c1 = Close[1];
                        double c2 = (CurrentBar > 1) ? Close[2] : c1;
                        if (CloseTouchesOrCrosses(c1, c2, lvl.Price))
                            InvalidateLevel(lvl, Times[0][1]);
                    }
                }

                // Confirmación adicional al cierre de la vela primaria (p. ej., 5m)
                if (IsFirstTickOfBar && CurrentBar > 0)
                {
                    var vbt = BarsArray[bipVol].BarsType as VolumetricBarsType;
                    if (vbt == null)
                        return;

                    int barrasVentana = Math.Max(1, VentanaMin / Math.Max(1, VelasTimeFrameMin));
                    int disponibles   = Math.Min(barrasVentana, BarsArray[bipVol].CurrentBar + 1);

                    var pocs = new List<double>(disponibles);
                    for (int i = 0; i < disponibles; i++)
                    {
                        int absIndex = BarsArray[bipVol].CurrentBar - i;
                        if (absIndex < 0) break;

                        double pocPrice;
                        vbt.Volumes[absIndex].GetMaximumVolume(null, out pocPrice);
                        if (!double.IsNaN(pocPrice) && pocPrice > 0)
                            pocs.Add(Instrument.MasterInstrument.RoundToTickSize(pocPrice));
                    }

                    var clusters = GetClusterLevels(pocs);

                    foreach (var lvl in activeLevels.Where(x => x.State == NodeState.Pending).ToList())
                    {
                        bool hasCluster = clusters.Any(c => Math.Abs(c - lvl.Price) <= DedupTolerance);
                        if (!hasCluster)
                            DeleteLevel(lvl);
                    }
                }
            }
        }
        #endregion

        #region Helpers (clusters, draw, manage)
        // Devuelve niveles (precios) de clusters de POC con tamaño ≥2 dentro de ±TicksAlrededor
        private List<double> GetClusterLevels(List<double> pocs)
        {
            var result = new List<double>();
            if (pocs == null || pocs.Count < 2)
                return result;

            pocs.Sort();
            double tol = TicksAlrededor * tickSize;

            List<double> cluster = new List<double> { pocs[0] };
            double clusterStart = pocs[0];

            for (int i = 1; i < pocs.Count; i++)
            {
                double p = pocs[i];
                if (p - clusterStart <= tol)
                    cluster.Add(p);
                else
                {
                    if (cluster.Count >= 2)
                        result.Add(ClusterPrice(cluster));
                    cluster.Clear();
                    cluster.Add(p);
                    clusterStart = p;
                }
            }

            if (cluster.Count >= 2)
                result.Add(ClusterPrice(cluster));

            return result;
        }

        private double ClusterPrice(List<double> cluster)
        {
            if (cluster == null || cluster.Count == 0)
                return 0;

            return ModoPrecio == NinjaTrader.NinjaScript.LinePriceMode.Average
                ? Instrument.MasterInstrument.RoundToTickSize(cluster.Average())
                : Instrument.MasterInstrument.RoundToTickSize(0.5 * (cluster.First() + cluster.Last()));
        }

        private bool HasActiveLevelNear(double price)
        {
            foreach (var lvl in activeLevels)
                if (lvl.Active && Math.Abs(lvl.Price - price) <= DedupTolerance)
                    return true;
            return false;
        }

        private void CreateLevel(double price, DateTime startTime)
        {
            var lvl = new Level
            {
                Price               = Instrument.MasterInstrument.RoundToTickSize(price),
                StartTime           = startTime,
                NextMinuteCloseTime = startTime.AddMinutes(1),
                State               = NodeState.Pending,
                Active              = true,
                Tag                 = $"{tagPrefix}_{++uniqueId}_{(long)Math.Round(price / tickSize)}",
                HitCount            = 1,
                LastClusterTime     = startTime
            };

            activeLevels.Add(lvl);
            DrawLevel(lvl, Brushes.DarkOrange); // pendiente
        }

        private void DrawLevel(Level lvl, Brush brush)
        {
            string labelText = GetLabelText(lvl);

            // Línea activa
            Brush lineBrush = ColorLinea;

            // Color del texto = el brush que le pasas (Gold al crear, o LimeGreen/Red al recolorear)
            Brush textBrush = brush;

            // Ray horizontal
            Draw.Ray(this, lvl.Tag,
                     lvl.StartTime, lvl.Price,
                     lvl.StartTime.AddMinutes(1), lvl.Price,
                     lineBrush, DashStyleHelper.Solid, 2);

            // Etiqueta X / XM
            Draw.Text(this, lvl.Tag + "_label", false, labelText,
                      lvl.StartTime, lvl.Price + 2 * tickSize, 0,
                      textBrush, labelFont, TextAlignment.Left,
                      Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void RecolorLevel(Level lvl, Brush brush)
        {
            string labelText = GetLabelText(lvl);

            // Línea activa
            Brush lineBrush = ColorLinea;

            // Texto usa el brush pasado (Gold / LimeGreen / Red)
            Brush textBrush = brush;

            Draw.Ray(this, lvl.Tag,
                     lvl.StartTime, lvl.Price,
                     lvl.StartTime.AddMinutes(1), lvl.Price,
                     lineBrush, DashStyleHelper.Solid, 2);

            Draw.Text(this, lvl.Tag + "_label", false, labelText,
                      lvl.StartTime, lvl.Price + 2 * tickSize, 0,
                      textBrush, labelFont, TextAlignment.Left,
                      Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void InvalidateLevel(Level lvl, DateTime endTime)
        {
            RemoveDrawObject(lvl.Tag);
            RemoveDrawObject(lvl.Tag + "_label");

            Brush textBrush = Brushes.Gold;
            if (lvl.State == NodeState.Demand)
                textBrush = Brushes.LimeGreen;
            else if (lvl.State == NodeState.Supply)
                textBrush = Brushes.Red;

            if (ShowHistory)
            {
                Draw.Line(this, lvl.Tag + "_hist", false, lvl.StartTime, lvl.Price, endTime, lvl.Price, Brushes.LightGray, DashStyleHelper.Solid, 2);

                Draw.Text(this, lvl.Tag + "_hist_label", false, GetLabelText(lvl),
                          lvl.StartTime, lvl.Price + 2 * tickSize, 0,
                          textBrush, labelFont, TextAlignment.Left,
                          Brushes.Transparent, Brushes.Transparent, 0);
            }

            lvl.InvalidationTime = endTime;
            lvl.Active = false;
        }

        private void DeleteLevel(Level lvl)
        {
            RemoveDrawObject(lvl.Tag);
            RemoveDrawObject(lvl.Tag + "_label");
            RemoveDrawObject(lvl.Tag + "_hist");
            RemoveDrawObject(lvl.Tag + "_hist_label");
            lvl.Active = false;
        }

        // Toca/cruza por CIERRE (usar dos cierres consecutivos). Se mantiene para nodos Pending y para otras fuentes de cierre.
        private bool CloseTouchesOrCrosses(double lastClose, double prevClose, double levelPrice)
        {
            if (Math.Abs(lastClose - levelPrice) <= CloseTouchEps)
                return true;

            bool crossDown = lastClose < levelPrice && prevClose > levelPrice;
            bool crossUp   = lastClose > levelPrice && prevClose < levelPrice;
            return crossDown || crossUp;
        }

        private Brush GetStateBrush(NodeState state)
        {
            if (state == NodeState.Demand)
                return Brushes.LimeGreen;
            if (state == NodeState.Supply)
                return Brushes.Red;
            return Brushes.Gold;
        }

        private string GetLabelText(Level lvl)
        {
            return (lvl?.HitCount ?? 0) >= 2 ? "X M" : "X";
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a2multiplenodes[] cachea2multiplenodes;
        public a2multiplenodes a2multiplenodes(int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            return a2multiplenodes(Input, velasTimeFrameMin, ventanaMin, ticksAlrededor, toleranciaTicks, restartSession);
        }

        public a2multiplenodes a2multiplenodes(ISeries<double> input, int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            if (cachea2multiplenodes != null)
                for (int idx = 0; idx < cachea2multiplenodes.Length; idx++)
                    if (cachea2multiplenodes[idx] != null && cachea2multiplenodes[idx].VelasTimeFrameMin == velasTimeFrameMin && cachea2multiplenodes[idx].VentanaMin == ventanaMin && cachea2multiplenodes[idx].TicksAlrededor == ticksAlrededor && cachea2multiplenodes[idx].ToleranciaTicks == toleranciaTicks && cachea2multiplenodes[idx].RestartSession == restartSession && cachea2multiplenodes[idx].EqualsInput(input))
                        return cachea2multiplenodes[idx];
            return CacheIndicator<a2multiplenodes>(new a2multiplenodes(){ VelasTimeFrameMin = velasTimeFrameMin, VentanaMin = ventanaMin, TicksAlrededor = ticksAlrededor, ToleranciaTicks = toleranciaTicks, RestartSession = restartSession }, input, ref cachea2multiplenodes);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a2multiplenodes a2multiplenodes(int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            return indicator.a2multiplenodes(Input, velasTimeFrameMin, ventanaMin, ticksAlrededor, toleranciaTicks, restartSession);
        }

        public Indicators.a2multiplenodes a2multiplenodes(ISeries<double> input , int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            return indicator.a2multiplenodes(input, velasTimeFrameMin, ventanaMin, ticksAlrededor, toleranciaTicks, restartSession);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a2multiplenodes a2multiplenodes(int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            return indicator.a2multiplenodes(Input, velasTimeFrameMin, ventanaMin, ticksAlrededor, toleranciaTicks, restartSession);
        }

        public Indicators.a2multiplenodes a2multiplenodes(ISeries<double> input , int velasTimeFrameMin, int ventanaMin, int ticksAlrededor, int toleranciaTicks, bool restartSession)
        {
            return indicator.a2multiplenodes(input, velasTimeFrameMin, ventanaMin, ticksAlrededor, toleranciaTicks, restartSession);
        }
    }
}

#endregion
