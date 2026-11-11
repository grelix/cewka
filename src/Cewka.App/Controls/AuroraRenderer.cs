using SkiaSharp;

namespace Cewka.App.Controls;

/// <summary>
/// Rysuje Zorzę: wolne, poziome wstęgi światła na ciemnym tle w dwóch barwach okładki. Jedna klasa
/// obsługuje powierzchnię GPU i rastrową, więc testy rysują ją bez okna. Shader kompiluje się raz
/// na proces; uniformy leżą w jednym obiekcie używanym w każdej klatce. Jedyna alokacja klatki to
/// <see cref="SKShader"/>, zwalniany od razu po rysowaniu.
/// <para>
/// Zasady bezpieczeństwa (WCAG 2.3.1) wpisane w sam shader: bez cięć, stroboskopu, obrotów i sprzężenia
/// zwrotnego; wszystkie częstotliwości czasowe są stałymi nie większymi niż 0,6 rad/s, a czas rośnie
/// najwyżej o <see cref="AuroraView.MaxRate"/> na sekundę, co ogranicza prędkość ruchu od góry; nasycenie
/// czerwieni w gotowym pikselu najwyżej 0,6; jasność wstęg ma sufit w motywie ciemnym, a tło nie jest
/// ani czarne, ani białe.
/// </para>
/// </summary>
public sealed class AuroraRenderer
{
    /// <summary>Źródło shadera SkSL.</summary>
    public const string Source = """
        uniform float uW;
        uniform float uH;
        uniform float uTime;
        uniform float uLow;
        uniform float uMid;
        uniform float uHigh;
        uniform float uLevel;
        uniform float uBright;
        uniform float uCeiling;
        uniform float uR1;
        uniform float uG1;
        uniform float uB1;
        uniform float uR2;
        uniform float uG2;
        uniform float uB2;

        const float kRedSaturation = 0.6;
        const float kTop = 0.34;
        const float kDefaultCeiling = 0.55;

        // Nasycenie czerwieni obcięte do 0,6 z zachowaniem jasności. Odcień do 24 stopni od czerwieni,
        // żeby zaokrąglenie kanału do 8 bitów nie wyprowadziło nasyconego piksela poza zakres 20 stopni.
        float3 limitRed(float3 c) {
            float mx = max(c.r, max(c.g, c.b));
            float mn = min(c.r, min(c.g, c.b));
            float span = mx - mn;
            float sat = span / max(mx, 0.0001);
            bool reddish = c.r >= c.g && c.r >= c.b && abs(c.g - c.b) < 0.4 * span;
            if (reddish && sat > kRedSaturation) {
                float k = kRedSaturation / sat;
                return float3(mx) - (float3(mx) - c) * k;
            }
            return c;
        }

        // Barwa z okładki podniesiona do jasności wstęgi; brak barwy daje chłodny błękit.
        float3 lift(float3 c) {
            float m = max(c.r, max(c.g, c.b));
            if (m < 0.02) return float3(0.40, 0.60, 0.80);
            return c * (max(m, 0.55) / m);
        }

        float hash(float2 p) {
            p = fract(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }

        float vnoise(float2 p) {
            float2 i = floor(p);
            float2 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = hash(i);
            float b = hash(i + float2(1.0, 0.0));
            float c = hash(i + float2(0.0, 1.0));
            float d = hash(i + float2(1.0, 1.0));
            return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
        }

        // Jedna wstęga: bas podnosi ją i poszerza, góra dodaje drobną fakturę, czas przesuwa falę.
        float ribbon(float2 uv, float base, float rise, float seed) {
            float x = uv.x;
            float breathe = 1.0 + 0.15 * uMid;
            float sway = breathe * (0.045 * sin(x * 5.0 + uTime * 0.55 + seed)
                + 0.025 * sin(x * 11.0 - uTime * 0.35 + seed * 1.7)
                + 0.015 * sin(x * 23.0 + uTime * 0.20 + seed * 2.3));
            float centre = base - rise * uLow + sway;
            float sigma = 0.045 + 0.030 * uLow;
            float d = (uv.y - centre) / sigma;
            float body = exp(-0.5 * d * d);
            float rays = vnoise(float2(x * 70.0 + seed * 13.0, uTime * 0.25 + seed));
            float grain = vnoise(float2(x * 26.0 - uTime * 0.15, uv.y * 40.0 + seed));
            float fine = 1.0 + uHigh * (0.55 * (rays - 0.5) + 0.35 * (grain - 0.5));
            return body * fine;
        }

        half4 main(float2 fragCoord) {
            float2 uv = float2(fragCoord.x / uW, fragCoord.y / uH);
            float3 c1 = lift(float3(uR1, uG1, uB1));
            float3 c2 = lift(float3(uR2, uG2, uB2));
            float3 bg = mix(float3(0.030, 0.034, 0.050), float3(0.052, 0.058, 0.080), uv.y);
            bg += 0.012 * mix(c1, c2, uv.x);

            float intensity = 0.30 + 0.70 * uLevel;
            float3 sum = float3(0.0);
            for (int i = 0; i < 3; i++) {
                float fi = float(i);
                float w = ribbon(uv, 0.78 - 0.12 * fi, 0.07 + 0.025 * fi, 1.3 + 2.1 * fi);
                float mixer = clamp(0.15 + 0.35 * fi + 0.25 * sin(uv.x * 3.0 + uTime * 0.2 + fi), 0.0, 1.0);
                sum += mix(c1, c2, mixer) * (w * intensity * (0.70 - 0.12 * fi));
            }

            sum *= uCeiling / kDefaultCeiling;
            float peak = max(sum.r, max(sum.g, sum.b));
            if (peak > uCeiling) sum *= uCeiling / peak;
            float fade = smoothstep(kTop, kTop + 0.12, uv.y);
            float3 rgb = bg + sum * (fade * uBright);
            return half4(half3(limitRed(rgb)), 1.0);
        }
        """;

