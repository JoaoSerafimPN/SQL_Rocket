using System.Drawing.Drawing2D;
using FerramentasDBA.Classes.Models;

namespace FerramentasDBA.UI.Controls;

/// <summary>
/// Diagrama gráfico simplificado da árvore de operadores de um plano de
/// execução — inspirado no plano gráfico do SSMS (usado na tela Manutenção
/// &gt; Análise do plano de execução, aba "Gráfico"), mas com ícones
/// GENÉRICOS desenhados aqui (retângulos/círculos/funis simples): não é
/// possível reproduzir os ícones reais do SSMS, que são da Microsoft.
///
/// Algoritmo de layout (aproximação própria, não é o algoritmo real do
/// SSMS): cada operador ocupa uma COLUNA igual à sua profundidade na
/// árvore (raiz = 0, aumentando para a direita) e uma LINHA determinada
/// por uma numeração em DFS dos operadores-folha (Index Seek/Scan etc.) —
/// o PRIMEIRO filho de cada operador herda a linha do próprio pai
/// (mantendo a "espinha" principal alinhada horizontalmente, como no
/// SSMS), enquanto os demais filhos (ex.: a segunda entrada de um Join)
/// começam uma linha nova abaixo. Como cada folha recebe um número de
/// linha único e crescente, e os nós internos só herdam a linha do seu
/// primeiro filho, nenhum nó do diagrama nunca fica na mesma
/// linha+coluna que outro — sem sobreposição, garantido pelo próprio
/// algoritmo (ver <see cref="AtribuirLinhas"/>).
///
/// Controle puramente de UI: só desenha a partir da árvore que recebe em
/// <see cref="CarregarOperador"/> — nenhum acesso a banco de dados aqui
/// (mesmo princípio de <see cref="GraficoFaixa"/>). Quem decide o que
/// fazer quando o usuário clica num operador é quem está ouvindo o evento
/// <see cref="OperadorSelecionado"/> (ver Menu_Raiz.MostrarPaginaPlanoExecucao).
///
/// Além do clique (evento OperadorSelecionado), passar o mouse sobre uma
/// caixa mostra uma dica (tooltip) no próprio diagrama com os mesmos
/// detalhes que o SSMS mostra ao passar o mouse sobre um operador no
/// plano gráfico (custo de E/S, custo de CPU, modo de execução estimado,
/// número de execuções, linhas estimadas por/para todas as execuções,
/// lista de saída etc. — ver <see cref="MontarDica"/> e os campos novos
/// em PlanoOperadorDto). É um `ToolTip` com OwnerDraw=true (desenho
/// próprio, não o balão padrão do Windows) para caber tudo isso formatado
/// em duas colunas (rótulo/valor), do mesmo jeito que o SSMS faz.
/// </summary>
public sealed class PlanoExecucaoDiagrama : Panel
{
    // Tamanho de cada caixa e espaçamento entre colunas/linhas — ajustados
    // à mão para caber o nome do operador + 2 linhas de detalhe sem
    // cortar, com uma folga confortável entre caixas vizinhas.
    private const int LarguraCaixa = 190;
    private const int AlturaCaixa = 76;
    private const int EspacoHorizontal = 60;
    private const int EspacoVertical = 28;
    private const int Margem = 20;

    private static readonly Font FonteOperador = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font FonteDetalhe = new("Segoe UI", 8F);
    private static readonly Font FonteVazio = new("Segoe UI", 9.5F, FontStyle.Italic);
    private static readonly Pen CanetaLinha = new(Color.FromArgb(150, 160, 180), 1.6f);
    private static readonly Color CorSelecao = Color.FromArgb(30, 60, 160);

    // Tamanho/fontes/cores da dica (tooltip) de detalhes do operador —
    // pensados para lembrar o balão amarelo-claro que o SSMS mostra ao
    // passar o mouse sobre um operador (ver comentário da classe).
    private const int DicaLarguraTotal = 520;
    private const int DicaPaddingH = 10;
    private const int DicaPaddingV = 8;
    private const int DicaLarguraRotulo = 190;
    private const int DicaEspacoColunas = 10;
    private const int DicaEspacoLinha = 3;
    private static readonly Font FonteDicaTitulo = new("Segoe UI", 9.5F, FontStyle.Bold);
    private static readonly Font FonteDicaRotulo = new("Segoe UI", 8F, FontStyle.Bold);
    private static readonly Font FonteDicaValor = new("Segoe UI", 8F);
    private static readonly Color CorFundoDica = Color.FromArgb(255, 255, 225);
    private static readonly Color CorBordaDica = Color.FromArgb(90, 90, 90);
    private static readonly Color CorRotuloDica = Color.FromArgb(60, 60, 60);

