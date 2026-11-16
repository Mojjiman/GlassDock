using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GlassDock.Logging;

namespace GlassDock.Glass;

/// <summary>WPF wrapper for <c>Shaders/LiquidLens.hlsl</c>. Every length is physical pixels.</summary>
public sealed class LiquidLensEffect : ShaderEffect
{
    private const string ResourceName = "GlassDock.LiquidLens.hlsl";
    private static PixelShader? _shader;
    private static bool _loaded;

    /// <summary>False on software rendering (RDP, broken drivers) or when the shader cannot be built.</summary>
    public static bool IsSupported
    {
        get
        {
            if ((RenderCapability.Tier >> 16) < 2 || !RenderCapability.IsPixelShaderVersionSupported(3, 0))
            {
                if (!_loaded) { _loaded = true; Log.Info("No ps_3_0 hardware rendering; liquid glass falls back to frosted"); }
                return false;
            }
            return Shader != null;
        }
    }

    private static PixelShader? Shader
    {
        get
        {
            if (_loaded) return _shader;
            _loaded = true;
            using var stream = typeof(LiquidLensEffect).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) { Log.Error($"Embedded shader {ResourceName} is missing"); return null; }
            using var reader = new StreamReader(stream);
            var bytes = ShaderCompiler.Compile(reader.ReadToEnd(), "LiquidLens.hlsl", "main", "ps_3_0");
            if (bytes == null) return null;
            var ps = new PixelShader();
            ps.SetStreamSource(new MemoryStream(bytes));
            ps.Freeze();
            PixelShader.InvalidPixelShaderEncountered += (_, _) => Log.Error("WPF rejected the liquid glass shader");
            Log.Info($"Liquid glass shader compiled ({bytes.Length} bytes)");
            return _shader = ps;
        }
    }

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidLensEffect), 0);
    public static readonly DependencyProperty BlurredProperty = RegisterPixelShaderSamplerProperty(nameof(Blurred), typeof(LiquidLensEffect), 1);
    public static readonly DependencyProperty PillSizeProperty = Constant<Point>(nameof(PillSize), 0, new Point(1, 1));
    public static readonly DependencyProperty RadiusProperty = Constant<double>(nameof(Radius), 1, 0.0);
    public static readonly DependencyProperty BezelProperty = Constant<double>(nameof(Bezel), 2, 1.0);
    public static readonly DependencyProperty RefractionProperty = Constant<double>(nameof(Refraction), 3, 0.0);
    public static readonly DependencyProperty FrostProperty = Constant<double>(nameof(Frost), 4, 0.0);
    public static readonly DependencyProperty ChromaProperty = Constant<double>(nameof(Chroma), 5, 0.0);
    public static readonly DependencyProperty RimGlowProperty = Constant<double>(nameof(RimGlow), 6, 0.0);
    public static readonly DependencyProperty VibrancyProperty = Constant<double>(nameof(Vibrancy), 7, 1.0);
    public static readonly DependencyProperty RimFrostProperty = Constant<double>(nameof(RimFrost), 8, 0.0);

    private static DependencyProperty Constant<T>(string name, int register, T initial) =>
        DependencyProperty.Register(name, typeof(T), typeof(LiquidLensEffect), new UIPropertyMetadata(initial, PixelShaderConstantCallback(register)));

    public LiquidLensEffect()
    {
        PixelShader = Shader;
        foreach (var dp in new[] { InputProperty, BlurredProperty, PillSizeProperty, RadiusProperty, BezelProperty, RefractionProperty,
                     FrostProperty, ChromaProperty, RimGlowProperty, VibrancyProperty, RimFrostProperty })
            UpdateShaderValue(dp);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Blurred { get => (Brush)GetValue(BlurredProperty); set => SetValue(BlurredProperty, value); }
    public Point PillSize { get => (Point)GetValue(PillSizeProperty); set => SetValue(PillSizeProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Bezel { get => (double)GetValue(BezelProperty); set => SetValue(BezelProperty, value); }
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    public double Frost { get => (double)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }
    public double Chroma { get => (double)GetValue(ChromaProperty); set => SetValue(ChromaProperty, value); }
    public double RimGlow { get => (double)GetValue(RimGlowProperty); set => SetValue(RimGlowProperty, value); }
    public double Vibrancy { get => (double)GetValue(VibrancyProperty); set => SetValue(VibrancyProperty, value); }
    public double RimFrost { get => (double)GetValue(RimFrostProperty); set => SetValue(RimFrostProperty, value); }
}
