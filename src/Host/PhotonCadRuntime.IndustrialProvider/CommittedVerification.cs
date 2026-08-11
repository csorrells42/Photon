using PhotonCadProjects;
using PhotonCadProjects.Codec;
using System.Text.Json;

namespace PhotonCadRuntime.IndustrialProvider;

public enum PhotonCadCommittedVerificationCheck
{
    ValidSolids,
    Interference,
    Dimensions,
    AssemblyStructure,
    ExportReadiness,
}

public sealed record PhotonCadCommittedVerificationResult(
    bool Available,
    bool Passed,
    string Reason,
    long Revision,
    DateTimeOffset MeasuredAtUtc);

internal static class CommittedVerification
{
    internal static async ValueTask<PhotonCadCommittedVerificationResult> VerifyAsync(
        PhotonCadCanonicalProject project,
        IReadOnlyList<PhotonCadCommittedVerificationCheck> checks,
        IIndustrialContainerRunner runner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(checks);
        cancellationToken.ThrowIfCancellationRequested();
        if (checks.Count is < 1 or > 5 || checks.Distinct().Count() != checks.Count || checks.Any(value => !Enum.IsDefined(value)))
            throw new ArgumentException("committed_verification_checks_invalid", nameof(checks));
        if (checks.Contains(PhotonCadCommittedVerificationCheck.Interference))
            return new(false, false, "interference_unavailable", project.Revision, DateTimeOffset.UtcNow);
        if (project.Dirty || project.Units != PhotonCadProjectUnit.Millimeter || project.Revision < 1)
            return new(false, false, "committed_project_unavailable", project.Revision, DateTimeOffset.UtcNow);

        try
        {
            var codec = new PhotonCadCanonicalProjectCodecV1();
            var state = codec.Inspect(codec.Decode(project.CanonicalBytes));
            if (state.Dirty
                || state.Revision != project.Revision
                || !StringComparer.Ordinal.Equals(state.SessionId, project.SessionId)
                || !StringComparer.Ordinal.Equals(state.ProjectId, project.ProjectId))
                return Failed(project, "committed_project_readback_mismatch");
            var steps = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.AuthoritativeGeometry)
                .OrderBy(value => value.OwnerEntityId, StringComparer.Ordinal).ToArray();
            var preview = state.Artifacts.Where(value => value.Role == PhotonCadArtifactRoleV1.ProjectPreview).ToArray();
            if (steps.Length < 1 || preview.Length != 1 || preview[0].Kind != PhotonCadArtifactKindV1.Glb
                || preview[0].Revision != project.Revision)
                return Failed(project, "committed_artifact_set_invalid");
            var committedPreview = preview[0];
            var committedBounds = committedPreview.Bounds;
            if (committedBounds is null)
                return Failed(project, "committed_artifact_set_invalid");
            foreach (var step in steps)
            {
                if (step.Kind != PhotonCadArtifactKindV1.Step || step.OwnerEntityId is null || step.Bounds is not null
                    || step.ByteLength != step.Content.Length || !ProtocolV1.FixedDigestEquals(ProtocolV1.Sha256(step.Content.Span), step.Digest))
                    return Failed(project, "committed_step_invalid");
                ArtifactReader.ValidateStep(step.Content.Span);
            }
            var sourceIds = steps.Select(value => value.OwnerEntityId!).ToHashSet(StringComparer.Ordinal);
            if (state.Occurrences.Count < 1
                || !state.Occurrences.Select(value => value.SourceEntityId).ToHashSet(StringComparer.Ordinal).SetEquals(sourceIds))
                return Failed(project, "committed_assembly_invalid");
            var sources = new List<IndustrialPreviewSource>(steps.Length);
            var inputs = new List<IndustrialInputArtifact>(steps.Length);
            for (var index = 0; index < steps.Length; index++)
            {
                var slot = $"part{index:D4}";
                sources.Add(new IndustrialPreviewSource(steps[index].OwnerEntityId!, slot, steps[index].Digest, steps[index].ByteLength));
                inputs.Add(new IndustrialInputArtifact(slot, steps[index].Content.ToArray(), steps[index].Digest));
            }
            var command = new IndustrialPreviewCommand(
                sources,
                state.Occurrences.OrderBy(value => value.OccurrenceId, StringComparer.Ordinal).Select(value =>
                    new IndustrialPreviewOccurrence(value.OccurrenceId, value.SourceEntityId, value.ParentOccurrenceId, value.Transform.ToArray())).ToArray());
            IndustrialPreviewResponse response;
            byte[] glb;
            await using (var invocation = await runner.ExecuteAsync(
                ProtocolV1.SerializePreview(command), inputs, cancellationToken).ConfigureAwait(false))
            {
                response = ProtocolV1.ParsePreviewResponse(invocation.Response, command);
                glb = await ArtifactReader.ReadSealedAsync(
                    invocation.OutputDirectory, "preview.glb", response.Artifact,
                    ProtocolV1.MaximumGlbBytes, cancellationToken).ConfigureAwait(false);
            }
            GlbValidator.Validate(glb, command, response.Bounds);
            if (!ProtocolV1.FixedDigestEquals(response.Artifact.ContentDigest, committedPreview.Digest)
                || !glb.AsSpan().SequenceEqual(committedPreview.Content.Span)
                || !BoundsEqual(response.Bounds, committedBounds))
                return Failed(project, "committed_preview_interference");
            return new(true, true, "passed", project.Revision, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is PhotonCadProjectException or InvalidDataException or JsonException)
        {
            return Failed(project, "committed_verification_failed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, false, "committed_verification_unavailable", project.Revision, DateTimeOffset.UtcNow);
        }
    }

    private static PhotonCadCommittedVerificationResult Failed(PhotonCadCanonicalProject project, string reason) =>
        new(true, false, reason, project.Revision, DateTimeOffset.UtcNow);

    private static bool BoundsEqual(IndustrialBounds actual, PhotonCadBoundsV1 expected) =>
        actual.MinimumX == expected.Minimum.X && actual.MinimumY == expected.Minimum.Y && actual.MinimumZ == expected.Minimum.Z
        && actual.MaximumX == expected.Maximum.X && actual.MaximumY == expected.Maximum.Y && actual.MaximumZ == expected.Maximum.Z;
}