    private enum CategoriaOperador { AcessoDados, Juncao, Ordenacao, Agregacao, Calculo, TopFiltro, Outros }

    /// <summary>Uma linha "rótulo: valor" já com a posição (relativa ao canto superior esquerdo da dica) calculada por <see cref="MontarDica"/>.</summary>
    private sealed class LinhaDicaLayout
    {
        public string Rotulo = string.Empty;
        public string Valor = string.Empty;
        public Rectangle RetanguloRotulo;
        public Rectangle RetanguloValor;
    }

    /// <summary>Layout completo (já medido) da dica de um operador — calculado uma única vez quando o mouse entra na caixa (não a cada frame), e reusado tanto no evento Popup (define o tamanho do balão) quanto no Draw (desenha o conteúdo).</summary>
    private sealed class LayoutDica
    {
        public string Titulo = string.Empty;
        public Rectangle RetanguloTitulo;
        public List<LinhaDicaLayout> Linhas { get; } = new();
        public Size Tamanho;
    }

    /// <summary>Nó de layout — envolve um <see cref="PlanoOperadorDto"/> com a posição calculada na grade (coluna/linha) e o retângulo em pixels resultante. Existe só dentro deste controle; a árvore "de dados" de verdade é PlanoOperadorDto.Filhos.</summary>
    private sealed class No
    {
        public No(PlanoOperadorDto operador, int coluna)
        {
            Operador = operador;
            Coluna = coluna;
        }

        public PlanoOperadorDto Operador { get; }
        public List<No> Filhos { get; } = new();
        public int Coluna { get; }
        public int Linha { get; set; }
        public Rectangle Retangulo { get; set; }
    }

    private No? _raiz;
    private readonly List<No> _todosOsNos = new();
    private No? _noSelecionado;

    private readonly ToolTip _dicaOperador;
    private No? _noComDicaAtual;
    private LayoutDica? _dicaAtual;

    /// <summary>Disparado quando o usuário clica numa caixa do diagrama — quem ouve decide o que mostrar (ex.: um painel de detalhes do operador).</summary>
    public event EventHandler<PlanoOperadorDto>? OperadorSelecionado;

    public PlanoExecucaoDiagrama()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        AutoScroll = true;
        BackColor = Color.FromArgb(250, 250, 252);

        // OwnerDraw=true porque a dica precisa caber MUITO mais informação
        // (rótulo/valor em duas colunas) do que um balão de tooltip normal
        // suporta — quem desenha de verdade é DicaOperador_Draw, a partir
        // de _dicaAtual (não do texto passado para Show/SetToolTip).
        _dicaOperador = new ToolTip
        {
            OwnerDraw = true,
            ShowAlways = true,
            InitialDelay = 200,
            ReshowDelay = 80,
            AutoPopDelay = 30000,
        };
        _dicaOperador.Popup += DicaOperador_Popup;
        _dicaOperador.Draw += DicaOperador_Draw;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dicaOperador.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Recebe a árvore (raiz) do statement atualmente selecionado e
    /// recalcula todo o layout — chamar com null limpa o diagrama (ex.:
    /// statement sem plano, como um "USE [Banco]", ou nenhum statement
    /// selecionado ainda).
    /// </summary>
    public void CarregarOperador(PlanoOperadorDto? raizOperador)
    {
        _todosOsNos.Clear();
        _noSelecionado = null;
        _noComDicaAtual = null;
        _dicaAtual = null;
        _dicaOperador.Hide(this);

        if (raizOperador is null)
        {
            _raiz = null;
            AutoScrollMinSize = Size.Empty;
            Invalidate();
            return;
        }

        _raiz = ConstruirNo(raizOperador);
        var proximaLinha = 0;
        AtribuirLinhas(_raiz, ref proximaLinha);
        CalcularRetangulos(_raiz);

        var larguraTotal = (_todosOsNos.Max(n => n.Coluna) + 1) * (LarguraCaixa + EspacoHorizontal) + Margem * 2;
        var alturaTotal = (_todosOsNos.Max(n => n.Linha) + 1) * (AlturaCaixa + EspacoVertical) + Margem * 2;
        AutoScrollMinSize = new Size(larguraTotal, alturaTotal);

        Invalidate();
    }

