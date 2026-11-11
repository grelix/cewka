using Cewka.App.Models;

namespace Cewka.App.Views;

/// <summary>Mnożniki wielkości tekstu sceny.</summary>
internal static class SceneText
{
    /// <summary>Mnożnik rozmiaru pisma dla wybranej wielkości.</summary>
    public static double Factor(SceneTextSize size) => size switch
    {
        SceneTextSize.Large => 1.15,
        SceneTextSize.ExtraLarge => 1.3,
        _ => 1.0,
    };
}
