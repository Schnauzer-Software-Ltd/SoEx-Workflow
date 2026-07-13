namespace SoEx.Workflow;

/// <summary>
/// The health of a backstop loop reported after each pass, so a loop that has silently stopped making progress
/// (expired credentials, an unreachable store) is observable instead of failing forever inside an empty catch.
/// A host wires the loops' <c>onError</c>/<c>onPass</c> hooks to metrics and alerting: alert when
/// <see cref="ConsecutiveFailures"/> crosses a threshold or <see cref="LastSuccessUtc"/> ages past an interval.
/// </summary>
/// <param name="ConsecutiveFailures">Failed passes since the last success — reset to 0 on any successful pass.</param>
/// <param name="LastSuccessUtc">When the loop last completed a pass without throwing; <c>null</c> until the
/// first success (a loop that has never succeeded is itself a signal).</param>
public readonly record struct LoopPassHealth(int ConsecutiveFailures, DateTimeOffset? LastSuccessUtc);
