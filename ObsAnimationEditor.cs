using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TwitchChatAgent;

internal sealed class ObsAnimationEditor : StackPanel
{
    private static readonly string[] Names = ["Сдвиг", "Масштаб", "Прозрачность", "Частицы"];
    private static readonly string[] Curves = ["Линейная", "Плавное ускорение", "Плавное замедление", "Ускорение и замедление"];
    private readonly StackPanel cards = new();
    private readonly Button add = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<ObsAnimationItem> items = [];
    private readonly ObsTransition profile;
    private readonly bool moving;
    private IEnumerable<ObsMessageEffect> AvailableKinds => moving ? [ObsMessageEffect.Slide] : Enum.GetValues<ObsMessageEffect>();
    public ObsTransition Value => profile with { Items = items.ToArray() };
    public event Action? Changed;

    public ObsAnimationEditor(string label, ObsTransition profile, ObsMessageEffects fallback, bool moving = false)
    {
        this.profile = profile;
        this.moving = moving;
        Margin = new Thickness(0, 16, 0, 4);
        Children.Add(new TextBlock { Text = Ui.T(label), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        Children.Add(cards);
        add.Content = Ui.T("Добавить эффект");
        Children.Add(add);
        foreach (var item in (profile.Items ?? AvailableKinds
                     .Where(kind => ((int)(profile.Effects ?? fallback) & (1 << (int)kind)) != 0)
                     .Select(kind => new ObsAnimationItem(kind, true, profile with { Effects = null, Items = null })))
                     .Where(item => AvailableKinds.Contains(item.Kind)))
            AddCard(item);
        UpdateAddButton();
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var kind in AvailableKinds.Where(kind => items.All(item => item.Kind != kind)))
            {
                var option = new MenuItem { Header = Ui.T(Names[(int)kind]) };
                option.Click += (_, _) =>
                {
                    var defaults = profile with { Effects = null, Items = null };
                    AddCard(new(kind, true, defaults), expanded: true);
                    UpdateAddButton();
                    Changed?.Invoke();
                };
                menu.Items.Add(option);
            }
            add.ContextMenu = menu;
            menu.IsOpen = true;
        };
    }

    private void UpdateAddButton() => add.IsEnabled = AvailableKinds.Any(kind => items.All(item => item.Kind != kind));

    private void AddCard(ObsAnimationItem item, bool expanded = false)
    {
        items.Add(item);
        var options = item.Options;
        var title = Ui.T(Names[(int)item.Kind]);
        var content = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = EffectCardButtons.Toggle();
        var remove = EffectCardButtons.Remove();
        actions.Children.Add(toggle);
        actions.Children.Add(remove);
        var card = new Expander { Header = title, Tag = actions, Content = content, IsExpanded = expanded,
            Margin = new Thickness(0, 0, 0, 5) };
        card.SetResourceReference(StyleProperty, "EffectExpander");
        cards.Children.Add(card);

        void UpdateToggle()
        {
            EffectCardButtons.SetEnabled(toggle, item.Enabled);
            content.IsEnabled = item.Enabled;
        }
        void Replace(ObsAnimationItem next)
        {
            items[items.IndexOf(item)] = next;
            item = next;
            Changed?.Invoke();
        }
        UpdateToggle();
        toggle.Click += (_, _) =>
        {
            Replace(item with { Enabled = !item.Enabled });
            UpdateToggle();
        };
        remove.Click += (_, _) =>
        {
            cards.Children.Remove(card);
            items.Remove(item);
            UpdateAddButton();
            Changed?.Invoke();
        };
        void Slider(string label, int initial, int min, int max, Func<ObsTransition, int, ObsTransition> set) =>
            ObsEffectsEditor.Slider(content, label, initial, min, max, value => Replace(item with { Options = set(item.Options, value) }));

        Slider("Длительность (мс)", options.DurationMs, 100, 5000, (p, v) => p with { DurationMs = v });
        Slider("Задержка (мс)", options.DelayMs, 0, 2000, (p, v) => p with { DelayMs = v });
        ObsEffectsEditor.Choices(content, "Кривая перехода", Curves, (int)options.Curve,
            value => Replace(item with { Options = item.Options with { Curve = (ObsMoveCurve)value } }));
        if (item.Kind == ObsMessageEffect.Slide && !moving)
        {
            Slider("Сдвиг X (пкс)", options.OffsetX, -500, 500, (p, v) => p with { OffsetX = v });
            Slider("Сдвиг Y (пкс)", options.OffsetY, -500, 500, (p, v) => p with { OffsetY = v });
        }
        if (item.Kind == ObsMessageEffect.Scale)
        {
            Slider("Масштаб на краю (%)", options.Scale, 0, 200, (p, v) => p with { Scale = v });
            Slider("Точка масштаба X (%)", options.OriginX, 0, 100, (p, v) => p with { OriginX = v });
            Slider("Точка масштаба Y (%)", options.OriginY, 0, 100, (p, v) => p with { OriginY = v });
        }
        if (item.Kind == ObsMessageEffect.Fade)
            Slider("Непрозрачность на краю (%)", options.Opacity, 0, 100, (p, v) => p with { Opacity = v });
        if (item.Kind == ObsMessageEffect.Particles)
        {
            Slider("Размер частиц (пкс)", options.ParticleSize, 2, 12, (p, v) => p with { ParticleSize = v });
            Slider("Разлёт частиц (пкс)", options.ParticleSpread, 16, 300, (p, v) => p with { ParticleSpread = v });
            Slider("Лимит частиц", options.ParticleLimit, 50, 2000, (p, v) => p with { ParticleLimit = v });
            Slider("Направление разлёта (°)", options.ParticleDirection, -180, 180, (p, v) => p with { ParticleDirection = v });
            Slider("Ширина конуса разлёта (°)", options.ParticleCone, 0, 360, (p, v) => p with { ParticleCone = v });
            var hint = new TextBlock { Text = Ui.T("0° вправо, −90° вверх, 90° вниз, 180° влево. При конусе 360° разлёт во все стороны."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FluentSecondary");
            content.Children.Add(hint);
        }
    }
}