    private No ConstruirNo(PlanoOperadorDto operador)
    {
        var no = new No(operador, operador.Profundidade);
        _todosOsNos.Add(no);
        foreach (var filho in operador.Filhos)
        {
            no.Filhos.Add(ConstruirNo(filho));
        }
        return no;
    }

    /// <summary>Ver explicação do algoritmo no comentário da classe.</summary>
    private static void AtribuirLinhas(No no, ref int proximaLinha)
    {
        if (no.Filhos.Count == 0)
        {
            no.Linha = proximaLinha;
            proximaLinha++;
            return;
        }

        AtribuirLinhas(no.Filhos[0], ref proximaLinha);
        no.Linha = no.Filhos[0].Linha;

        for (var i = 1; i < no.Filhos.Count; i++)
        {
            AtribuirLinhas(no.Filhos[i], ref proximaLinha);
        }
    }

    private void CalcularRetangulos(No no)
    {
        var x = Margem + no.Coluna * (LarguraCaixa + EspacoHorizontal);
        var y = Margem + no.Linha * (AlturaCaixa + EspacoVertical);
        no.Retangulo = new Rectangle(x, y, LarguraCaixa, AlturaCaixa);
        foreach (var filho in no.Filhos)
        {
            CalcularRetangulos(filho);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_raiz is null)
        {
            e.Graphics.DrawString(
                "Envie um plano de execução e selecione uma instrução na grade acima para ver o diagrama.",
                FonteVazio, Brushes.Gray, Margem, Margem);
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        // Linhas primeiro (por baixo), depois as caixas por cima — evita
        // que o traço de uma linha "risque" o meio de uma caixa vizinha.
        DesenharLinhas(g, _raiz);
        foreach (var no in _todosOsNos)
        {
            DesenharCaixa(g, no);
        }
    }

    /// <summary>
    /// Um filho na MESMA linha do pai é a continuação da "espinha"
    /// principal — linha reta horizontal. Um filho em linha DIFERENTE
    /// (ex.: a segunda entrada de um Join) usa uma quebra em ângulo reto
    /// (direita → baixo/cima → direita) passando pelo espaço vazio entre
    /// as colunas, para nunca cruzar por cima de outra caixa.
    /// </summary>
    private void DesenharLinhas(Graphics g, No no)
    {
        foreach (var filho in no.Filhos)
        {
            var origem = new Point(no.Retangulo.Right, no.Retangulo.Top + AlturaCaixa / 2);
            var destino = new Point(filho.Retangulo.Left, filho.Retangulo.Top + AlturaCaixa / 2);

            if (origem.Y == destino.Y)
            {
                g.DrawLine(CanetaLinha, origem, destino);
            }
            else
            {
                var xMeio = no.Retangulo.Right + EspacoHorizontal / 2;
                g.DrawLine(CanetaLinha, origem, new Point(xMeio, origem.Y));
                g.DrawLine(CanetaLinha, new Point(xMeio, origem.Y), new Point(xMeio, destino.Y));
                g.DrawLine(CanetaLinha, new Point(xMeio, destino.Y), destino);
            }

            DesenharLinhas(g, filho);
        }
    }

    private void DesenharCaixa(Graphics g, No no)
    {
        var op = no.Operador;
        var (corFundo, corBorda, categoria) = ObterEstiloCategoria(op);
        var selecionado = ReferenceEquals(no, _noSelecionado);

        using var pincelFundo = new SolidBrush(corFundo);
        using var canetaBorda = new Pen(selecionado ? CorSelecao : corBorda, selecionado ? 2.4f : 1.4f);
        using var caminho = CriarRetanguloArredondado(no.Retangulo, 8);
        g.FillPath(pincelFundo, caminho);
        g.DrawPath(canetaBorda, caminho);

        var areaIcone = new Rectangle(no.Retangulo.Left + 8, no.Retangulo.Top + 8, 22, 22);
        DesenharIcone(g, areaIcone, categoria, corBorda);

        using var formatoTexto = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
        using var pincelDetalhe = new SolidBrush(Color.FromArgb(90, 100, 120));

        var areaTitulo = new Rectangle(no.Retangulo.Left + 36, no.Retangulo.Top + 6, no.Retangulo.Width - 44, 32);
        g.DrawString(op.PhysicalOp, FonteOperador, Brushes.Black, areaTitulo, formatoTexto);

        var linhaDetalhe = string.IsNullOrEmpty(op.ObjetoAlvo) ? op.LogicalOp : op.ObjetoAlvo;
        var areaDetalhe = new Rectangle(no.Retangulo.Left + 8, no.Retangulo.Top + 40, no.Retangulo.Width - 16, 16);
        g.DrawString(linhaDetalhe, FonteDetalhe, pincelDetalhe, areaDetalhe, formatoTexto);

        var areaCusto = new Rectangle(no.Retangulo.Left + 8, no.Retangulo.Top + 56, no.Retangulo.Width - 16, 16);
        g.DrawString($"Custo: {op.DescricaoPercentualCusto}", FonteDetalhe, pincelDetalhe, areaCusto, formatoTexto);
    }

    private static GraphicsPath CriarRetanguloArredondado(Rectangle retangulo, int raio)
    {
        var caminho = new GraphicsPath();
        var diametro = raio * 2;
        var arco = new Rectangle(retangulo.Location, new Size(diametro, diametro));

        caminho.AddArc(arco, 180, 90);
        arco.X = retangulo.Right - diametro;
        caminho.AddArc(arco, 270, 90);
        arco.Y = retangulo.Bottom - diametro;
        caminho.AddArc(arco, 0, 90);
        arco.X = retangulo.Left;
        caminho.AddArc(arco, 90, 90);
        caminho.CloseFigure();
        return caminho;
    }

    private static (Color Fundo, Color Borda, CategoriaOperador Categoria) ObterEstiloCategoria(PlanoOperadorDto op)
    {
        var categoria = ClassificarOperador(op);
        return categoria switch
        {
            CategoriaOperador.AcessoDados => (Color.FromArgb(224, 236, 255), Color.FromArgb(60, 110, 210), categoria),
            CategoriaOperador.Juncao => (Color.FromArgb(255, 236, 214), Color.FromArgb(210, 130, 40), categoria),
            CategoriaOperador.Ordenacao => (Color.FromArgb(222, 245, 226), Color.FromArgb(50, 150, 80), categoria),
            CategoriaOperador.Agregacao => (Color.FromArgb(240, 224, 250), Color.FromArgb(140, 80, 190), categoria),
            CategoriaOperador.Calculo => (Color.FromArgb(216, 245, 245), Color.FromArgb(30, 150, 150), categoria),
            CategoriaOperador.TopFiltro => (Color.FromArgb(240, 240, 244), Color.FromArgb(110, 120, 140), categoria),
            _ => (Color.FromArgb(238, 238, 240), Color.FromArgb(120, 125, 135), categoria),
        };
    }

    /// <summary>
    /// Heurística simples por palavra-chave em PhysicalOp/LogicalOp — não
    /// tenta cobrir os ~60 operadores físicos possíveis do SQL Server, só
    /// os grupos mais comuns; qualquer operador não reconhecido cai em
    /// "Outros" (ícone genérico), o que é seguro (nunca falha, só fica
    /// menos específico visualmente).
    /// </summary>
    private static CategoriaOperador ClassificarOperador(PlanoOperadorDto op)
    {
        if (op.LogicalOp.Contains("Join", StringComparison.OrdinalIgnoreCase) ||
            op.PhysicalOp.Contains("Loops", StringComparison.OrdinalIgnoreCase))
        {
            return CategoriaOperador.Juncao;
        }
        if (op.EhScan || op.EhSeek)
        {
            return CategoriaOperador.AcessoDados;
        }
        if (op.PhysicalOp.Contains("Sort", StringComparison.OrdinalIgnoreCase))
        {
            return CategoriaOperador.Ordenacao;
        }
        if (op.LogicalOp.Contains("Aggregate", StringComparison.OrdinalIgnoreCase) ||
            op.PhysicalOp.Contains("Aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return CategoriaOperador.Agregacao;
        }
        if (op.PhysicalOp.Contains("Compute Scalar", StringComparison.OrdinalIgnoreCase))
        {
            return CategoriaOperador.Calculo;
        }
        if (op.PhysicalOp.Contains("Top", StringComparison.OrdinalIgnoreCase) ||
            op.PhysicalOp.Contains("Filter", StringComparison.OrdinalIgnoreCase))
        {
            return CategoriaOperador.TopFiltro;
        }
        return CategoriaOperador.Outros;
    }

    /// <summary>Ícones GENÉRICOS (formas simples desenhadas com GDI+) — deliberadamente não tentam imitar os ícones reais do SSMS, que pertencem à Microsoft.</summary>
    private static void DesenharIcone(Graphics g, Rectangle area, CategoriaOperador categoria, Color cor)
    {
        using var caneta = new Pen(cor, 1.6f);
        using var pincel = new SolidBrush(cor);

        switch (categoria)
        {
            case CategoriaOperador.AcessoDados:
                // Tabela: retângulo com 2 linhas horizontais (linhas de dados).
                g.DrawRectangle(caneta, area);
                g.DrawLine(caneta, area.Left, area.Top + area.Height / 3, area.Right, area.Top + area.Height / 3);
                g.DrawLine(caneta, area.Left, area.Top + area.Height * 2 / 3, area.Right, area.Top + area.Height * 2 / 3);
                break;

            case CategoriaOperador.Juncao:
                // Duas circunferências sobrepostas — ícone clássico de "join".
                var raio = area.Width * 2 / 3;
                g.DrawEllipse(caneta, area.Left, area.Top + (area.Height - raio) / 2, raio, raio);
                g.DrawEllipse(caneta, area.Right - raio, area.Top + (area.Height - raio) / 2, raio, raio);
                break;

            case CategoriaOperador.Ordenacao:
                // 3 barras horizontais de tamanho crescente.
                var alturaBarra = Math.Max(2, area.Height / 5);
                for (var i = 0; i < 3; i++)
                {
                    var largura = area.Width * (i + 1) / 3;
                    var y = area.Top + i * (alturaBarra + 2);
                    g.FillRectangle(pincel, area.Left, y, largura, alturaBarra);
                }
                break;

            case CategoriaOperador.Agregacao:
                // Funil (afunila as linhas — representa agregação/agrupamento).
                var pontosFunil = new[]
                {
                    new Point(area.Left, area.Top),
                    new Point(area.Right, area.Top),
                    new Point(area.Left + area.Width * 3 / 4, area.Top + area.Height / 2),
                    new Point(area.Left + area.Width * 3 / 4, area.Bottom),
                    new Point(area.Left + area.Width / 4, area.Bottom),
                    new Point(area.Left + area.Width / 4, area.Top + area.Height / 2),
                    new Point(area.Left, area.Top),
                };
                g.DrawLines(caneta, pontosFunil);
                break;

            case CategoriaOperador.Calculo:
                // Quadrado com "x" — representa uma expressão/cálculo.
                g.DrawRectangle(caneta, area);
                g.DrawLine(caneta, area.Left + 4, area.Top + 4, area.Right - 4, area.Bottom - 4);
                g.DrawLine(caneta, area.Right - 4, area.Top + 4, area.Left + 4, area.Bottom - 4);
                break;

            case CategoriaOperador.TopFiltro:
                // Triângulo apontando para baixo — reduz o conjunto de linhas.
                var pontosTop = new[]
                {
                    new Point(area.Left, area.Top),
                    new Point(area.Right, area.Top),
                    new Point(area.Left + area.Width / 2, area.Bottom),
                };
                g.DrawLines(caneta, pontosTop);
                g.DrawLine(caneta, pontosTop[^1], pontosTop[0]);
                break;

            default:
                // Genérico: um losango.
                var centroX = area.Left + area.Width / 2;
                var centroY = area.Top + area.Height / 2;
                var pontosLosango = new[]
                {
                    new Point(centroX, area.Top),
                    new Point(area.Right, centroY),
                    new Point(centroX, area.Bottom),
                    new Point(area.Left, centroY),
                };
                g.DrawPolygon(caneta, pontosLosango);
                break;
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (_raiz is null)
        {
            return;
        }

        var pontoClicado = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        var noClicado = _todosOsNos.FirstOrDefault(n => n.Retangulo.Contains(pontoClicado));
        if (noClicado is null)
        {
            return;
        }

        _noSelecionado = noClicado;
        Invalidate();
        OperadorSelecionado?.Invoke(this, noClicado.Operador);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_raiz is null)
        {
            return;
        }

        var ponto = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
        var noSobMouse = _todosOsNos.FirstOrDefault(n => n.Retangulo.Contains(ponto));
        Cursor = noSobMouse != null ? Cursors.Hand : Cursors.Default;

        // Só remonta/reexibe a dica quando o mouse entra numa caixa
        // DIFERENTE da que já estava mostrando a dica — evita ficar
        // remontando o layout (e piscando o balão) a cada pixel de
        // movimento dentro da mesma caixa.
        if (!ReferenceEquals(noSobMouse, _noComDicaAtual))
        {
            _noComDicaAtual = noSobMouse;
            _dicaOperador.Hide(this);

            if (noSobMouse != null)
            {
                _dicaAtual = MontarDica(noSobMouse.Operador);
                // O texto passado aqui só serve para o ToolTip aceitar
                // mostrar algo (não pode ser vazio) — quem desenha de
                // verdade é DicaOperador_Draw, a partir de _dicaAtual.
                _dicaOperador.Show(noSobMouse.Operador.PhysicalOp, this, e.X + 18, e.Y + 18, 30000);
            }
            else
            {
                _dicaAtual = null;
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _noComDicaAtual = null;
        _dicaAtual = null;
        _dicaOperador.Hide(this);
    }

    /// <summary>
    /// Monta o layout (rótulo/valor de cada linha, já com o tamanho de
    /// cada uma medido) da dica de um operador — mesma informação que o
    /// SSMS mostra no hover do plano gráfico. Linhas que dependem de um
    /// campo que pode não existir no XML deste plano específico (ex.:
    /// "Armazenamento" só existe em operador de acesso a dados,
    /// "EstimatedRowsRead" só em planos do SQL 2016 SP1+) só entram na
    /// lista quando o valor realmente existe — em vez de aparecer "-" para
    /// todo mundo, o que deixaria a dica maior à toa na maioria dos casos.
    /// </summary>
    private static LayoutDica MontarDica(PlanoOperadorDto op)
    {
        var pares = new List<(string Rotulo, string Valor)>
        {
            ("Operação física", op.PhysicalOp),
            ("Operação lógica", op.LogicalOp),
        };

        if (!string.IsNullOrEmpty(op.ModoExecucaoEstimado))
        {
            pares.Add(("Modo de execução estimado", op.ModoExecucaoEstimado));
        }
        if (!string.IsNullOrEmpty(op.Armazenamento))
        {
            pares.Add(("Armazenamento", op.Armazenamento));
        }

        pares.Add(("Custo de E/S estimado", op.CustoIO.HasValue ? op.CustoIO.Value.ToString("N7") : "-"));
        pares.Add(("Custo de CPU estimado", op.CustoCPU.HasValue ? op.CustoCPU.Value.ToString("N7") : "-"));
        pares.Add(("Custo do operador estimado", $"{op.CustoExclusivo:N7} ({op.DescricaoPercentualCusto})"));
        pares.Add(("Custo da subárvore estimado", op.CustoSubTreeTotal.ToString("N7")));
        pares.Add(("Número de execuções estimado", op.NumeroExecucoesEstimado.ToString("N0")));
        pares.Add(("Linhas estimadas (todas as execuções)", op.LinhasEstimadasTodasExecucoes.ToString("N0")));
        pares.Add(("Linhas estimadas (por execução)", op.LinhasEstimadas.ToString("N0")));

        if (op.LinhasParaLer.HasValue)
        {
            pares.Add(("Linhas estimadas para ler", op.LinhasParaLer.Value.ToString("N0")));
        }

        pares.Add(("Tamanho de linha estimado", op.TamanhoLinhaBytes.HasValue ? $"{op.TamanhoLinhaBytes.Value:N0} B" : "-"));

        if (op.Ordenado.HasValue)
        {
            pares.Add(("Ordenado", op.Ordenado.Value ? "Sim" : "Não"));
        }

        pares.Add(("Node ID", op.NodeId.ToString()));

        if (!string.IsNullOrEmpty(op.ObjetoAlvo))
        {
            pares.Add(("Objeto", op.ObjetoAlvo));
        }
        if (op.LinhasReais.HasValue)
        {
            pares.Add(("Linhas reais (execução)", op.LinhasReais.Value.ToString("N0")));
        }
        if (op.ListaSaida.Count > 0)
        {
            // Uma coluna por linha (quebra "\n" dentro do próprio valor,
            // que o TextRenderer respeita além do word-wrap normal) — em
            // vez de um parágrafo só separado por "; ", que força uma
            // quebra no meio de um nome de coluna mais longo.
            pares.Add(("Lista de saída", string.Join("\n", op.ListaSaida)));
        }
        if (op.Avisos.Count > 0)
        {
            pares.Add(("Avisos", string.Join(" | ", op.Avisos.Select(a => a.Descricao))));
        }

        var dica = new LayoutDica { Titulo = op.PhysicalOp };
        var larguraConteudo = DicaLarguraTotal - DicaPaddingH * 2;
        var larguraValor = larguraConteudo - DicaLarguraRotulo - DicaEspacoColunas;
        var y = DicaPaddingV;

        var tamanhoTitulo = TextRenderer.MeasureText(dica.Titulo, FonteDicaTitulo, new Size(larguraConteudo, int.MaxValue), TextFormatFlags.WordBreak);
        dica.RetanguloTitulo = new Rectangle(DicaPaddingH, y, larguraConteudo, tamanhoTitulo.Height);
        y += tamanhoTitulo.Height + 8;

        foreach (var (rotulo, valor) in pares)
        {
            var tamanhoValor = TextRenderer.MeasureText(valor, FonteDicaValor, new Size(larguraValor, int.MaxValue), TextFormatFlags.WordBreak);
            var alturaLinha = Math.Max(16, tamanhoValor.Height);
            dica.Linhas.Add(new LinhaDicaLayout
            {
                Rotulo = rotulo,
                Valor = valor,
                RetanguloRotulo = new Rectangle(DicaPaddingH, y, DicaLarguraRotulo, alturaLinha),
                RetanguloValor = new Rectangle(DicaPaddingH + DicaLarguraRotulo + DicaEspacoColunas, y, larguraValor, alturaLinha),
            });
            y += alturaLinha + DicaEspacoLinha;
        }

        y += DicaPaddingV - DicaEspacoLinha;
        dica.Tamanho = new Size(DicaLarguraTotal, y);
        return dica;
    }

    private void DicaOperador_Popup(object? sender, PopupEventArgs e)
    {
        e.ToolTipSize = _dicaAtual?.Tamanho ?? new Size(200, 40);
    }

    private void DicaOperador_Draw(object? sender, DrawToolTipEventArgs e)
    {
        var dica = _dicaAtual;
        if (dica is null)
        {
            e.DrawBackground();
            e.DrawBorder();
            return;
        }

        using var pincelFundo = new SolidBrush(CorFundoDica);
        e.Graphics.FillRectangle(pincelFundo, e.Bounds);
        using var canetaBorda = new Pen(CorBordaDica);
        e.Graphics.DrawRectangle(canetaBorda, new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1));

        TextRenderer.DrawText(e.Graphics, dica.Titulo, FonteDicaTitulo, Deslocar(dica.RetanguloTitulo, e.Bounds.Location), Color.Black, TextFormatFlags.WordBreak);
        var yLinhaSeparadora = e.Bounds.Y + dica.RetanguloTitulo.Bottom + 3;
        e.Graphics.DrawLine(Pens.DarkGray, e.Bounds.X + DicaPaddingH, yLinhaSeparadora, e.Bounds.Right - DicaPaddingH, yLinhaSeparadora);

        foreach (var linha in dica.Linhas)
        {
            TextRenderer.DrawText(e.Graphics, linha.Rotulo, FonteDicaRotulo, Deslocar(linha.RetanguloRotulo, e.Bounds.Location), CorRotuloDica, TextFormatFlags.WordBreak | TextFormatFlags.Right);
            TextRenderer.DrawText(e.Graphics, linha.Valor, FonteDicaValor, Deslocar(linha.RetanguloValor, e.Bounds.Location), Color.Black, TextFormatFlags.WordBreak);
        }
    }

    private static Rectangle Deslocar(Rectangle retangulo, Point deslocamento) =>
        new(retangulo.X + deslocamento.X, retangulo.Y + deslocamento.Y, retangulo.Width, retangulo.Height);
}
