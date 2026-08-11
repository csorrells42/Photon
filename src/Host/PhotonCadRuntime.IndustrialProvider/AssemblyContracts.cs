using PhotonCadProjects.Codec;
using PhotonCadProjects.RuntimeSync;

namespace PhotonCadRuntime.IndustrialProvider;

public static class PhotonCadAssemblyContract
{
    public const string PlaceCapabilityId = "assembly.occurrence.place.v1";
    public const string RemoveCapabilityId = "assembly.occurrence.remove.v1";
    public const string TransformCapabilityId = "assembly.occurrence.transform.v1";
    public const string PreviewCapabilityId = "industrial.preview.glb.v1";
}

public sealed class PhotonCadAssemblyBoundMutation
{
    internal PhotonCadAssemblyBoundMutation(
        PhotonCadRuntimeSyncRequest request,
        AssemblyMutationProvider provider)
    {
        Request = request;
        Provider = provider;
        Compensator = provider;
    }

    public PhotonCadRuntimeSyncRequest Request { get; }
    public IPhotonCadSealedMutationProvider Provider { get; }
    public IPhotonCadSealedMutationCompensator Compensator { get; }
}

internal enum AssemblyMutationKind
{
    Place,
    Remove,
    Transform,
}

internal sealed class AssemblyMutationCommand
{
    internal AssemblyMutationCommand(
        AssemblyMutationKind kind,
        string occurrenceId,
        string sourceEntityId,
        string? parentOccurrenceId,
        IReadOnlyList<double> transform)
    {
        Kind = kind;
        OccurrenceId = AssemblyGuards.Identifier(occurrenceId, nameof(occurrenceId));
        SourceEntityId = AssemblyGuards.Identifier(sourceEntityId, nameof(sourceEntityId));
        ParentOccurrenceId = parentOccurrenceId is null
            ? null
            : AssemblyGuards.Identifier(parentOccurrenceId, nameof(parentOccurrenceId));
        if (kind == AssemblyMutationKind.Place && ParentOccurrenceId is null)
            throw new ArgumentException("assembly_parent_required", nameof(parentOccurrenceId));
        if (kind is AssemblyMutationKind.Remove or AssemblyMutationKind.Transform && ParentOccurrenceId is not null)
            throw new ArgumentException("assembly_parent_not_allowed", nameof(parentOccurrenceId));
        Transform = kind == AssemblyMutationKind.Remove
            ? Array.AsReadOnly(MutationMapperV1.IdentityTransform.ToArray())
            : AssemblyGuards.RigidTransform(transform, nameof(transform));
    }

    internal AssemblyMutationKind Kind { get; }
    internal string OccurrenceId { get; }
    internal string SourceEntityId { get; }
    internal string? ParentOccurrenceId { get; }
    internal IReadOnlyList<double> Transform { get; }
    internal string CapabilityId => Kind switch
    {
        AssemblyMutationKind.Place => PhotonCadAssemblyContract.PlaceCapabilityId,
        AssemblyMutationKind.Remove => PhotonCadAssemblyContract.RemoveCapabilityId,
        _ => PhotonCadAssemblyContract.TransformCapabilityId,
    };
    internal string Label => Kind switch
    {
        AssemblyMutationKind.Place => "Place assembly occurrence",
        AssemblyMutationKind.Remove => "Remove assembly occurrence",
        _ => "Transform assembly occurrence",
    };

    internal IReadOnlyList<PhotonCadSyncOperationInput> Inputs()
    {
        var result = new List<PhotonCadSyncOperationInput>
        {
            new("occurrenceId", PhotonCadSyncInputValue.Text(OccurrenceId)),
            new("sourceEntityId", PhotonCadSyncInputValue.Entity(SourceEntityId)),
        };
        if (Kind == AssemblyMutationKind.Place)
            result.Add(new("parentOccurrenceId", PhotonCadSyncInputValue.Text(ParentOccurrenceId!)));
        if (Kind != AssemblyMutationKind.Remove)
        {
            for (var index = 0; index < Transform.Count; index++)
                result.Add(new PhotonCadSyncOperationInput($"matrix{index:D2}", PhotonCadSyncInputValue.Number(Transform[index])));
        }
        return result;
    }
}

internal static class AssemblyGuards
{
    private const double OrthogonalTolerance = 1e-10;

    internal static string Identifier(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not ':'))
            throw new ArgumentException("assembly_identifier_invalid", field);
        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
            throw new ArgumentException("assembly_path_like_identifier_rejected", field);
        return value;
    }

    internal static IReadOnlyList<double> RigidTransform(IReadOnlyList<double>? value, string field)
    {
        if (value is null || value.Count != 16) throw new ArgumentException("assembly_transform_length_invalid", field);
        var matrix = value.ToArray();
        if (matrix.Any(number => !double.IsFinite(number))) throw new ArgumentException("assembly_transform_number_invalid", field);
        if (!Near(matrix[12], 0) || !Near(matrix[13], 0) || !Near(matrix[14], 0) || !Near(matrix[15], 1))
            throw new ArgumentException("assembly_transform_affine_invalid", field);
        if (Math.Abs(matrix[3]) > 1_000_000 || Math.Abs(matrix[7]) > 1_000_000 || Math.Abs(matrix[11]) > 1_000_000)
            throw new ArgumentException("assembly_transform_translation_invalid", field);

        var x = new[] { matrix[0], matrix[1], matrix[2] };
        var y = new[] { matrix[4], matrix[5], matrix[6] };
        var z = new[] { matrix[8], matrix[9], matrix[10] };
        if (!Near(Dot(x, x), 1) || !Near(Dot(y, y), 1) || !Near(Dot(z, z), 1)
            || !Near(Dot(x, y), 0) || !Near(Dot(x, z), 0) || !Near(Dot(y, z), 0))
            throw new ArgumentException("assembly_transform_not_rigid", field);
        var determinant = x[0] * (y[1] * z[2] - y[2] * z[1])
            - y[0] * (x[1] * z[2] - x[2] * z[1])
            + z[0] * (x[1] * y[2] - x[2] * y[1]);
        if (!Near(determinant, 1)) throw new ArgumentException("assembly_transform_orientation_invalid", field);
        return Array.AsReadOnly(matrix);
    }

    internal static InvalidOperationException Failure(string code) => new(code);

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right) =>
        left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

    private static bool Near(double left, double right) => Math.Abs(left - right) <= OrthogonalTolerance;
}
