using Microsoft.AspNetCore.Mvc;
using PiiMaker.Manager.Membership.Interface;

namespace PiiMaker.Generated.Controllers;

/// <summary>
/// The HTTP trigger seam for the Membership manager: one <c>POST /IMembershipManager/Trigger</c> action that
/// resolves the entry proxy for the current SoEx scope and forwards the body. The body is a polymorphic
/// <see cref="TriggerBase"/> — its <c>$type</c> discriminator (a full type name) names the trigger — bound by
/// the System.Text.Json options the web host configures from <see cref="PiiMaker.Generated.SoExKnownTypes"/>.
/// The example control-panel UI posts here, one button per trigger.
/// <para>Hand-written replacement for the <c>SoEx.Method.Generators.AspNetCore</c> output, which lifts every
/// public interface in a <c>*.Manager.*.Interface</c> assembly into a controller keyed by simple name — that
/// collides once the manager also exposes the same-named <c>Native</c>/<c>Portable</c> governed-step
/// contracts, which are durable-flow seams and never HTTP endpoints. Discovered via the application part the
/// web host adds for this assembly.</para>
/// </summary>
[Route("[controller]/[action]")]
[ApiController]
public sealed class IMembershipManagerController : Controller
{
    private static IMembershipManager Entry() => PiiMaker.iFx.Proxy.Proxy.ForService<IMembershipManager>();

    [HttpPost]
    public async Task<IActionResult> Trigger([FromBody] TriggerBase trigger)
    {
        TriggerResult result = await Entry().Trigger(trigger);

        // A deduplicated start is a meaningful outcome, not a fault: answer 409 with a message the caller can
        // show, instead of letting the backend's raw "already started" error surface as a 500. The instance id
        // is PII-free by construction, so it is safe to name. Bumping the trigger's attempt starts a new run.
        if (result.AlreadyStarted)
        {
            return Conflict(new
            {
                error = $"already started — a run already owns instance '{result.InstanceId}'. "
                    + "Bump the attempt to start a new run for this identity.",
                instanceId = result.InstanceId,
            });
        }

        return Json(result.InstanceId);
    }
}
