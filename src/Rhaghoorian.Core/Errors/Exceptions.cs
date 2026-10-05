namespace Rhaghoorian.Core.Errors;

/// <summary>Base das falhas esperadas. <see cref="Exception.Message"/> já é uma mensagem apropriada ao usuário final.</summary>
public class AppException : Exception
{
    public AppException(string userMessage, Exception? inner = null) : base(userMessage, inner) { }
}

public sealed class InvalidInputException : AppException
{
    public InvalidInputException(string message) : base(message) { }
}

public sealed class NotAuthenticatedException : AppException
{
    public NotAuthenticatedException(string message = "Entre com sua conta para alterar o dicionário.") : base(message) { }
}

public sealed class InvalidCredentialsException : AppException
{
    public InvalidCredentialsException(string message = "E-mail ou senha incorretos.") : base(message) { }
}

public sealed class OfflineException : AppException
{
    public OfflineException(string message = "Sem conexão com a internet. Esta operação precisa de conexão.", Exception? inner = null) : base(message, inner) { }
}

public sealed class RemoteTimeoutException : AppException
{
    public RemoteTimeoutException(string message = "O servidor demorou demais para responder. Tente novamente.", Exception? inner = null) : base(message, inner) { }
}

public sealed class RemoteStoreException : AppException
{
    public RemoteStoreException(string message = "O banco de dados online está indisponível no momento.", Exception? inner = null) : base(message, inner) { }
}

public sealed class ConflictException : AppException
{
    public ConflictException(string message = "Este registro foi alterado por outra pessoa. Recarregue-o antes de salvar novamente.", Exception? inner = null) : base(message, inner) { }
}

public sealed class LocalDatabaseException : AppException
{
    public LocalDatabaseException(string message = "Não foi possível acessar o banco de dados local.", Exception? inner = null) : base(message, inner) { }
}

public sealed class AiUnavailableException : AppException
{
    public AiUnavailableException(string message = "O recurso de IA está indisponível agora. A tradução local continua funcionando.", Exception? inner = null) : base(message, inner) { }
}

public sealed class AiRateLimitException : AppException
{
    public AiRateLimitException(string message = "Você atingiu o limite diário de usos da IA. Tente novamente amanhã.") : base(message) { }
}

public sealed class InvalidAiResponseException : AppException
{
    public InvalidAiResponseException(string message = "A IA devolveu uma resposta inválida e ela foi descartada.", Exception? inner = null) : base(message, inner) { }
}

public static class UserMessages
{
    /// <summary>Converte qualquer exceção em mensagem clara e não técnica.</summary>
    public static string For(Exception ex) => ex switch
    {
        AppException app => app.Message,
        OperationCanceledException => "A operação foi cancelada.",
        TimeoutException => "O servidor demorou demais para responder. Tente novamente.",
        HttpRequestException => "Não foi possível se conectar ao servidor. Verifique sua internet.",
        _ => "Ocorreu um erro inesperado. Tente novamente."
    };
}
