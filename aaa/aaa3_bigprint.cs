#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Windows;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// aaa3_bigprint.cs - Big print indicator updated for NinjaTrader 8.1.5
// Derived from original a15 indicator for NinjaTrader 8.1.4

namespace NinjaTrader.NinjaScript.Indicators
{
    public class aaa3_bigprint : Indicator
    {
        private Brush buyBrush;
        private Brush sellBrush;

        [Range(1, int.MaxValue)]
        [Display(Name = "Minimum Volume", Order = 0, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public int MinimumVolume { get; set; } = 100;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Highlights large prints on the chart.";
                Name = "aaa3_bigprint";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
            }
            else if (State == State.DataLoaded)
            {
                buyBrush = Brushes.Lime;
                sellBrush = Brushes.Red;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            if (e.Volume < MinimumVolume)
                return;

            string tagBase = "BP" + CurrentBar + "_" + CurrentBar + "_" + Bars.TickCount;
            Brush brush = e.Price >= Close[0] ? buyBrush : sellBrush;

            Draw.Dot(this, tagBase, false, 0, e.Price, brush);
            Draw.Text(this, tagBase + "T", false, e.Volume.ToString(), 0, e.Price, 0, brush, new SimpleFont("Arial", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private aaa3_bigprint[] cacheaaa3_bigprint;
        public aaa3_bigprint aaa3_bigprint(int minimumVolume)
        {
            return aaa3_bigprint(Input, minimumVolume);
        }

        public aaa3_bigprint aaa3_bigprint(ISeries<double> input, int minimumVolume)
        {
            if (cacheaaa3_bigprint != null)
                for (int idx = 0; idx < cacheaaa3_bigprint.Length; idx++)
                    if (cacheaaa3_bigprint[idx] != null && cacheaaa3_bigprint[idx].MinimumVolume == minimumVolume && cacheaaa3_bigprint[idx].EqualsInput(input))
                        return cacheaaa3_bigprint[idx];
            return CacheIndicator<aaa3_bigprint>(new aaa3_bigprint(){ MinimumVolume = minimumVolume }, input, ref cacheaaa3_bigprint);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.aaa3_bigprint aaa3_bigprint(int minimumVolume)
        {
            return indicator.aaa3_bigprint(Input, minimumVolume);
        }

        public Indicators.aaa3_bigprint aaa3_bigprint(ISeries<double> input , int minimumVolume)
        {
            return indicator.aaa3_bigprint(input, minimumVolume);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.aaa3_bigprint aaa3_bigprint(int minimumVolume)
        {
            return indicator.aaa3_bigprint(Input, minimumVolume);
        }

        public Indicators.aaa3_bigprint aaa3_bigprint(ISeries<double> input , int minimumVolume)
        {
            return indicator.aaa3_bigprint(input, minimumVolume);
        }
    }
}

#endregion
