namespace Rhaghoorian.Core.Text;

/// <summary>
/// Ponto de extensão para o ALFABETO ESPECIAL do Rhagho'oriano (futuro). Nesta versão só existe o alfabeto latino do computador
/// e NENHUMA conversão de caracteres é feita. Todo o sistema guarda, pesquisa e compara sempre a forma latina; uma futura
/// implementação deve converter apenas na borda da interface (exibir / receber digitação), sem tocar em SQLite, Firestore,
/// regras linguísticas ou IA.
/// </summary>
public interface IRhaghoScript
{
    /// <summary>Forma latina (armazenada) → forma exibida ao usuário.</summary>
    string ToDisplay(string latin);
    /// <summary>Texto digitado/colado pelo usuário → forma latina (armazenada).</summary>
    string FromDisplay(string display);
}

/// <summary>Alfabeto latino convencional: identidade.</summary>
public sealed class LatinRhaghoScript : IRhaghoScript
{
    public string ToDisplay(string latin) => latin;
    public string FromDisplay(string display) => display;
}
