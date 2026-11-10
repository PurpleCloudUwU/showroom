using Microsoft.UI.Xaml.Markup;
using RetroShowroom.Core;

namespace RetroShowroom;

/// <summary>
/// Zweisprachiger Text in XAML: <c>Text="{l:Tr De='Rendern', En='Render'}"</c>.
/// Die Sprache wird beim Start festgelegt (<see cref="Loc"/>).
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string De { get; set; } = "";
    public string En { get; set; } = "";

    protected override object ProvideValue() => Loc.T(De, En);
}
