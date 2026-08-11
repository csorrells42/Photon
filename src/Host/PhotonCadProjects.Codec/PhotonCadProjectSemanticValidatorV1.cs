namespace PhotonCadProjects.Codec;

internal static class PhotonCadProjectSemanticValidatorV1
{
    private const double MatrixTolerance = 1e-9;

    internal static void Validate(PhotonCadProjectStateV1 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var entities = Unique(state.Entities, entity => entity.Id, "entities");
        ValidateEntityTree(state.Entities, entities);
        ValidateOperations(state, entities);
        ValidateOccurrences(state, entities);
        ValidateIssues(state, entities);
        ValidateBom(state, entities);
        ValidateArtifacts(state, entities);
    }

    private static Dictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> key, string field)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!result.TryAdd(key(value), value)) throw PhotonCadFileGuardsV1.Failure("duplicate_item", field);
        }
        return result;
    }

    private static void ValidateEntityTree(
        IReadOnlyList<PhotonCadEntityV1> values,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        foreach (var entity in values)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cursor = entity;
            var depth = 0;
            while (cursor.ParentId is not null)
            {
                if (++depth > PhotonCadProjectFileV1.MaximumEntityTreeDepth)
                    throw PhotonCadFileGuardsV1.Failure("entity_tree_too_deep", "entities");
                if (!visited.Add(cursor.Id)) throw PhotonCadFileGuardsV1.Failure("entity_cycle", "entities");
                if (!entities.TryGetValue(cursor.ParentId, out cursor!))
                    throw PhotonCadFileGuardsV1.Failure("entity_parent_missing", "entities");
            }
        }
    }

    private static void ValidateOperations(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        _ = Unique(state.Operations, operation => operation.Id, "operations");
        var ordered = state.Operations.OrderBy(operation => operation.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var operation = ordered[index];
            if (operation.Ordinal != index) throw PhotonCadFileGuardsV1.Failure("noncontiguous_operation_ordinal", "operations");
            if (operation.Inputs.Select(input => input.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != operation.Inputs.Count)
                throw PhotonCadFileGuardsV1.Failure("duplicate_operation_input", "operations");
            if (operation.TargetEntityIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != operation.TargetEntityIds.Count)
                throw PhotonCadFileGuardsV1.Failure("duplicate_operation_target", "operations");
            foreach (var target in operation.TargetEntityIds)
            {
                if (!entities.ContainsKey(target)) throw PhotonCadFileGuardsV1.Failure("operation_target_missing", "operations");
            }
            foreach (var input in operation.Inputs)
            {
                if (input.Value.Value is string entity && input.Value.Kind == PhotonCadInputKindV1.Entity && !entities.ContainsKey(entity))
                    throw PhotonCadFileGuardsV1.Failure("operation_input_entity_missing", "operations");
                if (input.Value.Value is IReadOnlyList<string> entityList && input.Value.Kind == PhotonCadInputKindV1.EntityList
                    && entityList.Any(entityId => !entities.ContainsKey(entityId)))
                    throw PhotonCadFileGuardsV1.Failure("operation_input_entity_missing", "operations");
            }
        }
        if (ordered.LongLength > state.Revision)
            throw PhotonCadFileGuardsV1.Failure("operation_count_ahead_of_revision", "operations");
    }

    private static void ValidateOccurrences(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        var occurrences = Unique(state.Occurrences, occurrence => occurrence.OccurrenceId, "occurrences");
        if (state.Occurrences.Count > 0 && state.Occurrences.Count(occurrence => occurrence.ParentOccurrenceId is null) != 1)
            throw PhotonCadFileGuardsV1.Failure("occurrence_root_count_invalid", "occurrences");
        foreach (var occurrence in state.Occurrences)
        {
            if (!entities.ContainsKey(occurrence.SourceEntityId))
                throw PhotonCadFileGuardsV1.Failure("occurrence_source_missing", "occurrences");
            ValidateRigidTransform(occurrence.TransformSpan);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cursor = occurrence;
            var depth = 0;
            while (cursor.ParentOccurrenceId is not null)
            {
                if (++depth > PhotonCadProjectFileV1.MaximumEntityTreeDepth)
                    throw PhotonCadFileGuardsV1.Failure("occurrence_tree_too_deep", "occurrences");
                if (!visited.Add(cursor.OccurrenceId)) throw PhotonCadFileGuardsV1.Failure("occurrence_cycle", "occurrences");
                if (!occurrences.TryGetValue(cursor.ParentOccurrenceId, out cursor!))
                    throw PhotonCadFileGuardsV1.Failure("occurrence_parent_missing", "occurrences");
            }
        }
    }

    private static void ValidateIssues(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        foreach (var issue in state.Issues)
        {
            if (issue.EntityIds.Any(entityId => !entities.ContainsKey(entityId)))
                throw PhotonCadFileGuardsV1.Failure("issue_entity_missing", "issues");
        }
    }

    private static void ValidateBom(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        foreach (var row in state.Bom)
        {
            if (!entities.ContainsKey(row.SourceEntityId))
                throw PhotonCadFileGuardsV1.Failure("bom_source_missing", "bom");
        }
        _ = PhotonCadBomCanonicalizer.Compute(state.Units, state.Bom);
    }

    private static void ValidateArtifacts(
        PhotonCadProjectStateV1 state,
        IReadOnlyDictionary<string, PhotonCadEntityV1> entities)
    {
        var geometryOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operationById = state.Operations.ToDictionary(operation => operation.Id, StringComparer.OrdinalIgnoreCase);
        var previews = 0;
        foreach (var artifact in state.Artifacts)
        {
            if (artifact.Revision > state.Revision)
                throw PhotonCadFileGuardsV1.Failure("artifact_revision_ahead", "artifacts");
            if (!operationById.TryGetValue(artifact.Provenance.OperationId, out var operation)
                || operation.State != PhotonCadOperationStateV1.Applied
                || !StringComparer.Ordinal.Equals(operation.CapabilityId, artifact.Provenance.CapabilityId))
                throw PhotonCadFileGuardsV1.Failure("artifact_operation_binding_mismatch", "artifacts");
            if (!SameSource(operation.Source, artifact.Provenance.Source))
                throw PhotonCadFileGuardsV1.Failure("artifact_source_binding_mismatch", "artifacts");

            switch (artifact.Role)
            {
                case PhotonCadArtifactRoleV1.AuthoritativeGeometry:
                    if (artifact.Kind != PhotonCadArtifactKindV1.Step || artifact.OwnerEntityId is null || artifact.Bounds is not null)
                        throw PhotonCadFileGuardsV1.Failure("invalid_geometry_artifact", "artifacts");
                    if (!entities.ContainsKey(artifact.OwnerEntityId))
                        throw PhotonCadFileGuardsV1.Failure("artifact_owner_missing", "artifacts");
                    if (!geometryOwners.Add(artifact.OwnerEntityId))
                        throw PhotonCadFileGuardsV1.Failure("duplicate_geometry_artifact", "artifacts");
                    break;
                case PhotonCadArtifactRoleV1.ProjectPreview:
                    previews++;
                    if (artifact.Kind != PhotonCadArtifactKindV1.Glb || artifact.OwnerEntityId is not null
                        || artifact.Bounds is null || artifact.Revision != state.Revision)
                        throw PhotonCadFileGuardsV1.Failure("invalid_preview_artifact", "artifacts");
                    break;
                default:
                    throw PhotonCadFileGuardsV1.Failure("unsupported_artifact_role", "artifacts");
            }
        }
        if (previews > 1) throw PhotonCadFileGuardsV1.Failure("duplicate_project_preview", "artifacts");
        foreach (var entity in state.Entities)
        {
            if (entity.Kind is PhotonCadEntityKindV1.Body or PhotonCadEntityKindV1.Part or PhotonCadEntityKindV1.Assembly
                && !geometryOwners.Contains(entity.Id))
                throw PhotonCadFileGuardsV1.Failure("geometry_artifact_missing", "artifacts");
        }
    }

    private static bool SameSource(PhotonCadSourceIdentityV1 left, PhotonCadSourceIdentityV1 right) =>
        StringComparer.Ordinal.Equals(left.Package, right.Package)
        && StringComparer.Ordinal.Equals(left.Version, right.Version)
        && PhotonCadFileGuardsV1.FixedDigestEquals(left.Digest, right.Digest)
        && StringComparer.Ordinal.Equals(left.License, right.License);

    private static void ValidateRigidTransform(ReadOnlySpan<double> matrix)
    {
        if (matrix.Length != 16 || !Near(matrix[12], 0) || !Near(matrix[13], 0) || !Near(matrix[14], 0) || !Near(matrix[15], 1))
            throw PhotonCadFileGuardsV1.Failure("invalid_transform_convention", "transform");
        for (var row = 0; row < 3; row++)
        {
            var length = 0d;
            for (var column = 0; column < 3; column++) length += matrix[(row * 4) + column] * matrix[(row * 4) + column];
            if (!Near(length, 1)) throw PhotonCadFileGuardsV1.Failure("nonrigid_transform", "transform");
            for (var other = row + 1; other < 3; other++)
            {
                var dot = 0d;
                for (var column = 0; column < 3; column++) dot += matrix[(row * 4) + column] * matrix[(other * 4) + column];
                if (!Near(dot, 0)) throw PhotonCadFileGuardsV1.Failure("nonrigid_transform", "transform");
            }
        }
        var determinant =
            matrix[0] * ((matrix[5] * matrix[10]) - (matrix[6] * matrix[9]))
            - matrix[1] * ((matrix[4] * matrix[10]) - (matrix[6] * matrix[8]))
            + matrix[2] * ((matrix[4] * matrix[9]) - (matrix[5] * matrix[8]));
        if (!Near(determinant, 1)) throw PhotonCadFileGuardsV1.Failure("nonrigid_transform", "transform");
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) <= MatrixTolerance;
}
