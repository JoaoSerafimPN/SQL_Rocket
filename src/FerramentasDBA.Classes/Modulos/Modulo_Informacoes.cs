using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Atende ao menu raiz "Informações" (SQL, Servidor).
/// Este módulo não constava na lista original de 4 módulos do documento de
/// arquitetura, mas é necessário para cobrir os itens de menu "Informações
/// > SQL" e "Informações > Servidor" descritos na estrutura de MenuStrip.
/// </summary>
public class Modulo_Informacoes
{
    private readonly Conectar_SQL _conexao;

    public Modulo_Informacoes(Conectar_SQL conexao)
    {
        _conexao = conexao;
    }

    /// <summary>
    /// Retorna informações da instância/versão do SQL Server conectado
    /// (hora do servidor, edição, versão, SP, collation, nome da instância
    /// e do host) — via SERVERPROPERTY(...) e SYSDATETIME().
    /// </summary>
    public async Task<InformacoesSqlDto> ObterInformacoesSqlAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText =
            "SELECT " +
            "    SYSDATETIME() AS HoraServidor, " +
            "    CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)) AS Edicao, " +
            "    CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)) AS Versao, " +
            "    CAST(SERVERPROPERTY('ProductLevel') AS NVARCHAR(128)) AS AtualizacoesSp, " +
            "    CAST(SERVERPROPERTY('Collation') AS NVARCHAR(128)) AS Collation, " +
            "    CAST(SERVERPROPERTY('InstanceName') AS NVARCHAR(128)) AS InstanceName, " +
            "    CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS MachineName;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        if (!await leitor.ReadAsync(ct))
        {
            throw new InvalidOperationException("Não foi possível obter as informações da instância.");
        }

