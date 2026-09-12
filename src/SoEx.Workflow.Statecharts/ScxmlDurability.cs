using System.Xml.Linq;

namespace SoEx.Workflow.Statecharts;

/// <summary>
/// Checks, at load time, whether an SCXML chart can safely back a durable flow.
/// <para>
/// An SCXML machine's datamodel lives in a JavaScript engine, and that engine cannot be journaled — so a
/// durable SCXML flow has to run with <see cref="StatechartOptions.ScxmlContextIsNotCarried"/>, which means
/// the datamodel does not survive a park. For a chart that only routes on event names that costs nothing. For
/// a chart that keeps values in its datamodel it is silent data loss, and the loss appears at the first wait,
/// in production, months after the chart was drawn.
/// </para>
/// <para>
/// So the choice is made loudly here instead: call this when you load the chart, and one that would lose state
/// is refused before a single instance starts. It is a default, not a verdict — a chart that genuinely needs
/// its datamodel can carry it, by supplying <see cref="StatechartOptions.CaptureContext"/> and
/// <see cref="StatechartOptions.ApplyContext"/>; such a binding simply does not call this. What this stops is
/// the silent case: a datamodel chart deployed as though it were stateless.
/// <para>
/// Uses only the BCL's XML reader, so it costs no dependency — a chart can be checked without referencing the
/// SCXML importer at all.
/// </para>
/// </para>
/// </summary>
public static class ScxmlDurability
{
    private static readonly XNamespace Scxml = "http://www.w3.org/2005/07/scxml";

    /// <summary>Every datamodel-dependent construct SCXML has: each one reads or writes the JS engine.</summary>
    private static readonly string[] Elements = ["datamodel", "data", "assign", "script", "log", "if", "elseif", "foreach"];

    private static readonly string[] Attributes = ["cond", "expr", "eventexpr", "targetexpr", "srcexpr", "typeexpr", "delayexpr", "idlocation", "location", "array", "item", "index"];

    /// <summary>
    /// Returns <paramref name="xml"/> when the chart routes only on event names, and throws naming everything
    /// it found when the chart depends on its datamodel.
    /// </summary>
    public static string RequireDurable(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        IReadOnlyList<string> found = DatamodelUse(xml);
        if (found.Count > 0)
        {
            throw new InvalidOperationException(
                "this SCXML chart depends on its datamodel, so it needs a decision before it can back a " +
                "durable flow: an SCXML datamodel lives in a JavaScript engine that cannot be journaled, and " +
                $"left alone its values would be silently lost at the first wait. Found {string.Join(", ", found)}. " +
                "Three ways forward, in the order most consumers should prefer them: author the chart as XState " +
                "JSON, whose context is plain data and survives a park with nothing extra; or move the state " +
                "into the workflow's own step DTO and keep the chart to routing on event names; or carry the " +
                "values explicitly by supplying StatechartOptions.CaptureContext and ApplyContext, which reads " +
                "them out of the engine when the flow parks and puts them back when it resumes.");
        }

        return xml;
    }

    /// <summary>
    /// The datamodel-dependent constructs a chart uses, as readable names — empty for a chart that only routes
    /// on event names. Use it to report on a folder of charts without throwing on the first one.
    /// </summary>
    public static IReadOnlyList<string> DatamodelUse(string xml)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);

        XDocument document = XDocument.Parse(xml);
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (XElement element in document.Descendants())
        {
            // Namespace-agnostic on the local name: a chart may or may not carry the SCXML namespace, and a
            // check that only matched the namespaced form would pass exactly the charts it should catch.
            if (Elements.Contains(element.Name.LocalName, StringComparer.Ordinal))
            {
                found.Add($"<{element.Name.LocalName}>");
            }

            foreach (XAttribute attribute in element.Attributes())
            {
                if (Attributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                {
                    found.Add($"{attribute.Name.LocalName}=");
                }
            }
        }

        // `datamodel="ecmascript"` on the root only declares the language; on its own it means nothing is
        // actually stored, so it is not counted against a chart that never uses it.
        found.Remove("<datamodel>");
        _ = document.Root?.Attribute("datamodel");

        return [.. found];
    }
}
