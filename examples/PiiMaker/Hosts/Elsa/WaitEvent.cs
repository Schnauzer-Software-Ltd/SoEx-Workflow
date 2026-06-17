using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;

namespace PiiMaker.Host.Elsa;

/// <summary>A native Elsa wait: suspend on a bookmark, resume when the host delivers the named event.</summary>
public sealed class WaitEvent : Activity
{
    public string EventName { get; set; } = "";

    protected override void Execute(ActivityExecutionContext context) =>
        context.CreateBookmark(new CreateBookmarkArgs { BookmarkName = EventName, Stimulus = EventName, Callback = Resume, AutoBurn = true });

    private async ValueTask Resume(ActivityExecutionContext context) => await context.CompleteActivityAsync();
}
