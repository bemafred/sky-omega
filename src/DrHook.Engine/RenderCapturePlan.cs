namespace SkyOmega.DrHook.Engine;

/// <summary>The framework-agnostic recipe a <see cref="DebugSession.TryEvalRenderCapture"/> follows to capture
/// a live GUI debuggee's top-level visual to an image file by func-evaluating the debuggee's OWN rendering APIs
/// (ADR-012 Q8 approach (a), mechanic 7). Every type / method name is a parameter, so the substrate carries no
/// framework reference — the caller (a poc, or an MCP tool) supplies the Avalonia / WPF / WinUI specifics.
///
/// The chain the plan describes:
///   1. <c>AppType.CurrentGetter</c> (a static property, e.g. <c>Application.get_Current</c>) →
///      the instance getters in <c>WindowGetterChain</c> (e.g. <c>ApplicationLifetime</c>, <c>MainWindow</c>),
///      each resolved on the prior result's runtime type → the top-level visual.
///   2. <c>new PixelSizeType(Width, Height)</c> — the framework's pixel-size value type.
///   3. <c>new BitmapType(pixelSize)</c> — a render-target bitmap.
///   4. <c>bitmap.RenderMethod(visual)</c> — render the visual INTO the bitmap.
///   5. <c>bitmap.SaveMethod(OutputPath)</c> — write the image the debugger then reads back.</summary>
public readonly record struct RenderCapturePlan(
    string AppModule,
    string AppType,
    string CurrentGetter,
    string[] WindowGetterChain,
    string GfxModule,
    string PixelSizeType,
    string BitmapType,
    string RenderMethod,
    string SaveMethod,
    int Width,
    int Height,
    string OutputPath);
