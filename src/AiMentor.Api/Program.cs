using System.Text.Json.Serialization;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using AiMentor.Api;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var questionRateLimit = builder.Configuration.GetValue("Api:QuestionRateLimitPerMinute", 60);
if (questionRateLimit <= 0) throw new InvalidOperationException("Api:QuestionRateLimitPerMinute 必须大于 0。");
var enableLegacyV0 = builder.Configuration.GetValue("Api:EnableLegacyV0", false);
var workflowProvider = builder.Configuration["Workflow:Provider"] ?? "InMemory";
var authenticationOptions = new AiMentorAuthenticationOptions
{
    Mode = builder.Configuration["Authentication:Mode"] ?? "Development",
    Authority = builder.Configuration["Authentication:Authority"],
    Audience = builder.Configuration["Authentication:Audience"],
    RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true),
    RefreshIntervalSeconds = builder.Configuration.GetValue("Authentication:RefreshIntervalSeconds", 300),
    SubjectClaim = builder.Configuration["Authentication:SubjectClaim"] ?? "sub",
    TenantClaim = builder.Configuration["Authentication:TenantClaim"] ?? "tenant_id",
    GroupsClaim = builder.Configuration["Authentication:GroupsClaim"] ?? "groups",
    DevelopmentTenantId = builder.Configuration["Authentication:Development:TenantId"] ?? "demo-beichen",
    DevelopmentSubjectId = builder.Configuration["Authentication:Development:SubjectId"] ?? "development-user",
    DevelopmentGroups = builder.Configuration.GetSection("Authentication:Development:Groups").Get<string[]>() ?? ["all-rnd"]
};
var jwtAuthenticationEnabled = string.Equals(authenticationOptions.Mode, "OidcJwt", StringComparison.OrdinalIgnoreCase);
if (builder.Environment.IsProduction()
    && (!jwtAuthenticationEnabled || enableLegacyV0
        || !string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Production 环境必须启用 OIDC JWT、禁用 V0，并使用 Workflow:Provider=SqlServer。");
if (jwtAuthenticationEnabled)
{
    if (!Uri.TryCreate(authenticationOptions.Authority, UriKind.Absolute, out var authorityUri)
        || (authorityUri.Scheme != Uri.UriSchemeHttps && authorityUri.Scheme != Uri.UriSchemeHttp)
        || (authorityUri.Scheme == Uri.UriSchemeHttp
            && (builder.Environment.IsProduction() || authenticationOptions.RequireHttpsMetadata
                || !authorityUri.IsLoopback)))
        throw new InvalidOperationException("OIDC JWT Authority 无效；仅非生产环境可显式允许 loopback HTTP。");
    if (string.IsNullOrWhiteSpace(authenticationOptions.Audience))
        throw new InvalidOperationException("OIDC JWT 模式要求配置 Authentication:Audience。");
    if (authenticationOptions.RefreshIntervalSeconds is < 1 or > 86_400)
        throw new InvalidOperationException("Authentication:RefreshIntervalSeconds 必须在 1 到 86400 秒之间。");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwtOptions =>
    {
        jwtOptions.Authority = authenticationOptions.Authority;
        jwtOptions.Audience = authenticationOptions.Audience;
        jwtOptions.RequireHttpsMetadata = authenticationOptions.RequireHttpsMetadata;
        jwtOptions.RefreshOnIssuerKeyNotFound = true;
        jwtOptions.RefreshInterval = TimeSpan.FromSeconds(authenticationOptions.RefreshIntervalSeconds);
        jwtOptions.AutomaticRefreshInterval = TimeSpan.FromHours(12);
        jwtOptions.MapInboundClaims = false;
        jwtOptions.IncludeErrorDetails = false;
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = authenticationOptions.SubjectClaim,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        jwtOptions.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                try { _ = ClaimsAccessContextProvider.CreateValidated(context.Principal!, authenticationOptions); }
                catch (UnauthorizedAccessException) { context.Fail("JWT 身份声明无效或存在歧义。"); }
                return Task.CompletedTask;
            }
        };
    });
    builder.Services.AddAuthorization(authorizationOptions => authorizationOptions.AddPolicy("question-api", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(authenticationOptions.SubjectClaim);
        policy.RequireClaim(authenticationOptions.TenantClaim);
    }));
    builder.Services.AddSingleton<IRequestAccessContextProvider>(new ClaimsAccessContextProvider(authenticationOptions));
}
else
{
    builder.Services.AddSingleton<IRequestAccessContextProvider>(new DevelopmentAccessContextProvider(authenticationOptions));
}
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(openApiOptions =>
{
    if (jwtAuthenticationEnabled)
    {
        openApiOptions.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        openApiOptions.AddOperationTransformer<BearerSecuritySchemeTransformer>();
    }
});
builder.Services.AddValidation();
builder.Services.AddRateLimiter(rateLimitOptions =>
{
    rateLimitOptions.AddPolicy("questions", context =>
    {
        var partitionKey = context.User.FindFirst(authenticationOptions.SubjectClaim)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = questionRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
    rateLimitOptions.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "请求过于频繁",
            Detail = $"问答 API 每个调用方每分钟最多允许 {questionRateLimit} 次请求。",
            Type = "https://httpstatuses.com/429",
            Instance = context.HttpContext.Request.Path
        };
        await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };
});

var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(
    builder.Configuration["Knowledge:RootPath"] ?? Environment.GetEnvironmentVariable("AIMENTOR_KNOWLEDGE_ROOT"));
builder.Services.AddSingleton(new MarkdownKnowledgeRepository(knowledgeRoot));
builder.Services.AddSingleton<IKnowledgeChunkSource>(services => services.GetRequiredService<MarkdownKnowledgeRepository>());
var modelProviderOptions = ReadModelProviderOptions(builder.Configuration, "Model", "AIMENTOR_MODEL_API_KEY",
    AiProviderKind.Deterministic);
var embeddingProviderOptions = ReadEmbeddingProviderOptions(builder.Configuration);
var rerankerProviderOptions = ReadRerankerProviderOptions(builder.Configuration);
var baseChatClient = AiProviderFactory.CreateChat(modelProviderOptions);
var embeddingGenerator = AiProviderFactory.CreateEmbedding(embeddingProviderOptions);
var evidenceReranker = AiProviderFactory.CreateReranker(rerankerProviderOptions);
var aiRuntime = new AiRuntimeDescriptor(modelProviderOptions.Provider.ToString(),
    embeddingProviderOptions.Provider.ToString(), rerankerProviderOptions.Provider.ToString(),
    modelProviderOptions.Provider == AiProviderKind.Deterministic,
    embeddingProviderOptions.Provider == AiProviderKind.Deterministic,
    rerankerProviderOptions.Provider == AiProviderKind.Lexical,
    embeddingGenerator.Dimensions, embeddingProviderOptions.IndexVersion, true);
builder.Services.AddSingleton(aiRuntime);
builder.Services.AddSingleton<ITextEmbeddingGenerator>(embeddingGenerator);

