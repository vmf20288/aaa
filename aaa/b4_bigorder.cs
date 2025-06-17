#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Windows;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// b4_bigorder.cs - Marks large trades on the chart for NinjaTrader 8.1.5.1

namespace NinjaTrader.NinjaScript.Indicators
{
    public class b4_bigorder : Indicator
    {
        private double lastTradePrice;
        private int    lastDirection;

        [Range(1, int.MaxValue)]
        [Display(Name = "Min Trade Size", Order = 0, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public int MinTradeSize { get; set; } = 15;

        [Range(1, int.MaxValue)]
        [Display(Name = "Font Size", Order = 1, GroupName = "Parameters")]
        [NinjaScriptProperty]
        public int FontSize { get; set; } = 16;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Marks trades bigger than a specified size.";
                Name        = "b4_bigorder";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            if (e.Volume < MinTradeSize)
                return;

            double price = e.Price;
            int sign;
            if (price > lastTradePrice)      sign = 1;
            else if (price < lastTradePrice) sign = -1;
            else                              sign = lastDirection;

            bool isAsk = sign > 0;
            bool isBid = sign < 0;

            if (sign != 0)
                lastDirection = sign;
            lastTradePrice = price;

            string tag = $"BO_{CurrentBar}_{e.Time.Ticks}";

            Draw.Text(this, tag, false, e.Volume.ToString(), 0, e.Price, 0,
                      Brushes.Black, new SimpleFont("Arial", FontSize),
                      isBid ? TextAlignment.Left : TextAlignment.Right,
                      Brushes.Transparent, Brushes.Transparent, 0);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private b4_bigorder[] cacheb4_bigorder;
        public b4_bigorder b4_bigorder(int minTradeSize, int fontSize)
        {
            return b4_bigorder(Input, minTradeSize, fontSize);
        }

        public b4_bigorder b4_bigorder(ISeries<double> input, int minTradeSize, int fontSize)
        {
            if (cacheb4_bigorder != null)
                for (int idx = 0; idx < cacheb4_bigorder.Length; idx++)
                    if (cacheb4_bigorder[idx] != null && cacheb4_bigorder[idx].MinTradeSize == minTradeSize && cacheb4_bigorder[idx].FontSize == fontSize && cacheb4_bigorder[idx].EqualsInput(input))
                        return cacheb4_bigorder[idx];
            return CacheIndicator<b4_bigorder>(new b4_bigorder(){ MinTradeSize = minTradeSize, FontSize = fontSize }, input, ref cacheb4_bigorder);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.b4_bigorder b4_bigorder(int minTradeSize, int fontSize)
        {
            return indicator.b4_bigorder(Input, minTradeSize, fontSize);
        }

        public Indicators.b4_bigorder b4_bigorder(ISeries<double> input , int minTradeSize, int fontSize)
        {
            return indicator.b4_bigorder(input, minTradeSize, fontSize);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.b4_bigorder b4_bigorder(int minTradeSize, int fontSize)
        {
            return indicator.b4_bigorder(Input, minTradeSize, fontSize);
        }

        public Indicators.b4_bigorder b4_bigorder(ISeries<double> input , int minTradeSize, int fontSize)
        {
            return indicator.b4_bigorder(input, minTradeSize, fontSize);
        }
    }
}

#endregion
