using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace 币安量化机器人.Controls;

public sealed class RadialGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(double),typeof(RadialGauge),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender,Changed));
    private static readonly DependencyProperty DisplayValueProperty=DependencyProperty.Register(nameof(DisplayValue),typeof(double),typeof(RadialGauge),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty=DependencyProperty.Register(nameof(Caption),typeof(string),typeof(RadialGauge),new FrameworkPropertyMetadata("METRIC",FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty=DependencyProperty.Register(nameof(Accent),typeof(Brush),typeof(RadialGauge),new FrameworkPropertyMetadata(Brushes.Cyan,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value{get=>(double)GetValue(ValueProperty);set=>SetValue(ValueProperty,Math.Clamp(value,0,100));}
    public string Caption{get=>(string)GetValue(CaptionProperty);set=>SetValue(CaptionProperty,value);}
    public Brush Accent{get=>(Brush)GetValue(AccentProperty);set=>SetValue(AccentProperty,value);}
    private double DisplayValue{get=>(double)GetValue(DisplayValueProperty);set=>SetValue(DisplayValueProperty,value);}
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e){if(d is RadialGauge g&&e.NewValue is double n){var a=new DoubleAnimation(g.DisplayValue,n,TimeSpan.FromMilliseconds(520)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};g.BeginAnimation(DisplayValueProperty,a,HandoffBehavior.SnapshotAndReplace);}}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var size=Math.Min(ActualWidth,ActualHeight);var c=new Point(ActualWidth/2,ActualHeight/2-5);var r=Math.Max(18,size*.34);var track=new Pen(new SolidColorBrush(Color.FromArgb(80,46,79,98)),8){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};dc.DrawEllipse(null,track,c,r,r);
        var glowBrush=Accent.Clone();glowBrush.Opacity=.16;var glow=new Pen(glowBrush,13){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};DrawArc(dc,c,r,-90,DisplayValue*3.6,glow);var pen=new Pen(Accent,7){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};DrawArc(dc,c,r,-90,DisplayValue*3.6,pen);
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;var value=new FormattedText($"{DisplayValue:0}",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),size*.18,Brushes.White,dpi);dc.DrawText(value,new Point(c.X-value.Width/2,c.Y-value.Height/2-3));var cap=new FormattedText(Caption,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),Math.Max(9,size*.07),new SolidColorBrush(Color.FromRgb(123,159,178)),dpi);dc.DrawText(cap,new Point(c.X-cap.Width/2,c.Y+r+11));
    }
    private static void DrawArc(DrawingContext dc,Point c,double r,double start,double sweep,Pen pen){if(sweep<=.1)return;var end=start+Math.Min(359.9,sweep);Point P(double a){var x=a*Math.PI/180;return new(c.X+r*Math.Cos(x),c.Y+r*Math.Sin(x));}var g=new StreamGeometry();using(var x=g.Open()){x.BeginFigure(P(start),false,false);x.ArcTo(P(end),new Size(r,r),0,sweep>180,SweepDirection.Clockwise,true,false);}g.Freeze();dc.DrawGeometry(null,pen,g);}
}

public sealed class SparklineChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty=DependencyProperty.Register(nameof(Points),typeof(IReadOnlyList<double>),typeof(SparklineChart),new FrameworkPropertyMetadata(Array.Empty<double>(),FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty=DependencyProperty.Register(nameof(Accent),typeof(Brush),typeof(SparklineChart),new FrameworkPropertyMetadata(Brushes.Cyan,FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<double> Points{get=>(IReadOnlyList<double>)GetValue(PointsProperty);set=>SetValue(PointsProperty,value);}
    public Brush Accent{get=>(Brush)GetValue(AccentProperty);set=>SetValue(AccentProperty,value);}
    protected override void OnRender(DrawingContext dc){base.OnRender(dc);var grid=new Pen(new SolidColorBrush(Color.FromArgb(35,91,130,151)),1);for(var i=1;i<4;i++)dc.DrawLine(grid,new(0,ActualHeight*i/4),new(ActualWidth,ActualHeight*i/4));if(Points.Count<2)return;var min=Points.Min();var max=Points.Max();if(Math.Abs(max-min)<.0001){min-=1;max+=1;}var pts=Points.Select((v,i)=>new Point(i*ActualWidth/(Points.Count-1),ActualHeight-(v-min)/(max-min)*ActualHeight*.78-ActualHeight*.11)).ToArray();var geo=new StreamGeometry();using(var x=geo.Open()){x.BeginFigure(pts[0],false,false);x.PolyLineTo(pts.Skip(1).ToArray(),true,false);}geo.Freeze();dc.DrawGeometry(null,new Pen(new SolidColorBrush(Color.FromArgb(45,0,220,255)),7),geo);dc.DrawGeometry(null,new Pen(Accent,2),geo);}
}
