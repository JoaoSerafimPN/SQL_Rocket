namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma permissão granular (de servidor ou de banco de dados) e seu
/// estado atual — usada nas grades "Avançado — Permissões Granulares"
/// das abas "Permissões de Servidor"/"Permissões de Banco/Tabelas e
/// Views" da tela "Segurança &gt; Usuários". <see cref="Estado"/> segue a
/// semântica do próprio GRANT/DENY/REVOKE do SQL Server: <c>true</c> =
/// concedida (GRANT), <c>false</c> = negada (DENY), <c>null</c> = não
/// definida (REVOKE, sem GRANT/DENY explícito). Preenchido/consumido em
/// Modulo_Admin.ObterPermissoesServidorGranularesAsync/
/// ObterPermissoesBancoGranularesAsync e
/// AtualizarPermissoesServidorGranularesAsync/AtualizarPermissoesBancoGranularesAsync.
/// </summary>
public sealed class PermissaoDto
{
    public string Nome { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public bool? Estado { get; set; }
}
