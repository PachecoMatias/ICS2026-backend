namespace Dsw2025Tpi.UnitTests;

public class FalloIntencionalTests
{
    [Fact]
    public void Prueba_Que_Falla_Para_Demostrar_El_Bloqueo_Del_Deploy()
    {
        Assert.Fail("Fallo intencional: debe bloquear el despliegue.");
    }
}