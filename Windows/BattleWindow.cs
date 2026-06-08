using Dalamud.Interface.Windowing;

namespace DeathrollManager.Windows;

public class BattleWindow : Window
{
    private readonly BattleRenderer _renderer;

    public BattleWindow(BattleRenderer renderer) : base("⚔ Battle###DRBattle")
    {
        _renderer = renderer;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new(360, 340),
            MaximumSize = new(680, 560),
        };
    }

    public override void Draw() => _renderer.Draw();
}
