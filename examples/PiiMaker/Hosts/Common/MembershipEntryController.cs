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
    public async Task<ActionResult<string>> Trigger([FromBody] TriggerBase trigger)
    {
        string instanceId = await Entry().Trigger(trigger);
        return Json(instanceId);
    }
}