var ragProvider = builder.Configuration["Rag:Provider"] ?? "Local";
if (string.Equals(ragProvider, "OpenSearch", StringComparison.OrdinalIgnoreCase))
{
    var endpoint = builder.Configuration["OpenSearch:Endpoint"] ?? "http://127.0.0.1:9200";
    var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
    var username = builder.Configuration["OpenSearch:Username"] ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_USERNAME");
    var password = builder.Configuration["OpenSearch:Password"] ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_PASSWORD");
    if (!string.IsNullOrWhiteSpace(username) && password is not null)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }
    builder.Services.AddSingleton(client);
    var openSearchIndexName = builder.Configuration["OpenSearch:IndexName"] ?? "aimentor-knowledge-v1";
    var synchronizeOpenSearchOnStartup = builder.Configuration.GetValue("OpenSearch:SynchronizeOnStartup", true);
    if (builder.Environment.IsProduction() && synchronizeOpenSearchOnStartup)
        throw new InvalidOperationException("Production 禁止 OpenSearch 启动同步，必须通过蓝绿索引发布流程切换 alias。");
    if (builder.Environment.IsProduction()
        && !string.Equals(openSearchIndexName, "aimentor-knowledge-current", StringComparison.Ordinal))
        throw new InvalidOperationException("Production OpenSearch 读路径必须使用 aimentor-knowledge-current alias。");
    builder.Services.AddSingleton(new OpenSearchOptions
    {
        IndexName = openSearchIndexName,
        SearchPipelineName = builder.Configuration["OpenSearch:SearchPipelineName"] ?? "aimentor-hybrid-v1",
        SynchronizeOnStartup = synchronizeOpenSearchOnStartup
    });
    builder.Services.AddSingleton<IKnowledgeRepository, OpenSearchKnowledgeRepository>();
}
else
{
    builder.Services.AddSingleton<IKnowledgeRepository>(services => services.GetRequiredService<MarkdownKnowledgeRepository>());
}
builder.Services.AddSingleton<IInputSafetyService, RuleBasedInputSafetyService>();
builder.Services.AddSingleton<IQueryNormalizer, RuleBasedQueryNormalizer>();
builder.Services.AddSingleton<IRetrievedContentSafetyService, RuleBasedRetrievedContentSafetyService>();
builder.Services.AddSingleton(new ToolSafetyOptions
{
    AllowedTools = new HashSet<string>(["knowledge.stats", "memory.delete", "memory.correct"],
        StringComparer.OrdinalIgnoreCase)
});
builder.Services.AddSingleton<IToolInvocationSafetyService, RuleBasedToolInvocationSafetyService>();
builder.Services.AddSingleton<IServerTool, KnowledgeStatisticsTool>();
builder.Services.AddSingleton<IServerTool, MemoryDeleteTool>();
builder.Services.AddSingleton<IServerTool, MemoryCorrectTool>();
builder.Services.AddSingleton<IToolRegistry, ServerToolRegistry>();
builder.Services.AddSingleton<IToolCompensationCatalog, ToolCompensationCatalog>();
builder.Services.AddSingleton(new ToolApprovalOptions());
builder.Services.AddSingleton<IToolApprovalService>(services =>
    string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<SqlServerToolApprovalService>(services)
        : ActivatorUtilities.CreateInstance<InMemoryToolApprovalService>(services));
builder.Services.AddSingleton(new ToolExecutorOptions());
var compensationReconcilerGroups = builder.Configuration
    .GetSection("ToolCompensation:ReconcilerGroups").Get<string[]>()
    ?? ["tool-reconcilers"];
var compensationEvidenceLifetimeSeconds = builder.Configuration.GetValue(
    "ToolCompensation:ReconciliationEvidenceLifetimeSeconds", 300);
if (compensationReconcilerGroups.Length == 0
    || compensationReconcilerGroups.Any(string.IsNullOrWhiteSpace)
    || compensationEvidenceLifetimeSeconds is < 30 or > 3_600)
    throw new InvalidOperationException(
        "ToolCompensation 对账组不能为空，证据有效期必须在 30 到 3600 秒之间。");
builder.Services.AddSingleton(new ToolCompensationOptions
{
    ReconcilerGroups = new HashSet<string>(compensationReconcilerGroups, StringComparer.OrdinalIgnoreCase),
    ReconciliationEvidenceLifetime = TimeSpan.FromSeconds(compensationEvidenceLifetimeSeconds)
});
var barrierSignalPath = builder.Configuration["Testing:ToolExecutionBarrier:SignalPath"];
var barrierReleasePath = builder.Configuration["Testing:ToolExecutionBarrier:ReleasePath"];
if (builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrWhiteSpace(barrierSignalPath) != string.IsNullOrWhiteSpace(barrierReleasePath))
    throw new InvalidOperationException("Testing 工具执行屏障必须同时配置信号文件和释放文件。");
if (builder.Environment.IsEnvironment("Testing")
    && !string.IsNullOrWhiteSpace(barrierSignalPath)
    && !string.IsNullOrWhiteSpace(barrierReleasePath))
{
    // 故障注入只能由 Testing 环境显式开启；生产和普通开发环境始终走空屏障。
    builder.Services.AddSingleton<IToolExecutionBarrier>(new FileToolExecutionBarrier(
        new FileToolExecutionBarrierOptions
        {
            SignalPath = barrierSignalPath,
            ReleasePath = barrierReleasePath
        }));
}
else
{
    builder.Services.AddSingleton<IToolExecutionBarrier>(NoOpToolExecutionBarrier.Instance);
}
builder.Services.AddSingleton<IToolExecutor, SafeToolExecutor>();
builder.Services.AddSingleton(new ToolExecutionReconciliationOptions());
builder.Services.AddSingleton<IToolOutcomeProbe, MemoryDeleteOutcomeProbe>();
builder.Services.AddSingleton<IToolExecutionReconciliationService, ToolExecutionReconciliationService>();
builder.Services.AddSingleton<IToolCompensationOutcomeProbe, MemoryCorrectRestoreOutcomeProbe>();
var agentExecutionOptions = new AgentExecutionOptions
{
    // 最大运行时间可长于租约；活动恢复实例通过短租约心跳维持独占权。
    MaximumRunTime = TimeSpan.FromSeconds(builder.Configuration.GetValue("Agent:MaximumRunTimeSeconds", 10)),
    ResumeLeaseDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue("Agent:ResumeLeaseDurationSeconds", 30)),
    ResumeLeaseRenewalInterval = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Agent:ResumeLeaseRenewalIntervalSeconds", 10))
};
builder.Services.AddSingleton(agentExecutionOptions);
builder.Services.AddSingleton<IAgentRunner, AgentFrameworkToolRunner>();
builder.Services.AddSingleton<IOutputSafetyService, RuleBasedOutputSafetyService>();
builder.Services.AddSingleton<IEvidenceReranker>(evidenceReranker);
builder.Services.AddSingleton<ICitationMapper, RuleBasedCitationMapper>();
builder.Services.AddSingleton<ICitationVerifier, RuleBasedCitationVerifier>();
builder.Services.AddSingleton<IEvidenceConflictDetector, RuleBasedEvidenceConflictDetector>();
builder.Services.AddSingleton<IEvidenceSufficiencyEvaluator, RuleBasedEvidenceSufficiencyEvaluator>();
builder.Services.AddSingleton<InMemoryTraceSink>();
builder.Services.AddSingleton<OpenTelemetryTraceSink>();
builder.Services.AddSingleton<ITraceSink>(services => new CompositeTraceSink(
    services.GetRequiredService<InMemoryTraceSink>(), services.GetRequiredService<OpenTelemetryTraceSink>()));
builder.Services.AddSingleton<IChatClient>(services => new ChatClientBuilder(baseChatClient)
    .UseOpenTelemetry(services.GetRequiredService<ILoggerFactory>(), "AiMentor.Model",
        telemetry => telemetry.EnableSensitiveData = false)
    .Build(services));
builder.Services.AddSingleton<IAnswerComposer, AgentFrameworkAnswerComposer>();
builder.Services.AddSingleton(new TrustedQuestionOptions());
builder.Services.AddSingleton<ITrustedQuestionService, TrustedQuestionService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new MemoryWorkflowOptions());
var memoryDataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
var memoryStorePath = builder.Configuration["Memory:StorePath"]
    ?? Path.Combine(memoryDataDirectory, "aimentor-memory.json");
var configuredMemoryKey = builder.Configuration["Memory:EncryptionKey"]
    ?? Environment.GetEnvironmentVariable("AIMENTOR_MEMORY_ENCRYPTION_KEY");
