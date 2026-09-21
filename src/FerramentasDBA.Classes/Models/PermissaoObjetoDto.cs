namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Permissões de SELECT/INSERT/UPDATE/DELETE de um usuário do banco numa
/// tabela ou view específica — uma linha por tabela/view do banco, usada
/// na grade "Permissões em Tabelas e Views" da aba "Permissões de
/// Banco/Tabelas e Views" da tela "Segurança &gt; Usuários". Cada
/// permissão segue a mesma semântica de <see cref="PermissaoDto.Estado"/>
/// (true = GRANT, false = DENY, null = não definida/REVOKE). Preenchida/
/// consumida em Modulo_Admin.ObterPermissoesObjetosAsync/
/// AtualizarPermissoesObjetosAsync.
/// </summary>
public sealed class PermissaoObjetoDto
{
    public string Esquema { get; set; } = string.Empty;
    public string NomeObjeto { get; set; } = string.Empty;
    public string TipoObjeto { get; set; } = string.Empty; // "Tabela" ou "View"

    public bool? Select { get; set; }
    public bool? Insert { get; set; }
    public bool? Update { get; set; }
    public bool? Delete { get; set; }

    public string NomeCompleto => $"{Esquema}.{NomeObjeto}";
}
