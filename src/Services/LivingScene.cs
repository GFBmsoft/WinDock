using System.Diagnostics;
using System.Numerics;
using Windows.UI.Composition;

namespace WinDock.Services;

/// <summary>
/// Os efeitos da imagem com vida numa tela: a luz das velas tremulando, a neblina correndo no céu e
/// as brasas e a poeira no ar, numa janela transparente sobre o papel de parede do Windows.
///
/// <para><b>A imagem é a do Windows.</b> A primeira versão desenhava a imagem também, ampliada
/// pela bicúbica: ficava igual à do Windows (o mesmo pixel nos dois, conferido) e custava 55 MB a
/// mais na dock. Agora a janela só tem os efeitos, e o que é transparente nela deixa ver o papel
/// de parede de verdade, que é a mesma imagem e já é nítido. Os efeitos se apoiam no ajuste dele
/// (<see cref="WallpaperFit"/>).</para>
///
/// <para><b>Doze quadros por segundo, contados aqui.</b> A primeira versão entregava cada efeito
/// ao compositor como animação em laço, e o dwm a tocava a 60 quadros: medido em 17/11/2026, a
/// parte 3D dele ia de 4% a 44% e a CPU dele de 7% a 27% de um núcleo, mais que um vídeo de 1080p.
/// Agora o <see cref="AnimatedWallpaper"/> chama <see cref="Tick"/> doze vezes por segundo, e o dwm
/// só redesenha quando algo mudou. É também o ritmo da animação em pixel art, e as partículas andam
/// de pixel inteiro em pixel inteiro, nítidas como a arte em volta.</para>
///
/// <para>Pausar é apagar os efeitos (<see cref="Stop"/>); o papel de parede continua lá, parado, e
/// não custa nada.</para>
/// </summary>
internal sealed class LivingScene : IDisposable
{
    // ── quanto de cada efeito: afinados na Capela, para enfeitar sem roubar a cena ──
    private const float GlowPeak = 0.42f, GlowPeakExtra = 0.18f;   // alfa no centro do brilho, mais pela força da luz
    private const float GlowLowest = 0.5f;                          // a tremulação não apaga a vela
    private const float FogNear = 0.60f, FogFar = 0.38f;            // opacidade das duas camadas
    private const double FogNearSeconds = 150, FogFarSeconds = 95;  // o tempo de um ladrilho passar
    private const double MinSky = 0.01;
    private const int FlickerSteps = 8;

    private static readonly (byte R, byte G, byte B) Warm = (255, 150, 60);
    private static readonly (byte R, byte G, byte B) Core = (255, 236, 196);
    private static readonly (byte R, byte G, byte B) Ember = (255, 176, 82);
    private static readonly (byte R, byte G, byte B) Dust = (232, 222, 206);

    private sealed record Flame(SpriteVisual Visual, float[] Keys, double Seconds, double Phase);
    private sealed record FogLayer(CompositionSurfaceBrush Brush, float Step, float Y, double Seconds);
    private sealed record Breather(CompositionSurfaceBrush Brush, FigureSprite Sprite, CompositionDrawingSurface[] Frames, double Phase)
    {
        public int Shown { get; set; }
    }
    private sealed record Glint(SpriteVisual Visual, Vector2 From, Vector2 To, float Length, float Thickness, double Phase);

    // ── o brilho que corre a lâmina ──
    private const double GlintPeriod = 7.0, GlintSeconds = 0.9;
    private const float GlintLength = 30, GlintThickness = 3;      // a lâmina já é clara: risco fino e pálido sumia nela
    private static readonly (byte R, byte G, byte B) Steel = (255, 255, 248);

    private sealed class Mote(SpriteVisual visual, LivingParticle p, float side)
    {
        public SpriteVisual Visual { get; } = visual;
        public LivingParticle P { get; } = p;
        public float Side { get; } = side;
        public Vector3 Shown = new(float.NaN);
        public float Opacity = -1;
    }