var memoryKey = ResolveMemoryMasterKey(
    configuredMemoryKey,
    Path.Combine(memoryDataDirectory, "memory.key"), builder.Environment.IsProduction());
var memoryCipher = new AesGcmMemoryCipher(memoryKey);
builder.Services.AddSingleton<IMemoryCipher>(memoryCipher);
var workflowKeyVersion = builder.Configuration["Workflow:Encryption:ActiveKeyVersion"] ?? "v1";
var workflowKeys = ResolveWorkflowKeys(builder.Configuration, workflowKeyVersion, memoryKey,
    builder.Environment.IsProduction() && string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase));
builder.Services.AddSingleton<IWorkflowStateCipher>(
    new AesGcmWorkflowStateCipher(workflowKeyVersion, workflowKeys, memoryCipher));
var sqlConnectionConfigured = false;
var sqlEncrypt = false;
var sqlTrustServerCertificate = false;
if (string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("WorkflowSqlServer")
        ?? Environment.GetEnvironmentVariable("AIMENTOR_SQLSERVER_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Workflow:Provider=SqlServer 时必须配置 ConnectionStrings:WorkflowSqlServer。");
    var sqlConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
    sqlConnectionConfigured = true;
    sqlEncrypt = sqlConnection.Encrypt;
    sqlTrustServerCertificate = sqlConnection.TrustServerCertificate;
    if (builder.Environment.IsProduction() && (!sqlConnection.Encrypt || sqlConnection.TrustServerCertificate))
        throw new InvalidOperationException("Production SQL Server 连接必须启用 Encrypt 且禁用 TrustServerCertificate。");
    builder.Services.AddSingleton(new SqlServerWorkflowOptions
    {
        ConnectionString = connectionString,
        MaximumPendingRuns = builder.Configuration.GetValue("Workflow:MaximumPendingRuns", 10_000),
        InitializeSchema = builder.Configuration.GetValue("Workflow:InitializeSchema", !builder.Environment.IsProduction())
    });
    builder.Services.AddSingleton<IAgentRunCheckpointStore, SqlServerAgentRunCheckpointStore>();
    builder.Services.AddSingleton<IToolExecutionLedger, SqlServerToolExecutionLedger>();
    builder.Services.AddSingleton<IToolCompensationService, SqlServerToolCompensationService>();
}
else if (string.Equals(workflowProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IAgentRunCheckpointStore>(services =>
        new InMemoryAgentRunCheckpointStore(services.GetRequiredService<TimeProvider>(), 1_000));
    builder.Services.AddSingleton<IToolExecutionLedger, InMemoryToolExecutionLedger>();
    builder.Services.AddSingleton<IToolCompensationService, InMemoryToolCompensationService>();
}
else
{
    throw new InvalidOperationException("Workflow:Provider 仅支持 InMemory 或 SqlServer。");
}
builder.Services.AddSingleton<IToolCompensationReconciliationService>(services =>
    services.GetRequiredService<IToolCompensationService>() as IToolCompensationReconciliationService
    ?? throw new InvalidOperationException("当前补偿存储未实现结果不确定对账契约。"));
builder.Services.AddSingleton(new EncryptedFileMemoryStoreOptions { FilePath = memoryStorePath });
builder.Services.AddSingleton<IMemoryStore, EncryptedFileMemoryStore>();
builder.Services.AddSingleton<IMemoryContentSafetyService, RuleBasedMemoryContentSafetyService>();
builder.Services.AddSingleton<IMemoryWorkflowService, MemoryWorkflowService>();
builder.Services.AddSingleton(new MemoryContextOptions());
builder.Services.AddSingleton<IMemoryContextProvider, SafeMemoryContextProvider>();
builder.Services.AddSingleton(new AtlasIncidentWorkflowOptions());
if (string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IAtlasIncidentStore, SqlServerAtlasIncidentStore>();
else
    builder.Services.AddSingleton<IAtlasIncidentStore, InMemoryAtlasIncidentStore>();
builder.Services.AddSingleton<IAtlasIncidentWorkflow, AtlasIncidentWorkflow>();
var operationsReviewerGroups = builder.Configuration.GetSection("Operations:ReviewerGroups").Get<string[]>()
    ?? ["tool-approvers"];
var operationsEscalatorGroups = builder.Configuration.GetSection("Operations:EscalatorGroups").Get<string[]>()
    ?? ["operations-escalators"];
var operationsReviewLifetimeSeconds = builder.Configuration.GetValue("Operations:ReviewLifetimeSeconds", 900);
var operationsMaximumPendingActions = builder.Configuration.GetValue("Operations:MaximumPendingActions", 10_000);
if (operationsReviewerGroups.Length == 0 || operationsReviewerGroups.Any(string.IsNullOrWhiteSpace)
    || operationsEscalatorGroups.Length == 0 || operationsEscalatorGroups.Any(string.IsNullOrWhiteSpace)
    || operationsReviewLifetimeSeconds is < 30 or > 86_400 || operationsMaximumPendingActions <= 0)
    throw new InvalidOperationException("Operations 复核组、升级组或复核期限配置无效。");
builder.Services.AddSingleton(new OperationsActionOptions
{
    ReviewerGroups = new HashSet<string>(operationsReviewerGroups, StringComparer.OrdinalIgnoreCase),
    EscalatorGroups = new HashSet<string>(operationsEscalatorGroups, StringComparer.OrdinalIgnoreCase),
    ReviewLifetime = TimeSpan.FromSeconds(operationsReviewLifetimeSeconds),
    MaximumEntries = operationsMaximumPendingActions
});
builder.Services.AddSingleton<IOperationsActionStore>(services =>
    string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<SqlServerOperationsActionStore>(services)
        : ActivatorUtilities.CreateInstance<InMemoryOperationsActionStore>(services));
builder.Services.AddSingleton<IOperationsTaskService, OperationsTaskService>();
builder.Services.AddSingleton<IOperationsActionService, OperationsActionService>();

var configuredWorkflowKeys = builder.Configuration.GetSection("Workflow:Encryption:Keys").GetChildren().ToArray();
var openSearchUsername = builder.Configuration["OpenSearch:Username"]
    ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_USERNAME");
var openSearchPassword = builder.Configuration["OpenSearch:Password"]
    ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_PASSWORD");
builder.Services.AddSingleton(new SystemDoctorOptions
{
    IsProduction = builder.Environment.IsProduction(),
    LegacyApiEnabled = enableLegacyV0,
    AuthenticationMode = authenticationOptions.Mode,
    AuthenticationAuthority = authenticationOptions.Authority,
    AuthenticationRequireHttpsMetadata = authenticationOptions.RequireHttpsMetadata,
    AuthenticationAudienceConfigured = !string.IsNullOrWhiteSpace(authenticationOptions.Audience),
    SubjectClaimConfigured = !string.IsNullOrWhiteSpace(authenticationOptions.SubjectClaim),
    TenantClaimConfigured = !string.IsNullOrWhiteSpace(authenticationOptions.TenantClaim),
    WorkflowProvider = workflowProvider,
    AtlasIncidentStorePersistent = string.Equals(workflowProvider, "SqlServer", StringComparison.OrdinalIgnoreCase),
    MemoryKeyConfigured = !string.IsNullOrWhiteSpace(configuredMemoryKey),
    MemoryKeyValid = memoryKey.Length == 32,
    WorkflowKeyRingConfigured = configuredWorkflowKeys.Length > 0,
    WorkflowActiveKeyPresent = workflowKeys.ContainsKey(workflowKeyVersion),
    WorkflowUsesIndependentKey = workflowKeys.TryGetValue(workflowKeyVersion, out var activeWorkflowKey)
        && !activeWorkflowKey.AsSpan().SequenceEqual(memoryKey),
    SqlConnectionConfigured = sqlConnectionConfigured,
    SqlEncrypt = sqlEncrypt,
    SqlTrustServerCertificate = sqlTrustServerCertificate,
    RagProvider = ragProvider,
    OpenSearchEndpoint = builder.Configuration["OpenSearch:Endpoint"] ?? "http://127.0.0.1:9200",
    OpenSearchAuthenticationConfigured = !string.IsNullOrWhiteSpace(openSearchUsername)
        && !string.IsNullOrWhiteSpace(openSearchPassword),
    // 当前注册类型是沙箱实现；不能仅凭配置名称把它误报为生产供应商。
    ModelProvider = aiRuntime.ModelProvider,
    EmbeddingProvider = aiRuntime.EmbeddingProvider,
    RerankerProvider = aiRuntime.RerankerProvider,
    EmbeddingDimensions = embeddingGenerator.Dimensions,
    ExpectedEmbeddingDimensions = builder.Configuration.GetValue("OpenSearch:VectorDimensions",
        embeddingGenerator.Dimensions),
    EmbeddingIndexVersion = aiRuntime.EmbeddingIndexVersion,
    ExpectedEmbeddingIndexVersion = builder.Configuration["OpenSearch:IndexName"] ?? "aimentor-knowledge-v1",
    AgentMaximumRunTime = agentExecutionOptions.MaximumRunTime,
    AgentResumeLeaseDuration = agentExecutionOptions.ResumeLeaseDuration,
    AgentResumeLeaseRenewalInterval = agentExecutionOptions.ResumeLeaseRenewalInterval
});
builder.Services.AddSingleton<ISystemDoctor, SystemDoctor>();

var telemetryBuilder = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(OpenTelemetryTraceSink.SourceName)
        .AddSource("AiMentor.Model"));
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var otlpUri)
        || (builder.Environment.IsProduction() && otlpUri.Scheme != Uri.UriSchemeHttps))
        throw new InvalidOperationException("Telemetry:OtlpEndpoint 无效，生产环境必须使用 HTTPS。");
    telemetryBuilder.WithTracing(tracing => tracing.AddOtlpExporter(options => options.Endpoint = otlpUri));
}

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
if (jwtAuthenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseRateLimiter();
app.MapOpenApi();
var systemDoctor = app.Services.GetRequiredService<ISystemDoctor>();
var startupDiagnostic = await systemDoctor.RunAsync();
if (builder.Environment.IsProduction() && !startupDiagnostic.IsReady)
{
    // 生产关键配置失败时必须在接收流量前关闭，且只输出稳定代码，避免泄漏密钥和连接信息。
    var codes = string.Join(",", startupDiagnostic.Checks
        .Where(item => item.Status == SystemCheckStatus.Failed).Select(item => item.Code));
    throw new InvalidOperationException($"SystemDoctor 生产配置检查失败：{codes}");
}
var repository = app.Services.GetRequiredService<IKnowledgeRepository>();
await repository.InitializeAsync();

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
    .WithName("Liveness").WithTags("System").DisableRateLimiting();
app.MapGet("/health/ready", ReadyAsync)
    .WithName("Readiness").WithTags("System").DisableRateLimiting();
app.MapGet("/health", async (ISystemDoctor doctor, CancellationToken cancellationToken) =>
{
    var report = await doctor.RunAsync(cancellationToken);
    return Results.Json(new
    {
        status = report.IsReady ? "healthy" : "unready",
        ragProvider,
        workflowProvider,
        workflowKeyVersion,
        authenticationMode = authenticationOptions.Mode,
        knowledge = repository.Statistics,
        diagnostics = report
    }, statusCode: report.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
})
    .WithName("Health").WithTags("System").DisableRateLimiting();

var v1 = app.MapGroup("/api/v1").WithTags("AiMentor v1");
if (jwtAuthenticationEnabled) v1.RequireAuthorization("question-api");
v1.MapGet("/operations/tasks", ListOperationsTasksAsync)
    .WithName("ListOperationsTasksV1").WithTags("AiMentor operations v1")
    .Produces<OperationsTaskPage>();
v1.MapGet("/operations/actions", ListOperationsActionsAsync)
    .WithName("ListOperationsActionsV1").WithTags("AiMentor operations v1")
    .Produces<IReadOnlyList<OperationsActionSummary>>();
v1.MapPost("/operations/tasks/{targetType}/{targetId}/actions", RequestOperationsActionAsync)
    .WithName("RequestOperationsActionV1").WithTags("AiMentor operations v1")
    .Produces<OperationsActionSummary>(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status409Conflict);
v1.MapPost("/operations/actions/{requestId}/review", ReviewOperationsActionAsync)
    .WithName("ReviewOperationsActionV1").WithTags("AiMentor operations v1")
    .Produces<OperationsActionSummary>()
    .ProducesProblem(StatusCodes.Status409Conflict);
app.MapGet("/ops", () => Results.Redirect("/ops/index.html", permanent: false)).ExcludeFromDescription();
v1.MapGet("/knowledge/stats", () => Results.Ok(repository.Statistics))
    .WithName("GetKnowledgeStatisticsV1").Produces<KnowledgeStatistics>();
v1.MapPost("/questions", AskAsync)
    .WithName("AskQuestionV1")
    .WithSummary("提交可信问答请求")
    .Produces<TrustedAnswer>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting("questions");
v1.MapPost("/questions/stream", StreamAsync)
    .WithName("StreamQuestionV1")
    .WithSummary("通过 Server-Sent Events 返回可信问答事件")
    .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .RequireRateLimiting("questions");

var memories = v1.MapGroup("/memories").WithTags("AiMentor memory v1");
memories.MapPost("/proposals", ProposeMemoryAsync)
    .WithName("ProposeMemoryV1")
    .WithSummary("提出待用户显式批准的记忆变更")
    .Produces<MemoryProposal>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .RequireRateLimiting("questions");
memories.MapPost("/proposals/{proposalId}/approve", ApproveMemoryAsync)
    .WithName("ApproveMemoryV1")
    .WithSummary("批准记忆提案并创建正式记忆")
    .Produces<MemoryRecord>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
memories.MapGet("/", ListMemoriesAsync)
    .WithName("ListMemoriesV1")
    .WithSummary("查看当前用户尚未过期的已批准记忆")
    .Produces<IReadOnlyList<MemoryRecord>>()
    .RequireRateLimiting("questions");
memories.MapPut("/{memoryId}", CorrectMemoryAsync)
    .WithName("CorrectMemoryV1")
    .WithSummary("使用乐观版本号更正已批准记忆")
    .Produces<MemoryRecord>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
memories.MapDelete("/{memoryId}", DeleteMemoryAsync)
    .WithName("DeleteMemoryV1")
    .WithSummary("使用乐观版本号删除已批准记忆")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");

var tools = v1.MapGroup("/tools").WithTags("AiMentor tools v1");
tools.MapGet("/", (IToolRegistry registry) => Results.Ok(registry.Descriptors))
    .WithName("ListToolsV1")
    .WithSummary("列出服务器注册的工具及其服务端风险配置")
    .Produces<IReadOnlyList<ToolDescriptor>>()
    .RequireRateLimiting("questions");
tools.MapGet("/{toolName}/compensation", (string toolName, IToolCompensationCatalog catalog) =>
        catalog.TryDescribe(toolName, out var descriptor)
            ? Results.Ok(descriptor)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "工具不存在",
                detail: "请求的工具未在服务器注册表中。",
                extensions: new Dictionary<string, object?> { ["code"] = "TOOL_NOT_REGISTERED" }))
    .WithName("DescribeToolCompensationV1")
    .WithSummary("查询服务器对指定工具可证明的补偿能力和安全边界")
    .Produces<ToolCompensationDescriptor>()
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");
tools.MapPost("/{toolName}/execute", ExecuteToolAsync)
    .WithName("ExecuteToolV1")
    .WithSummary("通过强制安全执行器调用服务器注册工具")
    .Produces<ToolExecutionResult>()
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
    .ProducesProblem(StatusCodes.Status504GatewayTimeout)
    .RequireRateLimiting("questions");

var toolApprovals = v1.MapGroup("/tool-approvals").WithTags("AiMentor tool approvals v1");
toolApprovals.MapPost("/", RequestToolApprovalAsync)
    .WithName("RequestToolApprovalV1")
    .WithSummary("为精确的修改性工具调用申请短期审批")
    .Produces<ToolApprovalRequest>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
toolApprovals.MapGet("/", ListToolApprovalsAsync)
    .WithName("ListToolApprovalsV1")
    .WithSummary("查看当前申请人或当前租户审批人可访问的审批")
    .Produces<IReadOnlyList<ToolApprovalRequest>>()
    .RequireRateLimiting("questions");
toolApprovals.MapPost("/{approvalId}/decision", DecideToolApprovalAsync)
    .WithName("DecideToolApprovalV1")
    .WithSummary("由独立工具审批人批准或拒绝申请")
    .Produces<ToolApprovalRequest>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");

var toolCompensations = v1.MapGroup("/tool-compensations").WithTags("AiMentor tool compensations v1");
toolCompensations.MapGet("/", ListToolCompensationsAsync)
    .WithName("ListToolCompensationsV1")
    .WithSummary("查看当前申请人或当前租户审批人可访问的补偿摘要")
    .Produces<IReadOnlyList<ToolCompensationSummary>>()
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting("questions");
toolCompensations.MapPost("/{compensationId}/approval", RequestToolCompensationApprovalAsync)
    .WithName("RequestToolCompensationApprovalV1")
    .WithSummary("为绑定正向执行的补偿操作申请独立短期审批")
    .Produces<ToolCompensationSummary>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
toolCompensations.MapPost("/{compensationId}/decision", DecideToolCompensationAsync)
    .WithName("DecideToolCompensationV1")
    .WithSummary("由独立工具审批人批准或拒绝精确补偿记录")
    .Produces<ToolCompensationSummary>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
toolCompensations.MapPost("/{compensationId}/execute", ExecuteToolCompensationAsync)
    .WithName("ExecuteToolCompensationV1")
    .WithSummary("使用独立审批和独立 Idempotency-Key 执行一次反向补偿")
    .Produces<ToolCompensationExecutionResult>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting("questions");
toolCompensations.MapPost("/{compensationId}/probe", ProbeOutcomeUnknownToolCompensationAsync)
    .WithName("ProbeOutcomeUnknownToolCompensationV1")
    .WithSummary("由工具对账人员只读核验结果不确定反向补偿的目标状态")
    .Produces<ToolCompensationOutcomeProbeResult>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");
toolCompensations.MapPost("/{compensationId}/reviews", ReviewOutcomeUnknownToolCompensationAsync)
    .WithName("ReviewOutcomeUnknownToolCompensationV1")
    .WithSummary("由两名不同工具对账人员在证据有效期内复核结果不确定反向补偿")
    .Produces<ToolCompensationReconciliationReviewResult>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");

var toolExecutions = v1.MapGroup("/tool-executions").WithTags("AiMentor tool reconciliation v1");
toolExecutions.MapGet("/outcome-unknown", ListOutcomeUnknownToolExecutionsAsync)
    .WithName("ListOutcomeUnknownToolExecutionsV1")
    .WithSummary("由当前租户的工具对账人员查看结果不确定执行摘要")
    .Produces<IReadOnlyList<OutcomeUnknownToolExecution>>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .RequireRateLimiting("questions");
toolExecutions.MapPost("/{executionKey}/probe", ProbeOutcomeUnknownToolExecutionAsync)
    .WithName("ProbeOutcomeUnknownToolExecutionV1")
    .WithSummary("使用与原调用匹配的参数只读核验结果不确定操作的目标状态")
    .Produces<ToolOutcomeProbeResult>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");
toolExecutions.MapPost("/{executionKey}/reviews", ReviewOutcomeUnknownToolExecutionAsync)
    .WithName("ReviewOutcomeUnknownToolExecutionV1")
    .WithSummary("由两名不同对账人员在证据有效期内依次复核结果不确定操作")
    .Produces<ToolReconciliationReviewResult>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");

var agents = v1.MapGroup("/agents").WithTags("AiMentor agents v1");
agents.MapPost("/runs", RunAgentAsync)
    .WithName("RunAgentV1")
    .WithSummary("执行受限 Agent 规划与服务器工具调用闭环")
    .Produces<AgentRunResult>()
    .Produces<AgentRunResult>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting("questions");
agents.MapPost("/runs/{runId}/resume", ResumeAgentAsync)
    .WithName("ResumeAgentRunV1")
    .WithSummary("在独立审批人裁决后恢复暂停的 Agent 运行")
    .Produces<AgentRunResult>()
    .Produces<AgentRunResult>(StatusCodes.Status202Accepted)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .RequireRateLimiting("questions");
agents.MapPost("/runs/{runId}/cancel", CancelAgentRunAsync)
    .WithName("CancelAgentRunV1")
    .WithSummary("由原始调用者持久化取消等待审批或正在恢复的 Agent 运行")
    .Produces<AgentRunCancellationResult>(StatusCodes.Status202Accepted)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");

var atlasIncidents = v1.MapGroup("/incidents/atlasid").WithTags("AiMentor AtlasID incidents v1");
atlasIncidents.MapPost("/runs", StartAtlasIncidentAsync)
    .WithName("StartAtlasIncidentV1").Produces<AtlasIncidentCheckpoint>(StatusCodes.Status201Created)
    .ProducesValidationProblem().RequireRateLimiting("questions");
atlasIncidents.MapGet("/runs/{runId}", GetAtlasIncidentAsync)
    .WithName("GetAtlasIncidentV1").Produces<AtlasIncidentCheckpoint>()
    .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
    .RequireRateLimiting("questions");
atlasIncidents.MapPost("/runs/{runId}/resume", ResumeAtlasIncidentAsync)
    .WithName("ResumeAtlasIncidentV1").Produces<AtlasIncidentCheckpoint>()
    .ProducesProblem(StatusCodes.Status409Conflict).RequireRateLimiting("questions");
atlasIncidents.MapPost("/runs/{runId}/cancel", CancelAtlasIncidentAsync)
    .WithName("CancelAtlasIncidentV1").Produces<AtlasIncidentCheckpoint>()
    .ProducesProblem(StatusCodes.Status409Conflict).RequireRateLimiting("questions");

// V0 仅用于短期迁移，默认关闭，Production 环境禁止启用。
if (enableLegacyV0)
{
    app.MapGet("/api/knowledge/stats", () => Results.Ok(repository.Statistics)).ExcludeFromDescription();
    app.MapPost("/api/questions", LegacyAskAsync).RequireRateLimiting("questions").ExcludeFromDescription();
}

await app.RunAsync();

static async Task<IResult> AskAsync(AskV1Request request, ITrustedQuestionService service, IRequestAccessContextProvider accessProvider,
    HttpContext context,
    CancellationToken cancellationToken)
{
    var correlationId = GetCorrelationId(context);
    var access = accessProvider.GetAccessContext(context.User);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        access, correlationId, request.SessionId), cancellationToken);
    context.Response.Headers["X-Run-ID"] = answer.RunId;
    if (answer.Decision != AnswerDecision.Failed) return Results.Ok(answer);
    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "回答生成失败",
        detail: answer.Answer, extensions: new Dictionary<string, object?> { ["runId"] = answer.RunId });
}

