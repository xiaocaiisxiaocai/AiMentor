using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class DeploymentArtifactTests
{
    [Fact]
    public void ContainerImagePublishesApiAndMigratorIntoNonRootChiseledRuntime()
    {
        var root = FindRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var dockerIgnore = File.ReadAllText(Path.Combine(root, ".dockerignore"));

        Assert.Contains("mcr.microsoft.com/dotnet/sdk:10.0-noble", dockerfile, StringComparison.Ordinal);
        Assert.Contains("sdk:10.0-noble@sha256:", dockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet publish src/AiMentor.Api/AiMentor.Api.csproj", dockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet publish src/AiMentor.Migrations/AiMentor.Migrations.csproj", dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:", dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("COPY --from=publish --chown=1654:1654 /artifacts/api/ /app/api/", dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("COPY --from=publish --chown=1654:1654 /artifacts/migrations/ /app/migrations/", dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("COPY --chown=1654:1654 deploy/sql/ /app/deploy/sql/", dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("EXPOSE 8080", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Migrations__Root=/app/deploy/sql", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER 1654", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"/app/api/AiMentor.Api.dll\"]", dockerfile,
            StringComparison.Ordinal);
        Assert.DoesNotContain("COPY . .", dockerfile, StringComparison.Ordinal);
        Assert.Contains("**", dockerIgnore, StringComparison.Ordinal);
        Assert.Contains("!deploy/sql/**", dockerIgnore, StringComparison.Ordinal);
    }

    [Fact]
    public void HelmChartFailsClosedAroundSecretsMigrationsAndPodSecurity()
    {
        var chartRoot = Path.Combine(FindRoot(), "deploy", "helm", "aimentor");
        var values = File.ReadAllText(Path.Combine(chartRoot, "values.yaml"));
        var normalizedValues = values.ReplaceLineEndings("\n");
        var helpers = File.ReadAllText(Path.Combine(chartRoot, "templates", "_helpers.tpl"));
        var configMap = File.ReadAllText(Path.Combine(chartRoot, "templates", "configmap.yaml"));
        var deployment = File.ReadAllText(Path.Combine(chartRoot, "templates", "deployment.yaml"));
        var migration = File.ReadAllText(Path.Combine(chartRoot, "templates", "migration-job.yaml"));
        var ingress = File.ReadAllText(Path.Combine(chartRoot, "templates", "ingress.yaml"));
        var templates = Directory.GetFiles(Path.Combine(chartRoot, "templates"), "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains("replicaCount: 2", values, StringComparison.Ordinal);
        Assert.Contains("digest: \"\"", values, StringComparison.Ordinal);
        Assert.DoesNotContain("tag:", values, StringComparison.Ordinal);
        Assert.Contains("existingSecret: \"\"", values, StringComparison.Ordinal);
        Assert.Contains("migration:\n  # 迁移器使用独立最小权限 Secret，只读取工作流数据库连接串。\n  existingSecret: \"\"",
            normalizedValues, StringComparison.Ordinal);
        Assert.Contains("existingClaim: \"\"", values, StringComparison.Ordinal);
        Assert.Contains("operationsWeb:\n    enabled: true", normalizedValues, StringComparison.Ordinal);
        Assert.Contains("provider: OpenAI", values, StringComparison.Ordinal);
        Assert.Contains("provider: HttpSemantic", values, StringComparison.Ordinal);
        Assert.Contains("required \"existingSecret", helpers, StringComparison.Ordinal);
        Assert.Contains("required \"migration.existingSecret", helpers, StringComparison.Ordinal);
        Assert.Contains("migration.existingSecret 禁止复用 API existingSecret", helpers, StringComparison.Ordinal);
        Assert.Contains("required \"dataProtection.existingClaim", helpers, StringComparison.Ordinal);
        Assert.Contains("^sha256:[0-9a-f]{64}$", helpers, StringComparison.Ordinal);
        Assert.Contains("releaseId 必须与 image.digest 完全一致", helpers, StringComparison.Ordinal);
        Assert.Contains("printf \"%s@%s\"", helpers, StringComparison.Ordinal);
        Assert.Contains("Workflow__InitializeSchema: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("OpenSearch__SynchronizeOnStartup: \"false\"", configMap, StringComparison.Ordinal);
        Assert.Contains("Authentication__Mode: OidcJwt", configMap, StringComparison.Ordinal);
        Assert.Contains("Migrations__Root: /app/deploy/sql", configMap, StringComparison.Ordinal);
        Assert.Contains("Model__Endpoint:", configMap, StringComparison.Ordinal);
        Assert.Contains("Embedding__Dimensions:", configMap, StringComparison.Ordinal);
        Assert.Contains("Reranker__Model:", configMap, StringComparison.Ordinal);
        Assert.Contains("Production 禁止使用 Deterministic", configMap, StringComparison.Ordinal);
        Assert.Contains("Production 禁止使用 Lexical", configMap, StringComparison.Ordinal);
        Assert.Contains("Production 禁止关闭 application.operationsWeb.enabled", configMap,
            StringComparison.Ordinal);
        Assert.DoesNotContain("__ApiKey:", configMap, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret:", configMap, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__", configMap, StringComparison.Ordinal);

        Assert.Contains("maxUnavailable: 0", deployment, StringComparison.Ordinal);
        Assert.Contains("readOnlyRootFilesystem: true", deployment, StringComparison.Ordinal);
        Assert.Contains("allowPrivilegeEscalation: false", deployment, StringComparison.Ordinal);
        Assert.Contains("runAsUser: 1654", deployment, StringComparison.Ordinal);
        Assert.Contains("seccompProfile:", deployment, StringComparison.Ordinal);
        Assert.Contains("emptyDir:", deployment, StringComparison.Ordinal);
        Assert.Contains("persistentVolumeClaim:", deployment, StringComparison.Ordinal);
        Assert.Contains("startupProbe:", deployment, StringComparison.Ordinal);
        Assert.Contains("path: /health/live", deployment, StringComparison.Ordinal);
        Assert.Contains("path: /health/ready", deployment, StringComparison.Ordinal);
        Assert.Contains("name: POD_UID", deployment, StringComparison.Ordinal);
        Assert.Contains("fieldPath: metadata.uid", deployment, StringComparison.Ordinal);
        Assert.Contains("service.name=AiMentor.Api,service.instance.id=$(POD_UID)", deployment,
            StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: api", helpers, StringComparison.Ordinal);
        Assert.True(deployment.IndexOf("secretRef:", StringComparison.Ordinal)
                    < deployment.IndexOf("configMapRef:", StringComparison.Ordinal));

        Assert.Contains("\"helm.sh/hook\": pre-install,pre-upgrade", migration, StringComparison.Ordinal);
        Assert.Contains("\"helm.sh/hook-delete-policy\": before-hook-creation,hook-succeeded", migration,
            StringComparison.Ordinal);
        Assert.Contains("backoffLimit: 0", migration, StringComparison.Ordinal);
        Assert.Contains("/app/migrations/AiMentor.Migrations.dll", migration, StringComparison.Ordinal);
        Assert.Contains("AIMENTOR_RELEASE_ID", migration, StringComparison.Ordinal);
        Assert.Contains("name: ConnectionStrings__WorkflowSqlServer", migration, StringComparison.Ordinal);
        Assert.Contains("secretKeyRef:", migration, StringComparison.Ordinal);
        Assert.Contains("include \"aimentor.migrationExistingSecret\"", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("envFrom:", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("include \"aimentor.existingSecret\"", migration, StringComparison.Ordinal);
        Assert.Contains("readOnlyRootFilesystem: true", migration, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/component: migration", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("include \"aimentor.selectorLabels\"", migration, StringComparison.Ordinal);

        Assert.Contains("ingress.tlsSecretName 必须引用现有 TLS Secret", ingress, StringComparison.Ordinal);
        Assert.Contains("kind: PodDisruptionBudget", string.Join('\n', templates), StringComparison.Ordinal);
        Assert.Contains("kind: NetworkPolicy", string.Join('\n', templates), StringComparison.Ordinal);
        Assert.DoesNotContain("kind: Secret", string.Join('\n', templates), StringComparison.Ordinal);
        Assert.DoesNotContain("stringData:", string.Join('\n', templates), StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot();
        return Directory.GetParent(Directory.GetParent(knowledgeRoot)!.FullName)!.FullName;
    }
}
