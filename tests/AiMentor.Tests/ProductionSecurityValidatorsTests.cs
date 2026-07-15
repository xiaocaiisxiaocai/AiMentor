using AiMentor.Api;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace AiMentor.Tests;

public sealed class ProductionSecurityValidatorsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aimentor-security-{Guid.NewGuid():N}");

    [Fact]
    public void StaticRootItselfAndDescendantsAreRejectedWhileSiblingIsAccepted()
    {
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);

        Assert.True(ProductionSecurityValidators.IsPathWithin(webRoot, webRoot));
        Assert.True(ProductionSecurityValidators.IsPathWithin(webRoot, Path.Combine(webRoot, "keys")));
        Assert.False(ProductionSecurityValidators.IsPathWithin(webRoot, Path.Combine(_root, "private-keys")));
    }

    [Fact]
    public void DataProtectionDirectoryRequiresPreprovisionedMatchingClusterMarker()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "shared-keys"));
        var clusterId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateDataProtectionDirectory(
                directory.FullName, clusterId.ToString("D")));

        File.WriteAllText(Path.Combine(directory.FullName, ".aimentor-cluster-id"),
            Guid.NewGuid().ToString("D"));
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateDataProtectionDirectory(
                directory.FullName, clusterId.ToString("D")));

        File.WriteAllText(Path.Combine(directory.FullName, ".aimentor-cluster-id"), clusterId.ToString("D"));
        ProductionSecurityValidators.ValidateDataProtectionDirectory(
            directory.FullName, clusterId.ToString("D"));
    }

    [Fact]
    public void DiscoveryRequiresExpectedIssuerHttpsEndpointsAndSigningKeys()
    {
        var valid = Configuration();
        ProductionSecurityValidators.ValidateOidcDiscovery(valid, "https://identity.example.test/tenant");

        var wrongIssuer = Configuration();
        wrongIssuer.Issuer = "https://identity.example.test/other";
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateOidcDiscovery(
                wrongIssuer, "https://identity.example.test/tenant"));

        var insecureToken = Configuration();
        insecureToken.TokenEndpoint = "http://identity.example.test/token";
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateOidcDiscovery(
                insecureToken, "https://identity.example.test/tenant"));

        var missingKeys = Configuration();
        missingKeys.SigningKeys.Clear();
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateOidcDiscovery(
                missingKeys, "https://identity.example.test/tenant"));
    }

    [Fact]
    public void DataProtectionCertificateMustHaveCurrentlyValidRsaPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AiMentor-Test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        ProductionSecurityValidators.ValidateDataProtectionCertificate(certificate, true);

        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateDataProtectionCertificate(publicOnly, true));
    }

    [Fact]
    public void DataProtectionCertificateRingDecryptsKeysProtectedBeforeRotation()
    {
        var keyDirectory = Directory.CreateDirectory(Path.Combine(_root, "key-ring"));
        using var oldCertificate = Certificate("AiMentor-Old");
        using var newCertificate = Certificate("AiMentor-New");
        using var oldServices = Services(keyDirectory, oldCertificate, [oldCertificate]);
        var oldProtector = oldServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("rotation-test");
        var protectedValue = oldProtector.Protect("server-session");
        var keyRingFingerprint = ProductionSecurityValidators.GetDataProtectionKeyRingFingerprint(oldServices);
        ProductionSecurityValidators.ValidateExistingDataProtectionKeys(oldServices, keyRingFingerprint);

        using var rotatedServices = Services(
            keyDirectory, newCertificate, [newCertificate, oldCertificate]);
        var rotatedProtector = rotatedServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("rotation-test");
        ProductionSecurityValidators.ValidateExistingDataProtectionKeys(rotatedServices, keyRingFingerprint);
        Assert.Equal("server-session", rotatedProtector.Unprotect(protectedValue));

        using var missingOldCertificate = Services(keyDirectory, newCertificate, [newCertificate]);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateExistingDataProtectionKeys(
                missingOldCertificate, keyRingFingerprint));
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecurityValidators.ValidateExistingDataProtectionKeys(
                rotatedServices, new string('0', 64)));
    }

    private static OpenIdConnectConfiguration Configuration()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = "https://identity.example.test/tenant",
            AuthorizationEndpoint = "https://identity.example.test/authorize",
            TokenEndpoint = "https://identity.example.test/token",
            JwksUri = "https://identity.example.test/keys",
            EndSessionEndpoint = "https://identity.example.test/logout",
            UserInfoEndpoint = "https://identity.example.test/userinfo"
        };
        configuration.SigningKeys.Add(new SymmetricSecurityKey(new byte[32]));
        return configuration;
    }

    private static ServiceProvider Services(DirectoryInfo keyDirectory, X509Certificate2 activeCertificate,
        X509Certificate2[] decryptionCertificates)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("AiMentor.Operations.Test")
            .PersistKeysToFileSystem(keyDirectory)
            .ProtectKeysWithCertificate(activeCertificate)
            .UnprotectKeysWithAnyCertificate(decryptionCertificates);
        return services.BuildServiceProvider();
    }

    private static X509Certificate2 Certificate(string commonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
