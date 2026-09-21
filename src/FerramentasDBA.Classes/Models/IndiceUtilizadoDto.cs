using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno de "Índice > Índices mais utilizados", a partir do
/// script fornecido pelo usuário (sys.dm_db_index_usage_stats JOIN
/// sys.objects/sys.indexes para os contadores de seeks/scans/lookups de
/// usuário e de sistema, JOIN sys.dm_db_index_operational_stats para os
/// contadores leaf/non-leaf de insert/delete/update). Preenchido em
/// Modulo_Indices.ObterIndicesMaisUtilizadosAsync.
/// </summary>
/// <remarks>
/// O script original não define ordenação — como a tela é justamente um
/// ranking ("mais utilizados"), Modulo_Indices adiciona
/// "ORDER BY (user_seeks + user_scans + user_lookups) DESC" (soma dos usos
/// por consulta de usuário), sem alterar nenhuma coluna do script.
/// </remarks>
public sealed class IndiceUtilizadoDto
{
    [DisplayName("Tabela")]
    public string NomeTabela { get; set; } = string.Empty;

    [DisplayName("Índice")]
    public string NomeIndice { get; set; } = string.Empty;

    [DisplayName("User Seeks")]
    public long UserSeeks { get; set; }

    [DisplayName("User Scans")]
    public long UserScans { get; set; }

    [DisplayName("User Lookups")]
    public long UserLookups { get; set; }

    [DisplayName("System Seeks")]
    public long SystemSeeks { get; set; }

    [DisplayName("System Scans")]
    public long SystemScans { get; set; }

    [DisplayName("System Lookups")]
    public long SystemLookups { get; set; }

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
}
