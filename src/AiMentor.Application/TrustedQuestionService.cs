using System.Diagnostics;
using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>按固定顺序执行安全审核、ACL 检索、证据门禁、记忆注入、回答和输出审核。</summary>
public sealed class TrustedQuestionService(
    IKnowledgeRepository knowledge,
    IQueryNormalizer queryNormalizer,
    IInputSafetyService safety,
    IRetrievedContentSafetyService retrievedContentSafety,
    IEvidenceReranker reranker,
    IEvidenceSufficiencyEvaluator sufficiencyEvaluator,
    IAnswerComposer composer,
    IOutputSafetyService outputSafety,
    ITraceSink traceSink,
    TrustedQuestionOptions options,
    IMemoryContextProvider memoryContext,
    ICitationMapper citationMapper,
    ICitationVerifier citationVerifier,
    IEvidenceConflictDetector conflictDetector,
    IWorkflowMetrics? workflowMetrics = null) : ITrustedQuestionService
{
    public async Task<TrustedAnswer> AskAsync(TrustedQuestion question, CancellationToken cancellationToken = default)
    {
        var runId = question.CorrelationId ?? Guid.NewGuid().ToString("N");
        var trace = new List<TraceStep>();
        var workflowOutcome = "failure";
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            return await AskCoreAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            workflowOutcome = "cancelled";
            throw;
        }
        finally
        {
            try
            {
                workflowMetrics?.RecordCompleted("trusted_question", workflowOutcome,
                    Stopwatch.GetElapsedTime(startedAt), trace);
            }
            catch
            {
                // 指标监听器异常不能把已经完成的安全决策改写为请求失败。
            }
        }

        async Task<TrustedAnswer> AskCoreAsync()
        {
            Trace("run.started", "ok", new Dictionary<string, object?>
            {
                ["tenant"] = question.Access.TenantId,
                ["subject"] = question.Access.SubjectId
            });

            var inputReview = safety.ReviewContent(question.Question);
            var safetyDecision = inputReview.Decision;
            Trace("input.safety", safetyDecision.Action.ToString(), new Dictionary<string, object?>
            {
                ["code"] = safetyDecision.Code,
                ["policyVersion"] = safety.PolicyVersion,
                ["redactionTypes"] = string.Join(',', inputReview.Findings.Select(item => item.Type).Distinct(StringComparer.Ordinal)),
                ["redactionCount"] = inputReview.Findings.Sum(item => item.Count)
            });
            if (safetyDecision.Action is SafetyAction.Refuse or SafetyAction.RequireApproval)
            {
                return await CompleteAsync(AnswerDecision.Refused, safetyDecision.Message, false, safetyDecision, [], trace);
            }
            if (safetyDecision.Action == SafetyAction.Transform
                && (string.IsNullOrWhiteSpace(inputReview.SafeText)
                    || string.Equals(inputReview.SafeText, question.Question, StringComparison.Ordinal)
                    || inputReview.Findings.Count == 0))
            {
                var invalid = new SafetyDecision(SafetyAction.Refuse, "PII_TRANSFORM_INVALID",
                    "个人信息转换未形成可验证的安全文本，已停止处理。");
                return await CompleteAsync(AnswerDecision.Refused, invalid.Message, false, invalid, [], trace);
            }
            var safeQuestion = inputReview.SafeText;

            if (string.IsNullOrWhiteSpace(question.Access.TenantId) || string.IsNullOrWhiteSpace(question.Access.SubjectId))
            {
                var invalid = new SafetyDecision(SafetyAction.Refuse, "INVALID_IDENTITY", "缺少有效的租户或用户身份，无法执行授权检索。");
                Trace("identity.validation", "refused", new Dictionary<string, object?> { ["code"] = invalid.Code });
                return await CompleteAsync(AnswerDecision.Refused, invalid.Message, false, invalid, [], trace);
            }

            var normalizedQuery = queryNormalizer.Normalize(safeQuestion);
            Trace("query.normalized", string.Equals(normalizedQuery, safeQuestion, StringComparison.Ordinal) ? "unchanged" : "normalized",
                new Dictionary<string, object?> { ["characterCount"] = normalizedQuery.Length });
            var retrievedCandidates = await knowledge.SearchAsync(normalizedQuery, question.Access, options.SearchLimit, cancellationToken);
            Trace("knowledge.search", retrievedCandidates.Count > 0 ? "found" : "empty", new Dictionary<string, object?>
            {
                ["accessibleEvidenceCount"] = retrievedCandidates.Count,
                ["topScore"] = retrievedCandidates.Count > 0 ? retrievedCandidates[0].Score : 0
            });

            var contentReview = retrievedContentSafety.Review(retrievedCandidates);
            Trace("retrieval.safety", contentReview.Rejections.Count == 0 ? "passed" : "filtered", new Dictionary<string, object?>
            {
                ["acceptedCount"] = contentReview.AcceptedEvidence.Count,
                ["rejectedCount"] = contentReview.Rejections.Count,
                ["rejectionCodes"] = string.Join(',', contentReview.Rejections.Select(item => item.Code).Distinct(StringComparer.Ordinal)),
                ["policyVersion"] = retrievedContentSafety.PolicyVersion
            });

            var evidence = await reranker.RerankAsync(safeQuestion, contentReview.AcceptedEvidence, cancellationToken);
            Trace("evidence.rerank", evidence.Count > 0 ? "ranked" : "empty", new Dictionary<string, object?>
            {
                ["candidateCount"] = evidence.Count,
                ["topRerankedScore"] = evidence.Count > 0 ? evidence[0].Score : 0
            });
            var assessment = sufficiencyEvaluator.Evaluate(safeQuestion, evidence, options.MinimumTopScore);
            if (!assessment.IsSufficient || evidence.Count < options.MinimumEvidenceCount)
            {
                Trace("evidence.gate", "insufficient", new Dictionary<string, object?>
                {
                    ["code"] = assessment.Code,
                    ["confidence"] = assessment.Confidence,
                    ["threshold"] = options.MinimumTopScore
                });
                return await CompleteAsync(AnswerDecision.InsufficientEvidence, "现有且您有权访问的知识中证据不足，我不能据此给出可靠答案。", false, safetyDecision, [], trace);
            }

            Trace("evidence.gate", "passed", new Dictionary<string, object?>
            {
                ["evidenceCount"] = evidence.Count,
                ["code"] = assessment.Code,
                ["confidence"] = assessment.Confidence
            });
            var conflicts = conflictDetector.Detect(evidence);
            Trace("evidence.conflict", conflicts.Count == 0 ? "none" : "expert_review_required",
                new Dictionary<string, object?>
                {
                    ["conflictCount"] = conflicts.Count,
                    ["kinds"] = string.Join(',', conflicts.Select(conflict => conflict.Kind).Distinct(StringComparer.Ordinal))
                });
            if (conflicts.Count > 0)
            {
                var conflictDecision = new SafetyDecision(SafetyAction.RequireApproval, "EVIDENCE_CONFLICT_REQUIRES_EXPERT",
                    "可访问证据存在无法自动裁决的冲突，需要领域专家处理。");
                return await CompleteAsync(AnswerDecision.Refused,
                    "可访问证据存在版本或同权来源冲突，我不能自行选择结论；请交由领域专家复核。", false,
                    conflictDecision, [], trace, conflicts);
            }
            try
            {
                var memories = await memoryContext.GetRelevantAsync(safeQuestion, question.Access, question.SessionId,
                    cancellationToken);
                Trace("memory.context", memories.Count > 0 ? "injected" : "empty", new Dictionary<string, object?>
                {
                    ["count"] = memories.Count,
                    ["scopes"] = string.Join(',', memories.Select(item => item.Scope).Distinct())
                });
                var answer = await composer.ComposeAsync(safeQuestion, evidence, memories, cancellationToken);
                var preflight = outputSafety.ReviewContent(answer, evidence, []);
                SafetyDecision? outputTransform = null;
                if (preflight.Decision.Action == SafetyAction.Transform)
                {
                    if (string.IsNullOrWhiteSpace(preflight.SafeText)
                        || string.Equals(preflight.SafeText, answer, StringComparison.Ordinal)
                        || preflight.Findings.Count == 0)
                    {
                        var invalid = new SafetyDecision(SafetyAction.Refuse, "OUTPUT_PII_TRANSFORM_INVALID",
                            "输出脱敏未形成可验证的安全文本，已阻止返回。");
                        return await CompleteAsync(AnswerDecision.Refused, invalid.Message, false, invalid, [], trace);
                    }
                    answer = preflight.SafeText;
                    outputTransform = preflight.Decision;
                    Trace("output.transform", "Transform", new Dictionary<string, object?>
                    {
                        ["code"] = preflight.Decision.Code,
                        ["redactionTypes"] = string.Join(',', preflight.Findings.Select(item => item.Type).Distinct(StringComparer.Ordinal)),
                        ["redactionCount"] = preflight.Findings.Sum(item => item.Count),
                        ["policyVersion"] = outputSafety.PolicyVersion
                    });
                }
                else if (preflight.Decision.Code != "OUTPUT_WITHOUT_CITATION")
                {
                    Trace("output.safety", preflight.Decision.Action.ToString(), new Dictionary<string, object?>
                    {
                        ["code"] = preflight.Decision.Code,
                        ["policyVersion"] = outputSafety.PolicyVersion
                    });
                    return await CompleteAsync(AnswerDecision.Refused, preflight.Decision.Message, false, preflight.Decision, [], trace);
                }
                var citations = citationMapper.Map(answer, evidence);
                Trace("citation.mapping", citations.Count > 0 ? "mapped" : "empty", new Dictionary<string, object?>
                {
                    ["citationCount"] = citations.Count,
                    ["claimCount"] = citations.Select(citation => citation.SentenceIndex).Distinct().Count()
                });
                var verification = citationVerifier.Verify(answer, evidence, citations);
                Trace("citation.verification", verification.IsValid ? "passed" : "failed", new Dictionary<string, object?>
                {
                    ["code"] = verification.Code
                });
                if (!verification.IsValid)
                {
                    var invalidCitation = new SafetyDecision(SafetyAction.Refuse, verification.Code, verification.Message);
                    return await CompleteAsync(AnswerDecision.Refused, verification.Message, false, invalidCitation, [], trace);
                }
                Trace("answer.composed", "ok", new Dictionary<string, object?> { ["citationCount"] = citations.Count });
                var outputDecision = outputSafety.ReviewContent(answer, evidence, citations).Decision;
                Trace("output.safety", outputDecision.Action.ToString(), new Dictionary<string, object?>
                {
                    ["code"] = outputDecision.Code,
                    ["policyVersion"] = outputSafety.PolicyVersion
                });
                if (outputDecision.Action != SafetyAction.Allow)
                    return await CompleteAsync(AnswerDecision.Refused, outputDecision.Message, false, outputDecision, [], trace);

                return await CompleteAsync(AnswerDecision.Answered, answer, true,
                    outputTransform ?? safetyDecision, citations, trace);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Trace("answer.composed", "failed", new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
                return await CompleteAsync(AnswerDecision.Failed, "回答生成失败，请稍后重试。", true, safetyDecision, [], trace);
            }
        }

        void Trace(string name, string outcome, IReadOnlyDictionary<string, object?> details) =>
            trace.Add(new TraceStep(name, outcome, DateTimeOffset.UtcNow, details));

        async Task<TrustedAnswer> CompleteAsync(AnswerDecision decision, string answer, bool hasEvidence, SafetyDecision safetyResult,
            IReadOnlyList<Citation> citations, IReadOnlyList<TraceStep> steps,
            IReadOnlyList<EvidenceConflict>? answerConflicts = null)
        {
            var result = new TrustedAnswer(runId, decision, answer, hasEvidence, safetyResult, citations, steps,
                answerConflicts ?? []);
            await traceSink.WriteAsync(runId, steps, cancellationToken);
            workflowOutcome = decision switch
            {
                AnswerDecision.Answered => "success",
                AnswerDecision.Failed => "failure",
                _ => "refused"
            };
            return result;
        }
    }
}
