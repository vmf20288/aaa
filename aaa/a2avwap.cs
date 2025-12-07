// a2avwap.cs - Anchored VWAP doble con bandas y anchors arrastrables
// - Dos módulos independientes (Anchored 1 y Anchored 2)
// - Cada módulo tiene fecha/hora de anclaje, bandas ±1σ y ±2σ
// - Línea vertical en el punto de anclaje, que se puede mover con el ratón
// - Cálculo en OnBarClose para reducir carga

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2avwap : Indicator
    {
        // --- Acumuladores módulo 1 ---
        private double anchor1SumPV;
        private double anchor1SumV;
        private double anchor1SumP2V;
        private bool   anchor1Active;

        // --- Acumuladores módulo 2 ---
        private double anchor2SumPV;
        private double anchor2SumV;
        private double anchor2SumP2V;
        private bool   anchor2Active;

        // DateTime interno efectivo (propiedades + drag)
        private DateTime anchor1DateTime;
        private DateTime anchor2DateTime;

        // Líneas verticales (para drag con el ratón)
        private VerticalLine anchor1Line;
        private VerticalLine anchor2Line;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a2avwap";
                Calculate               = Calculate.OnBarClose;   // más ligero
                IsOverlay               = true;
                IsSuspendedWhileInactive = true;

                // --- GLOBAL ---
                ShowAnchoredVwap        = true;
                Anchored1               = true;
                Anchored2               = false;

                // --- Anchored VWAP 1 ---
                Anchor1Date             = DateTime.Today;
                Anchor1Time             = "00:00";
                ShowAnchor1Bands1       = true;
                ShowAnchor1Bands2       = false;

                // --- Anchored VWAP 2 ---
                Anchor2Date             = DateTime.Today;
                Anchor2Time             = "00:00";
                ShowAnchor2Bands1       = true;
                ShowAnchor2Bands2       = false;

                // Colores por defecto
                Anchor1VwapBrush        = Brushes.Blue;
                Anchor1Band1Brush       = Brushes.Green;
                Anchor1Band2Brush       = Brushes.Green;

                Anchor2VwapBrush        = Brushes.Blue;
                Anchor2Band1Brush       = Brushes.Green;
                Anchor2Band2Brush       = Brushes.Green;

                // Plots módulo 1
                AddPlot(Brushes.Blue,  "AnchoredVWAP1");   // 0
                AddPlot(Brushes.Green, "Anch1+1");         // 1
                AddPlot(Brushes.Green, "Anch1-1");         // 2
                AddPlot(Brushes.Green, "Anch1+2");         // 3
                AddPlot(Brushes.Green, "Anch1-2");         // 4

                // Plots módulo 2
                AddPlot(Brushes.Blue,  "AnchoredVWAP2");   // 5
                AddPlot(Brushes.Green, "Anch2+1");         // 6
                AddPlot(Brushes.Green, "Anch2-1");         // 7
                AddPlot(Brushes.Green, "Anch2+2");         // 8
                AddPlot(Brushes.Green, "Anch2-2");         // 9
            }
            else if (State == State.DataLoaded)
            {
                // Construir DateTime inicial desde propiedades
                anchor1DateTime = BuildAnchorDateTime(Anchor1Date, Anchor1Time);
                anchor2DateTime = BuildAnchorDateTime(Anchor2Date, Anchor2Time);

                // Asegurar que los plots usan los colores de las propiedades
                UpdatePlotBrushes();

                ResetAnchor1();
                ResetAnchor2();
            }
            else if (State == State.Terminated)
            {
                // Limpiar las líneas al quitar el indicador
                RemoveDrawObject("a2avwap_Anchor1");
                RemoveDrawObject("a2avwap_Anchor2");
            }
        }

        protected override void OnBarUpdate()
        {
            if (!ShowAnchoredVwap)
            {
                SetAllNan();
                // No dibujar líneas ni acumular cuando está apagado global
                if (ChartControl != null)
                {
                    RemoveDrawObject("a2avwap_Anchor1");
                    RemoveDrawObject("a2avwap_Anchor2");
                }
                return;
            }

            // 1) Detectar si el usuario ha movido las líneas con el ratón
            if (ChartControl != null)
            {
                if (anchor1Line != null && Anchored1)
                {
                    DateTime lineTime = anchor1Line.StartAnchor.Time;
                    if (lineTime != anchor1DateTime)
                    {
                        anchor1DateTime = lineTime;
                        Anchor1Date     = anchor1DateTime.Date;
                        Anchor1Time     = anchor1DateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                        ResetAnchor1();
                    }
                }

                if (anchor2Line != null && Anchored2)
                {
                    DateTime lineTime = anchor2Line.StartAnchor.Time;
                    if (lineTime != anchor2DateTime)
                    {
                        anchor2DateTime = lineTime;
                        Anchor2Date     = anchor2DateTime.Date;
                        Anchor2Time     = anchor2DateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
                        ResetAnchor2();
                    }
                }
            }

            // 2) Detectar cambios en las propiedades (fecha/hora) desde el panel
            DateTime propAnchor1 = BuildAnchorDateTime(Anchor1Date, Anchor1Time);
            if (propAnchor1 != anchor1DateTime)
            {
                anchor1DateTime = propAnchor1;
                ResetAnchor1();
            }

            DateTime propAnchor2 = BuildAnchorDateTime(Anchor2Date, Anchor2Time);
            if (propAnchor2 != anchor2DateTime)
            {
                anchor2DateTime = propAnchor2;
                ResetAnchor2();
            }

            // 3) Dibujar / borrar las líneas verticales de anclaje
            if (ChartControl != null)
            {
                if (Anchored1)
                {
                    anchor1Line = Draw.VerticalLine(this, "a2avwap_Anchor1", anchor1DateTime, Anchor1VwapBrush);
                    if (anchor1Line != null)
                        anchor1Line.IsLocked = false; // para poder arrastrar
                }
                else
                {
                    RemoveDrawObject("a2avwap_Anchor1");
                    anchor1Line = null;
                }

                if (Anchored2)
                {
                    anchor2Line = Draw.VerticalLine(this, "a2avwap_Anchor2", anchor2DateTime, Anchor2VwapBrush);
                    if (anchor2Line != null)
                        anchor2Line.IsLocked = false;
                }
                else
                {
                    RemoveDrawObject("a2avwap_Anchor2");
                    anchor2Line = null;
                }
            }

            // 4) Cálculo del precio medio (típico) y volumen de la barra
            double price  = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double volume = Volume[0];

            // 5) Procesar cada módulo
            ProcessModule(
                ref anchor1Active,
                ref anchor1SumPV,
                ref anchor1SumV,
                ref anchor1SumP2V,
                anchor1DateTime,
                Anchored1,
                ShowAnchor1Bands1,
                ShowAnchor1Bands2,
                0, 1, 2, 3, 4,
                price, volume);

            ProcessModule(
                ref anchor2Active,
                ref anchor2SumPV,
                ref anchor2SumV,
                ref anchor2SumP2V,
                anchor2DateTime,
                Anchored2,
                ShowAnchor2Bands1,
                ShowAnchor2Bands2,
                5, 6, 7, 8, 9,
                price, volume);
        }

        /// <summary>
        /// Lógica común para cada módulo de Anchored VWAP
        /// </summary>
        private void ProcessModule(
            ref bool moduleActive,
            ref double sumPV,
            ref double sumV,
            ref double sumP2V,
            DateTime anchorDateTime,
            bool isEnabled,
            bool showBands1,
            bool showBands2,
            int vwapPlot,
            int plus1Plot,
            int minus1Plot,
            int plus2Plot,
            int minus2Plot,
            double price,
            double volume)
        {
            if (!isEnabled)
            {
                Values[vwapPlot][0]  = double.NaN;
                Values[plus1Plot][0] = double.NaN;
                Values[minus1Plot][0]= double.NaN;
                Values[plus2Plot][0] = double.NaN;
                Values[minus2Plot][0]= double.NaN;
                return;
            }

            // Activar el módulo cuando llegamos al tiempo de anclaje
            if (!moduleActive && Time[0] >= anchorDateTime)
            {
                sumPV    = 0.0;
                sumV     = 0.0;
                sumP2V   = 0.0;
                moduleActive = true;
            }

            if (!moduleActive)
            {
                Values[vwapPlot][0]  = double.NaN;
                Values[plus1Plot][0] = double.NaN;
                Values[minus1Plot][0]= double.NaN;
                Values[plus2Plot][0] = double.NaN;
                Values[minus2Plot][0]= double.NaN;
                return;
            }

            // Acumulación volumen ponderado
            if (volume > 0)
            {
                sumPV  += price * volume;
                sumV   += volume;
                sumP2V += price * price * volume;
            }

            double vwap;
            double stdDev;

            if (sumV == 0)
            {
                vwap   = price;
                stdDev = 0.0;
            }
            else
            {
                vwap = sumPV / sumV;
                double meanP2 = sumP2V / sumV;
                double variance = meanP2 - vwap * vwap;
                if (variance < 0)
                    variance = 0;
                stdDev = Math.Sqrt(variance);
            }

            // VWAP principal
            Values[vwapPlot][0] = vwap;

            // Bandas ±1σ
            if (showBands1)
            {
                Values[plus1Plot][0]  = vwap + stdDev;
                Values[minus1Plot][0] = vwap - stdDev;
            }
            else
            {
                Values[plus1Plot][0]  = double.NaN;
                Values[minus1Plot][0] = double.NaN;
            }

            // Bandas ±2σ
            if (showBands2)
            {
                Values[plus2Plot][0]  = vwap + 2.0 * stdDev;
                Values[minus2Plot][0] = vwap - 2.0 * stdDev;
            }
            else
            {
                Values[plus2Plot][0]  = double.NaN;
                Values[minus2Plot][0] = double.NaN;
            }
        }

        private DateTime BuildAnchorDateTime(DateTime anchorDate, string anchorTime)
        {
            TimeSpan ts;
            if (!TimeSpan.TryParseExact(anchorTime ?? "00:00", "hh\\:mm", CultureInfo.InvariantCulture, out ts))
                ts = TimeSpan.Zero;

            return anchorDate.Date + ts;
        }

        private void ResetAnchor1()
        {
            anchor1SumPV  = 0.0;
            anchor1SumV   = 0.0;
            anchor1SumP2V = 0.0;
            anchor1Active = false;
        }

        private void ResetAnchor2()
        {
            anchor2SumPV  = 0.0;
            anchor2SumV   = 0.0;
            anchor2SumP2V = 0.0;
            anchor2Active = false;
        }

        private void SetAllNan()
        {
            for (int i = 0; i < Values.Length; i++)
                Values[i][0] = double.NaN;
        }

        private void UpdatePlotBrushes()
        {
            if (Plots == null || Plots.Length < 10)
                return;

            // Módulo 1
            Plots[0].Brush = Anchor1VwapBrush;
            Plots[1].Brush = Anchor1Band1Brush;
            Plots[2].Brush = Anchor1Band1Brush;
            Plots[3].Brush = Anchor1Band2Brush;
            Plots[4].Brush = Anchor1Band2Brush;

            // Módulo 2
            Plots[5].Brush = Anchor2VwapBrush;
            Plots[6].Brush = Anchor2Band1Brush;
            Plots[7].Brush = Anchor2Band1Brush;
            Plots[8].Brush = Anchor2Band2Brush;
            Plots[9].Brush = Anchor2Band2Brush;
        }

        private static class Serialize
        {
            private static readonly BrushConverter BrushConverter = new BrushConverter();

            public static string BrushToString(Brush brush)
            {
                return BrushConverter.ConvertToString(brush);
            }

            public static Brush StringToBrush(string value)
            {
                try
                {
                    return (Brush)BrushConverter.ConvertFromString(value);
                }
                catch
                {
                    return null;
                }
            }
        }

        #region Propiedades

        // --- Global ---
        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 0, GroupName = "GLOBAL")]
        public bool ShowAnchoredVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchored 1", Order = 1, GroupName = "GLOBAL")]
        public bool Anchored1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchored 2", Order = 2, GroupName = "GLOBAL")]
        public bool Anchored2 { get; set; }

        // --- Anchored VWAP 1 ---
        [NinjaScriptProperty]
        [Display(Name = "Anchor 1 Date", Order = 0, GroupName = "Anchored VWAP 1")]
        public DateTime Anchor1Date { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 1 Time (HH:mm)", Order = 1, GroupName = "Anchored VWAP 1")]
        public string Anchor1Time { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 1 (±1σ)", Order = 2, GroupName = "Anchored VWAP 1")]
        public bool ShowAnchor1Bands1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 2 (±2σ)", Order = 3, GroupName = "Anchored VWAP 1")]
        public bool ShowAnchor1Bands2 { get; set; }

        // --- Anchored VWAP 2 ---
        [NinjaScriptProperty]
        [Display(Name = "Anchor 2 Date", Order = 0, GroupName = "Anchored VWAP 2")]
        public DateTime Anchor2Date { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor 2 Time (HH:mm)", Order = 1, GroupName = "Anchored VWAP 2")]
        public string Anchor2Time { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 1 (±1σ)", Order = 2, GroupName = "Anchored VWAP 2")]
        public bool ShowAnchor2Bands1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored Bands 2 (±2σ)", Order = 3, GroupName = "Anchored VWAP 2")]
        public bool ShowAnchor2Bands2 { get; set; }

        // --- Colores Anchored 1 ---
        [XmlIgnore]
        [Display(Name = "Anchored VWAP 1 Color", Order = 0, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1VwapBrush { get; set; }

        [Browsable(false)]
        public string Anchor1VwapBrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1VwapBrush); }
            set { Anchor1VwapBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 1 ±1σ Color", Order = 1, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1Band1Brush { get; set; }

        [Browsable(false)]
        public string Anchor1Band1BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1Band1Brush); }
            set { Anchor1Band1Brush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 1 ±2σ Color", Order = 2, GroupName = "Anchored VWAP 1 Colors")]
        public Brush Anchor1Band2Brush { get; set; }

        [Browsable(false)]
        public string Anchor1Band2BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor1Band2Brush); }
            set { Anchor1Band2Brush = Serialize.StringToBrush(value); }
        }

        // --- Colores Anchored 2 ---
        [XmlIgnore]
        [Display(Name = "Anchored VWAP 2 Color", Order = 0, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2VwapBrush { get; set; }

        [Browsable(false)]
        public string Anchor2VwapBrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2VwapBrush); }
            set { Anchor2VwapBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 2 ±1σ Color", Order = 1, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2Band1Brush { get; set; }

        [Browsable(false)]
        public string Anchor2Band1BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2Band1Brush); }
            set { Anchor2Band1Brush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Anch 2 ±2σ Color", Order = 2, GroupName = "Anchored VWAP 2 Colors")]
        public Brush Anchor2Band2Brush { get; set; }

        [Browsable(false)]
        public string Anchor2Band2BrushSerializable
        {
            get { return Serialize.BrushToString(Anchor2Band2Brush); }
            set { Anchor2Band2Brush = Serialize.StringToBrush(value); }
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a2avwap[] cachea2avwap;
		public a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			return a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
		}

		public a2avwap a2avwap(ISeries<double> input, bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			if (cachea2avwap != null)
				for (int idx = 0; idx < cachea2avwap.Length; idx++)
					if (cachea2avwap[idx] != null && cachea2avwap[idx].ShowAnchoredVwap == showAnchoredVwap && cachea2avwap[idx].Anchored1 == anchored1 && cachea2avwap[idx].Anchored2 == anchored2 && cachea2avwap[idx].Anchor1Date == anchor1Date && cachea2avwap[idx].Anchor1Time == anchor1Time && cachea2avwap[idx].ShowAnchor1Bands1 == showAnchor1Bands1 && cachea2avwap[idx].ShowAnchor1Bands2 == showAnchor1Bands2 && cachea2avwap[idx].Anchor2Date == anchor2Date && cachea2avwap[idx].Anchor2Time == anchor2Time && cachea2avwap[idx].ShowAnchor2Bands1 == showAnchor2Bands1 && cachea2avwap[idx].ShowAnchor2Bands2 == showAnchor2Bands2 && cachea2avwap[idx].EqualsInput(input))
						return cachea2avwap[idx];
			return CacheIndicator<a2avwap>(new a2avwap(){ ShowAnchoredVwap = showAnchoredVwap, Anchored1 = anchored1, Anchored2 = anchored2, Anchor1Date = anchor1Date, Anchor1Time = anchor1Time, ShowAnchor1Bands1 = showAnchor1Bands1, ShowAnchor1Bands2 = showAnchor1Bands2, Anchor2Date = anchor2Date, Anchor2Time = anchor2Time, ShowAnchor2Bands1 = showAnchor2Bands1, ShowAnchor2Bands2 = showAnchor2Bands2 }, input, ref cachea2avwap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			return indicator.a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
		}

		public Indicators.a2avwap a2avwap(ISeries<double> input , bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			return indicator.a2avwap(input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a2avwap a2avwap(bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			return indicator.a2avwap(Input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
		}

		public Indicators.a2avwap a2avwap(ISeries<double> input , bool showAnchoredVwap, bool anchored1, bool anchored2, DateTime anchor1Date, string anchor1Time, bool showAnchor1Bands1, bool showAnchor1Bands2, DateTime anchor2Date, string anchor2Time, bool showAnchor2Bands1, bool showAnchor2Bands2)
		{
			return indicator.a2avwap(input, showAnchoredVwap, anchored1, anchored2, anchor1Date, anchor1Time, showAnchor1Bands1, showAnchor1Bands2, anchor2Date, anchor2Time, showAnchor2Bands1, showAnchor2Bands2);
		}
	}
}

#endregion