        return new InformacoesSqlDto
        {
            HoraServidor = leitor.GetDateTime(leitor.GetOrdinal("HoraServidor")),
            Edicao = ObterTextoOuPadrao(leitor, "Edicao"),
            Versao = ObterTextoOuPadrao(leitor, "Versao"),
            AtualizacoesSp = ObterTextoOuPadrao(leitor, "AtualizacoesSp"),
            Collation = ObterTextoOuPadrao(leitor, "Collation"),
            // InstanceName vem NULL numa instância default — mesma convenção
            // usada por ferramentas de tuning tradicionais (mostrar
            // "DEFAULT INSTANCE" em vez de deixar em branco).
            InstanceName = ObterTextoOuPadrao(leitor, "InstanceName", "DEFAULT INSTANCE"),
            MachineName = ObterTextoOuPadrao(leitor, "MachineName")
        };
    }

    /// <summary>
    /// Retorna informações de hardware/sistema operacional do host (nome, IP,
    /// memória, processador, unidades de disco, versão do Windows e
    /// domínio). A maior parte vem de DMVs do SQL Server; Domínio e Versão do
    /// Windows vêm de WMI (ver <see cref="ObterInfoWmiServidor"/>), sem
    /// depender de xp_cmdshell. Cada campo é obtido em uma consulta/chamada
    /// própria e, se não estiver disponível na edição conectada (ex: SQL
    /// Azure — PaaS, sem SO exposto —, versão antiga do SQL Server, ou host
    /// inacessível via WMI), o campo individual cai para "Não disponível" em
    /// vez de derrubar a tela inteira.
    /// </summary>
    public async Task<InformacoesServidorDto> ObterInformacoesServidorAsync(CancellationToken ct = default)
    {
        await using SqlConnection conexao = await _conexao.AbrirConexaoAsync(ct);
        var dto = new InformacoesServidorDto();

        // Nome do host físico + IP do servidor na conexão atual. Disponível
        // mesmo no SQL Azure (MachineName pode vir NULL nesse caso — ver
        // ObterTextoOuPadrao).
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText =
                "SELECT " +
                "    CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) AS Nome, " +
                "    c.local_net_address AS Ip " +
                "FROM sys.dm_exec_connections AS c " +
                "WHERE c.session_id = @@SPID;";
            await using var leitor = await comando.ExecuteReaderAsync(ct);
            if (await leitor.ReadAsync(ct))
            {
                dto.Nome = ObterTextoOuPadrao(leitor, "Nome");
                dto.Ip = ObterTextoOuPadrao(leitor, "Ip");
            }
        }
        catch (Exception ex)
        {
            dto.Nome = "Não disponível";
            dto.Ip = $"Não disponível ({ex.Message})";
        }

        // local_net_address vem NULL quando a conexão não é por TCP/IP (ex:
        // Shared Memory, comum ao conectar em "localhost"/"." na mesma
        // máquina) — não é erro do SQL Server, só não se aplica a esse
        // protocolo. Nesse caso, resolve o IP direto pelo nome do host via
        // DNS (System.Net.Dns, biblioteca padrão do .NET/Visual Studio — sem
        // depender do SQL Server para isso).
        if ((string.IsNullOrWhiteSpace(dto.Ip) || dto.Ip == "-") &&
            !string.IsNullOrWhiteSpace(dto.Nome) && dto.Nome != "-" && dto.Nome != "Não disponível")
        {
            try
            {
                var enderecos = await Dns.GetHostAddressesAsync(dto.Nome, ct);
                var enderecoIpv4 = enderecos.FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork);
                dto.Ip = (enderecoIpv4 ?? enderecos.FirstOrDefault())?.ToString() ?? "Não disponível";
            }
            catch (Exception)
            {
                dto.Ip = "Não disponível (host não resolvido via DNS)";
            }
        }

        // Memória física total e núcleos do processador (sys.dm_os_sys_info
        // existe tanto em SQL Server local quanto em SQL Azure). cpu_count é
        // a contagem de núcleos que o SQL Server enxerga (lógicos, já
        // considerando hyperthreading quando houver) — exibido direto, sem
        // tentar estimar físico x lógico separadamente.
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText = "SELECT physical_memory_kb, cpu_count FROM sys.dm_os_sys_info;";
            await using var leitor = await comando.ExecuteReaderAsync(ct);
            if (await leitor.ReadAsync(ct))
            {
                var memoriaKb = leitor.GetInt64(leitor.GetOrdinal("physical_memory_kb"));
                var nucleos = leitor.GetInt32(leitor.GetOrdinal("cpu_count"));

                dto.Memoria = $"{memoriaKb / 1024.0 / 1024.0:0.00} GB";
                dto.Processador = $"{nucleos} núcleos";
            }
        }
        catch (Exception ex)
        {
            dto.Memoria = "Não disponível";
            dto.Processador = $"Não disponível ({ex.Message})";
        }

        // Unidades de disco que hospedam arquivos de banco de dados — não é
        // necessariamente todo disco físico do host (só o que tem algum
        // arquivo de banco), mas é a unidade que mais importa para um DBA e
        // funciona mesmo quando o host não está acessível via WMI (abaixo).
        try
        {
            await using var comando = conexao.CreateCommand();
            comando.CommandText =
                "SELECT DISTINCT " +
                "    vs.volume_mount_point, " +
                "    CAST(vs.total_bytes / 1073741824.0 AS DECIMAL(10,2)) AS TotalGb, " +
                "    CAST(vs.available_bytes / 1073741824.0 AS DECIMAL(10,2)) AS LivreGb " +
                "FROM sys.master_files AS mf " +
                "CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) AS vs " +
                "ORDER BY vs.volume_mount_point;";
            await using var leitor = await comando.ExecuteReaderAsync(ct);

            var linhas = new List<string>();
            while (await leitor.ReadAsync(ct))
            {
                var unidade = leitor.GetString(leitor.GetOrdinal("volume_mount_point"));
                var totalGb = leitor.GetDecimal(leitor.GetOrdinal("TotalGb"));
                var livreGb = leitor.GetDecimal(leitor.GetOrdinal("LivreGb"));
                linhas.Add($"{unidade} — {livreGb:0.00} GB livres de {totalGb:0.00} GB");
            }

            dto.Hds = linhas.Count > 0 ? string.Join(Environment.NewLine, linhas) : "Nenhuma unidade encontrada.";
        }
        catch (Exception ex)
        {
            dto.Hds = $"Não disponível ({ex.Message})";
        }

        // Domínio e Versão do Windows: obtidos via WMI, direto do host onde o
        // SQL Server roda — a mesma técnica usada pelo SSMS/Visual Studio nas
        // propriedades do servidor. Não depende de xp_cmdshell (que
        // continua desabilitado no SQL Server, evitando a superfície de
        // ataque que habilitá-lo abriria); em vez disso usa a identidade do
        // Windows de quem está rodando o SQL Rocket para consultar o
        // serviço WMI do host pela rede. Só funciona se: o host em "Nome"
        // for alcançável na rede (portas de WMI/DCOM) e esse usuário tiver
        // permissão lá — não se aplica ao SQL Azure (PaaS, sem host de SO) e
        // pode não funcionar se cliente e servidor não estiverem na mesma
        // rede/domínio (nesse caso cai para "Não disponível").
        //
        // O teste abaixo repete EXATAMENTE o mesmo conjunto de sentinelas do
        // guarda do DNS (acima): "-" (coluna NULL) e "Não disponível" (a
        // consulta que traria o nome falhou, ver catch lá em cima). Antes só
        // "-" era testado, então o literal "Não disponível" passava adiante
        // COMO SE FOSSE UM NOME DE MÁQUINA: o código montava
        // \\Não disponível\root\cimv2, esperava o timeout de rede e ainda
        // reportava "sem acesso WMI" para uma falha que, na verdade, tinha sido
        // de DMV/permissão no próprio SQL Server.
        if (!string.IsNullOrWhiteSpace(dto.Nome) && dto.Nome != "-" && dto.Nome != "Não disponível")
        {
            try
            {
                // ObterInfoWmiServidor é uma chamada síncrona/bloqueante (WMI
                // sobre DCOM) — Task.Run tira ela da thread atual (a UI thread,
                // já que o restante do método já rodou nela após os awaits
                // anteriores) para não travar a tela enquanto espera a rede.
                var infoWmi = await Task.Run(() => ObterInfoWmiServidor(dto.Nome), ct);
                dto.Dominio = infoWmi.Dominio ?? "Não disponível";
                dto.VersaoWindows = infoWmi.VersaoWindows ?? "Não disponível";
            }
            catch (Exception ex)
            {
                dto.Dominio = $"Não disponível (sem acesso WMI ao host: {ex.Message})";
                dto.VersaoWindows = "Não disponível (sem acesso WMI ao host)";
            }
        }
        else
        {
            // Duas causas bem diferentes levam a este ponto, e a mensagem
            // precisa separá-las: ou a instância não tem host de SO exposto
            // (PaaS), ou a leitura do nome do host falhou antes — culpar o WMI
            // nesse segundo caso mandava o usuário investigar a rede quando o
            // problema estava no SQL Server.
            var motivo = dto.Nome == "Não disponível"
                ? "Não disponível (o nome do host não pôde ser lido do SQL Server — ver o campo Nome)"
                : "Não disponível (host não identificado — provável instância PaaS, ex: SQL Azure)";
            dto.Dominio = motivo;
            dto.VersaoWindows = motivo;
        }

        // Fallback: se o WMI não respondeu a versão do Windows (host
        // inacessível pela rede, por exemplo), tenta a DMV como segunda
        // opção — existe a partir do SQL Server 2016; também ausente em
        // versões mais antigas e no SQL Azure.
        if (dto.VersaoWindows.StartsWith("Não disponível", StringComparison.Ordinal))
        {
            try
            {
                await using var comando = conexao.CreateCommand();
                comando.CommandText = "SELECT windows_release, windows_service_pack_level FROM sys.dm_os_windows_info;";
                await using var leitor = await comando.ExecuteReaderAsync(ct);
                if (await leitor.ReadAsync(ct))
                {
                    // windows_service_pack_level é nvarchar(256) em
                    // sys.dm_os_windows_info — NÃO é numérico. Era lido com
                    // GetDouble(...) e formatado com ":0.0", o que lançava
                    // InvalidCastException em 100% das execuções; o catch vazio
                    // abaixo engolia o erro e este fallback — que existe
                    // exatamente para o caso comum de o WMI estar bloqueado —
                    // nunca funcionou uma única vez. Agora é lido como o texto
                    // que é; quando vier vazio (comum a partir do Windows 10/
                    // Server 2016, que não usam mais Service Pack) o sufixo
                    // simplesmente não é exibido, em vez de mostrar "(SP 0,0)".
                    var versao = ObterTextoOuPadrao(leitor, "windows_release", string.Empty);
                    var servicePack = ObterTextoOuPadrao(leitor, "windows_service_pack_level", string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(versao))
                    {
                        dto.VersaoWindows = string.IsNullOrWhiteSpace(servicePack)
                            ? versao
                            : $"{versao} ({servicePack})";
                    }
                }
            }
            catch (Exception ex)
            {
                // A degradação graciosa continua (a tela não cai), mas sem
                // silêncio: o campo fica com o motivo. A DMV só existe a partir
                // do SQL Server 2016 e não existe no SQL Azure — o usuário
                // precisa distinguir isso de uma falha de permissão.
                dto.VersaoWindows = $"{dto.VersaoWindows} — e a DMV sys.dm_os_windows_info também não respondeu: {ex.Message}";
            }
        }

        return dto;
    }

    /// <summary>
    /// Consulta o WMI (Windows Management Instrumentation) do host informado
    /// para obter o domínio e a versão do Windows — mesma fonte que
    /// ferramentas como o SSMS/Visual Studio usam nas propriedades do
    /// servidor, sem precisar de xp_cmdshell no SQL Server. Usa a identidade
    /// do Windows do usuário atual (processo do SQL Rocket), então só
    /// funciona se esse usuário tiver acesso WMI ao host pela rede.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string? Dominio, string? VersaoWindows) ObterInfoWmiServidor(string maquina)
    {
        var escopo = new ManagementScope(
            $@"\\{maquina}\root\cimv2",
            new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
                // Timeout curto: se o host não responder no WMI (rede/firewall
                // bloqueando DCOM), falha rápido em vez de travar por muito
                // tempo — o padrão do WMI, sem isso, pode demorar bem mais.
                Timeout = TimeSpan.FromSeconds(5)
            });
        escopo.Connect();

        // ConnectionOptions.Timeout (acima) cobre, no máximo, o Connect(). A
        // ENUMERAÇÃO do resultado — o foreach sobre Get() — não tem timeout
        // NENHUM por padrão (EnumerationOptions.Timeout nasce como
        // TimeSpan.MaxValue), então com um firewall descartando pacotes DCOM a
        // tela ficava ~45 s travada mesmo com os 5 s configurados, que era
        // justamente o que o timeout curto queria evitar. As duas consultas
        // abaixo compartilham estas opções.
        // Qualificado com o namespace inteiro de propósito: "EnumerationOptions"
        // sozinho é AMBÍGUO (erro CS0104) porque existe tanto em
        // System.Management quanto em System.IO, e o projeto usa ImplicitUsings
        // (que já traz System.IO). Aqui é o do WMI.
        var opcoesEnumeracao = new System.Management.EnumerationOptions
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        string? dominio = null;
        using (var pesquisa = new ManagementObjectSearcher(
            escopo, new ObjectQuery("SELECT Domain, PartOfDomain FROM Win32_ComputerSystem"), opcoesEnumeracao))
        using (var colecao = pesquisa.Get())
        {
            // ManagementObjectCollection e cada ManagementObject são wrappers de
            // objeto COM e implementam IDisposable: sem Dispose explícito a
            // referência COM só é liberada pelo finalizador (em hora
            // indeterminada), e o "break" — que é o caminho NORMAL aqui, já que
            // só interessa a primeira instância — saía do foreach deixando o
            // objeto para trás. O "using (item)" libera antes do break.
            foreach (ManagementObject item in colecao)
            {
                using (item)
                {
                    var parteDeDominio = item["PartOfDomain"] is bool valor && valor;
                    dominio = parteDeDominio ? item["Domain"]?.ToString() : "(grupo de trabalho, sem domínio)";
                }

                break;
            }
        }

        string? versaoWindows = null;
        using (var pesquisa = new ManagementObjectSearcher(
            escopo, new ObjectQuery("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"), opcoesEnumeracao))
        using (var colecao = pesquisa.Get())
        {
            foreach (ManagementObject item in colecao)
            {
                using (item)
                {
                    versaoWindows = $"{item["Caption"]} (build {item["BuildNumber"]})".Trim();
                }

                break;
            }
        }

        return (dominio, versaoWindows);
    }

    /// <summary>Lê uma coluna string do reader, retornando um valor padrão se ela vier NULL.</summary>
    private static string ObterTextoOuPadrao(SqlDataReader leitor, string coluna, string padrao = "-")
    {
        var indice = leitor.GetOrdinal(coluna);
        return leitor.IsDBNull(indice) ? padrao : leitor.GetString(indice);
    }
}
