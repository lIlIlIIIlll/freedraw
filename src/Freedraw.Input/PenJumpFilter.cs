namespace Freedraw.Input;

/// <summary>
/// Descarta teleporte de coordenada.
///
/// A MT1060 tem uma regiao onde a posicao reportada salta para outro Y ao passar
/// por cima. Como a caneta e um objeto fisico e os reports vem a ~200Hz, um salto
/// grande entre duas amostras consecutivas nao pode ser movimento real.
///
/// O filtro nao rejeita o salto de cara para sempre: se as amostras SEGUINTES
/// concordarem com a nova posicao, o movimento e aceito. Isso preserva o caso
/// legitimo de levantar a caneta e recolocar em outro lugar -- so o pulso
/// isolado, que e a assinatura do glitch, e jogado fora.
/// </summary>
public sealed class PenJumpFilter(float maxJump = 0.06f, int confirmSamples = 2)
{
    private float _lastX, _lastY;
    private bool _hasLast;

    private float _candidateX, _candidateY;
    private int _candidateHits;

    public int Rejected { get; private set; }
    public int Accepted { get; private set; }
    public float LargestRejectedJump { get; private set; }

    /// <summary>Distancia normalizada acima da qual um passo e considerado suspeito.</summary>
    public float MaxJump => maxJump;

    /// <summary>Chame quando a caneta sair de alcance: o proximo ponto recomeca limpo.</summary>
    public void Reset()
    {
        _hasLast = false;
        _candidateHits = 0;
    }

    /// <summary>
    /// Devolve true se a amostra deve ser usada. Quando devolve false, a amostra
    /// e um teleporte e deve ser ignorada por completo.
    /// </summary>
    public bool Accept(float x, float y)
    {
        if (!_hasLast)
        {
            _lastX = x; _lastY = y; _hasLast = true;
            Accepted++;
            return true;
        }

        float dx = x - _lastX, dy = y - _lastY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance <= maxJump)
        {
            _lastX = x; _lastY = y;
            _candidateHits = 0;
            Accepted++;
            return true;
        }

        // Salto suspeito: so aceita se as proximas amostras confirmarem a regiao.
        float cdx = x - _candidateX, cdy = y - _candidateY;
        bool nearCandidate = _candidateHits > 0
                             && MathF.Sqrt(cdx * cdx + cdy * cdy) <= maxJump;

        if (nearCandidate)
        {
            _candidateX = x; _candidateY = y;
            if (++_candidateHits >= confirmSamples)
            {
                // Confirmado: a caneta esta mesmo na posicao nova.
                _lastX = x; _lastY = y;
                _candidateHits = 0;
                Accepted++;
                return true;
            }
        }
        else
        {
            _candidateX = x; _candidateY = y;
            _candidateHits = 1;
        }

        Rejected++;
        LargestRejectedJump = MathF.Max(LargestRejectedJump, distance);
        return false;
    }
}