    private readonly Compositor _c;
    private readonly LivingImage _image;
    private readonly ContainerVisual _effects;
    private readonly double _sx, _sy, _x, _y;   // da análise para a tela, e o canto da imagem
    private double Scale => (_sx + _sy) / 2;      // para tamanhos: no Esticar os eixos diferem
    private readonly List<IDisposable> _owned = new();
    private readonly List<IDisposable> _running = new();
    private readonly List<Flame> _flames = new();
    private readonly List<Mote> _motes = new();
    private readonly List<FogLayer> _fogs = new();
    private readonly List<Breather> _breathers = new();
    private readonly List<Glint> _glints = new();
    private readonly Stopwatch _clock = new();
    private readonly CompositionDrawingSurface? _sky, _fog;
    private readonly List<(FigureSprite Sprite, CompositionDrawingSurface[] Frames)> _figures = new();

    /// <summary>Precisa de <see cref="CompositionPixels"/> só para a neblina e as figuras (<see cref="NeedsPixels"/>).</summary>
    public LivingScene(Compositor compositor, CompositionPixels? pixels, LivingImage image, ContainerVisual root,
                       int width, int height, WallpaperFit fit)
    {
        _c = compositor;
        _image = image;
        (_sx, _sy, _x, _y) = image.Place(fit, width, height);

        _effects = Own(_c.CreateContainerVisual());
        _effects.Size = new Vector2(width, height);
        root.Children.InsertAtTop(_effects);

        if (pixels is null) return;
        if (HasFog(image))
        {
            var caixa = image.Map.SkyBox;
            _sky = Own(pixels.Surface(image.SkyBgra, caixa.Width, caixa.Height));
            _fog = Own(pixels.Surface(image.FogBgra, LivingImage.FogTileWidth * 2, LivingImage.FogTileHeight));
        }
        foreach (var s in image.SpritesFor(width, height))
            _figures.Add((s, s.Frames.Select(q => Own(pixels.Surface(q, s.Width, s.Height))).ToArray()));
    }

    /// <summary>A imagem tem céu bastante para a neblina.</summary>
    public static bool HasFog(LivingImage image) =>
        image.Map.SkyShare >= MinSky && image.Map.SkyBox.Width > 0 && image.Map.SkyBox.Height > 0;

    /// <summary>A cena vai precisar de textura (e de Direct3D): neblina ou figura.</summary>
    public static bool NeedsPixels(LivingImage image) => HasFog(image) || image.Figures.Count > 0;

    public bool Running => _running.Count > 0;

    /// <summary>Acende os efeitos. Sempre com a mesma semente: a cena volta igual depois de uma pausa.</summary>
    public void Start()
    {
        if (Running) return;
        var rnd = new Random(_image.Seed);

        // a figura vai embaixo de tudo, como parte da imagem: a cópia leva uns pixels do fundo em
        // volta, e com a neblina por baixo dela esse fundo aparecia sem neblina, desenhando o
        // contorno da cópia em volta do capirote
        foreach (var (s, quadros) in _figures) Figure(s, quadros, rnd);
        if (_sky is not null && _fog is not null)
        {
            Fog(1.0f, 0f, FogNear, FogNearSeconds);
            Fog(1.6f, -0.25f, FogFar, FogFarSeconds);
        }
        foreach (var l in _image.Map.Lights) Glow(l, rnd);
        foreach (var p in _image.Particles) Particle(p);
        foreach (var (s, _) in _figures) Blade(s.Figure, rnd);

        _clock.Restart();
        Tick();
    }

    /// <summary>Apaga os efeitos; a imagem fica.</summary>
    public void Stop()
    {
        _clock.Reset();
        _effects.Children.RemoveAll();
        foreach (var d in _running) d.Dispose();
        _running.Clear();
        _flames.Clear();
        _motes.Clear();
        _fogs.Clear();
        _breathers.Clear();
        _glints.Clear();
    }

