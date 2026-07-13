using AiMentor.Domain;

namespace AiMentor.Application;

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
    TrustedQuestionOptions options) : ITrustedQuestionService
{
    public async Task<TrustedAnswer> AskAsync(TrustedQuestion question, CancellationToken cancellationToken = default)
    {
        var runId = question.CorrelationId ?? Guid.NewGuid().ToString("N");
        var trace = new List<TraceStep>();
        Trace("run.started", "ok", new Dictionary<string, object?> { ["tenant"] = question.Access.TenantId, ["subject"] = question.Access.SubjectId });

        var safetyDecision = safety.Review(question.Question);
        Trace("input.safety", safetyDecision.Action.ToString(), new Dictionary<string, object?>
        {
            ["code"] = safetyDecision.Code,
            ["policyVersion"] = safety.PolicyVersion
        });
        if (safetyDecision.Action != SafetyAction.Allow)
        {
            return await CompleteAsync(AnswerDecision.Refused, safetyDecision.Message, false, safetyDecision, [], trace);
        }

        if (string.IsNullOrWhiteSpace(question.Access.TenantId) || string.IsNullOrWhiteSpace(question.Access.SubjectId))
        {
            var invalid = new SafetyDecision(SafetyAction.Refuse, "INVALID_IDENTITY", "缺少有效的租户或用户身份，无法执行授权检索。");
            Trace("identity.validation", "refused", new Dictionary<string, object?> { ["code"] = invalid.Code });
            return await CompleteAsync(AnswerDecision.Refused, invalid.Message, false, invalid, [], trace);
        }

        var normalizedQuery = queryNormalizer.Normalize(question.Question);
        Trace("query.normalized", string.Equals(normalizedQuery, question.Question, StringComparison.Ordinal) ? "unchanged" : "normalized",
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

        var evidence = await reranker.RerankAsync(question.Question, contentReview.AcceptedEvidence, cancellationToken);
        Trace("evidence.rerank", evidence.Count > 0 ? "ranked" : "empty", new Dictionary<string, object?>
        {
            ["candidateCount"] = evidence.Count,
            ["topRerankedScore"] = evidence.Count > 0 ? evidence[0].Score : 0
        });
        var assessment = sufficiencyEvaluator.Evaluate(question.Question, evidence, options.MinimumTopScore);
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
        try
        {
            var answer = await composer.ComposeAsync(question.Question, evidence, cancellationToken);
            var citationRanking = evidence.OrderByDescending(item => item.RetrievalScore ?? item.Score).ToArray();
            var topRetrievalScore = citationRanking[0].RetrievalScore ?? citationRanking[0].Score;
            var citationThreshold = Math.Max(options.MinimumTopScore, topRetrievalScore * 0.6);
            var citations = citationRanking
                .Where(item => (item.RetrievalScore ?? item.Score) >= citationThreshold)
                .Take(3)
                .Select(ToCitation)
                .ToArray();
            Trace("answer.composed", "ok", new Dictionary<string, object?> { ["citationCount"] = citations.Length });
            var outputDecision = outputSafety.Review(answer, evidence, citations);
            Trace("output.safety", outputDecision.Action.ToString(), new Dictionary<string, object?>
            {
                ["code"] = outputDecision.Code,
                ["policyVersion"] = outputSafety.PolicyVersion
            });
            if (outputDecision.Action != SafetyAction.Allow)
                return await CompleteAsync(AnswerDecision.Refused, outputDecision.Message, false, outputDecision, [], trace);

            return await CompleteAsync(AnswerDecision.Answered, answer, true, safetyDecision, citations, trace);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace("answer.composed", "failed", new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name });
            return await CompleteAsync(AnswerDecision.Failed, "回答生成失败，请稍后重试。", true, safetyDecision, [], trace);
        }

        Citation ToCitation(Evidence item)
        {
            var normalized = string.Join(' ', item.Chunk.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return new Citation(item.Chunk.DocumentId, item.Chunk.Version, item.Chunk.Title, item.Chunk.Section,
                normalized[..Math.Min(normalized.Length, 220)], Math.Round(item.RetrievalScore ?? item.Score, 4));
        }

        void Trace(string name, string outcome, IReadOnlyDictionary<string, object?> details) =>
            trace.Add(new TraceStep(name, outcome, DateTimeOffset.UtcNow, details));

        async Task<TrustedAnswer> CompleteAsync(AnswerDecision decision, string answer, bool hasEvidence, SafetyDecision safetyResult,
            IReadOnlyList<Citation> citations, IReadOnlyList<TraceStep> steps)
        {
            var result = new TrustedAnswer(runId, decision, answer, hasEvidence, safetyResult, citations, steps);
            await traceSink.WriteAsync(runId, steps, cancellationToken);
            return result;
        }
    }
}
