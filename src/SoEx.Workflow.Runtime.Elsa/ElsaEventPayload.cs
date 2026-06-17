using Elsa.Workflows.Models;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>
/// Resolves the step payload a resumed event bookmark should carry. An event raised with a
/// payload carries the next step; one raised empty resumes into the wait's sealed
/// <c>OnEvent</c> continuation, journaled on the bookmark's metadata by
/// <see cref="WorkflowDriverActivity"/> at wait time. Resolution happens host-side (before
/// the resume) so a timer resume with an empty <c>onTimeout</c> can never be mistaken for it.
/// </summary>
public static class ElsaEventPayload
{
    public static byte[] Resolve(Bookmark bookmark, byte[]? raised)
    {
        if (raised is { Length: > 0 })
        {
            return raised;
        }

        return bookmark.Metadata is { } md && md.TryGetValue("onEvent", out string? onEvent) && !string.IsNullOrEmpty(onEvent)
            ? Convert.FromBase64String(onEvent)
            : raised ?? [];
    }
}
