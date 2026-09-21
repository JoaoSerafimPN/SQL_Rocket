namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Script "ALTER TABLE ... ALTER COLUMN ... COLLATE ..." gerado para uma
/// coluna específica — script gerador de scripts de alteração fornecido
/// pelo usuário para a tela "Collation &gt; Collation Manager", com
/// "SUA_NOVA_COLLATION" substituído pela collation escolhida no combo da
/// tela. Preenchido em
/// Modulo_Admin.GerarScriptsAlteracaoCollationColunasAsync — consumido por
/// Modulo_Admin.AlterarCollationColunasAsync, que executa um a um.
/// </summary>
public sealed class ScriptAlteracaoColunaDto
{
    public string Esquema { get; set; } = string.Empty;
    public string Tabela { get; set; } = string.Empty;
    public string Coluna { get; set; } = string.Empty;
    public string ScriptAlteracao { get; set; } = string.Empty;
}
