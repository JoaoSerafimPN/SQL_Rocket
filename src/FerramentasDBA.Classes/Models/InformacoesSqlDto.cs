using System.ComponentModel;

namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Informações da instância SQL Server conectada, exibidas em
/// "Informações > SQL" — os mesmos campos de uma tela clássica de "Server
/// Info" de ferramentas de tuning (hora do servidor, edição, versão, nível
/// de atualização/SP, collation, nome da instância e do host).
/// Preenchido a partir de SERVERPROPERTY(...) e SYSDATETIME() em
/// Modulo_Informacoes.ObterInformacoesSqlAsync.
/// </summary>
/// <remarks>
/// Os atributos [DisplayName] controlam o rótulo exibido no PropertyGrid da
/// tela (que, por padrão, mostraria o nome da propriedade em C# sem espaços).
/// </remarks>
public sealed class InformacoesSqlDto
{
    [DisplayName("Hora do servidor")]
    public DateTime HoraServidor { get; set; }

    [DisplayName("Edição")]
    public string Edicao { get; set; } = string.Empty;

    [DisplayName("Versão")]
    public string Versao { get; set; } = string.Empty;

    [DisplayName("Atualizações (SP)")]
    public string AtualizacoesSp { get; set; } = string.Empty;

    [DisplayName("Collation")]
    public string Collation { get; set; } = string.Empty;

    [DisplayName("InstanceName")]
    public string InstanceName { get; set; } = string.Empty;

    [DisplayName("MachineName")]
    public string MachineName { get; set; } = string.Empty;
}
