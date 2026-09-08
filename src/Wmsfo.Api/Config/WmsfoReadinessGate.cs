namespace Wmsfo.Api.Config;

// Flipped by the migrator's hosted service when the boot completes; read by the
// readiness middleware and /api/health.
public sealed class WmsfoReadinessGate
{
    private volatile bool _ready;
    public bool IsReady => _ready;
    public void MarkReady() => _ready = true;
    public void Reset() => _ready = false;
}
