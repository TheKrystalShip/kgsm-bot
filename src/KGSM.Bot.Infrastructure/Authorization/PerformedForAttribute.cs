namespace KGSM.Bot.Infrastructure.Authorization;

/// <summary>
/// Marks an adapter method that performs an engine action for a person, below the check that passed
/// them for it: a command's <c>RequireAction</c>, or the evaluation a button runs at the click.
/// </summary>
/// <remarks>
/// It grants nothing and checks nothing. It names the action at the call, so the action manifest's
/// generator can tell a call made for a checked person from one this bot makes as its own service
/// account, which is declared with a requirement instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class PerformedForAttribute(string action) : Attribute
{
    /// <summary>The action, in full: <c>kgsm:server.start</c>.</summary>
    public string Action { get; } = action;
}
