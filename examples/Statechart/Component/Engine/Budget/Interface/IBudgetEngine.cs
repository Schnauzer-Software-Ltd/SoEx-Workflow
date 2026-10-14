namespace StatechartDemo.Engine.Budget.Interface;

/// <summary>
/// Engine-role component that applies the expense policy to a claim. A SoEx component contract; Task-returning.
/// It decides and keeps nothing, so a redelivered step that asks again gets the same answer.
/// </summary>
public interface IBudgetEngine
{
    /// <summary>
    /// Reviews a claim against the expense policy. Answers <c>"within-limit"</c> (approve without a person),
    /// <c>"needs-approval"</c> (a manager decides), or <c>"over-budget"</c> (reject).
    /// </summary>
    Task<string> ReviewAsync(string claimId, decimal amount);
}
