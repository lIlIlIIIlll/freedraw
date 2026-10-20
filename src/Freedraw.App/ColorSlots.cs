using System.Text.Json;
using SkiaSharp;

namespace Freedraw.App;

/// <summary>
/// Os slots de cor do menu, salvos em %APPDATA%\Freedraw\colors.json.
///
/// A cor da caneta e SEMPRE a do slot ativo: o menu edita o slot no lugar, o
/// conta-gotas grava no slot ativo e Ctrl+Alt+X troca de slot. Nao existe "cor
/// solta" fora dos slots -- e isso que deixa salvar sem botao de salvar.
/// </summary>
public sealed class ColorSlots
{
    private static readonly SKColor[] Defaults =
    [
        new(0x00, 0x00, 0x00), new(0xFF, 0xD6, 0x0A), new(0x30, 0xD1, 0x58),
        new(0x0A, 0x84, 0xFF), new(0xFF, 0xFF, 0xFF),
    ];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Freedraw", "colors.json");

    /// <summary>Editado no lugar pelo menu; o tamanho e fixo.</summary>
    public SKColor[] Slots { get; } = (SKColor[])Defaults.Clone();

    public int Active
    {
        get => _active;
        set => _active = Math.Clamp(value, 0, Slots.Length - 1);
    }
    private int _active;

    public SKColor Current
    {
        get => Slots[Active];
        set => Slots[Active] = value;
    }

    private sealed record Stored(string[] Slots, int Active);

    /// <summary>Arquivo ausente ou corrompido nao e erro: volta aos padroes.
    /// Um JSON quebrado nao pode impedir o app de abrir.</summary>
    public static ColorSlots Load()
    {
        var slots = new ColorSlots();
        try
        {
            if (!File.Exists(FilePath)) return slots;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (stored?.Slots is null) return slots;

            for (int i = 0; i < Math.Min(stored.Slots.Length, slots.Slots.Length); i++)
                if (SKColor.TryParse(stored.Slots[i], out var color))
                    slots.Slots[i] = color.WithAlpha(0xFF);
            slots.Active = stored.Active;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return slots;
    }

    /// <summary>
    /// Grava por arquivo temporario + troca: um app morto no meio da escrita
    /// deixaria um JSON pela metade e perderia todos os slots.
    /// Falha de disco nao derruba o app -- so deixa de salvar.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stored = new Stored(
                Slots.Select(c => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}").ToArray(), Active);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