static async Task StreamAsync(AskV1Request request, ITrustedQuestionService service, IRequestAccessContextProvider accessProvider, HttpContext context,
    IOptions<HttpJsonOptions> jsonOptions, CancellationToken cancellationToken)
{
    var runId = GetCorrelationId(context);
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Response.Headers["X-Run-ID"] = runId;
    await WriteEventAsync(context.Response, "run.started", new { runId }, jsonOptions.Value.SerializerOptions, cancellationToken);
    var access = accessProvider.GetAccessContext(context.User);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        access, runId, request.SessionId), cancellationToken);
    await WriteEventAsync(context.Response, "answer.completed", answer, jsonOptions.Value.SerializerOptions, cancellationToken);
}

static async Task<IResult> LegacyAskAsync(LegacyAskRequest request, ITrustedQuestionService service, HttpContext context,
    CancellationToken cancellationToken)
{
    var runId = GetCorrelationId(context);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        AccessContext.Create(request.TenantId, request.SubjectId, request.Groups), runId), cancellationToken);
    context.Response.Headers["X-Run-ID"] = answer.RunId;
    return Results.Ok(answer);
}

static async Task<IResult> ProposeMemoryAsync(ProposeMemoryRequest request, IMemoryWorkflowService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var proposal = await service.ProposeAsync(new ProposeMemoryCommand(request.Scope, request.Key, request.Value,
            request.SessionId, request.ExpiresAt), accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Accepted(value: proposal);
    }
    catch (MemoryWorkflowException exception)
    {
        return MemoryProblem(exception, context);
    }
}

