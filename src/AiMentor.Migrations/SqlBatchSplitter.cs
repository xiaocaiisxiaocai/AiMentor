using System.Text;

namespace AiMentor.Migrations;

internal static class SqlBatchSplitter
{
    public static IReadOnlyList<string> Split(string sqlText)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        using var reader = new StringReader(sqlText);
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                AddBatch(current, batches);
                continue;
            }

            current.AppendLine(line);
        }

        AddBatch(current, batches);
        return batches;
    }

    private static void AddBatch(StringBuilder current, List<string> batches)
    {
        if (!string.IsNullOrWhiteSpace(current.ToString())) batches.Add(current.ToString());
        current.Clear();
    }
}
