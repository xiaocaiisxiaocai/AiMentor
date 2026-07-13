namespace AiMentor.Infrastructure;

/// <summary>在开发、测试和命令行入口间统一定位知识包与评测文件。</summary>
public static class WorkspacePathLocator
{
    public static string FindKnowledgeRoot(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath);
            if (Directory.Exists(configured)) return configured;
            throw new DirectoryNotFoundException($"配置的知识目录不存在：{configured}");
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "AI-Agent-V1合成数据包", "knowledge");
                if (Directory.Exists(direct)) return direct;
                var sibling = Path.Combine(directory.FullName, "..", "AI-Agent-V1合成数据包", "knowledge");
                if (Directory.Exists(sibling)) return Path.GetFullPath(sibling);
            }
        }
        throw new DirectoryNotFoundException("未找到 AI-Agent-V1合成数据包\\knowledge，请设置 AIMENTOR_KNOWLEDGE_ROOT。");
    }

    public static string FindEvaluationFile(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return Path.GetFullPath(configuredPath);
        var knowledgeRoot = FindKnowledgeRoot();
        return Path.Combine(Directory.GetParent(knowledgeRoot)!.FullName, "evaluation", "evaluation-cases.jsonl");
    }
}
