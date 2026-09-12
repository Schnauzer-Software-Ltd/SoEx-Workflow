using Elsa.Workflows.Models;

namespace SoEx.Workflow.Runtime.Elsa;

/// <summary>What a resumed event bookmark carries: the next sealed step, and the sealed raise-time event
/// data to hand it (empty when the raise carried none).</summary>
public readonly record struct ElsaResume(byte[] Payload, byte[] EventData);

/// <summary>
/// Resolves what a resumed event bookmark should carry. A branch that journaled an <c>OnEvent</c>
/// continuation at wait time always resumes into it, and anything the raiser supplied travels alongside as
/// event data rather than replacing it; only a branch that journaled none lets the raised payload be the
/// next step itself. Resolution happens host-side (before the resume) so a timer resume with an empty
/// <c>onTimeout</c> can never be mistaken for it.
/// <para>
/// Elsa is the one adapter that resolves on the gateway side rather than inside the driver — it drives
/// consumer-authored definitions and has no in-flow place to decide — so this is where the rule lives for it.
/// Both halves stay sealed ciphertext throughout; nothing here decrypts.
/// </para>
/// </summary>
public static class ElsaEventPayload
{
    public static ElsaResume Resolve(Bookmark bookmark, byte[]? raised)
    {
        byte[] payload = raised ?? [];

        if (bookmark.Metadata is { } md && md.TryGetValue("onEvent", out string? onEvent) && !string.IsNullOrEmpty(onEvent))
        {
            return new ElsaResume(Convert.FromBase64String(onEvent), payload);
        }

        // No journaled continuation: the raiser supplies the next step, exactly as before. A bare raise here
        // resolves to empty and fails downstream on the unreadable step — the branch declared nothing to
        // resume into, which is the same refusal every other adapter makes.
        return new ElsaResume(payload, []);
    }
}