    /// <summary>Sufit jasności wstęg (kanał sRGB) w motywie ciemnym.</summary>
    public const float DefaultCeiling = 0.55f;

    private static readonly Lazy<SKRuntimeEffect> Shared = new(Compile);

    private readonly SKRuntimeEffectUniforms _uniforms;
    private readonly SKPaint _paint = new() { BlendMode = SKBlendMode.Src, IsAntialias = false };

    public AuroraRenderer()
    {
        _uniforms = new SKRuntimeEffectUniforms(Shared.Value);
    }

    /// <summary>
    /// Sufit jasności wstęg. Domyślnie <see cref="DefaultCeiling"/>; wyższy ma tylko test obciążeniowy
    /// ogranicznika błysków, który sprawdza go na obrazie jaśniejszym od rzeczywistego.
    /// </summary>
    public float Ceiling { get; set; } = DefaultCeiling;

    /// <summary>Kompiluje shader; błąd kompilacji jest wyjątkiem z komunikatem kompilatora.</summary>
    public static SKRuntimeEffect Compile()
    {
        var effect = SKRuntimeEffect.CreateShader(Source, out var errors);
        if (effect is null) throw new InvalidOperationException("Shader Zorzy nie skompilował się: " + errors);
        return effect;
    }

    /// <summary>
    /// Wypełnia prostokąt (0, 0, szerokość, wysokość) Zorzą. Pasma 0 do 1; <paramref name="time"/> to
    /// całka tempa falowania; <paramref name="brightness"/> 0 do 1 przyciemnia wstęgi (nie tło).
    /// </summary>
    public void Draw(SKCanvas canvas, float width, float height, float time, float low, float mid, float high,
        float level, SKColor colour1, SKColor colour2, float brightness)
    {
        _uniforms["uW"] = Math.Max(1f, width);
        _uniforms["uH"] = Math.Max(1f, height);
        _uniforms["uTime"] = float.IsFinite(time) ? time : 0f;
        _uniforms["uLow"] = Unit(low);
        _uniforms["uMid"] = Unit(mid);
        _uniforms["uHigh"] = Unit(high);
        _uniforms["uLevel"] = Unit(level);
        _uniforms["uBright"] = Unit(brightness);
        _uniforms["uCeiling"] = float.IsFinite(Ceiling) ? Math.Clamp(Ceiling, 0.05f, 4f) : DefaultCeiling;
        _uniforms["uR1"] = colour1.Red / 255f;
        _uniforms["uG1"] = colour1.Green / 255f;
        _uniforms["uB1"] = colour1.Blue / 255f;
        _uniforms["uR2"] = colour2.Red / 255f;
        _uniforms["uG2"] = colour2.Green / 255f;
        _uniforms["uB2"] = colour2.Blue / 255f;

        using var shader = Shared.Value.ToShader(_uniforms);
        _paint.Shader = shader;
        canvas.DrawRect(0, 0, width, height, _paint);
        _paint.Shader = null;
    }

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
