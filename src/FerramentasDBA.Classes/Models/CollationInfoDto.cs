namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Informações de collation de um banco de dados — script 1 fornecido pelo
/// usuário para a tela "Collation &gt; Collation Manager" (SELECT name,
/// collation_name FROM sys.databases WHERE name = ...). Preenchido em
/// Modulo_Admin.ObterCollationDatabaseAsync.
/// </summary>
/// <remarks>
/// <see cref="CollationServidor"/>/<see cref="DivergeDoServidor"/> não vêm
/// do script do usuário (que só traz a collation do banco) — continuam
/// aqui, não preenchidos por enquanto, para o caso de uma comparação com
/// SERVERPROPERTY('Collation') ser pedida numa próxima rodada.
/// </remarks>
public sealed class CollationInfoDto
{
    public string NomeBanco { get; set; } = string.Empty;
    public string CollationBanco { get; set; } = string.Empty;
    public string? CollationServidor { get; set; }
    public bool DivergeDoServidor { get; set; }
}
