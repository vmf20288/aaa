// a2avwap.cs - Indicador de doble Anchored VWAP con bandas ±1σ y ±2σ
// Cálculo sencillo y estable para NinjaTrader 8.
// - Anchored 1 y Anchored 2 independientes.
// - Cada uno con fecha, hora, bandas 1 y 2.
// - ShowAnchoredVwap apaga/enciende todo el indicador.

#region Using declarations
using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2avwap : Indicator
    {
        // --- Sumas para módulo 1 ---
        private double a1SumPV;    // Σ (precio * volumen)
        private double a1SumV;     // Σ volumen
        private double a1SumP2V;   // Σ (precio^2 * volumen)
        private bool   a1Active;

        // --- Sumas para módulo 2 ---
        private double a2SumPV;
        private double a2SumV;
        private double a2SumP2V;
        private bool   a2Active;

        // --- Métodos auxiliares de fecha/hora ---
        private DateTime BuildAnchorDateTime(DateTime date, string timeText)
        {
            if (string.IsNullOrEmpty(timeText))
                timeText = "00:00";

            TimeSpan ts;
            if (!TimeSpan.TryParseExact(timeText, "hh\\:mm", CultureInfo.InvariantCulture, out ts) &&
                !TimeSpan.TryParseExact(timeText, "HH\\:mm", CultureInfo.InvariantCulture, out ts))
            {
                ts = TimeSpan.Zero;
            }

            return date.Date + ts;
        }

        private void ResetAnchor1()
        {
            a1SumPV  = 0;
            a1SumV   = 0;
            a1SumP2V = 0;
            a1Active = false;
        }

        private void ResetAnchor2()
        {
            a2SumPV  = 0;
            a2SumV   = 0;
            a2SumP2V = 0;
            a2Active = false;
        }

        private void SetNaNAnchor1()
        {
            // Plots 0-4 pertenecen a Anchored 1
            for (int i = 0; i <= 4; i++)
                Values[i][0] = double.NaN;
        }

        private void SetNaNAnchor2()
        {
            // Plots 5-9 pertenecen a Anchored 2
            for (int i = 5; i <= 9; i++)
                Values[i][0] = double.NaN;
        }

        private void SetNaNAll()
        {
            for (int i = 0; i < Values.Length; i++)
                Values[i][0] = double.NaN;
        }

        // ---------------------------------------------------------
        // STATE
        // ---------------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a2avwap";
                Calculate               = Calculate.OnBarClose;   // VWAP por cierre de vela
                IsOverlay               = true;
                IsSuspendedWhileInactive = true;

                // --- Parámetros Globales ---
                ShowAnchoredVwap        = true;
                Anchored1               = true;
                Anchored2               = false;

                // --- Parámetros Anchored 1 ---
                Anchor1Date             = DateTime.Today;
                Anchor1Time             = "00:00";
                ShowAnchor1Bands1       = true;
                ShowAnchor1Bands2       = false;

                // --- Parámetros Anchored 2 ---
                Anchor2Date             = DateTime.Today;
                Anchor2Time             = "00:00";
                ShowAnchor2Bands1       = true;
                ShowAnchor2Bands2       = false;

                // --- Plots ---
                // Anchored 1
                AddPlot(Brushes.Blue,      "AnchoredVWAP1"); // 0
                AddPlot(Brushes.Green,     "Anch1 +1σ");     // 1
                AddPlot(Brushes.Green,     "Anch1 -1σ");     // 2
                AddPlot(Brushes.DarkGreen, "Anch1 +2σ");     // 3
                AddPlot(Brushes.DarkGreen, "Anch1 -2σ");     // 4

                // Anchored 2
                AddPlot(Brushes.SteelBlue, "AnchoredVWAP2"); // 5
                AddPlot(Brushes.ForestGreen, "Anch2 +1σ");   // 6
                AddPlot(Brushes.ForestGreen, "Anch2 -1σ");   // 7
                AddPlot(Brushes.OliveDrab,   "Anch2 +2σ");   // 8
                AddPlot(Brushes.OliveDrab,   "Anch2 -2σ");   // 9
            }
            else if (State == State.DataLoaded)
            {
                ResetAnchor1();
                ResetAnchor2();
            }
        }

        // ---------------------------------------------------------
        // LÓGICA PRINCIPAL
        // ---------------------------------------------------------
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0)
                return;

            // Si el usuario apaga todo el indicador
            if (!ShowAnchoredVwap)
            {
                SetNaNAll();
                ResetAnchor1();
                ResetAnchor2();
                return;
            }

            DateTime barTime    = Time[0];
            DateTime anchor1DT  = BuildAnchorDateTime(Anchor1Date, Anchor1Time);
            DateTime anchor2DT  = BuildAnchorDateTime(Anchor2Date, Anchor2Time);

            double price  = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;   // precio típico
            double volume = Volume[0];

            // -----------------------------------------------------
            // ANCHORED 1
            // -----------------------------------------------------
            if (!Anchored1)
            {
                ResetAnchor1();
                SetNaNAnchor1();
            }
            else
            {
                // Activar ancla cuando se alcanza la fecha/hora
                if (!a1Active && barTime >= anchor1DT)
                {
                    ResetAnchor1();
                    a1Active = true;
                }

                if (!a1Active)
                {
                    SetNaNAnchor1();
                }
                else
                {
                    if (volume > 0)
                    {
                        a1SumPV  += price * volume;
                        a1SumV   += volume;
                        a1SumP2V += price * price * volume;
                    }

                    if (a1SumV > 0)
                    {
                        double vwap1    = a1SumPV / a1SumV;
                        double variance = (a1SumP2V / a1SumV) - vwap1 * vwap1;
                        if (variance < 0)
                            variance = 0;
                        double sigma    = Math.Sqrt(variance);

                        // VWAP
                        Values[0][0] = vwap1;

                        // Bandas ±1σ
                        if (ShowAnchor1Bands1)
                        {
                            Values[1][0] = vwap1 + sigma;
                            Values[2][0] = vwap1 - sigma;
                        }
                        else
                        {
                            Values[1][0] = double.NaN;
                            Values[2][0] = double.NaN;
                        }

                        // Bandas ±2σ
                        if (ShowAnchor1Bands2)
                        {
                            Values[3][0] = vwap1 + 2.0 * sigma;
                            Values[4][0] = vwap1 - 2.0 * sigma;
                        }
                        else
                        {
                            Values[3][0] = double.NaN;
                            Values[4][0] = double.NaN;
                        }
                    }
                    else
                    {
                        SetNaNAnchor1();
                    }
                }
            }

            // -----------------------------------------------------
            // ANCHORED 2
            // -----------------------------------------------------
            if (!Anchored2)
            {
                ResetAnchor2();
                SetNaNAnchor2();
            }
            else
            {
                // Activar ancla cuando se alcanza la fecha/hora
                if (!a2Active && barTime >= anchor2DT)
                {
                    ResetAnchor2();
                    a2Active = true;
                }

                if (!a2Active)
                {
                    SetNaNAnchor2();
                }
                else
                {
                    if (volume > 0)
                    {
                        a2SumPV  += price * volume;
                        a2SumV   += volume;
                        a2SumP2V += price * price * volume;
                    }

                    if (a2SumV > 0)
                    {
                        double vwap2    = a2SumPV / a2SumV;
                        double variance = (a2SumP2V / a2SumV) - vwap2 * vwap2;
                        if (variance < 0)
                            variance = 0;
                        double sigma    = Math.Sqrt(variance);

                        // VWAP
                        Values[5][0] = vwap2;

                        // Bandas ±1σ
                        if (ShowAnchor2Bands1)
                        {
                            Values[6][0] = vwap2 + sigma;
                            Values[7][0] = vwap2 - sigma;
                        }
                        else
                        {
                            Values[6][0] = double.NaN;
                            Values[7][0] = double.NaN;
                        }

                        // Bandas ±2σ
                        if (ShowAnchor2Bands2)
                        {
                            Values[8][0] = vwap2 + 2.0 * sigma;
                            Values[9][0] = vwap2 - 2.0 * sigma;
                        }
                        else
                        {
                            Values[8][0] = double.NaN;
                            Values[9][0] = double.NaN;
                        }
                    }
                    else
                    {
                        SetNaNAnchor2();
                    }
                }
            }
        }

        // ---------------------------------------------------------
        // PROPIEDADES (aparecen en el panel de parámetros)
        // ---------------------------------------------------------

        // --- GLOBAL ---
        [NinjaScriptProperty]
        [DisplayName("Show Anchored VWAP")]
        [Description("Muestra u oculta todo el indicador.")]
        public bool ShowAnchoredVwap { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Anchored 1")]
        [Description("Activa el primer VWAP anclado.")]
        public bool Anchored1 { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Anchored 2")]
        [Description("Activa el segundo VWAP anclado.")]
        public bool Anchored2 { get; set; }

        // --- ANCHORED VWAP 1 ---
        [NinjaScriptProperty]
        [DisplayName("Anchor 1 Date")]
        public DateTime Anchor1Date { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Anchor 1 Time (HH:mm)")]
        public string Anchor1Time { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Show Bands 1 ±1σ (A1)")]
        public bool ShowAnchor1Bands1 { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Show Bands 2 ±2σ (A1)")]
        public bool ShowAnchor1Bands2 { get; set; }

        // --- ANCHORED VWAP 2 ---
        [NinjaScriptProperty]
        [DisplayName("Anchor 2 Date")]
        public DateTime Anchor2Date { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Anchor 2 Time (HH:mm)")]
        public string Anchor2Time { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Show Bands 1 ±1σ (A2)")]
        public bool ShowAnchor2Bands1 { get; set; }

        [NinjaScriptProperty]
        [DisplayName("Show Bands 2 ±2σ (A2)")]
        public bool ShowAnchor2Bands2 { get; set; }
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