static async Task<IResult> ApproveMemoryAsync(string proposalId, IMemoryWorkflowService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var memory = await service.ApproveAsync(proposalId, accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Created($"/api/v1/memories/{memory.Id}", memory);
    }
    catch (MemoryWorkflowException exception)
    {
        return MemoryProblem(exception, context);
    }
}

static async Task<IResult> ListMemoriesAsync(MemoryScope? scope, string? sessionId, IMemoryWorkflowService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await service.ListAsync(accessProvider.GetAccessContext(context.User), scope, sessionId, cancellationToken);
        return Results.Ok(result);
    }
    catch (MemoryWorkflowException exception)
    {
        return MemoryProblem(exception, context);
    }
}

static async Task<IResult> CorrectMemoryAsync(string memoryId, CorrectMemoryRequest request, IMemoryWorkflowService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var memory = await service.CorrectAsync(memoryId,
            new CorrectMemoryCommand(request.Value, request.ExpectedVersion, request.ExpiresAt),
            accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Ok(memory);
    }
    catch (MemoryWorkflowException exception)
    {
        return MemoryProblem(exception, context);
    }
}

static async Task<IResult> DeleteMemoryAsync(string memoryId, int expectedVersion, IMemoryWorkflowService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        await service.DeleteAsync(memoryId, expectedVersion, accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.NoContent();
    }
    catch (MemoryWorkflowException exception)
    {
        return MemoryProblem(exception, context);
    }
}

