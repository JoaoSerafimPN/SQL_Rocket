using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha do comparativo de collation por coluna — script "VISÃO COMPARATIVA"
/// fornecido pelo usuário: uma coluna de texto de uma tabela de usuário, sua
/// collation, a collation do banco de dados a que pertence, e se as duas
/// divergem. Preenchido em Modulo_Admin.ObterComparativoCollationColunasAsync
/// — usado para popular a grade "Collations found" da tela "Collation &gt;
/// Collation Manager" (agrupado por <see cref="CollationDaColuna"/>, com
/// contagem) e, futuramente, para decidir com mais precisão quais colunas
/// realmente precisam de ALTER.
/// </summary>
public sealed class CollationColunaDto
{
    [DisplayName("Esquema")]
    public string Esquema { get; set; } = string.Empty;

    [DisplayName("Tabela")]
    public string Tabela { get; set; } = string.Empty;

    [DisplayName("Coluna")]
    public string Coluna { get; set; } = string.Empty;

    [DisplayName("Collation da Coluna")]
    public string CollationDaColuna { get; set; } = string.Empty;

    [DisplayName("Collation do Banco")]
    public string CollationDoBanco { get; set; } = string.Empty;

    [DisplayName("Status")]
    public string StatusCollation { get; set; } = string.Empty;
}
