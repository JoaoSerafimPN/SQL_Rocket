using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Linha do log de erros do SQL Server — tela "Segurança &gt; Logs", no
/// mesmo formato do "Log File Viewer" do SSMS. <see cref="DataHora"/>,
/// <see cref="ProcessInfo"/> e <see cref="Texto"/> vêm direto do
/// procedimento de sistema xp_readerrorlog (colunas LogDate/ProcessInfo/Text);
/// <see cref="TipoLog"/> e <see cref="FonteLog"/> não existem no retorno do
/// procedimento — são metadados de apresentação calculados em
/// Modulo_Admin.ObterLogsErroAsync (qual arquivo de log gerou a linha),
/// só pra a grade ficar igual ao Log File Viewer do SSMS.
/// </summary>
public sealed class LogErroDto
{
    [DisplayName("Data")]
    public DateTime DataHora { get; set; }

    [DisplayName("Origem")]
    public string? ProcessInfo { get; set; }

    [DisplayName("Mensagem")]
    public string Texto { get; set; } = string.Empty;

    [DisplayName("Tipo de Log")]
    public string TipoLog { get; set; } = "SQL Server";

    [DisplayName("Fonte do Log")]
    public string FonteLog { get; set; } = string.Empty;
}
