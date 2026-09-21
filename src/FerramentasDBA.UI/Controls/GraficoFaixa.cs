namespace FerramentasDBA.UI.Controls;

/// <summary>
/// "Strip chart" (faixa de gráfico em tempo real, fundo preto/linha verde)
/// usado no painel "Overview" de Monitoramento &gt; Active Monitor SQL Local
/// — mesmo estilo visual do Activity Monitor nativo do SSMS (% Processor
/// Time, Waiting Tasks, Database I/O, Batch Requests/sec).
///
/// Controle puramente de UI: só guarda uma janela deslizante das últimas N
/// amostras (<see cref="AdicionarAmostra"/>) e desenha (fundo preto, grade
/// verde escura, linha verde clara). Quem decide O QUE plotar (consultar o
/// banco, calcular taxas/segundo a partir de contadores cumulativos, etc.)
/// é a tela que usa este controle — nada de acesso a banco de dados aqui.
/// </summary>
public sealed class GraficoFaixa : Panel
{
    private static readonly Color CorGrade = Color.FromArgb(40, 70, 40);
    private static readonly Color CorLinha = Color.FromArgb(40, 230, 40);

    private readonly Queue<double> _amostras = new();
    private readonly int _maxAmostras;

    /// <summary>
    /// Valor mínimo para o topo do eixo Y — evita que o gráfico fique
    /// "gigante" com pequenas oscilações perto de zero (ex.: Waiting Tasks
    /// oscilando entre 0 e 1 não deve fazer o eixo variar loucamente). O
    /// eixo cresce acima disso conforme as amostras exigem (com uma folga
    /// de 20%), mas nunca fica menor que este valor.
    /// </summary>
    public double EixoMinimo { get; set; } = 10;

    /// <summary>Quando definido, trava o eixo Y nesse valor fixo (ex.: 100 para os gráficos de %).</summary>
    public double? EixoFixo { get; set; }

    public GraficoFaixa(int maxAmostras = 60)
    {
        _maxAmostras = Math.Max(2, maxAmostras);
        DoubleBuffered = true;
        BackColor = Color.Black;
    }

    /// <summary>Adiciona uma amostra nova à direita da faixa, descartando a mais antiga se já estiver cheia.</summary>
    public void AdicionarAmostra(double? valor)
    {
        _amostras.Enqueue(valor ?? 0);
        while (_amostras.Count > _maxAmostras)
        {
            _amostras.Dequeue();
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var amostras = _amostras.ToArray();
        var maiorAmostra = amostras.Length > 0 ? amostras.Max() : 0;
        var eixoMaximo = EixoFixo ?? Math.Max(EixoMinimo, maiorAmostra * 1.2);
        if (eixoMaximo <= 0)
        {
            eixoMaximo = 1;
        }

        // Grade horizontal (5 faixas) com o valor do eixo à direita.
        using var canetaGrade = new Pen(CorGrade);
        using var fonteEixo = new Font("Segoe UI", 7.5F);
        using var pincelEixo = new SolidBrush(Color.FromArgb(120, 190, 120));
        const int divisoes = 5;
        for (var i = 0; i <= divisoes; i++)
        {
            var y = Height - 1f - (Height - 2f) * i / divisoes;
            g.DrawLine(canetaGrade, 0, y, Width, y);

            var valorEixo = eixoMaximo * i / divisoes;
            var texto = valorEixo >= 100 ? valorEixo.ToString("0") : valorEixo.ToString("0.#");
            var tamanhoTexto = g.MeasureString(texto, fonteEixo);
            g.DrawString(texto, fonteEixo, pincelEixo, Width - tamanhoTexto.Width - 2, y - tamanhoTexto.Height - 1);
        }

        // Grade vertical (uma coluna a cada 5 amostras).
        for (var i = 0; i < _maxAmostras; i += 5)
        {
            var x = Width * i / (float)(_maxAmostras - 1);
            g.DrawLine(canetaGrade, x, 0, x, Height);
        }

        if (amostras.Length < 2)
        {
            return;
        }

        using var canetaLinha = new Pen(CorLinha, 1.6f);
        var pontos = new PointF[amostras.Length];
        for (var i = 0; i < amostras.Length; i++)
        {
            var x = Width * i / (float)(_maxAmostras - 1);
            var y = Height - 2f - (float)(amostras[i] / eixoMaximo * (Height - 4f));
            pontos[i] = new PointF(x, Math.Clamp(y, 0, Height - 1));
        }

        g.DrawLines(canetaLinha, pontos);
    }
}
