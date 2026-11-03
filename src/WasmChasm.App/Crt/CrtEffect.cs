using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WasmChasm.App.Crt;

/// <summary>Pixel shader for curvature + scanlines + vignette. Only used when the compiled shader is present.</summary>
public sealed class CrtEffect : ShaderEffect
{
    private static readonly Lazy<PixelShader?> Shader = new(LoadShader);

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(CrtEffect), 0);

    public static readonly DependencyProperty CurvatureProperty = DependencyProperty.Register(
        nameof(Curvature), typeof(double), typeof(CrtEffect), new UIPropertyMetadata(0.06, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty ScanlinesProperty = DependencyProperty.Register(
        nameof(Scanlines), typeof(double), typeof(CrtEffect), new UIPropertyMetadata(0.22, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty RenderHeightProperty = DependencyProperty.Register(
        nameof(RenderHeight), typeof(double), typeof(CrtEffect), new UIPropertyMetadata(712.0, PixelShaderConstantCallback(2)));

    private CrtEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(CurvatureProperty);
        UpdateShaderValue(ScanlinesProperty);
        UpdateShaderValue(RenderHeightProperty);
    }

    /// <summary>Returns null when the shader wasn't compiled into this build or the GPU can't run it.</summary>
    public static CrtEffect? TryCreate()
    {
        if (!RenderCapability.IsPixelShaderVersionSupported(2, 0)) return null;
        var shader = Shader.Value;
        return shader is null ? null : new CrtEffect(shader);
    }

    private static PixelShader? LoadShader()
    {
        try
        {
            var shader = new PixelShader();
            shader.UriSource = new Uri("pack://application:,,,/WasmChasm;component/Shaders/crt.ps", UriKind.Absolute);
            shader.Freeze();
            return shader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double Curvature
    {
        get => (double)GetValue(CurvatureProperty);
        set => SetValue(CurvatureProperty, value);
    }

    public double Scanlines
    {
        get => (double)GetValue(ScanlinesProperty);
        set => SetValue(ScanlinesProperty, value);
    }

    public double RenderHeight
    {
        get => (double)GetValue(RenderHeightProperty);
        set => SetValue(RenderHeightProperty, value);
    }
}
