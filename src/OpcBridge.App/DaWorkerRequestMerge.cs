namespace OpcBridge.App;

/// <summary>
/// Combines the worker block of a source-upsert request with the source's stored options
/// before validation. A blank request password keeps the stored one (same rule as the UA
/// credentials) but only while the request still carries a run-as identity: switching to
/// in-process or clearing the account must drop the stored password, or the merge produces
/// the impossible "password without an account" combination and the save is rejected.
/// </summary>
public static class DaWorkerRequestMerge
{
    public static DaWorkerOptions Merge(DaWorkerRequest request, DaWorkerOptions? existing)
    {
        string mode = request.Mode ?? DaWorkerModes.InProcess;
        bool keepsRunAsIdentity =
            !string.Equals(DaWorkerModes.Normalize(mode), DaWorkerModes.InProcess, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(request.RunAsUser);

        string? password = string.IsNullOrWhiteSpace(request.RunAsPassword) && keepsRunAsIdentity
            ? existing?.RunAsPassword
            : request.RunAsPassword;

        return new DaWorkerOptions(mode, request.RunAsUser, password, request.RunAsDomain);
    }
}