static IResult MemoryProblem(MemoryWorkflowException exception, HttpContext context)
{
    var statusCode = exception.Kind switch
    {
        MemoryWorkflowErrorKind.NotFound => StatusCodes.Status404NotFound,
        MemoryWorkflowErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };
    return Results.Problem(statusCode: statusCode, title: "记忆工作流请求失败", detail: exception.Message,
        instance: context.Request.Path, extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> ExecuteToolAsync(string toolName, ExecuteToolRequest request, IToolExecutor executor,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    var arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>());
    var result = await executor.ExecuteAsync(toolName, arguments, accessProvider.GetAccessContext(context.User),
        context.Request.Headers["Idempotency-Key"].ToString(), request.ApprovalId, cancellationToken);
    context.Response.Headers["X-Run-ID"] = result.RunId;
    var statusCode = result.Status switch
    {
        ToolExecutionStatus.Completed => StatusCodes.Status200OK,
        ToolExecutionStatus.Reconciled => StatusCodes.Status200OK,
        ToolExecutionStatus.RequiresApproval => StatusCodes.Status409Conflict,
        ToolExecutionStatus.TimedOut => StatusCodes.Status504GatewayTimeout,
        ToolExecutionStatus.ResultTooLarge => StatusCodes.Status502BadGateway,
        ToolExecutionStatus.Failed => StatusCodes.Status502BadGateway,
        _ when result.Safety.Code == "TOOL_NOT_REGISTERED" => StatusCodes.Status404NotFound,
        _ when result.Safety.Code == "TOOL_ARGUMENTS_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
        _ => StatusCodes.Status403Forbidden
    };
    return Results.Json(result, statusCode: statusCode);
}

static async Task<IResult> RequestToolApprovalAsync(RequestToolApprovalRequest request, IToolApprovalService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>());
        var approval = await service.RequestAsync(request.ToolName, arguments, request.Justification,
            accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Accepted($"/api/v1/tool-approvals/{approval.Id}", approval);
    }
    catch (ToolApprovalException exception)
    {
        return ToolApprovalProblem(exception, context);
    }
}

static async Task<IResult> ListToolApprovalsAsync(ToolApprovalStatus? status, IToolApprovalService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.ListAsync(accessProvider.GetAccessContext(context.User), status,
            cancellationToken));
    }
    catch (ToolApprovalException exception)
    {
        return ToolApprovalProblem(exception, context);
    }
}

static async Task<IResult> ListOperationsTasksAsync(string? type, string? status, string? cursor, int? limit,
    IOperationsTaskService service, IRequestAccessContextProvider accessProvider, HttpContext context,
    CancellationToken cancellationToken)
{
    try
    {
        var page = await service.ListAsync(accessProvider.GetAccessContext(context.User), type, status, cursor,
            limit ?? 50, cancellationToken);
        return Results.Ok(page);
    }
    catch (ArgumentException exception)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "运营任务查询参数无效",
            detail: exception.Message, type: "https://httpstatuses.com/400");
    }
}

