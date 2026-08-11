namespace PhotonCadRuntime;

internal static class CadDockerRuntimeProtocolProbe
{
    internal static async ValueTask VerifyAsync(
        CadVerifiedDockerRuntimeEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var session = CadSessionHandle.New();
        var project = CadProjectHandle.New();
        CadDockerContainerProcess? geometryContainer = null;
        CadDockerContainerProcess? assemblyContainer = null;
        CadGeometryMcpClient? geometry = null;
        CadPartCadProtocolClient? assembly = null;
        try
        {
            geometryContainer = await CadDockerContainerProcess.StartAsync(
                evidence,
                CadDockerRuntimeRole.Geometry,
                session,
                project,
                cancellationToken).ConfigureAwait(false);
            assemblyContainer = await CadDockerContainerProcess.StartAsync(
                evidence,
                CadDockerRuntimeRole.Assembly,
                session,
                project,
                cancellationToken).ConfigureAwait(false);
            geometry = await CadGeometryMcpClient.ConnectAsync(
                geometryContainer,
                evidence.Settings.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            assembly = await CadPartCadProtocolClient.ConnectAndProveAsync(
                assemblyContainer,
                evidence.Settings.RequestTimeout,
                cancellationToken).ConfigureAwait(false);
            if (assembly.Proof.Version != CadPartCadProtocolClient.RuntimeVersion ||
                !assembly.Proof.ForbiddenMethodsRejected)
                throw new CadProtocolException("partcad_policy_proof_failed");
        }
        finally
        {
            await CadAsyncCleanup.DisposeAllAsync(
                assembly,
                geometry,
                assemblyContainer,
                geometryContainer).ConfigureAwait(false);
        }
    }
}
