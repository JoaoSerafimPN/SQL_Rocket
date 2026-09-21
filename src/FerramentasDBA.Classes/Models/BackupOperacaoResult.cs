namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Resultado padronizado de uma operação administrativa (Backup/Restore),
/// para que a UI faça bind do status sem precisar interpretar exceções.
/// </summary>
public sealed class BackupOperacaoResult
{
    public bool Sucesso { get; set; }
    public string? MensagemDetalhe { get; set; }
    public TimeSpan Duracao { get; set; }
    public string? CaminhoArquivo { get; set; }
}
