using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha de retorno do relatório de fragmentação de índices (Índice >
/// Fragmentação), a partir do script fornecido pelo usuário:
/// sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID(...), NULL, NULL, NULL)
/// JOIN sys.indexes. Preenchido em Modulo_Indices.ObterFragmentacaoIndicesAsync.
/// </summary>
/// <remarks>
/// <see cref="NomeTabela"/> e <see cref="EhChavePrimaria"/> não vêm do script
/// original — foram acrescentados: NomeTabela (JOIN extra com sys.objects)
/// porque o nome da tabela agora é opcional na tela (em branco = carrega os
/// índices de TODAS as tabelas do banco), então cada linha precisa dizer de
/// qual tabela ela é — tanto pra exibir na grade quanto pra montar o comando
/// ALTER INDEX certo em cada índice individualmente; EhChavePrimaria
/// (IND.is_primary_key, já disponível na mesma sys.indexes do JOIN) só para
/// o filtro "PK Indexes: Todos/Somente PK/Sem PK" da tela. Nenhuma coluna do
/// script original foi removida ou alterada.
/// </remarks>
public sealed class IndiceFragmentacaoDto
{
    [DisplayName("ID do Índice")]
    public int IndiceId { get; set; }

    [DisplayName("Tabela")]
    public string NomeTabela { get; set; } = string.Empty;

    [DisplayName("Nome do Índice")]
    public string NomeIndice { get; set; } = string.Empty;

    [DisplayName("Fragmentação (%)")]
    public double PercentualFragmentacao { get; set; }

    [Browsable(false)]
    public bool EhChavePrimaria { get; set; }
}
