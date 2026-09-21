namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Uma conexão guardada pelo usuário na tela de login, para não precisar
/// redigitar servidor/usuário/senha a cada abertura do programa.
///
/// Sobre a senha: ela NÃO fica aqui em texto. O campo
/// <see cref="SenhaProtegida"/> guarda o resultado da criptografia do Windows
/// (DPAPI) no escopo do usuário atual — ou seja, o arquivo só pode ser lido
/// pela mesma conta do Windows, na mesma máquina. Copiar o arquivo para outro
/// computador ou abri-lo com outro usuário não devolve a senha. Ver
/// <c>Infraestrutura.ConexoesSalvas</c>.
///
/// Guardar senha é sempre uma escolha: por isso ela só é gravada quando o
/// usuário marca a opção na tela, e uma conexão sem senha salva simplesmente
/// pede a senha na hora de conectar.
/// </summary>
public sealed class ConexaoSalvaDto
{
    /// <summary>Nome que o usuário deu (é o que aparece na lista da tela de login).</summary>
    public string Nome { get; set; } = string.Empty;

    /// <summary>Servidor/instância (mesmo conteúdo do campo "SERVIDOR").</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Banco inicial — opcional, normalmente preenchido só em Azure SQL.</summary>
    public string? InitialDatabase { get; set; }

    /// <summary>Autenticação do SQL Server ou do Windows.</summary>
    public TipoAutenticacao TipoAutenticacao { get; set; } = TipoAutenticacao.SqlServerAuthentication;

    /// <summary>Login — vazio quando a autenticação é a do Windows.</summary>
    public string? UsuarioLogin { get; set; }

    /// <summary>
    /// Senha cifrada com DPAPI e convertida para Base64, ou nulo quando o
    /// usuário optou por não guardar a senha. Nunca contém a senha legível.
    /// </summary>
    public string? SenhaProtegida { get; set; }

    /// <summary>Repete a escolha da caixa "Confiar no certificado do servidor".</summary>
    public bool ConfiarCertificadoServidor { get; set; }
}