    /// <summary>
    /// Um quadro: a neblina anda, as velas tremulam, as partículas sobem. Só manda ao compositor o
    /// que mudou; partícula parada no mesmo pixel e invisível não custa nada.
    /// </summary>
    public void Tick()
    {
        if (!Running) return;
        var t = _clock.Elapsed.TotalSeconds;

        foreach (var f in _fogs)
            f.Brush.Offset = new Vector2(-(float)(LivingRules.Frac(t / f.Seconds) * f.Step), f.Y);

        foreach (var f in _flames)
            f.Visual.Opacity = LivingRules.Flicker(f.Keys, t / f.Seconds + f.Phase);

        foreach (var b in _breathers)
        {
            var i = b.Sprite.FrameAt(t, b.Phase);
            if (i == b.Shown) continue;
            b.Brush.Surface = b.Frames[i];
            b.Shown = i;
        }

        foreach (var g in _glints)
        {
            var onde = LivingRules.Sweep(t, GlintPeriod, GlintSeconds, g.Phase);
            if (onde is not double p)
            {
                if (g.Visual.Opacity != 0) g.Visual.Opacity = 0;
                continue;
            }
            var centro = Vector2.Lerp(g.From, g.To, (float)p);
            g.Visual.Offset = new Vector3(MathF.Round(centro.X - g.Length / 2), MathF.Round(centro.Y - g.Thickness / 2), 0);
            g.Visual.Opacity = MathF.Sin((float)p * MathF.PI);   // acende no punho, apaga na ponta
        }

        foreach (var m in _motes)
        {
            var (x, y, o) = LivingRules.ParticleAt(m.P, t);
            var q = MathF.Round(o * 32) / 32;   // 32 degraus bastam, e poupam envios
            if (q != m.Opacity)
            {
                m.Visual.Opacity = q;
                m.Opacity = q;
            }
            if (q <= 0) continue;
            var (sx, sy) = Screen(x, y);
            // no pixel inteiro: quadradinho nítido, como os pixels da arte
            var onde = new Vector3(MathF.Round(sx - m.Side / 2), MathF.Round(sy - m.Side / 2), 0);
            if (onde == m.Shown) continue;
            m.Visual.Offset = onde;
            m.Shown = onde;
        }
    }

