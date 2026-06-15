namespace PiiMaker.iFx.Proxy;

/// <summary>
/// The method-side proxy entry that the generated controllers call by name:
/// <c>PiiMaker.iFx.Proxy.Proxy.ForService&lt;I&gt;()</c>. It delegates to the <c>SoEx.Proxy</c> package — the
/// SoEx-blessed seam — rather than reaching into the container directly, so resolution honours whatever
/// proxying/interception SoEx wires. <see cref="PiiMaker.Hosting.SoExContextMiddleware"/> sets the ambient
/// scope per request, so a generated POST endpoint dispatches into the composed "membership" system through
/// the pipeline. This mirrors the shim the SoEx solution template ships.
/// </summary>
public static class Proxy
{
    public static I ForService<I>() where I : class => SoEx.Proxy.ForService<I>();

    public static I ForComponent<I>(object service) where I : class => SoEx.Proxy.ForComponent<I>(service);
}
