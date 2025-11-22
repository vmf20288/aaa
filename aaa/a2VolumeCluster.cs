#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Reflection;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// =====================================================================================================
// a2VolumeCluster  — v4 (hotfix Draw.Text)
// -----------------------------------------------------------------------------------------------------
// RESUMEN (README):
//   - Detecta "volume clusters" en barras Volumetric (Order Flow+): en una ventana contigua de N ticks
//     (TicksAlrededor) se concentra ≥ P% del volumen total de la barra (PorcentajeConcentrado).
//   - Nivel = PRECIO con MAYOR VOLUMEN dentro del rango detectado (POC local).
//   - Activación:
//       * MinVelasConfirmacion = 0 -> activación inmediata al cierre de la barra volumétrica (5m por defecto).
//       * MinVelasConfirmacion > 0 -> espera X cierres de 1 minuto; cierre > nivel => soporte; < nivel => resistencia.
//   - Dibuja un **RAY HORIZONTAL** desde el instante del cluster hacia la **derecha** (infinito).
//   - Invalida la zona por **cruce con margen**.
//
// Qué hay de nuevo en v4:
//   1) **MargenTicksBorre** (default 6): invalida sólo si el precio supera el nivel por ese margen
//      — soporte: m1Low ≤ (nivel − margen*tick); resistencia: m1High ≥ (nivel + margen*tick).
//   2) Etiqueta **"VolCluster"** dibujada encima de la línea para identificarla.
//   3) **Exposición de valor** del último nivel activo/dibujado via `NivelExpuesto` (Series<double>) y
//      `UltimoNivelActual` (double), para que otro indicador/estrategia pueda leerlo en el futuro.
//
// Nota de compatibilidad (hotfix):
//   - Ajustada la llamada a `Draw.Text(...)` a la sobrecarga simple `Draw.Text(this, tag, text, barsAgo, y, brush)`
//     para versiones de NT donde no existe la sobrecarga extendida.
//
// (Histórico v3: limpieza de cola si vence antes de dibujar; evita pintar zonas vencidas con histórico profundo.)
// -----------------------------------------------------------------------------------------------------
// Parámetros:
//   - PorcentajeConcentrado (%) [25] | TicksAlrededor [6] | TimeFrameMin [5]
//   - MinVelasConfirmacion (1m) [0 = inmediata] | MargenTicksBorre [6]
// -----------------------------------------------------------------------------------------------------
// Requisitos: Order Flow+. Úsalo sobre un **chart normal de minutos**; el cálculo Volumetric es interno.
// =====================================================================================================

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2VolumeCluster : Indicator
    {
        #region Tipos y campos internos
        private int bipVol;                 // BarsInProgress de la serie Volumétrica interna
        private int bipM1;                  // BarsInProgress de la serie 1 minuto interna
        private VolumetricBarsType volBT;   // Cache del BarsType volumétrico

        private class ClusterZone
        {
            public DateTime FormationTime;  // cierre de la barra volumétrica con cluster
            public double Level;            // precio con mayor volumen dentro del rango
            public double RangeHigh;        // límite superior del rango
            public double RangeLow;         // límite inferior del rango
            public long   ClusterVolume;    // suma de volumen del rango
            public long   BarTotalVolume;   // volumen total de la barra
            public bool   Active;           // ya activada
            public bool   IsSupport;        // true soporte, false resistencia
            public string Tag;              // tag de dibujo
            public int    M1ClosesSeen;     // cierres 1m contados desde formación
            public DateTime ActivationTime; // tiempo de activación
            public DateTime InvalidationTime; // tiempo de invalidación por precio
        }

        private readonly List<ClusterZone> pending    = new List<ClusterZone>(); // esperando confirmación (si aplica)
        private readonly List<ClusterZone> active     = new List<ClusterZone>(); // activas ya pintadas (o por pintar)
        private readonly List<ClusterZone> drawQueue  = new List<ClusterZone>(); // cola para pintar en la serie principal
        private readonly List<ClusterZone> historical = new List<ClusterZone>(); // zonas historizadas tras invalidación

        private readonly Brush soporteBrush      = Brushes.LimeGreen;
        private readonly Brush resistenciaBrush  = Brushes.Red;
        private const int grosorLineaFijo        = 2;

        // Exposición de valor (v4)
        private Series<double> nivelExpuesto;
        private double ultimoNivelActual = double.NaN;
        #endregion

        #region Parámetros (propiedades)
        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Porcentaje concentrado (%)", GroupName = "Ajustes de cluster", Order = 0)]
        public int PorcentajeConcentrado { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Ticks alrededor (ancho del rango)", GroupName = "Ajustes de cluster", Order = 1)]
        public int TicksAlrededor { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Time frame (min) para Volumetric", GroupName = "Fuentes de datos", Order = 2)]
        public int TimeFrameMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Min de velas para confirmación (1m)", GroupName = "Confirmación", Order = 3)]
        public int MinVelasConfirmacion { get; set; }

        // v4: margen de ticks para invalidar (borrar) al superar el nivel
        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "Margen de ticks: borrar", GroupName = "Gestión de zonas", Order = 5)]
        public int MargenTicksBorre { get; set; }

        // Serie pública para lectura por otros scripts
        [Browsable(false), XmlIgnore]
        public Series<double> NivelExpuesto => nivelExpuesto;

        // Propiedad de conveniencia (último nivel activo)
        [Browsable(false)]
        public double UltimoNivelActual => ultimoNivelActual;
        #endregion

        #region Ciclo de vida
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "a2VolumeCluster";
                Description              = "Volume Clusters (Order Flow+) con soporte/resistencia; activación inmediata opcional.";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;

                PorcentajeConcentrado    = 25;
                TicksAlrededor           = 6;
                TimeFrameMin             = 5;
                MinVelasConfirmacion     = 0;   // activación inmediata
                MargenTicksBorre         = 6;   // v4: margen por defecto
            }
            else if (State == State.Configure)
            {
                // Series internas: Volumétrica (TimeFrameMin) + 1m
                bipVol = BarsArray.Length;
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, TimeFrameMin, VolumetricDeltaType.BidAsk, 1);

                bipM1 = BarsArray.Length;
                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                volBT         = BarsArray[bipVol].BarsType as VolumetricBarsType;
                nivelExpuesto = new Series<double>(this); // v4
            }
        }
        #endregion

        #region Lógica principal
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
                ResetPorNuevaSesion();

            // v4: publicar siempre el último nivel activo/dibujado en la serie (en la serie principal)
            if (BarsInProgress == 0)
                nivelExpuesto[0] = ultimoNivelActual;

            // 1) Dibujo pendiente en serie principal (BIP 0)
            if (BarsInProgress == 0 && drawQueue.Count > 0)
            {
                for (int j = drawQueue.Count - 1; j >= 0; j--)
                {
                    var cz = drawQueue[j];
                    drawQueue.RemoveAt(j);

                    // Si ya no está activa (pudo invalidarse antes de dibujar)
                    if (!IsActiveTag(cz.Tag))
                    {
                        RemoveDrawObject(cz.Tag);
                        RemoveDrawObject(cz.Tag + "_lbl");     // v4
                        continue;
                    }

                    // Dibujo horizontal garantizado + etiqueta "VolCluster"
                    DibujarLineaHorizontalEnPrincipal(cz);
                }
            }

            // 2) Detección de clusters (serie volumétrica)
            if (BarsInProgress == bipVol)
            {
                if (volBT == null || CurrentBars[bipVol] < 0)
                    return;

                DetectarClusterEnBarraVolumetrica();
            }

            // 3) Confirmación / invalidación (serie 1m)
            if (BarsInProgress == bipM1)
            {
                if (CurrentBars[bipM1] < 0)
                    return;

                ProcesarConfirmacionesYVigencias();
            }
        }

        private void DetectarClusterEnBarraVolumetrica()
        {
            int barIndex = CurrentBars[bipVol];
            var volRow   = volBT.Volumes[barIndex];

            long barTotalVol = volRow.TotalVolume;
            if (barTotalVol <= 0)
                return;

            double high = Highs[bipVol][0];
            double low  = Lows[bipVol][0];

            int niveles = Math.Max(1, (int)Math.Round((high - low) / TickSize)) + 1;
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
                    double price = Instrument.MasterInstrument.RoundToTickSize(high - (start + k) * TickSize);
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

            double rangoHigh = Instrument.MasterInstrument.RoundToTickSize(high - mejorInicio * TickSize);
            double rangoLow  = Instrument.MasterInstrument.RoundToTickSize(rangoHigh - (ventana - 1) * TickSize);

            // Nivel = precio con mayor volumen dentro del rango
            long   maxV  = -1;
            double nivel = (rangoHigh + rangoLow) * 0.5; // fallback
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
                FormationTime    = Times[bipVol][0],
                Level            = nivel,
                RangeHigh        = rangoHigh,
                RangeLow         = rangoLow,
                ClusterVolume    = mejorSuma,
                BarTotalVolume   = barTotalVol,
                Active           = false,
                IsSupport        = false,
                Tag              = $"a2vc_{Times[bipVol][0]:yyyyMMdd_HHmmss}_{Math.Round(nivel / TickSize)}",
                M1ClosesSeen     = 0,
                ActivationTime   = DateTime.MinValue,
                InvalidationTime = DateTime.MinValue
            };

            // Activación inmediata o diferida
            if (MinVelasConfirmacion == 0)
            {
                double volClose = Closes[bipVol][0];
                if (volClose > cz.Level)
                {
                    cz.IsSupport      = true;
                    cz.Active         = true;
                    cz.ActivationTime = cz.FormationTime;
                    active.Add(cz);
                    drawQueue.Add(cz); // se pintará en BIP 0
                    ultimoNivelActual = cz.Level;   // v4
                }
                else if (volClose < cz.Level)
                {
                    cz.IsSupport      = false;
                    cz.Active         = true;
                    cz.ActivationTime = cz.FormationTime;
                    active.Add(cz);
                    drawQueue.Add(cz);
                    ultimoNivelActual = cz.Level;   // v4
                }
                // Si cierra exactamente en el nivel, no activamos
            }
            else
            {
                pending.Add(cz);
            }
        }

        private void ProcesarConfirmacionesYVigencias()
        {
            DateTime m1CloseTime = Times[bipM1][0];
            double   m1Close     = Closes[bipM1][0];
            double   m1High      = Highs[bipM1][0];
            double   m1Low       = Lows[bipM1][0];

            if (MinVelasConfirmacion > 0)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var cz = pending[i];

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
                            active.Add(cz);
                            drawQueue.Add(cz);
                            ultimoNivelActual = cz.Level;   // v4
                        }
                        else if (m1Close < cz.Level)
                        {
                            cz.IsSupport      = false;
                            cz.Active         = true;
                            cz.ActivationTime = m1CloseTime;
                            active.Add(cz);
                            drawQueue.Add(cz);
                            ultimoNivelActual = cz.Level;   // v4
                        }
                        pending.RemoveAt(i);
                    }
                }
            }

            // Invalidación por margen
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var cz = active[i];

                double invalidateBelow = Instrument.MasterInstrument.RoundToTickSize(cz.Level - MargenTicksBorre * TickSize);
                double invalidateAbove = Instrument.MasterInstrument.RoundToTickSize(cz.Level + MargenTicksBorre * TickSize);

                bool invalidaSoporte     = cz.IsSupport  && m1Low  <= invalidateBelow;
                bool invalidaResistencia = !cz.IsSupport && m1High >= invalidateAbove;

                if (invalidaSoporte || invalidaResistencia)
                {
                    cz.InvalidationTime = m1CloseTime;
                    RemoveFromDrawQueue(cz.Tag);
                    ConvertirEnHistorico(cz);
                    active.RemoveAt(i);
                    RefreshUltimoNivel();              // v4
                }
            }
        }

        // --- PINTA SIEMPRE EN LA SERIE PRINCIPAL (BIP 0) Y EN HORIZONTAL ---
        private void DibujarLineaHorizontalEnPrincipal(ClusterZone cz)
        {
            Brush color = cz.IsSupport ? soporteBrush : resistenciaBrush;

            // Mapea el tiempo del cluster al índice de la serie principal
            int startBarsAgo = BarsAgoFromTimeOnPrimary(cz.FormationTime);
            if (startBarsAgo <= 0) startBarsAgo = 1;      // asegura diferencia en X
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;
            startBarsAgo = Math.Min(startBarsAgo, primaryCurrentBar);
            int endBarsAgo = Math.Max(0, startBarsAgo - 1);

            RemoveDrawObject(cz.Tag);
            RemoveDrawObject(cz.Tag + "_lbl");
            RemoveDrawObject(cz.Tag + "_hist");
            RemoveDrawObject(cz.Tag + "_lbl_hist");

            // MISMO Y en ambos anclajes => HORIZONTAL. Ray se extiende hacia la derecha.
            var ray = Draw.Ray(this, cz.Tag, startBarsAgo, cz.Level, endBarsAgo, cz.Level, color);

            // v4: Etiqueta "VolCluster" encima de la línea (anclada al inicio) — overload simple (compat)
            double yLabel = Instrument.MasterInstrument.RoundToTickSize(cz.Level + 2 * TickSize);
            Draw.Text(this, cz.Tag + "_lbl", "VolCluster", startBarsAgo, yLabel, color);

            // Estilo punteado / grosor si tu NT lo soporta (reflexión; no rompe si no existe)
            TryStyleRayWithReflection(ray, color, grosorLineaFijo);
        }

        private void ConvertirEnHistorico(ClusterZone cz)
        {
            Brush color = cz.IsSupport ? soporteBrush : resistenciaBrush;

            RemoveDrawObject(cz.Tag);
            RemoveDrawObject(cz.Tag + "_lbl");
            RemoveDrawObject(cz.Tag + "_hist");
            RemoveDrawObject(cz.Tag + "_lbl_hist");

            int startBarsAgo = BarsAgoFromTimeOnPrimary(cz.FormationTime);
            if (startBarsAgo <= 0) startBarsAgo = 1;
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;
            startBarsAgo = Math.Min(startBarsAgo, primaryCurrentBar);
            int endBarsAgo = BarsAgoFromTimeOnPrimary(cz.InvalidationTime);
            if (endBarsAgo < 0) endBarsAgo = 0;
            endBarsAgo = Math.Min(endBarsAgo, primaryCurrentBar);

            var line = Draw.Line(this, cz.Tag + "_hist", startBarsAgo, cz.Level, endBarsAgo, cz.Level, color);
            double yLabel = Instrument.MasterInstrument.RoundToTickSize(cz.Level + 2 * TickSize);
            Draw.Text(this, cz.Tag + "_lbl_hist", "VolCluster", startBarsAgo, yLabel, color);
            TryStyleRayWithReflection(line, color, grosorLineaFijo);

            if (!historical.Contains(cz))
                historical.Add(cz);
        }

        private void TryStyleRayWithReflection(object ray, Brush color, int width)
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
                // Silencioso: si no existen esas APIs en tu versión, queda línea sólida por defecto.
            }
        }

        private int BarsAgoFromTimeOnPrimary(DateTime time)
        {
            int primaryCurrentBar = CurrentBars[0];
            if (primaryCurrentBar < 0)
                primaryCurrentBar = 0;

            int idxOnPrimary = Bars.GetBar(time);
            if (idxOnPrimary < 0)
                return primaryCurrentBar; // fallback: desde el extremo izquierdo disponible

            int barsAgo = primaryCurrentBar - idxOnPrimary;
            if (barsAgo < 0)
                barsAgo = 0;
            if (barsAgo > primaryCurrentBar)
                barsAgo = primaryCurrentBar;
            return barsAgo;
        }

        // --- utilidades v4 ---
        private void RefreshUltimoNivel()
        {
            ultimoNivelActual = (active.Count > 0) ? active[active.Count - 1].Level : double.NaN;
        }

        private void RemoveFromDrawQueue(string tag)
        {
            for (int j = drawQueue.Count - 1; j >= 0; j--)
                if (drawQueue[j].Tag == tag) drawQueue.RemoveAt(j);
        }

        private void RemoveActiveByTag(string tag)
        {
            for (int i = active.Count - 1; i >= 0; i--)
                if (active[i].Tag == tag) active.RemoveAt(i);
            RefreshUltimoNivel(); // v4: mantener consistencia del valor expuesto
        }

        private bool IsActiveTag(string tag)
        {
            for (int i = 0; i < active.Count; i++)
                if (active[i].Tag == tag) return true;
            return false;
        }

        private void ResetPorNuevaSesion()
        {
            var tags = new HashSet<string>();
            foreach (var cz in pending)
                tags.Add(cz.Tag);
            foreach (var cz in active)
                tags.Add(cz.Tag);
            foreach (var cz in drawQueue)
                tags.Add(cz.Tag);
            foreach (var cz in historical)
            {
                tags.Add(cz.Tag);
                tags.Add(cz.Tag + "_hist");
                tags.Add(cz.Tag + "_lbl_hist");
            }

            foreach (var tag in tags)
            {
                RemoveDrawObject(tag);
                RemoveDrawObject(tag + "_lbl");
                RemoveDrawObject(tag + "_hist");
                RemoveDrawObject(tag + "_lbl_hist");
            }

            pending.Clear();
            active.Clear();
            drawQueue.Clear();
            historical.Clear();

            ultimoNivelActual = double.NaN;
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
{
private a2VolumeCluster[] cachea2VolumeCluster;
public a2VolumeCluster a2VolumeCluster(int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
return a2VolumeCluster(Input, porcentajeConcentrado, ticksAlrededor, timeFrameMin, minVelasConfirmacion, margenTicksBorre);
}

public a2VolumeCluster a2VolumeCluster(ISeries<double> input, int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
if (cachea2VolumeCluster != null)
for (int idx = 0; idx < cachea2VolumeCluster.Length; idx++)
if (cachea2VolumeCluster[idx] != null && cachea2VolumeCluster[idx].PorcentajeConcentrado == porcentajeConcentrado && cachea2VolumeCluster[idx].TicksAlrededor == ticksAlrededor && cachea2VolumeCluster[idx].TimeFrameMin == timeFrameMin && cachea2VolumeCluster[idx].MinVelasConfirmacion == minVelasConfirmacion && cachea2VolumeCluster[idx].MargenTicksBorre == margenTicksBorre && cachea2VolumeCluster[idx].EqualsInput(input))
return cachea2VolumeCluster[idx];
return CacheIndicator<a2VolumeCluster>(new a2VolumeCluster(){ PorcentajeConcentrado = porcentajeConcentrado, TicksAlrededor = ticksAlrededor, TimeFrameMin = timeFrameMin, MinVelasConfirmacion = minVelasConfirmacion, MargenTicksBorre = margenTicksBorre }, input, ref cachea2VolumeCluster);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.a2VolumeCluster a2VolumeCluster(int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
return indicator.a2VolumeCluster(Input, porcentajeConcentrado, ticksAlrededor, timeFrameMin, minVelasConfirmacion, margenTicksBorre);
}

public Indicators.a2VolumeCluster a2VolumeCluster(ISeries<double> input , int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
return indicator.a2VolumeCluster(input, porcentajeConcentrado, ticksAlrededor, timeFrameMin, minVelasConfirmacion, margenTicksBorre);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.a2VolumeCluster a2VolumeCluster(int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
return indicator.a2VolumeCluster(Input, porcentajeConcentrado, ticksAlrededor, timeFrameMin, minVelasConfirmacion, margenTicksBorre);
}

public Indicators.a2VolumeCluster a2VolumeCluster(ISeries<double> input , int porcentajeConcentrado, int ticksAlrededor, int timeFrameMin, int minVelasConfirmacion, int margenTicksBorre)
{
return indicator.a2VolumeCluster(input, porcentajeConcentrado, ticksAlrededor, timeFrameMin, minVelasConfirmacion, margenTicksBorre);
}
}
}

#endregion
