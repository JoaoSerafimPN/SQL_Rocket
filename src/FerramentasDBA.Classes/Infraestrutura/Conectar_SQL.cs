using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Infraestrutura;

/// <summary>
/// Responsável por centralizar a configuração da connection string e o
/// ciclo de vida das conexões ADO.NET (Microsoft.Data.SqlClient) usadas por
/// todos os módulos de negócio (Modulo_Indices, Modulo_Desempenho,
/// Modulo_Admin, etc.).
///
/// Suporta autenticação SQL Server e Windows; a estrutura já contempla
/// Azure Active Directory e ambiente Azure SQL para uma etapa futura,
/// mesmo que a tela de login atual não exponha essas opções ainda.
/// </summary>
public class Conectar_SQL
{
    /// <summary>
    /// Nome com que as conexões desta ferramenta se identificam no SQL Server
    /// (<c>Application Name</c> da connection string →
    /// <c>sys.dm_exec_sessions.program_name</c>). Usado também pelo Profiler
    /// para reconhecer (e poder esconder) as consultas da própria ferramenta —
    /// ver <c>Modulo_Profiler.NomeAplicativoProprio</c>.
    /// </summary>
    public const string NomeAplicacao = "SQL Rocket";

    /// <summary>
    /// Configuração de conexão atualmente ativa. Definida via
    /// <see cref="ConfigurarConexao"/> (geralmente a partir da tela Login_SQL).
    /// </summary>
    public ConnectionSettings? ConfiguracaoAtual { get; private set; }

    /// <summary>
    /// Define/atualiza os parâmetros de conexão que serão utilizados pelos
    /// próximos métodos desta classe.
    /// </summary>
    /// <param name="configuracao">Parâmetros de servidor, autenticação e ambiente.</param>
    /// <exception cref="ArgumentNullException">Configuração não informada.</exception>
    /// <exception cref="ArgumentException">Parâmetro obrigatório ausente.</exception>
    public void ConfigurarConexao(ConnectionSettings configuracao)
    {
        ArgumentNullException.ThrowIfNull(configuracao);

        if (string.IsNullOrWhiteSpace(configuracao.ServerName))
        {
            throw new ArgumentException("O nome/endereço do servidor é obrigatório.", nameof(configuracao));
        }

        if (configuracao.TipoAutenticacao == TipoAutenticacao.SqlServerAuthentication
            && string.IsNullOrWhiteSpace(configuracao.UsuarioLogin))
        {
            throw new ArgumentException("O usuário é obrigatório para autenticação SQL Server.", nameof(configuracao));
        }

        ConfiguracaoAtual = configuracao;
    }

    /// <summary>
    /// Monta a connection string a partir de <see cref="ConfiguracaoAtual"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nenhuma configuração foi definida ainda.</exception>
    public string ObterStringConexao()
    {
        if (ConfiguracaoAtual is null)
        {
            throw new InvalidOperationException(
                "Nenhuma configuração de conexão foi definida. Chame ConfigurarConexao() antes.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ConfiguracaoAtual.ServerName,
            ConnectTimeout = ConfiguracaoAtual.ConnectTimeoutSegundos,
            Encrypt = ConfiguracaoAtual.CriptografarConexao,
            TrustServerCertificate = ConfiguracaoAtual.ConfiarCertificadoServidor,
            // Identifica as conexões DESTA ferramenta no servidor
            // (sys.dm_exec_sessions.program_name). Sem isso, o driver usa o
            // padrão "Core Microsoft SqlClient Data Provider", que é o mesmo
            // de QUALQUER aplicação .NET que não configure o seu — o que
            // causava dois problemas reais: (1) o filtro "excluir as consultas
            // do próprio app" do Profiler escondia também as consultas dos
            // outros aplicativos .NET do cliente, justamente os que o DBA
            // estava tentando investigar; (2) no painel "Conexões por
            // Aplicação/Usuário", a ferramenta e os apps do cliente apareciam
            // somados numa linha só. Ver Modulo_Profiler.NomeAplicativoProprio.
            ApplicationName = NomeAplicacao
        };

        if (!string.IsNullOrWhiteSpace(ConfiguracaoAtual.InitialDatabase))
        {
            builder.InitialCatalog = ConfiguracaoAtual.InitialDatabase;
        }

        switch (ConfiguracaoAtual.TipoAutenticacao)
        {
            case TipoAutenticacao.WindowsAuthentication:
                builder.IntegratedSecurity = true;
                break;

            case TipoAutenticacao.SqlServerAuthentication:
                builder.UserID = ConfiguracaoAtual.UsuarioLogin;
                builder.Password = ConfiguracaoAtual.Senha;
                break;

            case TipoAutenticacao.AzureActiveDirectory:
                // TODO (etapa futura): revisar modo de autenticação Azure AD mais
                // adequado (Interactive, Default, Service Principal, etc.) quando
                // a tela de login voltar a expor a opção de Azure SQL.
                builder.UserID = ConfiguracaoAtual.UsuarioLogin;
                builder.Password = ConfiguracaoAtual.Senha;
                builder.Authentication = SqlAuthenticationMethod.SqlPassword;
                break;

            default:
                throw new InvalidOperationException(
                    $"Tipo de autenticação não suportado: {ConfiguracaoAtual.TipoAutenticacao}.");
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Testa se é possível conectar com os parâmetros configurados, sem manter
    /// a conexão aberta. Utilizado pela tela Login_SQL antes de liberar acesso.
    /// </summary>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>True quando a conexão é validada com sucesso.</returns>
    /// <exception cref="InvalidOperationException">
    /// Falha ao conectar (rede, timeout ou credenciais inválidas), com mensagem
    /// amigável para exibição na UI.
    /// </exception>
    public async Task<bool> TestarConexaoAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = new SqlConnection(ObterStringConexao());
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = ConfiguracaoAtual!.ConnectTimeoutSegundos;
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (SqlException ex) when (ex.Number is 18456 or 18452)
        {
            // Login inválido / usuário não associado a uma conexão confiável.
            throw new InvalidOperationException("Usuário ou senha inválidos.", ex);
        }
        catch (SqlException ex) when (ex.Number is 53 or -2 or 11001)
        {
            // Servidor não encontrado / timeout de rede / falha de resolução de nome.
            throw new InvalidOperationException(
                "Não foi possível encontrar o servidor. Verifique o nome/endereço informado e a rede.", ex);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Falha ao conectar ao SQL Server: {ex.Message}", ex);
        }
        catch (InvalidOperationException)
        {
            // Repassa erros de configuração ausente/inválida sem "re-embrulhar".
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Erro inesperado ao testar a conexão: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Abre (assincronamente) e retorna uma conexão pronta para uso pelos
    /// módulos de negócio. O chamador é responsável por fazer o dispose
    /// (ex: via "await using").
    /// </summary>
    /// <param name="ct">Token de cancelamento.</param>
    /// <exception cref="InvalidOperationException">Falha ao abrir a conexão.</exception>
    public async Task<SqlConnection> AbrirConexaoAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(ObterStringConexao());
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Falha ao abrir conexão com o SQL Server: {ex.Message}", ex);
        }
    }
}
