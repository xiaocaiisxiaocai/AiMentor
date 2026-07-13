using AiMentor.Domain;

namespace AiMentor.Application;

public sealed class TrustedQuestionService(
    IKnowledgeRepository knowledge,
    IInputSafetyService safety,
    IAnswerComposer composer,
    ITraceSink traceSink,
    TrustedQuestionOptions options) : ITrustedQuestionService
{
    public async Task<TrustedAnswer> AskAsync(TrustedQuestion question, CancellationToken cancellationToken = default)
    {
        var runId = question.CorrelationId ?? Guid.NewGuid().ToString("N");
        var trace = new List<TraceStep>();
        Trace("run.started", "ok", new Dictionary<string, object?> { ["tenant"] = question.Access.TenantId, ["subject"] = question.Access.SubjectId });

        var safetyDecision = safety.Review(question.Question);
        Trace("input.safety", safetyDecision.Action.ToString(), new Dictionary<string, object?> { ["code"] = safetyDecision.Code });
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

        var evidence = await knowledge.SearchAsync(question.Question, question.Access, options.SearchLimit, cancellationToken);
        Trace("knowledge.search", evidence.Count > 0 ? "found" : "empty", new Dictionary<string, object?>
        {
            ["accessibleEvidenceCount"] = evidence.Count,
            ["topScore"] = evidence.Count > 0 ? evidence[0].Score : 0
        });

        var sufficient = evidence.Count >= options.MinimumEvidenceCount && evidence[0].Score >= options.MinimumTopScore;
        if (!sufficient)
        {
            Trace("evidence.gate", "insufficient", new Dictionary<string, object?> { ["threshold"] = options.MinimumTopScore });
            return await CompleteAsync(AnswerDecision.InsufficientEvidence, "现有且您有权访问的知识中证据不足，我不能据此给出可靠答案。", false, safetyDecision, [], trace);
        }

        Trace("evidence.gate", "passed", new Dictionary<string, object?> { ["evidenceCount"] = evidence.Count });
        try
        {
            var answer = await composer.ComposeAsync(question.Question, evidence, cancellationToken);
            var citationThreshold = Math.Max(options.MinimumTopScore, evidence[0].Score * 0.6);
            var citations = evidence.Where(item => item.Score >= citationThreshold).Take(3).Select(ToCitation).ToArray();
            Trace("answer.composed", "ok", new Dictionary<string, object?> { ["citationCount"] = citations.Length });
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
                normalized[..Math.Min(normalized.Length, 220)], Math.Round(item.Score, 4));
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