static async Task<IResult> ListOperationsActionsAsync(IOperationsActionService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.ListAsync(accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (OperationsActionException exception) { return OperationsActionProblem(exception, context); }
}

static async Task<IResult> RequestOperationsActionAsync(string targetType, string targetId,
    RequestOperationsActionRequest request, IOperationsActionService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var etag = context.Request.Headers.IfMatch.ToString();
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var result = await service.RequestAsync(targetType, targetId, request.Action, etag, request.Reason,
            idempotencyKey, accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Accepted($"/api/v1/operations/actions/{result.Id}", result);
    }
    catch (OperationsActionException exception) { return OperationsActionProblem(exception, context); }
}

static async Task<IResult> ReviewOperationsActionAsync(string requestId, ReviewOperationsActionRequest request,
    IOperationsActionService service, IRequestAccessContextProvider accessProvider, HttpContext context,
    CancellationToken cancellationToken)
{
    try
    {
        var result = await service.ReviewAsync(requestId, request.ExpectedVersion,
            context.Request.Headers.IfMatch.ToString(), request.Approved, request.Reason,
            accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Ok(result);
    }
    catch (OperationsActionException exception) { return OperationsActionProblem(exception, context); }
}

static IResult OperationsActionProblem(OperationsActionException exception, HttpContext context)
{
    var status = exception.Kind switch
    {
        OperationsActionErrorKind.Validation => StatusCodes.Status400BadRequest,
        OperationsActionErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        OperationsActionErrorKind.NotFound => StatusCodes.Status404NotFound,
        OperationsActionErrorKind.Conflict => StatusCodes.Status409Conflict,
        OperationsActionErrorKind.Capacity or OperationsActionErrorKind.Unavailable =>
            StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
    return Results.Problem(statusCode: status, title: "运营动作失败", detail: exception.Message,
        type: $"https://httpstatuses.com/{status}", instance: context.Request.Path,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> DecideToolApprovalAsync(string approvalId, DecideToolApprovalRequest request,
    IToolApprovalService service, IRequestAccessContextProvider accessProvider, HttpContext context,
    CancellationToken cancellationToken)
{
    try
    {
        var approval = await service.DecideAsync(approvalId, request.Approved, request.Reason,
            accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Ok(approval);
    }
    catch (ToolApprovalException exception)
    {
        return ToolApprovalProblem(exception, context);
    }
}

static IResult ToolApprovalProblem(ToolApprovalException exception, HttpContext context)
{
    var statusCode = exception.Kind switch
    {
        ToolApprovalErrorKind.Validation => StatusCodes.Status400BadRequest,
        ToolApprovalErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ToolApprovalErrorKind.NotFound => StatusCodes.Status404NotFound,
        ToolApprovalErrorKind.Conflict => StatusCodes.Status409Conflict,
        ToolApprovalErrorKind.Capacity => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
    return Results.Problem(statusCode: statusCode, title: "工具审批失败", detail: exception.Message,
        instance: context.Request.Path,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> ListToolCompensationsAsync(ToolCompensationStatus? status,
    IToolCompensationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.ListAsync(accessProvider.GetAccessContext(context.User), status,
            cancellationToken));
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static async Task<IResult> RequestToolCompensationApprovalAsync(string compensationId,
    RequestToolCompensationApprovalRequest request, IToolCompensationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await service.RequestApprovalAsync(compensationId, request.Justification,
            accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Accepted($"/api/v1/tool-compensations/{result.Id}", result);
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static async Task<IResult> DecideToolCompensationAsync(string compensationId,
    DecideToolCompensationRequest request, IToolCompensationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.DecideAsync(compensationId, request.ApprovalId, request.Approved,
            request.Reason, accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static async Task<IResult> ExecuteToolCompensationAsync(string compensationId,
    ExecuteToolCompensationRequest request, IToolCompensationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        return Results.Ok(await service.ExecuteAsync(compensationId, request.ApprovalId, idempotencyKey,
            accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static IResult ToolCompensationProblem(ToolCompensationException exception, HttpContext context)
{
    var statusCode = exception.Kind switch
    {
        ToolCompensationErrorKind.Validation => StatusCodes.Status400BadRequest,
        ToolCompensationErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ToolCompensationErrorKind.NotFound => StatusCodes.Status404NotFound,
        ToolCompensationErrorKind.Conflict => StatusCodes.Status409Conflict,
        ToolCompensationErrorKind.Capacity or ToolCompensationErrorKind.Unavailable =>
            StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
    return Results.Problem(statusCode: statusCode, title: "工具补偿请求失败", detail: exception.Message,
        instance: context.Request.Path,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> ProbeOutcomeUnknownToolCompensationAsync(string compensationId,
    IToolCompensationReconciliationService service, IRequestAccessContextProvider accessProvider,
    HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.ProbeOutcomeAsync(compensationId,
            accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static async Task<IResult> ReviewOutcomeUnknownToolCompensationAsync(string compensationId,
    ReviewToolCompensationOutcomeRequest request, IToolCompensationReconciliationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await service.ReviewOutcomeAsync(compensationId, request.Confirmed!.Value, request.Reason,
            accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (ToolCompensationException exception)
    {
        return ToolCompensationProblem(exception, context);
    }
}

static async Task<IResult> ListOutcomeUnknownToolExecutionsAsync(int? limit,
    IToolExecutionReconciliationService service, IRequestAccessContextProvider accessProvider,
    HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var effectiveLimit = limit ?? 50;
        return Results.Ok(await service.ListOutcomeUnknownAsync(
            accessProvider.GetAccessContext(context.User), effectiveLimit, cancellationToken));
    }
    catch (ToolExecutionReconciliationException exception)
    {
        var status = exception.Kind == ToolExecutionReconciliationErrorKind.Forbidden
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status400BadRequest;
        return Results.Problem(statusCode: status, title: "工具执行对账请求失败", detail: exception.Message,
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }
}

static async Task<IResult> ProbeOutcomeUnknownToolExecutionAsync(string executionKey,
    ProbeToolOutcomeRequest request, IToolExecutionReconciliationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var arguments = JsonSerializer.SerializeToElement(request.Arguments);
        return Results.Ok(await service.ProbeOutcomeAsync(accessProvider.GetAccessContext(context.User),
            executionKey, arguments, cancellationToken));
    }
    catch (ToolExecutionReconciliationException exception)
    {
        return ToolReconciliationProblem(exception, context);
    }
}

static async Task<IResult> ReviewOutcomeUnknownToolExecutionAsync(string executionKey,
    ReviewToolOutcomeRequest request, IToolExecutionReconciliationService service,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var arguments = JsonSerializer.SerializeToElement(request.Arguments);
        var result = await service.ReviewOutcomeAsync(accessProvider.GetAccessContext(context.User), executionKey,
            arguments, request.Confirmed, request.Reason, cancellationToken);
        return Results.Ok(result);
    }
    catch (ToolExecutionReconciliationException exception)
    {
        return ToolReconciliationProblem(exception, context);
    }
}

static IResult ToolReconciliationProblem(ToolExecutionReconciliationException exception, HttpContext context)
{
    var status = exception.Kind switch
    {
        ToolExecutionReconciliationErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ToolExecutionReconciliationErrorKind.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest
    };
    return Results.Problem(statusCode: status, title: "工具执行对账请求失败", detail: exception.Message,
        instance: context.Request.Path,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> RunAgentAsync(RunAgentRequest request, IAgentRunner runner,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await runner.RunAsync(request.Input, accessProvider.GetAccessContext(context.User),
            GetCorrelationId(context), cancellationToken);
        context.Response.Headers["X-Run-ID"] = result.RunId;
        return AgentRunResultResponse(result);
    }
    catch (AgentRunWorkflowException exception)
    {
        return AgentRunProblem(exception, context);
    }
}

static async Task<IResult> ResumeAgentAsync(string runId, IAgentRunner runner,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await runner.ResumeAsync(runId, accessProvider.GetAccessContext(context.User), cancellationToken);
        context.Response.Headers["X-Run-ID"] = result.RunId;
        return AgentRunResultResponse(result);
    }
    catch (AgentRunWorkflowException exception)
    {
        return AgentRunProblem(exception, context);
    }
}

static async Task<IResult> CancelAgentRunAsync(string runId, CancelAgentRunRequest request, IAgentRunner runner,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await runner.CancelAsync(runId, accessProvider.GetAccessContext(context.User), request.Reason,
            cancellationToken);
        return Results.Json(result, statusCode: result.Status == AgentRunCancellationStatus.Requested
            ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
    }
    catch (AgentRunWorkflowException exception)
    {
        return AgentRunProblem(exception, context);
    }
}

static IResult AgentRunResultResponse(AgentRunResult result)
{
    var statusCode = result.Status switch
    {
        AgentRunStatus.Completed => StatusCodes.Status200OK,
        AgentRunStatus.AwaitingApproval => StatusCodes.Status202Accepted,
        AgentRunStatus.Cancelled => StatusCodes.Status200OK,
        AgentRunStatus.Refused => StatusCodes.Status403Forbidden,
        AgentRunStatus.LimitExceeded => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    return Results.Json(result, statusCode: statusCode);
}

static IResult AgentRunProblem(AgentRunWorkflowException exception, HttpContext context)
{
    var statusCode = exception.Kind switch
    {
        AgentRunWorkflowErrorKind.Validation => StatusCodes.Status400BadRequest,
        AgentRunWorkflowErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        AgentRunWorkflowErrorKind.NotFound => StatusCodes.Status404NotFound,
        AgentRunWorkflowErrorKind.Conflict => StatusCodes.Status409Conflict,
        AgentRunWorkflowErrorKind.Capacity => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
    return Results.Problem(statusCode: statusCode, title: "Agent 运行状态错误", detail: exception.Message,
        instance: context.Request.Path,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task<IResult> StartAtlasIncidentAsync(AtlasIncidentInput input, IAtlasIncidentWorkflow workflow,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        var result = await workflow.StartAsync(input, accessProvider.GetAccessContext(context.User), cancellationToken);
        return Results.Created($"/api/v1/incidents/atlasid/runs/{result.RunId}", result);
    }
    catch (AtlasIncidentWorkflowException exception) { return AtlasIncidentProblem(exception, context); }
}

static async Task<IResult> GetAtlasIncidentAsync(string runId, IAtlasIncidentWorkflow workflow,
    IRequestAccessContextProvider accessProvider, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await workflow.GetAsync(runId, accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (AtlasIncidentWorkflowException exception) { return AtlasIncidentProblem(exception, context); }
}

static async Task<IResult> ResumeAtlasIncidentAsync(string runId, ResumeAtlasIncidentCommand command,
    IAtlasIncidentWorkflow workflow, IRequestAccessContextProvider accessProvider, HttpContext context,
    CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await workflow.ResumeAsync(runId, command.ExpectedVersion, command.Input,
            accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (AtlasIncidentWorkflowException exception) { return AtlasIncidentProblem(exception, context); }
}

static async Task<IResult> CancelAtlasIncidentAsync(string runId, CancelAtlasIncidentCommand command,
    IAtlasIncidentWorkflow workflow, IRequestAccessContextProvider accessProvider, HttpContext context,
    CancellationToken cancellationToken)
{
    try
    {
        return Results.Ok(await workflow.CancelAsync(runId, command.ExpectedVersion,
            accessProvider.GetAccessContext(context.User), cancellationToken));
    }
    catch (AtlasIncidentWorkflowException exception) { return AtlasIncidentProblem(exception, context); }
}

static IResult AtlasIncidentProblem(AtlasIncidentWorkflowException exception, HttpContext context)
{
    var status = exception.Kind switch
    {
        AtlasIncidentErrorKind.Validation => StatusCodes.Status400BadRequest,
        AtlasIncidentErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        AtlasIncidentErrorKind.NotFound => StatusCodes.Status404NotFound,
        AtlasIncidentErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
    return Results.Problem(statusCode: status, title: "AtlasID 排查工作流失败", detail: exception.Message,
        instance: context.Request.Path, extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}

static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, JsonSerializerOptions options,
    CancellationToken cancellationToken)
{
    await response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, options)}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static async Task<IResult> ReadyAsync(ISystemDoctor doctor, CancellationToken cancellationToken)
{
    var report = await doctor.RunAsync(cancellationToken);
    return Results.Json(report, statusCode: report.IsReady
        ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}

static string GetCorrelationId(HttpContext context)
{
    var requested = context.Request.Headers["X-Correlation-ID"].ToString().Trim();
    if (requested.Length is > 0 and <= 128 && requested.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
        return requested;
    return Guid.NewGuid().ToString("N");
}

static byte[] ResolveMemoryMasterKey(string? configuredKey, string developmentKeyPath, bool production)
{
    if (!string.IsNullOrWhiteSpace(configuredKey))
    {
        try
        {
            var key = Convert.FromBase64String(configuredKey.Trim());
            if (key.Length == 32) return key;
        }
        catch (FormatException)
        {
            // 统一使用下方的安全配置错误，避免回显密钥内容。
        }
        throw new InvalidOperationException("Memory:EncryptionKey 必须是 Base64 编码的 32 字节密钥。");
    }
    if (production)
        throw new InvalidOperationException("Production 环境必须通过 AIMENTOR_MEMORY_ENCRYPTION_KEY 提供记忆加密密钥。");

    Directory.CreateDirectory(Path.GetDirectoryName(developmentKeyPath)!);
    if (File.Exists(developmentKeyPath))
    {
        var existing = Convert.FromBase64String(File.ReadAllText(developmentKeyPath).Trim());
        if (existing.Length == 32) return existing;
        throw new InvalidOperationException("开发记忆密钥文件格式无效，请先备份数据库后重新生成密钥。");
    }
    var generated = RandomNumberGenerator.GetBytes(32);
    File.WriteAllText(developmentKeyPath, Convert.ToBase64String(generated));
    return generated;
}

static IReadOnlyDictionary<string, byte[]> ResolveWorkflowKeys(IConfiguration configuration, string activeVersion,
    byte[] developmentFallback, bool requireConfiguredKeys)
{
    var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var child in configuration.GetSection("Workflow:Encryption:Keys").GetChildren())
    {
        try
        {
            var decoded = Convert.FromBase64String(child.Value?.Trim() ?? string.Empty);
            if (decoded.Length != 32) throw new FormatException();
            keys.Add(child.Key, decoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Workflow:Encryption:Keys:{child.Key} 必须是 Base64 编码的 32 字节密钥。");
        }
    }
    if (keys.Count == 0)
    {
        if (requireConfiguredKeys)
            throw new InvalidOperationException("Production SQL Server 模式必须配置独立的 Workflow:Encryption:Keys 密钥环。");
        keys.Add(activeVersion, developmentFallback);
    }
    if (!keys.ContainsKey(activeVersion))
        throw new InvalidOperationException("Workflow:Encryption:ActiveKeyVersion 必须存在于密钥环中。");
    return keys;
}

static ModelProviderOptions ReadModelProviderOptions(IConfiguration configuration, string sectionName,
    string apiKeyEnvironmentVariable, AiProviderKind defaultProvider)
{
    var section = configuration.GetSection(sectionName);
    var provider = ParseProvider(section["Provider"], defaultProvider, sectionName);
    return new ModelProviderOptions
    {
        Provider = provider,
        Endpoint = section["Endpoint"],
        ApiKey = section["ApiKey"] ?? Environment.GetEnvironmentVariable(apiKeyEnvironmentVariable),
        Model = section["Model"],
        TimeoutSeconds = section.GetValue("TimeoutSeconds", 30),
        MaximumRetries = section.GetValue("MaximumRetries", 2)
    };
}

static EmbeddingProviderOptions ReadEmbeddingProviderOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("Embedding");
    var common = ReadModelProviderOptions(configuration, "Embedding", "AIMENTOR_EMBEDDING_API_KEY",
        AiProviderKind.Deterministic);
    return new EmbeddingProviderOptions
    {
        Provider = common.Provider,
        Endpoint = common.Endpoint,
        ApiKey = common.ApiKey,
        Model = common.Model,
        TimeoutSeconds = common.TimeoutSeconds,
        MaximumRetries = common.MaximumRetries,
        Dimensions = section.GetValue("Dimensions", 256),
        IndexVersion = section["IndexVersion"] ?? "aimentor-knowledge-v1"
    };
}

static RerankerProviderOptions ReadRerankerProviderOptions(IConfiguration configuration)
{
    var common = ReadModelProviderOptions(configuration, "Reranker", "AIMENTOR_RERANKER_API_KEY",
        AiProviderKind.Lexical);
    return new RerankerProviderOptions
    {
        Provider = common.Provider,
        Endpoint = common.Endpoint,
        ApiKey = common.ApiKey,
        Model = common.Model,
        TimeoutSeconds = common.TimeoutSeconds,
        MaximumRetries = common.MaximumRetries
    };
}

static AiProviderKind ParseProvider(string? value, AiProviderKind fallback, string sectionName)
{
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    if (Enum.TryParse<AiProviderKind>(value, true, out var provider)) return provider;
    throw new InvalidOperationException($"{sectionName}:Provider 不受支持。");
}
