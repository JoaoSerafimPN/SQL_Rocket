using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma linha da listagem completa de colunas do banco (script fornecido
/// pelo usuário para a grade "Detalhe de colunas", logo abaixo de
/// "Collations found" na tela "Collation &gt; Collation Manager") — ao
/// contrário de <see cref="CollationColunaDto"/>, aqui entram TODAS as
/// colunas de TODAS as tabelas de usuário, inclusive as que não são de
/// texto (<see cref="CollationDaColuna"/> vem "N/A (Não-Texto)" nesse
/// caso) — sem comparação/agrupamento, é a listagem crua do script.
/// Preenchida em Modulo_Admin.ObterDetalheColunasAsync.
/// </summary>
public sealed class DetalheColunaDto
{
    [DisplayName("Tabela")]
    public string Tabela { get; set; } = string.Empty;

    [DisplayName("Coluna")]
    public string NomeDaColuna { get; set; } = string.Empty;

    [DisplayName("Tipo de Dado")]
    public string TipoDeDado { get; set; } = string.Empty;

    [DisplayName("Collation da Coluna")]
    public string CollationDaColuna { get; set; } = string.Empty;
}
