using System.Text;
using FerramentasDBA.Classes.Infraestrutura;
using FerramentasDBA.Classes.Models;
using Microsoft.Data.SqlClient;

namespace FerramentasDBA.Classes.Modulos;

/// <summary>
/// Atende ao menu raiz "Comparador" (Índices, Banco de dados).
///
/// Pressupõe duas conexões (origem/destino): o banco de ORIGEM é sempre um
/// banco da instância já conectada na ferramenta (reaproveita o mesmo
/// <see cref="Conectar_SQL"/> — cada método abre sua própria conexão e troca
/// de banco via <see cref="SqlConnection.ChangeDatabase"/>); o de DESTINO
/// pode ser outro banco da MESMA instância (o mesmo <see cref="Conectar_SQL"/>,
/// banco diferente) ou de uma instância totalmente diferente (um
/// <see cref="Conectar_SQL"/> próprio, configurado à parte pela tela com
/// servidor/usuário/senha informados ali mesmo).
///
/// "Comparador &gt; Índices" (<see cref="CompararIndicesAsync"/>/
/// <see cref="AplicarIndicesAsync"/>) e "Comparador &gt; Banco de dados"
/// (<see cref="CompararBancosAsync"/>/<see cref="AplicarBancosAsync"/>)
/// estão implementados — T-SQL próprio em ambos, pedido do usuário sem
/// script fornecido (mesma autorização geral já dada em rodadas
/// anteriores).
/// </summary>
public class Modulo_Comparador
{
    /// <summary>
    /// Compara os índices existentes entre dois bancos (ex: Homologação x
    /// Produção) — casados por tabela + nome do índice. Cada linha do
    /// resultado vem com "Status" igual a "Igual", "Diferente", "Ausente na
    /// Origem" ou "Ausente no Destino" (ver <see cref="ComparacaoIndiceDto"/>).
    /// </summary>
    /// <remarks>
    /// Índices apoiados em PRIMARY KEY/UNIQUE CONSTRAINT são excluídos de
    /// propósito — não podem ser recriados com um simples DROP/CREATE
    /// INDEX (exigiriam ALTER TABLE DROP/ADD CONSTRAINT, fora do escopo
    /// desta primeira versão) — só entram índices "soltos" (CREATE INDEX).
    /// Índices columnstore/hash também ficam de fora (sintaxe de criação
    /// bem diferente de um índice comum) — só CLUSTERED/NONCLUSTERED
    /// tradicionais.
    /// </remarks>
    public async Task<List<ComparacaoIndiceDto>> CompararIndicesAsync(
        Conectar_SQL conexaoOrigem,
        string bancoOrigem,
        Conectar_SQL conexaoDestino,
        string bancoDestino,
        CancellationToken ct = default)
    {
        var indicesOrigem = await ObterDefinicoesIndicesAsync(conexaoOrigem, bancoOrigem, ct);
        var indicesDestino = await ObterDefinicoesIndicesAsync(conexaoDestino, bancoDestino, ct);

        var chaves = indicesOrigem.Keys.Union(indicesDestino.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(chave => chave, StringComparer.OrdinalIgnoreCase);

        var resultado = new List<ComparacaoIndiceDto>();
        foreach (var chave in chaves)
        {
            indicesOrigem.TryGetValue(chave, out var defOrigem);
            indicesDestino.TryGetValue(chave, out var defDestino);

            var referencia = defOrigem ?? defDestino!;
            var status = (defOrigem, defDestino) switch
            {
                (null, not null) => "Ausente na Origem",
                (not null, null) => "Ausente no Destino",
                _ => DefinicoesIguais(defOrigem!, defDestino!) ? "Igual" : "Diferente"
            };

            resultado.Add(new ComparacaoIndiceDto
            {
                Tabela = referencia.TabelaCompleta,
                NomeIndice = referencia.NomeIndice,
                Status = status,
                DescricaoOrigem = defOrigem?.Resumo ?? "(não existe)",
                DescricaoDestino = defDestino?.Resumo ?? "(não existe)",
                DefinicaoOrigem = defOrigem,
                DefinicaoDestino = defDestino
            });
        }

        return resultado;
    }

    /// <summary>
    /// Aplica a definição de índice de um lado no outro, para as linhas
    /// informadas. <paramref name="origemParaDestino"/> = true copia a
    /// definição da ORIGEM para o DESTINO (recriando lá, se já existir com
    /// definição diferente); false copia do DESTINO para a ORIGEM. Uma
    /// linha sem definição no lado "fonte" da direção escolhida (ex.:
    /// índice que só existe do outro lado) é ignorada silenciosamente para
    /// essa chamada — "Aplicar" nunca EXCLUI um índice sem ter uma
    /// definição pronta pra recriá-lo no lugar. Todas as instruções são
    /// enviadas como um único lote (uma conexão, uma execução), não uma
    /// chamada por índice — e esse lote roda dentro de uma transação
    /// explícita (<see cref="ExecutarLoteEmTransacaoAsync"/>): se algum índice do
    /// meio do lote falhar, os anteriores já aplicados nessa mesma chamada
    /// são desfeitos também, em vez de deixar o banco com só parte do
    /// "Aplicar" concluída.
    /// </summary>
    public async Task AplicarIndicesAsync(
        Conectar_SQL conexaoOrigem,
        string bancoOrigem,
        Conectar_SQL conexaoDestino,
        string bancoDestino,
        IEnumerable<ComparacaoIndiceDto> linhas,
        bool origemParaDestino,
        CancellationToken ct = default)
    {
        var lote = new StringBuilder();
        foreach (var linha in linhas)
        {
            var definicaoFonte = origemParaDestino ? linha.DefinicaoOrigem : linha.DefinicaoDestino;
            if (definicaoFonte is null)
            {
                continue;
            }

            var definicaoAtualNoAlvo = origemParaDestino ? linha.DefinicaoDestino : linha.DefinicaoOrigem;
            if (definicaoAtualNoAlvo is not null)
            {
                lote.AppendLine($"DROP INDEX [{IdentificadorSql.EscaparColchetes(definicaoAtualNoAlvo.NomeIndice)}] ON [{IdentificadorSql.EscaparColchetes(definicaoAtualNoAlvo.Esquema)}].[{IdentificadorSql.EscaparColchetes(definicaoAtualNoAlvo.Tabela)}];");
            }
            lote.AppendLine(MontarCreateIndex(definicaoFonte));
        }

        if (lote.Length == 0)
        {
            return;
        }

        var conexaoAlvo = origemParaDestino ? conexaoDestino : conexaoOrigem;
        var bancoAlvo = origemParaDestino ? bancoDestino : bancoOrigem;

        await using SqlConnection conexao = await conexaoAlvo.AbrirConexaoAsync(ct);
        if (!string.IsNullOrWhiteSpace(bancoAlvo))
        {
            conexao.ChangeDatabase(bancoAlvo);
        }

        await ExecutarLoteEmTransacaoAsync(conexao, lote.ToString(), ct);
    }

    /// <summary>
    /// Compara a estrutura de tabelas/colunas entre dois bancos — casadas
    /// por tabela + nome da coluna, uma linha por coluna (uma tabela
    /// inteira ausente de um lado aparece como TODAS as suas colunas em
    /// "Ausente na Origem"/"Ausente no Destino" — não existe uma linha
    /// separada "a nível de tabela").
    /// </summary>
    /// <remarks>
    /// "Igual"/"diferente" considera tipo de dado, tamanho/precisão/escala,
    /// nulidade e IDENTITY — não considera DEFAULT, collation da coluna,
    /// nem PRIMARY KEY/FOREIGN KEY/CHECK/triggers (ficam de fora do escopo
    /// desta primeira versão, assim como fill factor/filtro ficaram de
    /// fora de "Comparador &gt; Índices"). Uma tabela criada do zero por
    /// <see cref="AplicarBancosAsync"/> sai sem nenhuma dessas
    /// constraints/chaves.
    /// </remarks>
    public async Task<List<ComparacaoColunaDto>> CompararBancosAsync(
        Conectar_SQL conexaoOrigem,
        string bancoOrigem,
        Conectar_SQL conexaoDestino,
        string bancoDestino,
        CancellationToken ct = default)
    {
        var colunasOrigem = await ObterDefinicoesColunasAsync(conexaoOrigem, bancoOrigem, ct);
        var colunasDestino = await ObterDefinicoesColunasAsync(conexaoDestino, bancoDestino, ct);

        var chaves = colunasOrigem.Keys.Union(colunasDestino.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(chave => chave, StringComparer.OrdinalIgnoreCase);

        var resultado = new List<ComparacaoColunaDto>();
        foreach (var chave in chaves)
        {
            colunasOrigem.TryGetValue(chave, out var defOrigem);
            colunasDestino.TryGetValue(chave, out var defDestino);

            var referencia = defOrigem ?? defDestino!;
            var status = (defOrigem, defDestino) switch
            {
                (null, not null) => "Ausente na Origem",
                (not null, null) => "Ausente no Destino",
                _ => ColunasIguais(defOrigem!, defDestino!) ? "Igual" : "Diferente"
            };

            resultado.Add(new ComparacaoColunaDto
            {
                Tabela = referencia.TabelaCompleta,
                NomeColuna = referencia.NomeColuna,
                Status = status,
                DescricaoOrigem = defOrigem?.Resumo ?? "(não existe)",
                DescricaoDestino = defDestino?.Resumo ?? "(não existe)",
                DefinicaoOrigem = defOrigem,
                DefinicaoDestino = defDestino
            });
        }

        return resultado;
    }

    /// <summary>
    /// Aplica a definição de coluna de um lado no outro, para as linhas
    /// informadas — mesma semântica de direção de
    /// <see cref="AplicarIndicesAsync"/> (<paramref name="origemParaDestino"/>
    /// = true copia da ORIGEM para o DESTINO; false o contrário) e mesma
    /// regra de segurança: uma linha sem definição no lado "fonte" da
    /// direção escolhida é ignorada silenciosamente.
    /// </summary>
    /// <remarks>
    /// Particularidade em relação a índices: uma coluna não existe sozinha
    /// — precisa de uma tabela. Quando NENHUMA coluna de uma tabela existe
    /// no lado que recebe a aplicação (a tabela inteira está ausente lá),
    /// em vez de tentar ALTER TABLE numa tabela inexistente, monta um
    /// CREATE TABLE com TODAS as colunas conhecidas da tabela de origem —
    /// mesmo que só uma das colunas tenha sido selecionada/esteja na lista
    /// de <paramref name="linhas"/> — para nunca criar uma tabela faltando
    /// colunas (pedido explícito do usuário). Quando a tabela já existe no
    /// lado de destino, cada coluna vira um ADD COLUMN (se estava ausente)
    /// ou ALTER COLUMN (se só a definição divergia).
    ///
    /// Limitações conhecidas desta primeira versão (herdadas de não
    /// rastrear DEFAULT/constraints — ver <see cref="CompararBancosAsync"/>):
    /// um ADD COLUMN NOT NULL falha se a tabela de destino já tiver linhas
    /// (o SQL Server exige um DEFAULT nesse caso — erro 4901, traduzido de
    /// forma amigável em Menu_Raiz.ObterMensagemAmigavel). Exceção
    /// combinada com o usuário: para colunas de data/hora (ver
    /// <see cref="TiposDataHora"/>) o próprio <see cref="MontarAddColumn"/>
    /// já contorna isso sozinho, adicionando "DEFAULT GETDATE() WITH
    /// VALUES" — as linhas já existentes recebem a data/hora do momento em
    /// que o ADD COLUMN rodou como valor de preenchimento (o DBA pode
    /// ajustar depois, se precisar de um valor histórico real). Para os
    /// demais tipos a limitação continua valendo: sem DEFAULT rastreado, um
    /// ADD COLUMN NOT NULL numa tabela com linhas falha e cai no erro 4901
    /// explicado acima. Além disso, um ALTER COLUMN falha se a coluna
    /// estiver em uso por um índice/constraint/coluna computada, ou se os
    /// dados existentes não couberem no novo tipo/tamanho; e não é possível
    /// ALTER COLUMN para adicionar/remover IDENTITY. Igual a
    /// <see cref="AplicarIndicesAsync"/>, todo o lote roda dentro de uma
    /// transação explícita (<see cref="ExecutarLoteEmTransacaoAsync"/>) — se uma
    /// dessas falhas acontecer no meio do lote, tudo que já tinha sido
    /// aplicado antes dela nessa mesma chamada é desfeito também.
    /// </remarks>
    public async Task AplicarBancosAsync(
        Conectar_SQL conexaoOrigem,
        string bancoOrigem,
        Conectar_SQL conexaoDestino,
        string bancoDestino,
        IEnumerable<ComparacaoColunaDto> linhas,
        bool origemParaDestino,
        CancellationToken ct = default)
    {
        var linhasComFonte = linhas
            .Where(l => (origemParaDestino ? l.DefinicaoOrigem : l.DefinicaoDestino) is not null)
            .ToList();

        if (linhasComFonte.Count == 0)
        {
            return;
        }

        var conexaoFonte = origemParaDestino ? conexaoOrigem : conexaoDestino;
        var bancoFonte = origemParaDestino ? bancoOrigem : bancoDestino;
        var conexaoAlvo = origemParaDestino ? conexaoDestino : conexaoOrigem;
        var bancoAlvo = origemParaDestino ? bancoDestino : bancoOrigem;

        // Colunas completas de TODAS as tabelas da fonte — só usado quando
        // uma tabela precisa ser criada do zero no destino (ver <remarks>
        // acima), pra não criar uma tabela incompleta.
        var colunasFonteCompletas = await ObterDefinicoesColunasAsync(conexaoFonte, bancoFonte, ct);

        // Catálogo completo de colunas do ALVO — usado só para checar se a
        // tabela já existe lá. Importante: essa checagem NÃO pode ser feita
        // olhando só pra "linhas" (já filtradas para excluir as "Igual"),
        // porque se TODAS as colunas diferentes/selecionadas de uma tabela
        // forem "Ausente no Destino" (ex.: só colunas novas foram
        // adicionadas na origem), toda linha do grupo teria
        // DefinicaoDestino nulo mesmo com a tabela já existindo no alvo
        // (com outras colunas iguais, que ficaram de fora de "linhas") —
        // isso faria o código tentar um CREATE TABLE numa tabela que já
        // existe (erro "There is already an object named ...").
        var colunasAlvoCompletas = await ObterDefinicoesColunasAsync(conexaoAlvo, bancoAlvo, ct);

        var lote = new StringBuilder();
        foreach (var grupo in linhasComFonte.GroupBy(l => l.Tabela, StringComparer.OrdinalIgnoreCase))
        {
            var tabelaExisteNoAlvo = colunasAlvoCompletas.Values
                .Any(c => string.Equals(c.TabelaCompleta, grupo.Key, StringComparison.OrdinalIgnoreCase));

            if (!tabelaExisteNoAlvo)
            {
                var todasColunasDaTabela = colunasFonteCompletas.Values
                    .Where(c => string.Equals(c.TabelaCompleta, grupo.Key, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.Posicao)
                    .ToList();

                if (todasColunasDaTabela.Count > 0)
                {
                    lote.AppendLine(MontarCreateTable(todasColunasDaTabela));
                }
                continue;
            }

            foreach (var linha in grupo)
            {
                var definicaoFonte = (origemParaDestino ? linha.DefinicaoOrigem : linha.DefinicaoDestino)!;
                var definicaoAlvo = origemParaDestino ? linha.DefinicaoDestino : linha.DefinicaoOrigem;

                lote.AppendLine(definicaoAlvo is null ? MontarAddColumn(definicaoFonte) : MontarAlterColumn(definicaoFonte));
            }
        }

        if (lote.Length == 0)
        {
            return;
        }

        await using SqlConnection conexao = await conexaoAlvo.AbrirConexaoAsync(ct);
        if (!string.IsNullOrWhiteSpace(bancoAlvo))
        {
            conexao.ChangeDatabase(bancoAlvo);
        }

        await ExecutarLoteEmTransacaoAsync(conexao, lote.ToString(), ct);
    }

    /// <summary>
    /// Coleta a definição de todos os índices "soltos" (não apoiados em
    /// PRIMARY KEY/UNIQUE CONSTRAINT, só CLUSTERED/NONCLUSTERED) do banco
    /// informado, indexados por "esquema.tabela.índice" — usado por
    /// <see cref="CompararIndicesAsync"/> para os dois lados da comparação.
    /// </summary>
    private static async Task<Dictionary<string, IndiceDefinicaoDto>> ObterDefinicoesIndicesAsync(
        Conectar_SQL conexaoInfo, string banco, CancellationToken ct)
    {
        var indices = new Dictionary<string, IndiceDefinicaoDto>(StringComparer.OrdinalIgnoreCase);

        await using SqlConnection conexao = await conexaoInfo.AbrirConexaoAsync(ct);
        if (!string.IsNullOrWhiteSpace(banco))
        {
            conexao.ChangeDatabase(banco);
        }

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT
                s.name AS Esquema, t.name AS Tabela, i.name AS NomeIndice,
                i.is_unique AS Unico, i.type_desc AS TipoIndice,
                c.name AS NomeColuna, ic.is_descending_key AS Descendente, ic.is_included_column AS Incluida
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name IS NOT NULL
              AND i.is_hypothetical = 0
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.type_desc IN ('CLUSTERED', 'NONCLUSTERED')
              AND t.is_ms_shipped = 0
            ORDER BY s.name, t.name, i.name, ic.key_ordinal, ic.index_column_id;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var esquema = leitor.GetString(0);
            var tabela = leitor.GetString(1);
            var nomeIndice = leitor.GetString(2);
            var chave = $"{esquema}.{tabela}.{nomeIndice}";

            if (!indices.TryGetValue(chave, out var definicao))
            {
                definicao = new IndiceDefinicaoDto
                {
                    Esquema = esquema,
                    Tabela = tabela,
                    NomeIndice = nomeIndice,
                    Unico = leitor.GetBoolean(3),
                    TipoIndice = leitor.GetString(4)
                };
                indices[chave] = definicao;
            }

            if (leitor.GetBoolean(7))
            {
                definicao.ColunasIncluidas.Add(leitor.GetString(5));
            }
            else
            {
                definicao.ColunasChave.Add(new ColunaIndiceDto { Nome = leitor.GetString(5), Descendente = leitor.GetBoolean(6) });
            }
        }

        return indices;
    }

    /// <summary>
    /// Compara a estrutura funcional de duas definições de índice — nome
    /// já é o mesmo (é a chave usada para casar as duas), então só entram
    /// aqui: unicidade, tipo (CLUSTERED/NONCLUSTERED), colunas-chave (na
    /// mesma ordem e direção ASC/DESC) e colunas incluídas (como conjunto —
    /// a ordem de INCLUDE não afeta o funcionamento do índice).
    /// </summary>
    private static bool DefinicoesIguais(IndiceDefinicaoDto a, IndiceDefinicaoDto b)
    {
        if (a.Unico != b.Unico || !string.Equals(a.TipoIndice, b.TipoIndice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (a.ColunasChave.Count != b.ColunasChave.Count)
        {
            return false;
        }

        for (var i = 0; i < a.ColunasChave.Count; i++)
        {
            if (!string.Equals(a.ColunasChave[i].Nome, b.ColunasChave[i].Nome, StringComparison.OrdinalIgnoreCase)
                || a.ColunasChave[i].Descendente != b.ColunasChave[i].Descendente)
            {
                return false;
            }
        }

        var incluidasA = new HashSet<string>(a.ColunasIncluidas, StringComparer.OrdinalIgnoreCase);
        var incluidasB = new HashSet<string>(b.ColunasIncluidas, StringComparer.OrdinalIgnoreCase);
        return incluidasA.SetEquals(incluidasB);
    }

    private static string MontarCreateIndex(IndiceDefinicaoDto definicao)
    {
        var colunasChave = string.Join(", ", definicao.ColunasChave.Select(c => $"[{IdentificadorSql.EscaparColchetes(c.Nome)}] {(c.Descendente ? "DESC" : "ASC")}"));
        var sql = $"CREATE {(definicao.Unico ? "UNIQUE " : string.Empty)}{definicao.TipoIndice} INDEX [{IdentificadorSql.EscaparColchetes(definicao.NomeIndice)}] " +
                  $"ON [{IdentificadorSql.EscaparColchetes(definicao.Esquema)}].[{IdentificadorSql.EscaparColchetes(definicao.Tabela)}] ({colunasChave})";

        if (definicao.ColunasIncluidas.Count > 0)
        {
            sql += $" INCLUDE ({string.Join(", ", definicao.ColunasIncluidas.Select(c => $"[{IdentificadorSql.EscaparColchetes(c)}]"))})";
        }

        return sql + ";";
    }

    /// <summary>Nomes de tipo (sys.types.name) cujo tamanho é em caracteres/bytes (precisa de "(n)" ou "(MAX)").</summary>
    private static readonly string[] TiposComTamanhoCaracteres = { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" };

    /// <summary>Nomes de tipo cujo tamanho é precisão/escala (precisa de "(p, s)").</summary>
    private static readonly string[] TiposComPrecisao = { "decimal", "numeric" };

    /// <summary>
    /// Tipos de data/hora para os quais <see cref="MontarAddColumn"/>
    /// contorna sozinho o erro 4901 (ADD COLUMN NOT NULL sem DEFAULT numa
    /// tabela com linhas) usando GETDATE() como valor de preenchimento —
    /// pedido explícito do usuário depois de esbarrar nesse erro numa
    /// coluna de data. Não se aplica a nenhum outro tipo (string/numérico/
    /// bit etc.) — nesses casos a limitação documentada continua valendo.
    /// </summary>
    private static readonly string[] TiposDataHora = { "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset" };

    /// <summary>
    /// Coleta a definição de todas as colunas de todas as tabelas de
    /// usuário do banco informado, indexadas por "esquema.tabela.coluna" —
    /// usado por <see cref="CompararBancosAsync"/> para os dois lados da
    /// comparação e por <see cref="AplicarBancosAsync"/> para montar um
    /// CREATE TABLE completo quando a tabela inteira está ausente no alvo.
    /// </summary>
    private static async Task<Dictionary<string, ColunaTabelaDto>> ObterDefinicoesColunasAsync(
        Conectar_SQL conexaoInfo, string banco, CancellationToken ct)
    {
        var colunas = new Dictionary<string, ColunaTabelaDto>(StringComparer.OrdinalIgnoreCase);

        await using SqlConnection conexao = await conexaoInfo.AbrirConexaoAsync(ct);
        if (!string.IsNullOrWhiteSpace(banco))
        {
            conexao.ChangeDatabase(banco);
        }

        await using SqlCommand comando = conexao.CreateCommand();
        comando.CommandText = @"
            SELECT
                s.name AS Esquema, t.name AS Tabela, c.name AS NomeColuna, c.column_id AS Posicao,
                ty.name AS TipoDado, c.max_length AS MaxLength, c.precision AS Precisao, c.scale AS Escala,
                c.is_nullable AS Nulavel, c.is_identity AS IsIdentity
            FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;";

        await using SqlDataReader leitor = await comando.ExecuteReaderAsync(ct);
        while (await leitor.ReadAsync(ct))
        {
            var esquema = leitor.GetString(0);
            var tabela = leitor.GetString(1);
            var nomeColuna = leitor.GetString(2);
            var tipoDado = leitor.GetString(4);
            var maxLength = leitor.GetInt16(5);

            int? tamanhoCaracteres = null;
            if (TiposComTamanhoCaracteres.Contains(tipoDado, StringComparer.OrdinalIgnoreCase))
            {
                // nchar/nvarchar guardam o tamanho em bytes (2 por caractere);
                // -1 é o marcador de (MAX) e não deve ser dividido.
                tamanhoCaracteres = maxLength == -1
                    ? -1
                    : tipoDado.Equals("nchar", StringComparison.OrdinalIgnoreCase) || tipoDado.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)
                        ? maxLength / 2
                        : maxLength;
            }

            byte? precisao = null;
            byte? escala = null;
            if (TiposComPrecisao.Contains(tipoDado, StringComparer.OrdinalIgnoreCase))
            {
                precisao = leitor.GetByte(6);
                escala = leitor.GetByte(7);
            }

            var chave = $"{esquema}.{tabela}.{nomeColuna}";
            colunas[chave] = new ColunaTabelaDto
            {
                Esquema = esquema,
                Tabela = tabela,
                NomeColuna = nomeColuna,
                Posicao = leitor.GetInt32(3),
                TipoDado = tipoDado,
                MaxLength = tamanhoCaracteres,
                Precisao = precisao,
                Escala = escala,
                Nulavel = leitor.GetBoolean(8),
                IsIdentity = leitor.GetBoolean(9)
            };
        }

        return colunas;
    }

    /// <summary>
    /// Compara a estrutura funcional de duas definições de coluna já
    /// casadas por nome — ver escopo (o que entra/não entra) na
    /// documentação de <see cref="CompararBancosAsync"/>.
    /// </summary>
    private static bool ColunasIguais(ColunaTabelaDto a, ColunaTabelaDto b)
    {
        return string.Equals(a.TipoDado, b.TipoDado, StringComparison.OrdinalIgnoreCase)
            && a.MaxLength == b.MaxLength
            && a.Precisao == b.Precisao
            && a.Escala == b.Escala
            && a.Nulavel == b.Nulavel
            && a.IsIdentity == b.IsIdentity;
    }

    /// <summary>Monta o pedaço "[tipo](tamanho)" ou "[tipo](precisão, escala)" de uma definição de coluna, compartilhado por CREATE TABLE/ADD COLUMN/ALTER COLUMN.</summary>
    private static string MontarDefinicaoTipoColuna(ColunaTabelaDto coluna)
    {
        if (TiposComTamanhoCaracteres.Contains(coluna.TipoDado, StringComparer.OrdinalIgnoreCase) && coluna.MaxLength.HasValue)
        {
            var tamanho = coluna.MaxLength.Value == -1 ? "MAX" : coluna.MaxLength.Value.ToString();
            return $"[{coluna.TipoDado}]({tamanho})";
        }

        if (TiposComPrecisao.Contains(coluna.TipoDado, StringComparer.OrdinalIgnoreCase) && coluna.Precisao.HasValue)
        {
            return $"[{coluna.TipoDado}]({coluna.Precisao}, {coluna.Escala ?? 0})";
        }

        return $"[{coluna.TipoDado}]";
    }

    /// <summary>
    /// Monta um CREATE TABLE completo a partir de TODAS as colunas
    /// conhecidas de uma tabela de origem (usado quando a tabela inteira
    /// está ausente no lado de destino — ver <see cref="AplicarBancosAsync"/>).
    /// Sai sem PRIMARY KEY/FOREIGN KEY/CHECK/DEFAULT/índices — só as colunas.
    /// </summary>
    private static string MontarCreateTable(List<ColunaTabelaDto> colunas)
    {
        var primeira = colunas[0];
        var definicoesColunas = colunas
            .OrderBy(c => c.Posicao)
            .Select(c => $"    [{IdentificadorSql.EscaparColchetes(c.NomeColuna)}] {MontarDefinicaoTipoColuna(c)} " +
                         $"{(c.IsIdentity ? "IDENTITY(1,1) " : string.Empty)}{(c.Nulavel ? "NULL" : "NOT NULL")}");

        return $"CREATE TABLE [{IdentificadorSql.EscaparColchetes(primeira.Esquema)}].[{IdentificadorSql.EscaparColchetes(primeira.Tabela)}] (\n" +
               string.Join(",\n", definicoesColunas) + "\n);";
    }

    /// <summary>
    /// Monta um ALTER TABLE ... ADD (coluna) a partir da definição de
    /// origem — tabela de destino já existe, só falta a coluna. Para uma
    /// coluna NOT NULL de data/hora (<see cref="TiposDataHora"/>), inclui
    /// "DEFAULT GETDATE() WITH VALUES" para contornar sozinho o erro 4901
    /// do SQL Server (ADD COLUMN NOT NULL sem DEFAULT numa tabela com
    /// linhas) — as linhas existentes recebem a data/hora do momento do
    /// ADD como valor de preenchimento. Para os demais tipos NOT NULL essa
    /// situação continua sem contorno automático (ver <see cref="AplicarBancosAsync"/>).
    /// </summary>
    private static string MontarAddColumn(ColunaTabelaDto coluna)
    {
        var identity = coluna.IsIdentity ? "IDENTITY(1,1) " : string.Empty;
        var nulidade = coluna.Nulavel ? "NULL" : "NOT NULL";

        var defaultPreenchimento = !coluna.Nulavel && !coluna.IsIdentity
            && TiposDataHora.Contains(coluna.TipoDado, StringComparer.OrdinalIgnoreCase)
            ? " DEFAULT GETDATE() WITH VALUES"
            : string.Empty;

        return $"ALTER TABLE [{IdentificadorSql.EscaparColchetes(coluna.Esquema)}].[{IdentificadorSql.EscaparColchetes(coluna.Tabela)}] " +
               $"ADD [{IdentificadorSql.EscaparColchetes(coluna.NomeColuna)}] {MontarDefinicaoTipoColuna(coluna)} " +
               $"{identity}{nulidade}{defaultPreenchimento};";
    }

    /// <summary>Monta um ALTER TABLE ... ALTER COLUMN a partir da definição de origem — coluna já existe no destino, só a definição diverge. Não altera IDENTITY (o SQL Server não permite via ALTER COLUMN).</summary>
    private static string MontarAlterColumn(ColunaTabelaDto coluna)
    {
        return $"ALTER TABLE [{IdentificadorSql.EscaparColchetes(coluna.Esquema)}].[{IdentificadorSql.EscaparColchetes(coluna.Tabela)}] " +
               $"ALTER COLUMN [{IdentificadorSql.EscaparColchetes(coluna.NomeColuna)}] {MontarDefinicaoTipoColuna(coluna)} " +
               $"{(coluna.Nulavel ? "NULL" : "NOT NULL")};";
    }

    /// <summary>
    /// Executa o lote de instruções DDL dentro de uma transação explícita —
    /// assim, se qualquer instrução falhar no meio do lote (ex.: um ADD
    /// COLUMN NOT NULL numa tabela com dados, sem DEFAULT), TUDO que já
    /// tinha sido aplicado antes dela nesse mesmo "Aplicar" é desfeito
    /// também, em vez de deixar o banco num estado parcialmente aplicado.
    /// Usado tanto por <see cref="AplicarIndicesAsync"/> quanto por
    /// <see cref="AplicarBancosAsync"/>.
    /// </summary>
    /// <remarks>
    /// POR QUE UMA SqlTransaction DE VERDADE, E NÃO "BEGIN/COMMIT
    /// TRANSACTION" COMO TEXTO (como era antes): com a transação só no
    /// texto do lote, um timeout do CLIENTE fazia o driver mandar um
    /// "attention" e desistir da execução — o COMMIT do fim do texto nunca
    /// chegava a rodar e NINGUÉM mandava ROLLBACK, então a transação ficava
    /// ABERTA no servidor segurando locks Sch-M das tabelas envolvidas.
    /// Qualquer consulta contra essas tabelas ficava travada até a conexão
    /// do pool ser reciclada ou um DBA matar o SPID à mão, e o usuário só
    /// via "Timeout expired". Com uma SqlTransaction do lado do cliente, o
    /// ROLLBACK é garantido pelo catch abaixo e, mesmo num cenário extremo
    /// (processo morto), o próprio fechamento da conexão desfaz a transação.
    ///
    /// SET XACT_ABORT ON continua valendo: garante que um erro em tempo de
    /// execução dentro do lote aborte a transação inteira no servidor, em
    /// vez de só o comando com erro (comportamento padrão) — importante
    /// porque o lote tem várias instruções.
    /// </remarks>
    private static async Task ExecutarLoteEmTransacaoAsync(SqlConnection conexao, string lote, CancellationToken ct)
    {
        // BeginTransactionAsync é herdado de DbConnection e devolve um
        // DbTransaction — o cast para SqlTransaction é necessário porque
        // SqlCommand.Transaction é tipado como SqlTransaction.
        var transacaoBase = await conexao.BeginTransactionAsync(ct);
        await using SqlTransaction transacao = (SqlTransaction)transacaoBase;

        try
        {
            await using (SqlCommand comando = conexao.CreateCommand())
            {
                comando.Transaction = transacao;

                // Sem timeout de cliente (0) de propósito: DDL legítima numa
                // tabela grande (recriar índice, ALTER COLUMN que reescreve a
                // tabela inteira) passa dos 300s antigos com facilidade, e um
                // timeout aqui só serviria para ABANDONAR no meio um trabalho
                // que o servidor vai continuar fazendo mesmo assim. Quem quiser
                // desistir usa o CancellationToken (a tela já o fornece), que
                // cancela de forma controlada e cai no ROLLBACK abaixo.
                comando.CommandTimeout = 0;

                comando.CommandText = "SET XACT_ABORT ON;\n" + lote;
                await comando.ExecuteNonQueryAsync(ct);
            }

            await transacao.CommitAsync(ct);
        }
        catch
        {
            try
            {
                // CancellationToken.None de propósito: se o motivo da falha foi
                // justamente um cancelamento, passar o mesmo token faria o
                // ROLLBACK ser cancelado antes de acontecer — exatamente o
                // problema que este método existe para evitar.
                await transacao.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Uma falha no ROLLBACK (ex.: conexão já caiu — caso em que o
                // servidor desfaz a transação sozinho) NÃO pode mascarar a
                // exceção original, que é a que explica o que deu errado.
            }

            throw;
        }
    }
}
