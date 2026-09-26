using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace OpcBridge.Ua;

/// <summary>
/// The application URI is written into the SAN of the application certificate, and the stack
/// refuses a stored certificate whose URI no longer matches the configured one:
/// <c>ApplicationInstance.CheckApplicationInstanceCertificatesAsync</c> reports it as invalid
/// and never issues a replacement. Older versions built that URI from the vendor prefix
/// <c>urn:ohmypi</c>, so an installation upgrading to <c>urn:opcbridge</c> still holds a
/// certificate for the old identity — without this cleanup every UA start would fail. The
/// mismatched certificate (and its private key) is deleted through the certificate store so
/// the stack creates one for the configured identity on the same run.
/// </summary>
internal static class UaCertificateIdentity
{
    internal static async Task EnsureOwnCertificateMatchesApplicationUriAsync(
        ApplicationConfiguration configuration,
        ITelemetryContext? telemetry,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        CertificateIdentifier identifier = configuration.SecurityConfiguration.ApplicationCertificate;
        using ICertificateStore store = identifier.OpenStore(telemetry);
        X509Certificate2Collection certificates = await store
            .EnumerateAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (X509Certificate2 certificate in certificates)
        {
            if (X509Utils.CompareApplicationUriWithCertificate(certificate, configuration.ApplicationUri))
            {
                continue;
            }

            await store.DeleteAsync(certificate.Thumbprint, cancellationToken).ConfigureAwait(false);
            logger?.LogWarning(
                "Deleted application certificate {Subject} because it was issued for a different application URI. A new certificate for {ApplicationUri} is created now, and every OPC UA peer must trust it.",
                certificate.Subject,
                configuration.ApplicationUri);
        }
    }
}
