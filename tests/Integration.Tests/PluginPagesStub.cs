// Exercise the documented reflection boundary without a hard Plugin Pages runtime dependency.
namespace Jellyfin.Plugin.PluginPages;
public sealed class Payload
{
    public string Json { get; init; } = "";
    public static Payload Parse(string value) => new() { Json = value };
}
public static class PluginInterface
{
    public static Payload? Registered { get; private set; }
    public static void RegisterPage(Payload value) => Registered = value;
}
