namespace AiMentor.Migrations;

public sealed class MigrationException : Exception
{
    public MigrationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public MigrationException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
