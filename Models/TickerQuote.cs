using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace 币安量化机器人.Models;

public class TickerQuote : INotifyPropertyChanged
{
    private double _lastPrice;
    private double _indexPrice;
    private double _changePercent;
    private double _volume;
    private double _highPrice;
    private double _lowPrice;

    private readonly Queue<double> _priceHistory = new();
    private readonly int _historyCapacity;

    public TickerQuote(string symbol, double lastPrice, double indexPrice, double changePercent,
        double volume, double highPrice, double lowPrice, int historyCapacity = 180)
    {
        Symbol = symbol;
        _lastPrice = lastPrice;
        _indexPrice = indexPrice;
        _changePercent = changePercent;
        _volume = volume;
        _highPrice = highPrice;
        _lowPrice = lowPrice;
        _historyCapacity = historyCapacity;

        AddPricePoint(lastPrice);
    }

    public string Symbol { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double LastPrice
    {
        get => _lastPrice;
        set
        {
            if (Math.Abs(_lastPrice - value) < 1e-9) return;
            _lastPrice = value;
            OnPropertyChanged(nameof(LastPrice));
        }
    }

    public double IndexPrice
    {
        get => _indexPrice;
        set
        {
            if (Math.Abs(_indexPrice - value) < 1e-9) return;
            _indexPrice = value;
            OnPropertyChanged(nameof(IndexPrice));
        }
    }

    public double ChangePercent
    {
        get => _changePercent;
        set
        {
            if (Math.Abs(_changePercent - value) < 1e-9) return;
            _changePercent = value;
            OnPropertyChanged(nameof(ChangePercent));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (Math.Abs(_volume - value) < 1e-9) return;
            _volume = value;
            OnPropertyChanged(nameof(Volume));
        }
    }

    public double HighPrice
    {
        get => _highPrice;
        set
        {
            if (Math.Abs(_highPrice - value) < 1e-9) return;
            _highPrice = value;
            OnPropertyChanged(nameof(HighPrice));
        }
    }

    public double LowPrice
    {
        get => _lowPrice;
        set
        {
            if (Math.Abs(_lowPrice - value) < 1e-9) return;
            _lowPrice = value;
            OnPropertyChanged(nameof(LowPrice));
        }
    }

    public IReadOnlyList<double> PriceHistory => _priceHistory.ToArray();

    public void AddPricePoint(double price)
    {
        _priceHistory.Enqueue(price);
        while (_priceHistory.Count > _historyCapacity)
            _priceHistory.Dequeue();
    }

    protected virtual void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public double Basis => LastPrice - IndexPrice;
    public double BasisPct => IndexPrice.Equals(0) ? 0 : Basis / IndexPrice;

    public double MidPrice => (HighPrice + LowPrice) / 2.0;

    public double RangePercent => MidPrice.Equals(0) ? 0 : (HighPrice - LowPrice) / MidPrice;
}
