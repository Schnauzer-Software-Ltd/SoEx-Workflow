using StatechartDemo.Access.Notification.Interface;
using StatechartDemo.Engine.Budget.Interface;

namespace StatechartDemo.Manager.Expense.Service;

/// <summary>
/// The components the expense process calls, as the manager received them for one call. The manager hands
/// this to the step; the chart's calls and actions reach the components through it.
/// </summary>
public sealed record ExpenseComponents(IBudgetEngine Budget, INotificationAccess Notifications);
