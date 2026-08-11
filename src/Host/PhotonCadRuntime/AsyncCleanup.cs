namespace PhotonCadRuntime;

internal static class CadAsyncCleanup
{
    internal static async ValueTask DisposeAllAsync(params IAsyncDisposable?[] resources)
    {
        Exception? firstFailure = null;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CadDockerRuntimeException or CadProtocolException or
                CadContractException or IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                firstFailure ??= exception;
            }
        }
        if (firstFailure is not null)
            throw new CadDockerRuntimeException("runtime_cleanup_failed", innerException: firstFailure);
    }
}
