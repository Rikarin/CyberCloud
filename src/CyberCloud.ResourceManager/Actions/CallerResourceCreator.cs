namespace CyberCloud.ResourceManager.Actions;

/// <summary>
///     The <see cref="IResourceCreator" /> a synchronous action is handed: bound to one request's
///     caller and one resource's group, and writing through <see cref="ResourceManagerService" />.
/// </summary>
/// <param name="manager">The manager whose write path the create goes through.</param>
/// <param name="owner">The resource the action is on.</param>
/// <param name="caller">The action's caller.</param>
/// <remarks>
///     ⚠ <b>Built per request in <c>CompleteActionAsync</c> and never registered in a container.</b>
///     A creator that could be resolved from the silo's services would have no caller to be bound to,
///     which is the one thing that makes it safe.
/// </remarks>
sealed class CallerResourceCreator(ResourceManagerService manager, ResourceId owner, CallerContext caller)
    : IResourceCreator {
    /// <inheritdoc />
    public Task<Result<ResourceCreated>> CreateAsync(
        ResourceTypeName type,
        string name,
        string apiVersion,
        string body,
        CancellationToken cancellationToken = default
    ) =>
        manager.CreateForActionAsync(owner, caller, type, name, apiVersion, body, cancellationToken);
}
