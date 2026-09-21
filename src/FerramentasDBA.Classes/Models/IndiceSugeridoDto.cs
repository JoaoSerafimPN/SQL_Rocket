using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno de "Índice > Índices sugeridos", a partir do script
/// fornecido pelo usuário ("Listagem 2" — TOP 15
/// sys.dm_db_missing_index_group_stats JOIN sys.dm_db_missing_index_groups
/// JOIN sys.dm_db_missing_index_details, ORDER BY impacto estimado DESC).
/// Preenchido em Modulo_Indices.ObterIndicesSugeridosAsync.
/// </summary>
/// <remarks>
/// <see cref="NomeTabela"/> e <see cref="ScriptCriacaoSugerido"/> não vêm
/// do script original: NomeTabela (OBJECT_NAME(mid.object_id)) é só para
/// exibir/filtrar de forma legível na grade — <see cref="Statement"/> já
/// traz o nome totalmente qualificado (banco.schema.tabela, entre
/// colchetes) usado de fato no script de criação. ScriptCriacaoSugerido é
/// o CREATE NONCLUSTERED INDEX já pronto pra rodar (colunas de igualdade +
/// desigualdade como chave, colunas incluídas via INCLUDE — mesma fórmula
/// que o SSMS usa), usado pelo botão "Criar Índice": o texto exibido na
/// tela é exatamente o que é executado ao confirmar.
/// </remarks>
public sealed class IndiceSugeridoDto
{
    [DisplayName("Impacto Estimado")]
    public double Impacto { get; set; }

    [DisplayName("Tabela")]
    public string NomeTabela { get; set; } = string.Empty;

    [DisplayName("User Seeks")]
    public long UserSeeks { get; set; }

    [DisplayName("User Scans")]
    public long UserScans { get; set; }

    [DisplayName("Colunas de Igualdade")]
    public string? ColunasIgualdade { get; set; }

    [DisplayName("Colunas de Desigualdade")]
    public string? ColunasDesigualdade { get; set; }

    [DisplayName("Colunas Incluídas")]
    public string? ColunasIncluidas { get; set; }

    [Browsable(false)]
    public int GroupHandle { get; set; }

    [Browsable(false)]
    public int IndexHandle { get; set; }

    [Browsable(false)]
    public string Statement { get; set; } = string.Empty;

    [Browsable(false)]
    public string ScriptCriacaoSugerido { get; set; } = string.Empty;
}