    public void Dispose()
    {
        Stop();
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try { _owned[i].Dispose(); } catch { /* placa reiniciada: já foi */ }
        }
        _owned.Clear();
    }

    // ── os efeitos ────────────────────────────────────────────

    /// <summary>
    /// Uma camada de neblina, só no retângulo do céu, recortada pela máscara dele. A textura tem
    /// dois ladrilhos iguais, ampliados (é neblina, a ampliação só a deixa mais macia), e anda um
    /// ladrilho para a esquerda por volta: no fim da volta está igual ao começo, sem emenda.
    /// </summary>
    private void Fog(float zoom, float rise, float opacity, double seconds)
    {
        var caixa = _image.Map.SkyBox;
        float passoX = LivingRules.MaskStep * (float)_sx, passoY = LivingRules.MaskStep * (float)_sy;
        var altura = (float)(_image.Height * _sy);
        var k = altura / LivingImage.FogTileHeight * zoom;
        var y = rise * altura;

        var neblina = Run(_c.CreateSurfaceBrush(_fog!));
        neblina.Stretch = CompositionStretch.None;
        neblina.HorizontalAlignmentRatio = 0;
        neblina.VerticalAlignmentRatio = 0;
        neblina.Scale = new Vector2(k, k);
        neblina.Offset = new Vector2(0, y);

        var mascara = Run(_c.CreateSurfaceBrush(_sky!));
        mascara.Stretch = CompositionStretch.Fill;
        var recorte = Run(_c.CreateMaskBrush());
        recorte.Source = neblina;
        recorte.Mask = mascara;

        var v = Run(_c.CreateSpriteVisual());
        v.Size = new Vector2(caixa.Width * passoX, caixa.Height * passoY);
        v.Offset = new Vector3((float)_x + caixa.X * passoX, (float)_y + caixa.Y * passoY, 0);
        v.Brush = recorte;
        v.Opacity = opacity;
        _effects.Children.InsertAtTop(v);
        _fogs.Add(new FogLayer(neblina, LivingImage.FogTileWidth * k, y, seconds));
    }

    /// <summary>
    /// Um brilho redondo em volta de uma luz: miolo claro, halo quente que some na borda. A
    /// opacidade tremula por uma volta de oito valores sorteados, cada vela no seu ritmo.
    /// </summary>
    private void Glow(LivingLight l, Random rnd)
    {
        var raio = (float)(l.Radius * Scale);
        var quente = Mix((l.R, l.G, l.B), Warm, 0.55f);
        var pico = GlowPeak + GlowPeakExtra * l.Strength;

        var g = Run(_c.CreateRadialGradientBrush());
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0f, Color(Alpha(0.55f), Core))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0.12f, Color(Alpha(pico), quente))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0.4f, Color(Alpha(pico * 0.45f), quente))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0.75f, Color(Alpha(pico * 0.12f), quente))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(1f, Color(0, quente))));

        var (cx, cy) = Screen(l.X, l.Y);
        var v = Run(_c.CreateSpriteVisual());
        v.Size = new Vector2(raio * 2, raio * 2);
        v.Offset = new Vector3(MathF.Round(cx - raio), MathF.Round(cy - raio), 0);
        v.Brush = g;
        _effects.Children.InsertAtTop(v);

        var segundos = 1.6 + rnd.NextDouble() * 1.6;
        _flames.Add(new Flame(v, LivingRules.FlickerKeys(rnd, FlickerSteps, GlowLowest), segundos, rnd.NextDouble()));
    }

    /// <summary>
    /// Uma parte que se mexe: uma cópia exata do pedaço da imagem por cima do original, trocada a
    /// cada tique pelo quadro da vez — o tronco um pixel da arte acima, ou o pano numa fase da onda.
    /// </summary>
    private void Figure(FigureSprite s, CompositionDrawingSurface[] quadros, Random rnd)
    {
        var pincel = Run(_c.CreateSurfaceBrush(quadros[0]));
        pincel.Stretch = CompositionStretch.None;
        pincel.HorizontalAlignmentRatio = 0;
        pincel.VerticalAlignmentRatio = 0;
        var v = Run(_c.CreateSpriteVisual());
        v.Size = new Vector2(s.Width, s.Height);
        v.Offset = new Vector3(s.X, s.Y, 0);
        v.Brush = pincel;
        _effects.Children.InsertAtTop(v);
        _breathers.Add(new Breather(pincel, s, quadros, rnd.NextDouble()));
    }

    /// <summary>Um risco claro que corre a lâmina do punho à ponta de tempos em tempos.</summary>
    private void Blade(LivingFigure f, Random rnd)
    {
        if (f.BladeFrom is not { } de || f.BladeTo is not { } ate) return;
        var (x0, y0) = Screen(de.X, de.Y);
        var (x1, y1) = Screen(ate.X, ate.Y);

        var g = Run(_c.CreateLinearGradientBrush());
        g.StartPoint = new Vector2(0, 0.5f);
        g.EndPoint = new Vector2(1, 0.5f);
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0f, Color(0, Steel))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(0.5f, Color(255, Steel))));
        g.ColorStops.Add(Run(_c.CreateColorGradientStop(1f, Color(0, Steel))));

        var v = Run(_c.CreateSpriteVisual());
        v.Size = new Vector2(GlintLength, GlintThickness);
        v.CenterPoint = new Vector3(GlintLength / 2, GlintThickness / 2, 0);
        v.RotationAngleInDegrees = MathF.Atan2(y1 - y0, x1 - x0) * 180 / MathF.PI;
        v.Brush = g;
        v.Opacity = 0;
        _effects.Children.InsertAtTop(v);
        _glints.Add(new Glint(v, new Vector2(x0, y0), new Vector2(x1, y1), GlintLength, GlintThickness, rnd.NextDouble()));
    }

    /// <summary>Uma brasa ou um grão de poeira: um quadradinho do tamanho do pixel da arte, invisível até a vez dele.</summary>
    private void Particle(LivingParticle p)
    {
        var lado = Math.Max(2f, MathF.Round((float)(p.Size * Scale)));
        var v = Run(_c.CreateSpriteVisual());
        v.Size = new Vector2(lado, lado);
        v.Brush = Run(_c.CreateColorBrush(Color(255, p.Ember ? Ember : Dust)));
        v.Opacity = 0;
        _effects.Children.InsertAtTop(v);
        _motes.Add(new Mote(v, p, lado) { Opacity = 0 });
    }

    // ── contas miúdas ─────────────────────────────────────────

    private (float X, float Y) Screen(float x, float y) => ((float)(_x + x * _sx), (float)(_y + y * _sy));

    private static byte Alpha(float a) => (byte)Math.Clamp(a * 255, 0, 255);

    private static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float t) =>
        ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Windows.UI.Color Color(byte a, (byte R, byte G, byte B) c) => new() { A = a, R = c.R, G = c.G, B = c.B };

    private T Own<T>(T o) where T : IDisposable { _owned.Add(o); return o; }
    private T Run<T>(T o) where T : IDisposable { _running.Add(o); return o; }
}
