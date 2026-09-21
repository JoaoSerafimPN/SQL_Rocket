using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno de "Índice > Índices nunca Utilizados", a partir do
/// script fornecido pelo usuário (sys.dm_db_index_usage_stats sem
/// seeks/scans/lookups de usuário nem de sistema, JOIN sys.objects/
/// sys.indexes/sys.dm_db_index_operational_stats para os contadores
/// leaf/non-leaf de insert/delete/update). Preenchido em
/// Modulo_Indices.ObterIndicesNuncaUtilizadosAsync.
/// </summary>
/// <remarks>
/// IMPORTANTE (limitação da própria sys.dm_db_index_usage_stats, não deste
/// app): essa DMV só acumula estatísticas desde o último restart do
/// serviço SQL Server — um índice aparecer nesta lista significa "sem uso
/// registrado desde o último restart", não necessariamente "nunca usado
/// desde que foi criado". Vale conferir o tempo de atividade da instância
/// antes de excluir algo com base só nesta lista.
///
/// <see cref="EhChavePrimaria"/>/<see cref="EhRestricaoUnica"/> não vêm do
/// script original — usados só para avisar o usuário antes de excluir:
/// chave primária/restrição única não são removidas com DROP INDEX, e sim
/// ALTER TABLE ... DROP CONSTRAINT (ver Modulo_Indices.ExcluirIndiceAsync).
/// </remarks>
public sealed class IndiceNaoUtilizadoDto
{
    [DisplayName("Tabela")]
    public string NomeTabela { get; set; } = string.Empty;

    [DisplayName("Índice")]
    public string NomeIndice { get; set; } = string.Empty;

    [DisplayName("Tipo")]
    public string TipoIndice { get; set; } = string.Empty;

    [DisplayName("Leaf Insert")]
    public long LeafInsertCount { get; set; }

    [DisplayName("Leaf Delete")]
    public long LeafDeleteCount { get; set; }

    [DisplayName("Leaf Update")]
    public long LeafUpdateCount { get; set; }

    [DisplayName("Non-Leaf Insert")]
    public long NonLeafInsertCount { get; set; }

    [DisplayName("Non-Leaf Delete")]
    public long NonLeafDeleteCount { get; set; }

    [DisplayName("Non-Leaf Update")]
    public long NonLeafUpdateCount { get; set; }

    [Browsable(false)]
    public bool EhChavePrimaria { get; set; }

    [Browsable(false)]
    public bool EhRestricaoUnica { get; set; }
}
