namespace FerramentasDBA.Classes.Models;

/// <summary>
/// Representa os parâmetros necessários para montar a connection string
/// utilizada por <see cref="Infraestrutura.Conectar_SQL"/>.
/// Estrutura apenas (sem lógica de montagem da string ainda).
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>Nome/endereço do servidor ou instância (ex: "localhost\SQLEXPRESS" ou "meuservidor.database.windows.net").</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Banco de dados inicial (opcional).</summary>
    public string? InitialDatabase { get; set; }

    /// <summary>Tipo de autenticação a ser utilizado.</summary>
    public TipoAutenticacao TipoAutenticacao { get; set; } = TipoAutenticacao.SqlServerAuthentication;

    /// <summary>Usuário (quando aplicável).</summary>
    public string? UsuarioLogin { get; set; }

    /// <summary>Senha (quando aplicável). TODO: avaliar uso de SecureString futuramente.</summary>
    public string? Senha { get; set; }

    /// <summary>Indica se o destino é Azure SQL (habilita ajustes de compatibilidade nas queries).</summary>
    public AmbienteSqlType Ambiente { get; set; } = AmbienteSqlType.Local;

    /// <summary>Timeout de conexão, em segundos.</summary>
    public int ConnectTimeoutSegundos { get; set; } = 15;

    /// <summary>Exige criptografia na conexão (Encrypt=True).</summary>
    public bool CriptografarConexao { get; set; } = true;

    /// <summary>
    /// Confia no certificado do servidor mesmo que a cadeia não seja validável
    /// (TrustServerCertificate=True).
    ///
    /// Padrão FALSE (valida o certificado). Era true fixo até a v1.0, sem
    /// nenhuma forma de desligar: a conexão era criptografada mas a identidade
    /// do servidor NUNCA era verificada — inclusive para Azure SQL pela
    /// internet, que tem certificado de CA pública perfeitamente validável.
    /// Na prática isso abre espaço para alguém no meio do caminho apresentar um
    /// certificado próprio e capturar usuário, senha e todo o tráfego.
    ///
    /// Instância local com certificado autoassinado continua funcionando: a
    /// tela de login liga esta opção automaticamente quando o servidor é local
    /// (localhost, ".", 127.0.0.1, nome de máquina sem domínio) e deixa o
    /// usuário marcar/desmarcar à mão — ver Login_SQL.
    /// </summary>
    public bool ConfiarCertificadoServidor { get; set; }
}
