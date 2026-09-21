namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Resultado da execução de um ALTER COLUMN de mudança de collation
/// (Modulo_Admin.AlterarCollationColunasAsync) — uma linha do .txt de log
/// que a tela "Collation &gt; Collation Manager" gera ao final das ações
/// "Mudar a collation das colunas"/"das tabelas": quando
/// <see cref="Sucesso"/> é falso, <see cref="MensagemErro"/> traz o motivo
/// (ex.: coluna usada em chave estrangeira, computed column ou publicada
/// por replicação, ou um índice de tipo não suportado pela recriação
/// automática — casos em que o SQL Server recusa o ALTER COLUMN).
/// </summary>
public sealed class ResultadoAlteracaoColunaDto
{
    public string Esquema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;
    public string Coluna { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public bool Sucesso { get; set; }
    public string? MensagemErro { get; set; }

    /// <summary>
    /// Nomes dos índices que precisaram ser removidos temporariamente e
    /// foram recriados automaticamente para permitir este ALTER COLUMN
    /// (separados por vírgula) — nulo quando a coluna não participava de
    /// nenhum índice. Só preenchido quando <see cref="Sucesso"/> é
    /// verdadeiro (uma falha na recriação vira
    /// <see cref="IndicesNaoRecriados"/>).
    /// </summary>
    public string? IndicesRecriados { get; set; }

    /// <summary>
    /// Índices que foram removidos para permitir o ALTER COLUMN e que NÃO
    /// puderam ser recriados — cada um com o motivo da falha e o script para
    /// recriá-lo à mão. Nulo no caso normal (todos voltaram).
    ///
    /// Existe porque uma tabela pode terminar a operação sem um índice (ou sem
    /// a própria chave primária): o caso típico é sair de uma collation
    /// case-sensitive para uma case-insensitive e o índice UNIQUE recusar a
    /// recriação com "duplicate key", já que 'ABC' e 'abc' passaram a ser
    /// iguais. Isso precisa aparecer em destaque no log/tela, e não sumir
    /// junto com a exceção da primeira falha.
    /// </summary>
    public string? IndicesNaoRecriados { get; set; }
}
