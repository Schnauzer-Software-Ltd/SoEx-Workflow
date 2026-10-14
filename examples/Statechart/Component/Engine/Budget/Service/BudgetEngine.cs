using StatechartDemo.Engine.Budget.Interface;

namespace StatechartDemo.Engine.Budget.Service;

/// <summary>The demo's expense policy: small claims approve themselves, large ones are refused.</summary>
public sealed class BudgetEngine : IBudgetEngine
{
    private const decimal AutoApproveLimit = 100m;
    private const decimal BudgetLimit = 1000m;

    public Task<string> ReviewAsync(string claimId, decimal amount) =>
        Task.FromResult(amount switch
        {
            <= AutoApproveLimit => "within-limit",
            <= BudgetLimit => "needs-approval",
            _ => "over-budget",
        });
}
