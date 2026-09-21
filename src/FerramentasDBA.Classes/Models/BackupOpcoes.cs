namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Opções adicionais de uma operação de backup, refletindo os checkboxes
/// da página "Backup > Full" (e futuramente "Backup > Restore", quando aplicável).
/// </summary>
public sealed class BackupOpcoes
{
    /// <summary>WITH COPY_ONLY — não afeta a cadeia de backups incrementais/log da base.</summary>
    public bool CopyOnly { get; set; }

    /// <summary>WITH COMPRESSION — compacta o arquivo de backup gerado.</summary>
    public bool Compressao { get; set; }

    /// <summary>
    /// Após o backup, executa RESTORE VERIFYONLY para validar a integridade
    /// do arquivo gerado (sem efetivamente restaurá-lo).
    /// </summary>
    public bool VerificarIntegridade { get; set; }

    /// <summary>
    /// WITH INIT — APAGA todos os conjuntos de backup já existentes no arquivo
    /// de destino antes de gravar. Padrão FALSE (WITH NOINIT: o backup novo é
    /// ANEXADO, preservando os anteriores).
    ///
    /// O padrão da v1.0 era o contrário: o script sempre usava INIT + SKIP, ou
    /// seja, sobrescrevia sem avisar. Como a tela só alertava quando o caminho
    /// NÃO existia no servidor, apontar o backup para o arquivo do job noturno
    /// apagava o backup da noite anterior em silêncio — e se o backup novo
    /// falhasse no meio (disco cheio), os dois se perdiam. Agora sobrescrever é
    /// uma escolha explícita do usuário, feita na tela quando o arquivo já
    /// existe.
    /// </summary>
    public bool SobrescreverArquivo { get; set; }
}
